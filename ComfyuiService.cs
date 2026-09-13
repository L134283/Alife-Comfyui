using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Alife.Platform;

namespace Alife.Plugin.Comfyui;

[Module(
    "ComfyUI 生图",
    "连接 ComfyUI 执行工作流生图。支持 UI/API 工作流、角色 Tag 模糊检索、可选在线 Danbooru 语义标签检索、固定提示词前缀与三档常用分辨率。",
    defaultCategory: "Doro的妙妙工具",
    EditorUI = typeof(ComfyuiServiceUI))]
public class ComfyuiService(
    XmlFunctionCaller functionService,
    IInteractor<ComfyuiService> interactor,
    ConfigurationSystem configurationSystem
) : ChatBehaviour, IConfigurable<ComfyuiConfig>
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // 图片下载专用客户端（复用 UniversalImageGen 模式，60s 超时）
    static readonly HttpClient _dlHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    const int MaxInputImageBytes = 20 * 1024 * 1024; // 20MB
    static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/x-png", "image/jpeg", "image/jpg",
        "image/webp", "image/gif", "image/bmp"
    };

    public ComfyuiConfig? Configuration { get; set; } = new();

    static void Log(string msg) => Console.WriteLine($"[ComfyUI] {msg}");
    static void LogWarn(string msg) => Console.WriteLine($"[ComfyUI][警告] {msg}");
    static void LogError(string msg) => Console.WriteLine($"[ComfyUI][错误] {msg}");

    /// <summary>当前是否有生图任务在跑（优先生图 / 普通模式共用）。</summary>
    public static bool IsGenerating => Volatile.Read(ref _activeGens) > 0;
    static int _activeGens;
    // 模型占用跟踪：本次会话是否已加载过模型（1=有，0=已卸载/未加载），以及最近一次生图活动时间
    static int _modelsLoadedFlag;
    static long _lastActivityUtc;
    // 优先生图串行，避免叠多个阻塞任务拖死对话
    static readonly SemaphoreSlim PriorityGate = new(1, 1);
    static readonly ConcurrentDictionary<string, long> ClaimedCustomOutputs = new(StringComparer.OrdinalIgnoreCase);

    record WorkflowEntry(string Name, string Path, bool Enabled, string Prefix = "");

    /// <summary>
    /// 画风预设：一个工作流/模板切换多种画风。
    /// Group = ZML 强力 LoRA 加载器的「组名」（只开该组即为一种画风，配置里只存组名，数据由插件从工作流读取）；
    /// Params = 节点覆盖（workflow 模式）或模板输入（appmcp 模式）的 JSON 对象字符串。
    /// </summary>
    record StyleEntry(string Name, string Template, string Prefix, string Params, string Group = "");

    // APP-MCP 模板模式：启动时缓存的模板列表（仅用于系统提示与报错提示）
    List<AppMcpClient.AppMcpTemplate> _appMcpTemplates = new();
    // 启动时缓存的「默认模板」明细（输入名/类型），让注入说明与 UI 提示精确到该模板
    AppMcpClient.AppMcpTemplate? _appMcpDefaultTemplate;

    CharacterPromptIndex? _characterPromptIndex;
    // AnimaDex 在线角色扩充：由本地索引构建的「中文名/别名 → 英文名」「中文作品 → 英文作品」映射（键已小写去标点）
    Dictionary<string, string>? _animadexCnNameToEn;
    Dictionary<string, string>? _animadexCnWorkToEn;
    // 注册到 XmlFunctionCaller 的处理器（热重载/销毁时注销，避免旧 handler 累积）
    XmlHandler? _registeredHandler;
    // status 处理器（无文档模式）：热重载/销毁时一并注销，避免函数表累积
    XmlHandler? _statusHandler;
    // 空闲自动卸载后台循环（OnDestroy 取消，热重载不泄漏）
    CancellationTokenSource? _unloadLoopCts;
    Task? _unloadLoopTask;

    record PromptPresetEntry(string Name, string Content);

    protected override async Task OnAwake()
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        var wf = ResolveWorkflowPath(cfg);

        try
        {
            var catalogPath = ResolveCharacterCatalogPath();
            if (File.Exists(catalogPath))
            {
                _characterPromptIndex = CharacterPromptIndex.Load(catalogPath);
                Log($"已加载角色提示词索引: {_characterPromptIndex.Count} 个角色");
                BuildAnimadexLookup(_characterPromptIndex);
            }
            else
            {
                LogWarn($"角色提示词索引不存在: {catalogPath}");
            }
        }
        catch (Exception ex)
        {
            _characterPromptIndex = null;
            LogWarn($"角色提示词索引加载失败: {ex.Message}");
        }

        var appMcpMode = IsAppMcpMode(cfg);
        var namedWorkflows = new List<WorkflowEntry>();
        bool hasNamedWorkflows;
        bool supportsImageInput;

        if (appMcpMode)
        {
            // APP-MCP 模板模式：不读工作流，改为读取模板列表（失败不阻塞，生图时会再试）
            hasNamedWorkflows = false;
            supportsImageInput = false;
            _appMcpTemplates = new List<AppMcpClient.AppMcpTemplate>();
            var apiBase = ResolveAppMcpApiBase(cfg);
            try
            {
                _appMcpTemplates = await AppMcpClient.ListTemplatesAsync(apiBase, cfg.ApiToken);
                if (_appMcpTemplates.Count == 0)
                    LogWarn($"APP-MCP 未返回任何模板（或未安装 ComfyUI-APP-MCP）: {apiBase}");
                else
                    Log($"APP-MCP 模板 {_appMcpTemplates.Count} 个: {string.Join("、", _appMcpTemplates.Select(t => t.Name))}");
            }
            catch (Exception ex)
            {
                LogWarn($"APP-MCP 模板列表获取失败: {ex.Message}（生图时会再试；地址 {apiBase}）");
            }

            // 顺带取默认模板的输入明细：让注入说明与「不可用项」判定精确到该模板（失败不阻塞）
            _appMcpDefaultTemplate = null;
            var defaultTemplateName = (cfg.AppMcpTemplate ?? "").Trim();
            if (defaultTemplateName.Length > 0)
            {
                try
                {
                    _appMcpDefaultTemplate = await AppMcpClient.GetTemplateAsync(
                        apiBase, defaultTemplateName, cfg.ApiToken);
                    if (_appMcpDefaultTemplate != null)
                    {
                        Log($"APP-MCP 默认模板「{defaultTemplateName}」输入: "
                            + (_appMcpDefaultTemplate.Inputs.Count == 0
                                ? "(无)"
                                : string.Join("、", _appMcpDefaultTemplate.Inputs.Select(
                                    kv => $"{kv.Key}({kv.Value.Type.ToLowerInvariant()})"))));
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"APP-MCP 默认模板读取失败（不影响生图）: {ex.Message}");
                }
            }
        }
        else
        {
            // 只向 AI 暴露名称唯一且文件存在的工作流。
            var configuredWorkflows = ParseNamedWorkflows(cfg.NamedWorkflows)
                .Where(w => w.Enabled
                            && !string.IsNullOrWhiteSpace(w.Name)
                            && !string.IsNullOrWhiteSpace(w.Path))
                .ToList();
            foreach (var group in configuredWorkflows.GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() != 1)
                {
                    LogWarn($"工作流名称重复，未向 AI 暴露: {group.Key}");
                    continue;
                }

                var entry = group.First();
                var resolved = ResolveSinglePath(cfg, entry.Path);
                if (!File.Exists(resolved))
                {
                    LogWarn($"命名工作流文件不存在，未向 AI 暴露: {entry.Name}");
                    continue;
                }
                namedWorkflows.Add(entry with { Path = resolved });
            }
            hasNamedWorkflows = namedWorkflows.Count > 0;
            var workflowPaths = new[] { wf }.Concat(namedWorkflows.Select(w => w.Path));
            supportsImageInput = await AnyWorkflowSupportsImageInputAsync(workflowPaths);
        }

        // UI→API widget 映射回归（FBCache fixed / KSampler seed control）；仅失败时打日志
        var mappingCheck = ComfyuiWorkflowConverter.RunWidgetMappingSelfCheck();
        if (mappingCheck != null)
            LogWarn($"UI转API 自检未通过: {mappingCheck}");

        var styleLabel = cfg.PromptStyle switch
        {
            "natural" => "自然语言",
            "hybrid"  => "混合模式",
            _         => "纯 Tag"
        };
        var styleGuide = cfg.PromptStyle switch
        {
            "natural" =>
                "检索得到的角色 Tag 必须原样放在开头；其后用 2~4 个简洁英文短句依次写服装、动作、构图、场景与光线。" +
                "多人时每个角色各用一句完整短句描述其外貌/服装/表情，人物关系与互动单独一句。" +
                "避免故事叙述、否定句和互相冲突的描述。",

            "hybrid" =>
                "静态元素（身份、外貌、服装、表情、光线）用精准英文 Tag；动态内容（动作、互动、人物关系、构图、氛围）用简洁自然短句。" +
                "单人：先写该角色特征 Tag，再用 1~2 个短句写动作、构图、场景与光线。" +
                "多人：先写人数（如 1girl, 1boy），每个角色各自一个独立短语单元，用自然语言串联其外貌/服装/表情（如 a silver-haired girl in a white maid outfit, smiling），角色间逗号分隔；最后用 1 个短句写人物关系、互动与场景。" +
                "表情/姿势归属所在角色；禁止把多个角色的特征混在同一串 Tag 里；去重并避免矛盾。",

            _ =>
                "使用具体的 Danbooru 风格英文 Tag，半角逗号分隔。顺序为主体身份、人数、外貌、服装、表情动作、构图、背景与光线。" +
                "多人时先写人数（1girl, 1boy 等），每个角色的特征 Tag 各自连续排列、角色间用逗号分隔，禁止混写；去重并避免矛盾。"
        };

        var autoOpenNote = cfg.AutoOpenImage
            ? "\n- 桌面端会自动打开成图，无需再发送本地图片。"
            : "";

        var priorityNote = "";
        if (cfg.PriorityImageGen)
        {
            var hardCap = Math.Clamp(cfg.PriorityMaxWaitSeconds, 30, 1800);
            priorityNote = $"""

            【优先生图】调用 generateimage 后等待结果再继续；等待期间不要调用语音功能。最长约 {hardCap} 秒。
            """;
        }

        // 4.0 隐式注入：函数文档按需加载，需提示 AI 先打开入口
        var implicitNote = cfg.ImplicitInjection
            ? "\n- 隐式注入已开启：使用生图/检索功能前，先调用 <comfyuiimagegeneration/> 加载完整函数说明与调用方式，再按文档调用对应函数。"
            : "";

        var workflowListDesc = hasNamedWorkflows
            ? $"\n- 可选 workflow：{string.Join("、", namedWorkflows.Select(w => $"\"{w.Name}\""))}。"
            : "";

        var nodeControlDesc = cfg.EnableNodeControl && !appMcpMode
            ? "\n- 高级节点控制已开启；仅在用户明确要求时修改。需要节点信息时先调用 getcomfyuicontrols。"
            : "";

        var imageInputNote = supportsImageInput
            ? "\n- 图生图 denoise 档位：微调 0.30~0.45、换装/姿势 0.55~0.70、大改 0.70~0.85；文生图不传。"
            : "";

        var characterLookupAvailable = _characterPromptIndex != null || cfg.EnableAnimadexCharacterSearch;
        var characterLookupNote = characterLookupAvailable
            ? "\n【角色检索】\n"
              + "- findcharacterprompt 只认 status: matched；ambiguous 返回候选时带作品名重查，禁止直接猜 tag；组合时保持 &amp; 等转义原文。\n"
              + (cfg.EnableAnimadexCharacterSearch
                  ? "- 已开在线扩充：本地未收录时该函数会自动联网查 AnimaDex；返回 source: animadex 时无默认服装段，服装按请求自写；在线查询失败会自动回到纯本地提示。\n"
                  : "")
            : "";

        var presetNote = """

            【提示词预设】
            - 预设内容按请求调整后组合进 prompt，勿原样塞入；用户要保存时调 savepromptpreset。
            """;

        var modelUnloadNote = cfg.EnableAutoUnload
            ? $"""

            【显存释放】
            - 空闲自动卸载已开启：距上次生图空闲超过 {cfg.AutoUnloadIdleHours} 小时 {cfg.AutoUnloadIdleMinutes} 分钟会自动卸载模型。用户要求立即腾显存/给其他程序让 GPU/长期不用生图时，仍可调 unloadcomfyuimodels 立即卸载；用 setcomfyuiidleunload 可查看或调整空闲时长。
            """
            : """

            【显存释放】
            - 用户要求释放显存/给其他程序腾 GPU/长时间不用生图时，调 unloadcomfyuimodels 立即卸载已加载模型；也可用 setcomfyuiidleunload 开启并设置空闲超时后自动卸载（当前空闲自动卸载为关闭）。
            """;

        var danbooruNote = "";
        if (cfg.EnableDanbooruSearch)
        {
            var styleUseLine = cfg.PromptStyle switch
            {
                "natural" =>
                    "- 自然语言模式同样积极 search（可用 full_scene 一次取概念）；禁止把返回 tag 列表原样当作整段 prompt。",
                "hybrid" =>
                    "- 混合模式：检索 tag 进身份/外貌/服装/表情的 Tag 段；动作、关系、场景仍用短句。",
                _ =>
                    "- 纯 Tag 模式：充分吸收返回英文 tag（去重去矛盾）；复杂画面可用 full_scene。"
            };

            var artistLine = cfg.EnableDanbooruArtistRecommend
                ? "\n- 仅当用户明确要画风/画师时，可再调用 getdanbooruartists（tags=已确定的英文 tag）；单次生图最多 1 次；未要求不要调用。"
                : "";

            danbooruNote = $"""

            【在线标签检索】
            - 单次生图 search≤1、related≤1；unavailable 就自写英文直接生成，勿反复重试。
            - 优先级：角色 > 预设 > 在线 > 自写；在线不得覆盖角色身份/服装。
            {styleUseLine}{artistLine}
            """;
        }

        // APP-MCP 模板模式说明（仅模板模式注入：函数文档里的 workflow/节点控制等规则已隐藏）
        var appMcpNote = "";
        if (appMcpMode)
        {
            var templateList = _appMcpTemplates.Count > 0
                ? string.Join("、", _appMcpTemplates.Select(t =>
                    string.IsNullOrWhiteSpace(t.Title) ? t.Name : $"{t.Name}({t.Title})"))
                : (string.IsNullOrWhiteSpace(cfg.AppMcpTemplate) ? "（未能读取模板列表）" : cfg.AppMcpTemplate);
            var defaultTemplate = string.IsNullOrWhiteSpace(cfg.AppMcpTemplate)
                ? "未设置（必须由 AI 指定 template）"
                : cfg.AppMcpTemplate;
            var promptParam = string.IsNullOrWhiteSpace(cfg.AppMcpPromptParam) ? "positive" : cfg.AppMcpPromptParam.Trim();
            var defaultParams = string.IsNullOrWhiteSpace(cfg.AppMcpDefaultParams) ? "{}" : cfg.AppMcpDefaultParams.Trim();

            // 按「默认模板」的实际声明给出精确说明（读不到时退化为通用说明）
            var templateDetail = "";
            var imageLine = "- 图生图：仅当模板声明了图片输入时才支持 imagepath，未声明会被忽略（插件会记日志）。";
            var prefixLine = cfg.AppMcpUseGlobalPrefix
                ? "- 画风/质量：插件固定前缀会拼在 prompt 最前；若模板内部已带同类前缀可能重复，可在插件配置里关闭。"
                : "";
            if (_appMcpDefaultTemplate != null)
            {
                var declaredInputs = _appMcpDefaultTemplate.Inputs.Count == 0
                    ? "(无输入)"
                    : string.Join("、", _appMcpDefaultTemplate.Inputs.Select(
                        kv => $"{kv.Key}({kv.Value.Type.ToLowerInvariant()})"));
                templateDetail = $"\n- 默认模板已声明输入：{declaredInputs}。";

                imageLine = FindTemplateImageParam(_appMcpDefaultTemplate) != null
                    ? "- 图生图：默认模板已声明图片输入，可用 imagepath。"
                    : ""; // 无图片输入时 imagepath 参数已隐藏，无需再解释

            }

            var defaultParamsLine = defaultParams == "{}"
                ? ""
                : $"- 模板默认参数已由插件配置：{defaultParams}（AI 可用 params 覆盖其中已声明的输入）。";

            appMcpNote = $$"""

            【当前后端：APP-MCP 模板模式】
            - 调用方式：prompt 写画面（插件会自动写入模板输入「{{promptParam}}」）；用 template 选模板（不传则用默认模板）；用 params 传其它模板输入（JSON 对象，如 {"参数名": 值}）。
            - 可用模板：{{templateList}}；默认模板：{{defaultTemplate}}。{{templateDetail}}
            {{defaultParamsLine}}
            - params 中只有模板已声明的输入才生效（未声明的会被忽略）；需要输入明细时调用 getcomfyuistyle。
            {{imageLine}}
            {{prefixLine}}
            """;
        }

        // 画风预设：一个工作流/模板切换多种画风
        var stylePresetList = ParseStylePresets(cfg.StylePresets);
        var styleNames = stylePresetList.Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var styleNote = appMcpMode && styleNames.Count > 0
            ? $"\n- 画风预设（generateimage 传 style=名称 可一键切换画风）：{string.Join("、", styleNames)}。用户指定某种画风/风格时优先使用对应预设；未命中再自写。"
            : "";

        // ===================== 系统提示词：严格按后端模式分流，禁止串味 =====================
        // 硬规则（两模式共有）中「画风/质量的归属」与「尺寸是否可控」必须按模式区分，
        // 否则模板模式下会出现「由固定前缀/工作流决定」「传 orientation」这类无效指令。
        var flavorOwner = appMcpMode
            ? "由所调用的 APP-MCP 模板自身决定（模板内通常已内置画风/质量词与 LoRA 触发词）"
            : "由插件固定前缀或工作流决定";
        var sizeRule = appMcpMode
            ? (_appMcpDefaultTemplate != null && HasTemplateSizeInput(_appMcpDefaultTemplate)
                ? "- 尺寸：模板已声明尺寸输入，可用 width/height 覆盖。"
                : "- 尺寸：画面尺寸由模板决定，无需指定。")
            : $"- 方向：portrait=竖版、landscape=横版、square=正方形；默认 {cfg.DefaultOrientation}，也可直接传 width/height。";

        var hardRules = $$"""
        【ComfyUI 生图】
        - 所有 prompt 使用英文，直接描述目标画面，不要把用户的中文命令原句塞进 prompt。
        - 不要写画风/质量类提示词（如 masterpiece、best quality、score、style、画师名等）：画风与质量{{flavorOwner}}，AI 只写主体、动作、服装、构图、场景与氛围；用户明确指定画风/画师时例外。
        {{sizeRule}}
        - 提示词模式：{{styleLabel}}。{{styleGuide}}
        - QQ 发图：<qimage type="Private/Group" targetid="QQ号或群号" image="完整路径" />（私聊 Private / 群聊 Group）。
        """;

        // 后端专属规则：按模式二选一，两边互不掺入
        // —— 工作流模式：显式声明模板参数无效，避免 AI 误传 template/templateparams
        var workflowModeNote = """

        【当前后端：工作流模式】
        - 只走工作流：不要传 template / templateparams（那是 APP-MCP 模板模式的参数，本模式无效）。
        """;

        var backendRules = appMcpMode
            ? appMcpNote
            : $"{workflowModeNote}{workflowListDesc}{imageInputNote}{nodeControlDesc}";

        // 详细规则：与各函数的使用时机/约束相关。显式模式直接注入；
        // 隐式模式放进 handler.Explanation，AI 调用 <comfyuiimagegeneration/> 后随文档一并加载，
        // 避免这些规则在开启隐式注入时仍常时占用 token。
        var detailedRules = $$"""
        {{backendRules}}{{styleNote}}{{autoOpenNote}}{{characterLookupNote}}{{presetNote}}{{danbooruNote}}{{priorityNote}}{{modelUnloadNote}}
        """;

        RegisterFunctionHandlers(cfg, hasNamedWorkflows, supportsImageInput, appMcpMode,
            cfg.ImplicitInjection ? detailedRules : null);

        if (cfg.ImplicitInjection)
            interactor.Prompt(hardRules + implicitNote);
        else
            interactor.Prompt(hardRules + detailedRules + implicitNote);

        // 空闲自动卸载后台循环（热重载/销毁时随 OnDestroy 取消）
        StartUnloadLoop();
    }

    /// <summary>热重载/活动销毁时注销本模块注册的 XmlHandler，避免旧 handler 残留在函数表中。</summary>
    protected override Task OnDestroy()
    {
        if (_registeredHandler != null)
        {
            functionService.UnregisterHandler(_registeredHandler);
            _registeredHandler = null;
        }
        if (_statusHandler != null)
        {
            functionService.UnregisterHandler(_statusHandler);
            _statusHandler = null;
        }
        if (_unloadLoopCts != null)
        {
            _unloadLoopCts.Cancel();
            _unloadLoopCts.Dispose();
            _unloadLoopCts = null;
            _unloadLoopTask = null;
        }
        return Task.CompletedTask;
    }

    void RegisterFunctionHandlers(
        ComfyuiConfig cfg, bool hasNamedWorkflows, bool supportsImageInput,
        bool appMcpMode, string? explanation = null)
    {
        var discovered = new XmlHandler(this);
        var functions = discovered.Functions.ToDictionary(
            function => function.Name, StringComparer.OrdinalIgnoreCase);

        if (functions.TryGetValue("checkcomfyuistatus", out var statusFunction))
        {
            _statusHandler = new XmlHandler("ComfyuiStatusInternal")
            {
                Functions = new List<XmlFunction> { statusFunction }
            };
            functionService.RegisterHandlerWithoutDocument(_statusHandler);
        }

        var exposed = new List<XmlFunction>();
        if (functions.TryGetValue("generateimage", out var generateFunction))
        {
            var hiddenParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (appMcpMode)
            {
                // 模板模式：工作流/节点参数不适用，改为 template / templateparams
                foreach (var name in new[]
                {
                    "workflow", "model", "steps", "cfg", "sampler", "scheduler",
                    "batch_size", "nodeoverrides", "denoise"
                })
                    hiddenParameters.Add(name);

                // 按默认模板实际能力隐藏无效参数，避免 AI 看到并误传
                if (_appMcpDefaultTemplate != null)
                {
                    if (FindTemplateImageParam(_appMcpDefaultTemplate) == null)
                        hiddenParameters.Add("imagepath");
                    if (!HasTemplateSizeInput(_appMcpDefaultTemplate))
                    {
                        hiddenParameters.Add("orientation");
                        hiddenParameters.Add("width");
                        hiddenParameters.Add("height");
                    }
                }
            }
            else
            {
                hiddenParameters.Add("template");
                hiddenParameters.Add("templateparams");
                hiddenParameters.Add("style");
                if (!hasNamedWorkflows)
                    hiddenParameters.Add("workflow");
                if (!supportsImageInput)
                {
                    hiddenParameters.Add("imagepath");
                    hiddenParameters.Add("denoise");
                }
                if (!cfg.EnableNodeControl)
                {
                    foreach (var name in new[]
                    {
                        "model", "steps", "cfg", "sampler", "scheduler",
                        "batch_size", "nodeoverrides"
                    })
                        hiddenParameters.Add(name);
                }
            }

            var description = appMcpMode
                ? "使用 ComfyUI-APP-MCP 模板生成图片：template 选模板（默认用配置模板），prompt 写画面，params 传模板输入（可选，含画风/LoRA 组切换参数）。"
                : supportsImageInput
                    ? "使用 ComfyUI 生成图片；传 imagepath 时执行图生图。"
                    : "使用 ComfyUI 生成图片。";
            exposed.Add(CloneFunctionDocument(
                generateFunction, description, hiddenParameters));
        }

        if ((_characterPromptIndex != null || cfg.EnableAnimadexCharacterSearch)
            && functions.TryGetValue("findcharacterprompt", out var characterFunction))
        {
            var characterDesc = _characterPromptIndex == null
                ? "仅在明确生成某个既有动漫、游戏、漫画或虚拟主播角色时调用（当前无本地索引，将联网检索；建议传英文/罗马字角色名与作品名）。"
                : "仅在明确生成某个既有动漫、游戏、漫画或虚拟主播角色时调用；返回身份、稳定外貌和默认服装 Tag。";
            exposed.Add(CloneFunctionDocument(
                characterFunction, characterDesc));
        }

        if (!appMcpMode && cfg.EnableNodeControl
            && functions.TryGetValue("getcomfyuicontrols", out var controlsFunction))
        {
            var hiddenParameters = hasNamedWorkflows
                ? null
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "workflow" };
            exposed.Add(CloneFunctionDocument(
                controlsFunction,
                "按需查看当前工作流允许 AI 调整的安全节点参数。",
                hiddenParameters));
        }

        // 提示词预设：始终暴露，AI 按需调用检索/保存
        if (functions.TryGetValue("getpromptpreset", out var getPresetFunction))
        {
            exposed.Add(CloneFunctionDocument(
                getPresetFunction,
                "检索提示词预设（角色人设/动作/背景等）。不传 name 返回列表；传 name 返回内容组合到 prompt"));
        }
        if (functions.TryGetValue("savepromptpreset", out var savePresetFunction))
        {
            exposed.Add(CloneFunctionDocument(
                savePresetFunction,
                "保存提示词预设（角色人设/动作/背景等），下次可按名称检索复用"));
        }

        // 在线 Danbooru 语义检索：总开关开才暴露；画师另开
        if (cfg.EnableDanbooruSearch)
        {
            if (functions.TryGetValue("searchdanboorutags", out var searchFn))
            {
                exposed.Add(CloneFunctionDocument(
                    searchFn,
                    "有画面细节时调用以提升质量：自然语言→标准 Danbooru 英文 tag。三种提示词模式均适用；结果按当前模式组织进 prompt，勿把中文原句当 prompt。"));
            }
            if (functions.TryGetValue("getrelateddanboorutags", out var relatedFn))
            {
                exposed.Add(CloneFunctionDocument(
                    relatedFn,
                    "已有英文 Danbooru tag 时补共现搭配；单次生图最多 1 次。"));
            }
            if (cfg.EnableDanbooruArtistRecommend
                && functions.TryGetValue("getdanbooruartists", out var artistsFn))
            {
                exposed.Add(CloneFunctionDocument(
                    artistsFn,
                    "用户明确要画风/画师时，按已确定英文 tag 推荐画师；未要求不要调用。"));
            }
        }

        // 模型卸载与空闲自动释放：始终暴露，AI 按需调用
        if (functions.TryGetValue("unloadcomfyuimodels", out var unloadFn))
        {
            exposed.Add(CloneFunctionDocument(
                unloadFn,
                "立即卸载 ComfyUI 已加载的模型并释放显存/内存，让出 GPU 给其他程序。用户要求腾显存/提速/长期不用生图时调用。"));
        }
        if (functions.TryGetValue("setcomfyuiidleunload", out var idleFn))
        {
            exposed.Add(CloneFunctionDocument(
                idleFn,
                "查看或设置 ComfyUI 空闲自动卸载：距最近一次生图结束空闲超过设定时长后自动卸载模型释放显存。用户想自动释放显存时调用；不传参可查看当前设置。"));
        }

        // 画风预设 / 模板输入明细：始终暴露，AI 按需查看
        if (appMcpMode && functions.TryGetValue("getcomfyuistyle", out var styleFunction))
        {
            exposed.Add(CloneFunctionDocument(
                styleFunction,
                "查看画风预设列表（可用 generateimage 的 style 参数一键切换画风）；附带当前模板的输入明细。"));
        }

        var handlerDesc = appMcpMode ? "ComfyUI 生图（APP-MCP 模板模式）" : "ComfyUI 生图（工作流模式）";
        if (_characterPromptIndex != null || cfg.EnableAnimadexCharacterSearch)
            handlerDesc += "与具体二次元角色提示词检索";
        if (cfg.EnableDanbooruSearch)
            handlerDesc += "、在线标签检索";
        handlerDesc += "。";

        // 4.0：DocumentMode 控制函数文档的注入方式。
        // 显式（默认）：完整函数文档直接注入系统提示词，AI 开箱即用；
        // 隐式：只暴露触发标签 <comfyuiimagegeneration/>，AI 需先调用它按需加载文档（省 token，渐进式）。
        var documentMode = cfg.ImplicitInjection
            ? DocumentMode.Implicit
            : DocumentMode.Explicit;
        var handler = new XmlHandler("ComfyuiImageGeneration")
        {
            Description = handlerDesc,
            // 隐式模式：详细规则随 <comfyuiimagegeneration/> 加载的文档一并输出；显式模式保持 null 避免重复注入
            Explanation = explanation,
            Functions = exposed
        };
        _registeredHandler = handler;
        if (documentMode == DocumentMode.Implicit)
            Log("隐式注入已开启：AI 需先调用 <comfyuiimagegeneration/> 按需加载函数文档");
        functionService.RegisterHandler(handler, documentMode);
    }

    static XmlFunction CloneFunctionDocument(
        XmlFunction source,
        string description,
        IReadOnlySet<string>? hiddenParameters = null)
    {
        return new XmlFunction
        {
            Name = source.Name,
            Order = source.Order,
            Mode = source.Mode,
            Description = description,
            ContentName = source.ContentName,
            ContentDescription = source.ContentDescription,
            Parameters = source.Parameters
                .Where(parameter => hiddenParameters?.Contains(parameter.Name) != true)
                .ToList(),
            Invoker = source.Invoker
        };
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("使用 ComfyUI 工作流生成图片（生图主入口）。正向提示词必填；其余方向/尺寸/图生图/切换工作流/采样参数等均为可选，见各参数说明。")]
    public async Task GenerateImage(
        [Description("正向提示词，描述画面内容（主体、动作、服装、构图、场景与氛围；画风/质量无需写）")] string prompt,
        [Description("图片方向：portrait=竖版、landscape=横版、square=正方形；实际尺寸取当前配置")] string? orientation = null,
        [Description("输入图片路径或 URL（本地路径、QQ 图片链接等）。传了则图生图，不传则为文生图")] string? imagePath = null,
        [Description("图片宽度，显式指定则覆盖 orientation")] int? width = null,
        [Description("图片高度，显式指定则覆盖 orientation")] int? height = null,
        [Description("【高级】指定工作流名称（如\"文生图\"），从已选工作流列表中切换。不传则用默认工作流")] string? workflow = null,
        [Description("【高级】采样步数，覆盖 KSampler 中的 steps")] int? steps = null,
        [Description("【高级】CFG Scale（如 7），覆盖 KSampler 中的 cfg")] double? cfg = null,
        [Description("【高级】采样器名称（如 euler, dpmpp_2m），覆盖 KSampler 中的 sampler_name")] string? sampler = null,
        [Description("【高级】调度器名称（如 normal, karras），覆盖 KSampler 中的 scheduler")] string? scheduler = null,
        [Description("【高级】大模型文件名（如 meinamix_v11.safetensors），覆盖 CheckpointLoader 中的 ckpt_name")] string? model = null,
        [Description("降噪强度 0.0~1.0，仅图生图有效。控制原图保留程度，文生图请勿传入")] double? denoise = null,
        [Description("【高级】批次大小，覆盖 EmptyLatentImage 中的 batch_size")] int? batch_size = null,
        [Description("【高级】安全节点覆盖，格式 {\"节点ID\":{\"参数名\":标量值}}")] string? nodeOverrides = null,
        [Description("画风预设名（配置里预设的「画风」/「风格」）。一个工作流或模板即可切换多种画风；不传则用默认画风")] string? style = null,
        [Description("【APP-MCP 模板模式】指定模板名（不传则用配置里的默认模板）")] string? template = null,
        [Description("【APP-MCP 模板模式】模板输入参数（JSON 对象），如 {\"参数名\": 值}；只有模板已声明的输入才会生效")] string? templateParams = null)
    {
        var cfgConfig = Configuration ?? new ComfyuiConfig();
        if (!cfgConfig.PriorityImageGen)
            LogWarn($"优先生图未生效：运行时 PriorityImageGen=false（Configuration={(Configuration is null ? "null" : "已注入")}）。请确认 UI 已开启并保存（注意区分「应用到角色/应用到全局」，角色级旧配置会覆盖全局新值），必要时重启活动。");
        if (cfgConfig.PriorityImageGen)
        {
            // 阻塞到出图结束：同轮 Speak 等函数返回后再执行，避免 TTS 与 Comfy 抢 GPU
            Log("优先生图：等待出图完成后再返回…");
            bool entered = false;
            try
            {
                // 若已有任务在跑，短等；超时则拒绝，避免对话永久挂起
                entered = await PriorityGate.WaitAsync(TimeSpan.FromSeconds(3));
                if (!entered)
                {
                    interactor.Poke("已有生图任务进行中，请稍后再试（优先生图模式不叠任务）");
                    return;
                }

                await RunGenerationSafelyAsync(
                    prompt, orientation, imagePath, width, height,
                    workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size, nodeOverrides,
                    style, template, templateParams,
                    priorityMode: true);
            }
            finally
            {
                if (entered)
                    PriorityGate.Release();
            }
        }
        else
        {
            interactor.Poke("生图请求已发出，可以继续聊天");
            _ = RunGenerationSafelyAsync(
                prompt, orientation, imagePath, width, height,
                workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size, nodeOverrides,
                style, template, templateParams,
                priorityMode: false);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查 ComfyUI 是否在线，并返回系统状态摘要")]
    public void CheckComfyuiStatus()
    {
        _ = CheckStatusAsync();
    }

    // ===================== 模型卸载与空闲自动释放 =====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立即卸载 ComfyUI 已加载的模型并释放显存/内存，让出 GPU 给其他程序。正在生图时不可用")]
    public async Task UnloadComfyuiModels()
    {
        var msg = await UnloadModelsNowAsync(Configuration);
        interactor.Poke(msg);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看或设置 ComfyUI 空闲自动卸载：距最近一次生图结束空闲超过设定时长后，自动卸载模型释放显存/内存。不传任何参数返回当前设置；enable 开/关；hours+minutes 设定空闲时长（合计最少 1 分钟）")]
    public void SetComfyuiIdleUnload(
        [Description("是否启用空闲自动卸载；不传则不改变当前启用状态")] bool? enable = null,
        [Description("空闲小时数 0~720，与 minutes 相加为总空闲时长")] int? hours = null,
        [Description("空闲分钟数 0~59，与 hours 相加为总空闲时长")] int? minutes = null)
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        var changed = false;
        if (enable.HasValue && cfg.EnableAutoUnload != enable.Value)
        {
            cfg.EnableAutoUnload = enable.Value;
            changed = true;
        }
        if (hours.HasValue && cfg.AutoUnloadIdleHours != hours.Value)
        {
            cfg.AutoUnloadIdleHours = Math.Clamp(hours.Value, 0, 720);
            changed = true;
        }
        if (minutes.HasValue && cfg.AutoUnloadIdleMinutes != minutes.Value)
        {
            cfg.AutoUnloadIdleMinutes = Math.Clamp(minutes.Value, 0, 59);
            changed = true;
        }

        var total = cfg.AutoUnloadIdleHours * 60 + cfg.AutoUnloadIdleMinutes;
        if (cfg.EnableAutoUnload && total < 1)
        {
            cfg.AutoUnloadIdleMinutes = 1;
            total = 1;
            changed = true;
        }

        // 仅在实际修改时落盘并打日志；纯查询（未传参数或值与现值相同）不写配置
        if (changed)
        {
            SaveConfig();
            Log($"设置空闲自动卸载: {(cfg.EnableAutoUnload ? "开" : "关")} 空闲 {cfg.AutoUnloadIdleHours}h{cfg.AutoUnloadIdleMinutes}m");
        }

        interactor.Poke(
            $"status: {(cfg.EnableAutoUnload ? "enabled" : "disabled")}\n" +
            $"idle_hours: {cfg.AutoUnloadIdleHours}\n" +
            $"idle_minutes: {cfg.AutoUnloadIdleMinutes}\n" +
            $"idle_total_minutes: {total}\n" +
            $"action: {(cfg.EnableAutoUnload
                ? $"空闲超过 {total} 分钟将自动卸载模型释放显存；也可随时调用 unloadcomfyuimodels 立即卸载"
                : "空闲自动卸载已关闭；如需立即释放显存可调用 unloadcomfyuimodels")}");
    }

    /// <summary>
    /// 立即卸载 ComfyUI 已加载模型并释放显存/内存（UI 按钮与 AI 函数共用入口）。
    /// 正在生图时拒绝，避免打断出图。
    /// </summary>
    public static async Task<string> UnloadModelsNowAsync(ComfyuiConfig? cfg)
    {
        var config = cfg ?? new ComfyuiConfig();
        if (IsGenerating)
            return "正在生图中，为避免打断出图，暂不能卸载模型";

        try
        {
            var baseUrl = NormalizeBaseUrl(config.BaseUrl);
            using var req = CreateRequest(HttpMethod.Post, $"{baseUrl}/free", config);
            req.Content = new StringContent(
                "{\"unload_models\": true, \"free_memory\": true}",
                Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync();
                var preview = string.IsNullOrWhiteSpace(raw) ? "" : (raw.Length > 200 ? raw[..200] + "..." : raw);
                LogWarn($"卸载模型失败 (HTTP {(int)resp.StatusCode}) {preview}");
                return $"卸载失败 (HTTP {(int)resp.StatusCode})";
            }

            Volatile.Write(ref _modelsLoadedFlag, 0);
            Volatile.Write(ref _lastActivityUtc, DateTime.UtcNow.Ticks);
            Log("已卸载模型并释放显存/内存");
            return "已卸载 ComfyUI 已加载的模型并释放显存/内存";
        }
        catch (Exception ex)
        {
            LogWarn($"卸载模型失败: {ex.Message}");
            return $"卸载模型失败: {ex.Message}";
        }
    }

    void StartUnloadLoop()
    {
        if (_unloadLoopTask != null)
            return;
        _unloadLoopCts = new CancellationTokenSource();
        _unloadLoopTask = RunUnloadLoopAsync(_unloadLoopCts.Token);
    }

    async Task RunUnloadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await TryAutoUnloadAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（OnDestroy）
        }
        catch (Exception ex)
        {
            LogWarn($"空闲自动卸载循环异常: {ex.Message}");
        }
    }

    async Task TryAutoUnloadAsync(CancellationToken ct)
    {
        var cfg = Configuration;
        if (cfg == null || !cfg.EnableAutoUnload)
            return;
        // 正在生图不卸，避免打断出图
        if (IsGenerating)
            return;
        // 本次会话没加载过模型就不空跑
        if (Volatile.Read(ref _modelsLoadedFlag) == 0)
            return;

        var totalMinutes = Math.Max(1, cfg.AutoUnloadIdleHours * 60 + cfg.AutoUnloadIdleMinutes);
        var lastTicks = Volatile.Read(ref _lastActivityUtc);
        if (lastTicks == 0)
            return;
        var idle = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
        if (idle < TimeSpan.FromMinutes(totalMinutes))
            return;

        // 发请求前再确认一次没有正在生图的任务
        if (IsGenerating)
            return;

        Log($"空闲已超过 {totalMinutes} 分钟，自动卸载模型释放显存…");
        try
        {
            var msg = await UnloadModelsNowAsync(cfg);
            interactor.Poke(msg);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogWarn($"自动卸载模型失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将当前 Configuration 落盘（角色级配置优先，回退全局）。
    /// AI 通过 setcomfyuiidleunload 修改的运行时配置若不主动保存，重载/重启后会丢失。
    /// </summary>
    void SaveConfig()
    {
        try
        {
            if (Configuration == null)
                return;
            configurationSystem.SetConfiguration(
                typeof(ComfyuiService),
                Configuration,
                Character?.StorageKey ?? "");
        }
        catch (Exception ex)
        {
            LogWarn($"配置保存失败: {ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("仅检索明确指定的既有二次元角色；支持中文、英文、别名、部分名称和少量错字。")]
    public async Task FindCharacterPrompt(
        [Description("角色名，支持中文、英文、别名或不完整名称")] string name,
        [Description("可选作品名；同名角色或短名称时建议填写")] string? work = null)
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        var onlineEnabled = cfg.EnableAnimadexCharacterSearch
            && !string.IsNullOrWhiteSpace(cfg.AnimadexBaseUrl);

        // 本地检索（优先）：中文/别名/错字消歧质量高且离线
        IReadOnlyList<CharacterPromptMatch> localMatches = Array.Empty<CharacterPromptMatch>();
        if (_characterPromptIndex != null)
        {
            localMatches = _characterPromptIndex.Search(name, work, 3);
        }
        else if (!onlineEnabled)
        {
            interactor.Poke("角色提示词索引未加载，请检查 character-prompts.json 是否位于插件目录，或在插件 UI 开启「在线角色检索扩充」");
            return;
        }

        var localAmbiguous = false;
        if (localMatches.Count > 0)
        {
            var best = localMatches[0];
            var strongWorkMatch = !string.IsNullOrWhiteSpace(work) && best.WorkScore >= 0.85;
            localAmbiguous = best.Score < 0.80
                || localMatches.Count > 1
                   && best.Score - localMatches[1].Score < 0.04
                   && !strongWorkMatch;

            if (!localAmbiguous)
            {
                PokeLocalMatched(name, best);
                return;
            }
            // 歧义且带 work：本地细分版本分不开，先尝试在线；否则保持候选提示
            if (!onlineEnabled || string.IsNullOrWhiteSpace(work))
            {
                PokeLocalAmbiguous(name, localMatches);
                return;
            }
        }

        // 在线扩充（默认关）：本地无命中，或带 work 仍歧义
        if (onlineEnabled)
        {
            var onlineText = await TryAnimadexLookupAsync(cfg, name, work);
            if (onlineText != null)
            {
                interactor.Poke(onlineText);
                return;
            }
        }

        if (localMatches.Count > 0)
        {
            PokeLocalAmbiguous(name, localMatches);
            return;
        }

        interactor.Poke("status: not_found\nquery: " + name
             + (string.IsNullOrWhiteSpace(work) ? "" : $"\nwork: {work}")
             + "\naction: 不要编造角色 Tag；检查角色名或作品名后再查询"
             + (onlineEnabled ? "；在线库也未命中，可尝试英文/罗马字角色名与作品名（AnimaDex 不支持中文检索）" : ""));
    }

    void PokeLocalMatched(string name, CharacterPromptMatch best)
    {
        var matchScore = (int)Math.Round(best.Score * 100);
        var result = new StringBuilder()
            .AppendLine("status: matched")
            .Append("character: ").Append(best.Entry.ChineseName)
            .Append(" [").Append(best.Entry.EnglishName).Append("] | ")
            .AppendLine(DisplayWork(best.Entry))
            .Append("match_score: ").Append(matchScore).AppendLine("/100")
            .Append("trigger_tags_xml: ").AppendLine(XmlSafe(best.Entry.TriggerTags))
            .Append("appearance_tags_xml: ").AppendLine(XmlSafe(best.Entry.AppearanceTags))
            .Append("default_outfit_tags_xml: ").AppendLine(XmlSafe(best.Entry.DefaultOutfitTags))
            .Append("usage: 必须保留前两项；指定新服装时不要使用默认服装，动作、构图和场景按请求自由组合");

        interactor.Poke(result.ToString());
        Log($"角色检索: {name} -> {best.Entry.EnglishName} ({matchScore}/100)");
    }

    void PokeLocalAmbiguous(string name, IReadOnlyList<CharacterPromptMatch> matches)
    {
        var candidates = string.Join("\n", matches.Select((match, index) =>
            $"{index + 1}. {match.Entry.ChineseName} [{match.Entry.EnglishName}] | " +
            $"{DisplayWork(match.Entry)} | match_score: {(int)Math.Round(match.Score * 100)}/100"));
        interactor.Poke($"status: ambiguous\nquery: {name}\ncandidates:\n{candidates}\n" +
             "action: 不要使用任何角色 Tag；请结合上下文选定角色后，带作品名 work 重新查询");
        Log($"角色检索存在歧义: {name}");
    }

    /// <summary>
    /// AnimaDex 在线角色扩充查询。返回可 Poke 文本；null 表示在线未能给出结果（服务不可用或未找到），由调用方走原本地提示。
    /// </summary>
    async Task<string?> TryAnimadexLookupAsync(ComfyuiConfig cfg, string name, string? work)
    {
        var enName = ResolveEnCandidate(name, _animadexCnNameToEn);
        if (string.IsNullOrEmpty(enName))
            return null;
        var enWork = string.IsNullOrWhiteSpace(work)
            ? null
            : ResolveEnCandidate(work, _animadexCnWorkToEn);

        var res = await AnimadexClient.SearchCharactersAsync(
            enName, enWork,
            cfg.AnimadexBaseUrl?.Trim() ?? AnimadexClient.DefaultBaseUrl,
            cfg.AnimadexTimeoutSeconds);

        if (res.Unavailable)
        {
            LogWarn($"AnimaDex 在线角色检索不可用({enName}): {res.Error}");
            return null;
        }
        if (res.Matches.Count == 0)
            return null;

        // 明确给了作品名但在线结果里该作品完全未命中：判定为该作品下未收录，避免拿同名异作品角色冒充
        if (!string.IsNullOrEmpty(enWork) && !res.WorkMatched)
        {
            Log($"角色检索(在线 AnimaDex): {name} 名称命中但作品「{enWork}」下未收录，判定未找到");
            return null;
        }

        var hit = res.Matches[0];
        var appearance = string.Join(", ", hit.Tags.Take(40));
        var sb = new StringBuilder()
            .AppendLine("status: matched")
            .AppendLine("source: animadex")
            .Append("character: ").Append(hit.Name)
            .Append(" [").Append(hit.Slug).Append("] | 作品: ")
            .AppendLine(string.IsNullOrWhiteSpace(hit.CopyrightName) ? "-" : hit.CopyrightName)
            .Append("images: ").AppendLine(hit.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
            .Append("trigger_tags_xml: ").AppendLine(XmlSafe(hit.Trigger))
            .Append("appearance_tags_xml: ").AppendLine(XmlSafe(appearance))
            .AppendLine("default_outfit_tags_xml: ")
            .Append("usage: 在线库结果无默认服装段；必须保留 trigger 与外观 Tag，服装按请求自写；同名多版本已选图片数最高/与你所给作品一致的版本");
        Log($"角色检索(在线 AnimaDex): {name} -> {hit.Name} ({hit.Count} 图)");
        return sb.ToString();
    }

    /// <summary>把角色/作品名转成 AnimaDex 能查的英文：纯 ASCII 直接用；中文查本地索引构建的映射。</summary>
    string? ResolveEnCandidate(string text, Dictionary<string, string>? map)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var ascii = true;
        foreach (var ch in text)
        {
            if (ch >= 128)
            {
                ascii = false;
                break;
            }
        }
        if (ascii)
            return text.Trim();

        if (map == null)
            return null;
        var key = NormalizeCnKey(text);
        if (key.Length == 0)
            return null;
        if (map.TryGetValue(key, out var exact))
            return exact;

        string? best = null;
        var bestLen = int.MaxValue;
        foreach (var kv in map)
        {
            if (kv.Key.Length == 0)
                continue;
            if (kv.Key.Contains(key, StringComparison.Ordinal)
                || key.Contains(kv.Key, StringComparison.Ordinal))
            {
                var overlap = Math.Min(kv.Key.Length, key.Length);
                if (best == null || overlap < bestLen)
                {
                    best = kv.Value;
                    bestLen = overlap;
                }
            }
        }
        return best;
    }

    static string NormalizeCnKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>启动时用本地角色索引构建「中文名/别名 → 英文名」「中文作品 → 英文作品」映射（供 AnimaDex 在线扩充中→英转换）。</summary>
    void BuildAnimadexLookup(CharacterPromptIndex index)
    {
        try
        {
            var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var workMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in index.Entries)
            {
                var enName = BaseEnglishName(entry.EnglishName);
                var enWork = BaseEnglishName(entry.EnglishWork);
                if (enName.Length == 0) enName = entry.EnglishName;
                if (enWork.Length == 0) enWork = entry.EnglishWork;

                AddCnMapping(nameMap, entry.ChineseName, enName);
                foreach (var alias in entry.Aliases) AddCnMapping(nameMap, alias, enName);
                AddCnMapping(workMap, entry.ChineseWork, enWork);
                foreach (var alias in entry.WorkAliases) AddCnMapping(workMap, alias, enWork);
            }
            _animadexCnNameToEn = nameMap.Count > 0 ? nameMap : null;
            _animadexCnWorkToEn = workMap.Count > 0 ? workMap : null;
            Log($"AnimaDex 中→英映射已构建: 角色 {nameMap.Count} 键 / 作品 {workMap.Count} 键");
        }
        catch (Exception ex)
        {
            _animadexCnNameToEn = null;
            _animadexCnWorkToEn = null;
            LogWarn($"AnimaDex 映射构建失败(不影响本地检索): {ex.Message}");
        }
    }

    static void AddCnMapping(Dictionary<string, string> map, string? chinese, string english)
    {
        if (string.IsNullOrWhiteSpace(chinese) || string.IsNullOrWhiteSpace(english))
            return;

        AddKey(chinese);
        var stripped = StripCnQualifier(chinese);
        if (!string.Equals(stripped, chinese, StringComparison.Ordinal))
            AddKey(stripped);

        void AddKey(string value)
        {
            var key = NormalizeCnKey(value);
            if (key.Length > 0 && !map.ContainsKey(key))
                map[key] = english;
        }
    }

    static string StripCnQualifier(string value)
    {
        var ascii = value.IndexOf('(');
        var fullWidth = value.IndexOf('（');
        var index = ascii < 0 ? fullWidth
            : fullWidth < 0 ? ascii
            : Math.Min(ascii, fullWidth);
        return index <= 0 ? value : value[..index].Trim();
    }

    static string BaseEnglishName(string english)
    {
        var without = StripCnQualifier(english);
        if (string.IsNullOrWhiteSpace(without))
            without = english;
        return without.Replace('_', ' ').Trim();
    }

    static string DisplayWork(CharacterPromptEntry entry)
        => !string.IsNullOrWhiteSpace(entry.ChineseWork)
            ? entry.ChineseWork
            : entry.EnglishWork;

    static string XmlSafe(string value)
        => SecurityElement.Escape(value) ?? "";

    // ===================== 提示词预设 =====================

    /// <summary>
    /// 用户数据目录（插件更新不会删除）：Storage/Config/Alife.Plugin.Comfyui/
    /// 与图片目录 Images/Comfyui 同属 Storage 下用户数据，不进 Plugins 包。
    /// </summary>
    public static string GetUserDataDirectory()
    {
        var dir = Path.Combine(
            AlifePath.StorageFolderPath, "Config", "Alife.Plugin.Comfyui");
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
        catch { }
        return dir;
    }

    /// <summary>
    /// 预设文件路径：固定在用户数据目录，插件升级/覆盖安装不会清空。
    /// 首次若仅有旧版「插件目录内」文件，会自动迁移过来。
    /// UI 与 AI 共享同一路径。
    /// </summary>
    public static string GetPresetFilePath()
    {
        var durable = Path.Combine(GetUserDataDirectory(), "prompt-presets.json");

        // 已在用户数据目录：直接用
        if (File.Exists(durable))
            return durable;

        // 旧位置：插件安装目录 / 热编译副本 —— 升级会整夹删除，需迁出
        foreach (var legacy in EnumerateLegacyPresetPaths())
        {
            if (!File.Exists(legacy))
                continue;
            if (TryMigratePresetFile(legacy, durable))
                return durable;
        }

        // 无旧数据：之后所有读写都落在 durable（Save 时建文件）
        return durable;
    }

    static IEnumerable<string> EnumerateLegacyPresetPaths()
    {
        // 1) 原始插件目录（市场安装/更新会整目录替换）
        yield return Path.Combine(
            AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui", "prompt-presets.json");

        // 2) 当前程序集目录（热编译副本）
        string? asmDir = null;
        try
        {
            var loc = typeof(ComfyuiService).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(loc))
                asmDir = Path.GetDirectoryName(loc);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(asmDir))
            yield return Path.Combine(asmDir, "prompt-presets.json");
    }

    static bool TryMigratePresetFile(string legacyPath, string durablePath)
    {
        try
        {
            if (PathsEqual(legacyPath, durablePath))
                return File.Exists(durablePath);

            var dir = Path.GetDirectoryName(durablePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 复制而非移动：旧路径若仍在包内可忽略；用户数据以 durable 为准
            File.Copy(legacyPath, durablePath, overwrite: false);
            Log($"提示词预设已迁移到用户数据目录: {durablePath}");
            return true;
        }
        catch (IOException) when (File.Exists(durablePath))
        {
            // 目标已存在（并发/二次迁移）
            return true;
        }
        catch (Exception ex)
        {
            LogWarn($"提示词预设迁移失败 ({legacyPath} → {durablePath}): {ex.Message}");
            return false;
        }
    }

    List<PromptPresetEntry> LoadPromptPresets()
    {
        var list = new List<PromptPresetEntry>();
        var path = GetPresetFilePath();
        if (!File.Exists(path)) return list;
        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var arr = JsonNode.Parse(json) as JsonArray;
            if (arr == null) return list;
            foreach (var item in arr.OfType<JsonObject>())
            {
                var n = item["n"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? "";
                var c = item["c"]?.GetValue<string>() ?? item["content"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(n))
                    list.Add(new PromptPresetEntry(n, c));
            }
        }
        catch (Exception ex)
        {
            LogWarn($"提示词预设加载失败: {ex.Message}");
        }
        return list;
    }

    void SavePromptPresets(List<PromptPresetEntry> presets)
    {
        // 始终写入用户数据目录，避免写回 Plugins 后被更新清掉
        var path = Path.Combine(GetUserDataDirectory(), "prompt-presets.json");
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var arr = new JsonArray();
            foreach (var p in presets)
            {
                arr.Add(new JsonObject { ["n"] = p.Name, ["c"] = p.Content });
            }
            File.WriteAllText(path, arr.ToJsonString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogWarn($"提示词预设保存失败: {ex.Message}");
        }
    }

    // ===================== 在线 Danbooru 语义标签检索 =====================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("有画面细节时调用以提升出图质量：自然语言描述→标准 Danbooru 英文 tag。三种提示词模式均适用；结果按当前模式组织，禁止中文原句当 prompt。")]
    public async Task SearchDanbooruTags(
        [Description("画面描述（中文或英文均可），写清服装/姿势/场景等关键内容")] string query,
        [Description("模式：full_scene=完整画面（默认）；concept_explore=发散；subject_describe=单物；precise_lookup=近精确/拼写")] string? mode = "full_scene")
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        if (!cfg.EnableDanbooruSearch)
        {
            interactor.Poke("status: unavailable\nreason: 在线标签检索未开启\naction: 请直接按当前提示词模式写英文并 generateimage");
            return;
        }

        try
        {
            Log($"Danbooru 搜索: {(query?.Length > 80 ? query[..80] + "…" : query)} mode={mode}");
            var text = await DanbooruSearchClient.SearchAsync(
                query ?? "",
                mode,
                cfg.DanbooruSearchShowNsfw,
                cfg.DanbooruSearchPrimaryUrl,
                cfg.DanbooruSearchFallbackUrl,
                cfg.DanbooruSearchTimeoutSeconds);
            interactor.Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 搜索异常: {ex.Message}");
            interactor.Poke("status: unavailable\nreason: " + ex.Message
                 + "\naction: 直接按当前模式写英文并 generateimage，勿反复重试检索");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("已有英文 Danbooru tag 时查询共现关联标签，用于补搭配；单次生图最多 1 次。")]
    public async Task GetRelatedDanbooruTags(
        [Description("逗号分隔的英文 Danbooru tag")] string tags)
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        if (!cfg.EnableDanbooruSearch)
        {
            interactor.Poke("status: unavailable\nreason: 在线标签检索未开启\naction: 用已有 tag 直接生图");
            return;
        }

        try
        {
            Log($"Danbooru 关联: {tags}");
            var text = await DanbooruSearchClient.RelatedAsync(
                tags ?? "",
                cfg.DanbooruSearchShowNsfw,
                cfg.DanbooruSearchPrimaryUrl,
                cfg.DanbooruSearchFallbackUrl,
                cfg.DanbooruSearchTimeoutSeconds);
            interactor.Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 关联异常: {ex.Message}");
            interactor.Poke("status: unavailable\nreason: " + ex.Message
                 + "\naction: 用已有英文 tag 直接 generateimage");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("用户明确要求画风/画师时，按已确定英文 tag 推荐画师；未要求不要调用。")]
    public async Task GetDanbooruArtists(
        [Description("逗号分隔的已确定英文 Danbooru tag")] string tags)
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        if (!cfg.EnableDanbooruSearch || !cfg.EnableDanbooruArtistRecommend)
        {
            interactor.Poke("status: unavailable\nreason: 画师推荐未开启\naction: 跳过画师，直接生图");
            return;
        }

        try
        {
            Log($"Danbooru 画师: {tags}");
            var text = await DanbooruSearchClient.ArtistsAsync(
                tags ?? "",
                cfg.DanbooruSearchShowNsfw,
                cfg.DanbooruSearchPrimaryUrl,
                cfg.DanbooruSearchFallbackUrl,
                cfg.DanbooruSearchTimeoutSeconds);
            interactor.Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 画师异常: {ex.Message}");
            interactor.Poke("status: unavailable\nreason: " + ex.Message
                 + "\naction: 跳过画师，直接 generateimage");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("保存提示词预设。可保存角色人设、复杂动作、完整背景等常用提示词片段，下次通过 getpromptpreset 检索复用")]
    public void SavePromptPreset(
        [Description("预设名称（≤60字符）")] string name,
        [Description("预设内容：tag 串或自然语言提示词（≤4000字符）")] string content)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            interactor.Poke("预设名称不能为空");
            return;
        }
        name = name.Trim();
        if (name.Length > 60) name = name[..60];
        if (content == null) content = "";
        if (content.Length > 4000) content = content[..4000];

        var presets = LoadPromptPresets();
        // 同名覆盖
        presets = presets
            .Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        presets.Add(new PromptPresetEntry(name, content));
        SavePromptPresets(presets);

        interactor.Poke($"status: saved\nname: {name}\naction: 下次可用 getpromptpreset name=\"{name}\" 检索复用");
        Log($"保存提示词预设: {name}");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检索提示词预设。不传 name 返回所有预设名称列表；传 name 返回对应完整内容，组合到 generateimage 的 prompt 中")]
    public void GetPromptPreset(
        [Description("预设名称；不传则返回所有预设名称列表")] string? name = null)
    {
        var presets = LoadPromptPresets();
        if (presets.Count == 0)
        {
            interactor.Poke("status: empty\naction: 暂无提示词预设，可调用 savepromptpreset 保存");
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var names = string.Join("\n", presets.Select((p, i) =>
                $"{i + 1}. {p.Name}"));
            interactor.Poke($"status: list\ncount: {presets.Count}\npresets:\n{names}");
            return;
        }

        name = name.Trim();
        var match = presets.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            interactor.Poke($"status: not_found\nname: {name}\naction: 预设不存在，可调用 getpromptpreset 查看列表");
            return;
        }

        interactor.Poke($"status: found\nname: {match.Name}\ncontent:\n{match.Content}");
        Log($"检索提示词预设: {name}");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按需返回当前工作流中允许 AI 调整的安全节点参数。")]
    public async Task GetComfyuiControls(
        [Description("可选工作流名称；不传则查看默认工作流")] string? workflow = null)
    {
        try
        {
            var cfg = Configuration ?? new ComfyuiConfig();
            if (!cfg.EnableNodeControl)
            {
                interactor.Poke("AI 节点控制未开启");
                return;
            }

            var workflowPath = ResolveWorkflowPath(cfg, workflow);
            if (string.IsNullOrWhiteSpace(workflowPath) || !File.Exists(workflowPath))
                throw new FileNotFoundException("工作流文件不存在", workflowPath);

            var raw = await File.ReadAllTextAsync(workflowPath);
            var root = JsonNode.Parse(raw) ?? throw new InvalidDataException("工作流 JSON 为空");
            var prompt = ComfyuiWorkflowConverter.ToApiPrompt(root);
            var label = string.IsNullOrWhiteSpace(workflow) ? "默认工作流" : workflow.Trim();
            interactor.Poke($"workflow: {label}\n{ComfyuiWorkflowConverter.BuildNodeControlDescription(prompt)}");
        }
        catch (Exception ex)
        {
            LogWarn($"读取节点控制信息失败: {ex.Message}");
            interactor.Poke($"读取节点控制信息失败: {ex.Message}");
        }
    }

    async Task CheckStatusAsync()
    {
        try
        {
            var cfg = Configuration ?? new ComfyuiConfig();
            var baseUrl = NormalizeBaseUrl(cfg.BaseUrl);
            using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/system_stats", cfg);
            using var resp = await Http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                interactor.Poke($"ComfyUI 不可用 (HTTP {(int)resp.StatusCode})");
                return;
            }
            var node = JsonNode.Parse(raw);
            var ver = node?["system"]?["comfyui_version"]?.ToString() ?? "?";
            var device = node?["devices"]?[0]?["name"]?.ToString() ?? "未知设备";
            interactor.Poke($"ComfyUI 在线\n版本: {ver}\n设备: {device}");
            Log($"状态正常 {ver} / {device}");
        }
        catch (Exception ex)
        {
            LogError($"状态检查失败: {ex.Message}");
            interactor.Poke($"ComfyUI 连接失败: {ex.Message}");
        }
    }

    async Task RunGenerationSafelyAsync(
        string prompt, string? orientation, string? imagePath,
        int? width, int? height,
        string? workflow, int? steps, double? cfg, string? sampler, string? scheduler,
        string? model, double? denoise, int? batch_size, string? nodeOverrides,
        string? style, string? template, string? templateParams,
        bool priorityMode)
    {
        try
        {
            var cfgNow = Configuration ?? new ComfyuiConfig();
            if (IsAppMcpMode(cfgNow))
            {
                await GenerateAppMcpAsync(
                    prompt, orientation, imagePath, width, height,
                    template, style, templateParams, priorityMode);
            }
            else
            {
                await GenerateImageAsync(
                    prompt, orientation, imagePath, width, height,
                    workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size,
                    nodeOverrides, style, priorityMode);
            }
        }
        catch (Exception ex)
        {
            LogError($"生图任务启动失败: {ex.Message}");
            interactor.Poke($"生图失败: {ex.Message}");
        }
    }

    async Task<bool> AnyWorkflowSupportsImageInputAsync(IEnumerable<string> workflowPaths)
    {
        foreach (var path in workflowPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var raw = await File.ReadAllTextAsync(path);
                var root = JsonNode.Parse(raw);
                if (root == null)
                    continue;
                var prompt = ComfyuiWorkflowConverter.ToApiPrompt(root);
                if (ComfyuiWorkflowConverter.FindLoadImageNodeId(prompt) != null)
                    return true;
            }
            catch (Exception ex)
            {
                LogWarn($"检查工作流图生图能力失败 ({Path.GetFileName(path)}): {ex.Message}");
            }
        }

        return false;
    }

    async Task GenerateImageAsync(string prompt, string? orientation, string? imagePath,
        int? width, int? height,
        string? workflow, int? steps, double? cfg, string? sampler, string? scheduler,
        string? model, double? denoise, int? batch_size, string? nodeOverrides,
        string? style, bool priorityMode = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            interactor.Poke("提示词不能为空");
            return;
        }

        var generationStartedUtc = DateTime.UtcNow;
        var cfgConfig = Configuration ?? new ComfyuiConfig();
        var baseUrl = NormalizeBaseUrl(cfgConfig.BaseUrl);
        var saveDir = ResolveSaveDir(cfgConfig);
        Directory.CreateDirectory(saveDir);

        var (w, h) = ResolveResolution(orientation, width, height, cfgConfig);

        // 画风预设（用户自带）：可覆盖正向前缀，并可向工作流注入节点覆盖（如 ZML LoRA 组）
        var styleEntry = ResolveStyleOrThrow(cfgConfig, style);

        // 正向提示词 = 固定前缀 + AI 提示词，合并后自动去重；分隔符强制英文逗号+空格 ", "
        var effectivePrefix = !string.IsNullOrWhiteSpace(styleEntry?.Prefix)
            ? styleEntry!.Prefix
            : ResolvePositivePromptPrefix(cfgConfig, workflow);
        var finalPositive = BuildPositivePrompt(prompt, effectivePrefix);
        // 固定负面：同样规范为英文逗号分隔（若配置了才覆盖工作流负面）
        var finalNegative = string.IsNullOrWhiteSpace(cfgConfig.NegativePrompt)
            ? cfgConfig.NegativePrompt
            : DeduplicateAndJoinPromptSegments(new[] { cfgConfig.NegativePrompt });

        // 图生图：临时图片文件路径（用完清理）
        string? tempImageFile = null;

        // 优先生图：硬超时 = min(配置超时, PriorityMaxWaitSeconds)，防止 Comfy 卡死拖死桌宠
        int pollTimeoutSec = Math.Clamp(cfgConfig.TimeoutSeconds, 30, 1800);
        if (priorityMode)
        {
            int hardCap = Math.Clamp(cfgConfig.PriorityMaxWaitSeconds, 30, 1800);
            pollTimeoutSec = Math.Min(pollTimeoutSec, hardCap);
        }

        Interlocked.Increment(ref _activeGens);
        // 生图即视为模型已加载；同时刷新空闲计时起点
        Volatile.Write(ref _modelsLoadedFlag, 1);
        Volatile.Write(ref _lastActivityUtc, DateTime.UtcNow.Ticks);
        using var hardCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(pollTimeoutSec + 30)); // 比轮询多 30s 余量（上传/下载）
        var ct = hardCts.Token;

        try
        {
            if (priorityMode)
                interactor.Poke($"优先生图进行中（最长约 {pollTimeoutSec} 秒），请稍候，期间不要语音…");
            Log($"{(priorityMode ? "[优先] " : "")}开始生图: {Truncate(finalPositive, 80)}");
            if (w.HasValue || h.HasValue)
                Log($"分辨率: {w ?? 0}x{h ?? 0}");

            // 1) 图生图：下载 + 上传输入图片
            string? uploadedImageName = null;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                Log($"图生图模式，输入图片: {Truncate(imagePath, 80)}");

                // 1a) 如果是 URL → 下载到临时文件
                if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    tempImageFile = await DownloadInputImageAsync(imagePath, ct);
                    Log($"图片下载完成 -> {Path.GetFileName(tempImageFile)}");
                }
                else if (File.Exists(imagePath))
                {
                    tempImageFile = imagePath;
                }
                else
                {
                    interactor.Poke($"输入图片不存在: {imagePath}");
                    return;
                }

                // 1b) 上传到 ComfyUI
                ct.ThrowIfCancellationRequested();
                uploadedImageName = await UploadImageToComfyUIAsync(baseUrl, tempImageFile, cfgConfig, ct);
                Log($"图片已上传到 ComfyUI: {uploadedImageName}");
            }

            // 2) 选择工作流
            var workflowPath = ResolveWorkflowPath(cfgConfig, workflow);
            if (string.IsNullOrWhiteSpace(workflowPath))
            {
                interactor.Poke("请先在配置中填写工作流 JSON 路径");
                LogError("工作流路径为空，请在配置 UI 中设置 WorkflowPath");
                return;
            }
            if (!File.Exists(workflowPath))
            {
                interactor.Poke($"工作流文件不存在: {workflowPath}");
                LogError($"工作流不存在: {workflowPath}");
                return;
            }

            // UI 中手动指定的节点 ID 属于默认工作流；其他工作流必须按自身结构重新识别。
            var defaultWorkflowPath = ResolveWorkflowPath(cfgConfig);
            var useConfiguredNodeIds = PathsEqual(workflowPath, defaultWorkflowPath);
            if (!useConfiguredNodeIds)
                Log("非默认工作流：自动识别提示词、分辨率和图片输入节点");

            if (!string.IsNullOrWhiteSpace(workflow))
                Log($"切换工作流: {workflow} -> {Path.GetFileName(workflowPath)}");

            var rawJson = await File.ReadAllTextAsync(workflowPath, ct);
            var root = JsonNode.Parse(rawJson)
                ?? throw new Exception("工作流 JSON 解析失败");

            // 3) UI 工作流才需要 object_info 辅助转换；API 格式直接使用。
            JsonObject? objectInfo = null;
            if (!ComfyuiWorkflowConverter.IsApiFormat(root))
            {
                try
                {
                    objectInfo = await FetchObjectInfoAsync(baseUrl, cfgConfig, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogWarn($"获取 object_info 失败，将用本地规则转换: {ex.Message}");
                }
            }

            // 4) 转 API（有 object_info 时做 required/combo 轻量校验，错误更可读）
            var apiPrompt = ComfyuiWorkflowConverter.ToApiPrompt(
                root, objectInfo, validate: objectInfo != null);
            Log($"工作流节点数: {apiPrompt.Count} ({(ComfyuiWorkflowConverter.IsApiFormat(root) ? "API格式" : "UI转API")})");

            // 5) 注入基本参数（原有简单模式不变）
            ComfyuiWorkflowConverter.ApplyRuntimeOverrides(
                apiPrompt,
                finalPositive,
                finalNegative,
                w,
                h,
                cfgConfig.RandomizeSeed,
                useConfiguredNodeIds && !string.IsNullOrWhiteSpace(cfgConfig.PositivePromptNodeId) ? cfgConfig.PositivePromptNodeId : null,
                string.IsNullOrWhiteSpace(cfgConfig.PositivePromptInput) ? "positive" : cfgConfig.PositivePromptInput,
                useConfiguredNodeIds && !string.IsNullOrWhiteSpace(cfgConfig.NegativePromptNodeId) ? cfgConfig.NegativePromptNodeId : null,
                string.IsNullOrWhiteSpace(cfgConfig.NegativePromptInput) ? "positive" : cfgConfig.NegativePromptInput,
                useConfiguredNodeIds && !string.IsNullOrWhiteSpace(cfgConfig.ResolutionNodeId) ? cfgConfig.ResolutionNodeId : null,
                isImg2Img: !string.IsNullOrWhiteSpace(uploadedImageName));

            // 5.1) 画风预设（无需开启 AI 节点控制）
            if (styleEntry != null)
            {
                // a) ZML LoRA 组：只开该组 = 一种画风（数据从当前工作流自身读取，配置里只存组名）
                if (!string.IsNullOrWhiteSpace(styleEntry.Group))
                {
                    var touched = ComfyuiWorkflowConverter.ApplyZmlLoraGroup(apiPrompt, styleEntry.Group);
                    if (touched == 0)
                        LogWarn($"画风预设「{styleEntry.Name}」指定了 ZML 组「{styleEntry.Group}」，"
                                + "但当前工作流没有 lora_loader_data 字段（已跳过）");
                    else
                        Log($"应用画风预设: {styleEntry.Name} → ZML 组「{styleEntry.Group}」（{touched} 个加载器）");
                }
                // b) 原始节点覆盖（用户自带数据，允许长字符串，如 ZML 的 lora_loader_data 整段 JSON）
                if (!string.IsNullOrWhiteSpace(styleEntry.Params))
                {
                    Log($"应用画风预设节点覆盖: {styleEntry.Name}");
                    ComfyuiWorkflowConverter.ApplyUserNodeOverrides(apiPrompt, styleEntry.Params);
                }
            }

            // denoise 始终可用；其余高级参数必须由配置开关明确授权。
            var advancedOverridesRequested = model != null || steps.HasValue || cfg.HasValue
                || sampler != null || scheduler != null || batch_size.HasValue
                || !string.IsNullOrWhiteSpace(nodeOverrides);
            if (advancedOverridesRequested && !cfgConfig.EnableNodeControl)
                LogWarn("AI 节点控制未开启，已忽略高级节点参数");

            if (denoise.HasValue || advancedOverridesRequested && cfgConfig.EnableNodeControl)
            {
                Log("注入节点参数...");
                ComfyuiWorkflowConverter.ApplyNodeOverrides(
                    apiPrompt,
                    cfgConfig.EnableNodeControl ? model : null,
                    cfgConfig.EnableNodeControl ? steps : null,
                    cfgConfig.EnableNodeControl ? cfg : null,
                    cfgConfig.EnableNodeControl ? sampler : null,
                    cfgConfig.EnableNodeControl ? scheduler : null,
                    denoise,
                    cfgConfig.EnableNodeControl ? batch_size : null,
                    cfgConfig.EnableNodeControl ? nodeOverrides : null);
            }

            // 在提交前确定自定义输出目录；后续只认领本任务开始后新写入的文件。
            var customDirs = TryFindCustomSavePaths(apiPrompt);

            // 5.6) 图生图：注入已上传的图片文件名到 LoadImage 节点
            if (!string.IsNullOrWhiteSpace(uploadedImageName))
            {
                var loadImageNodeId = useConfiguredNodeIds
                    && !string.IsNullOrWhiteSpace(cfgConfig.LoadImageNodeId)
                        ? cfgConfig.LoadImageNodeId : null;
                var loadImageInput = string.IsNullOrWhiteSpace(cfgConfig.LoadImageInput)
                    ? "image" : cfgConfig.LoadImageInput;

                var injectedNodeId = ComfyuiWorkflowConverter.InjectLoadImage(
                    apiPrompt, loadImageNodeId, loadImageInput, uploadedImageName);
                if (string.IsNullOrWhiteSpace(injectedNodeId))
                    throw new Exception("当前工作流未找到可用的 LoadImage 节点，无法执行图生图");
                Log($"注入图片 {uploadedImageName} 到 LoadImage 节点 #{injectedNodeId}");
            }

            // 6) 提交
            ct.ThrowIfCancellationRequested();
            var clientId = Guid.NewGuid().ToString("N");
            var promptId = await QueuePromptAsync(
                baseUrl, cfgConfig, apiPrompt, clientId, ct);
            Log($"已提交 prompt_id={promptId}");

            // 7) 轮询 history（优先生图用硬超时上限）
            var timeout = TimeSpan.FromSeconds(pollTimeoutSec);
            var interval = Math.Clamp(cfgConfig.PollIntervalMs, 500, 10000);
            var history = await WaitHistoryAsync(baseUrl, cfgConfig, promptId, timeout, interval, ct);

            // 8) 取图（只取 SaveImage 类节点，过滤预览节点）
            var images = ExtractImages(history, promptId, apiPrompt);
            if (images.Count == 0)
            {
                // 可能保存到自定义路径（ZML_SaveImageV2），history 无 images
                if (customDirs.Count > 0)
                {
                    var output = TryClaimCustomOutput(customDirs, generationStartedUtc);
                    if (output != null)
                    {
                        // ExtraSaveCopy 关闭时直接用工作流保存的路径，不额外复制
                        if (!cfgConfig.ExtraSaveCopy)
                        {
                            Log($"使用工作流保存路径: {output.FullName}");
                            interactor.Poke($"图片已生成\n{output.FullName}");
                            return;
                        }
                        var ext = string.IsNullOrWhiteSpace(output.Extension) ? ".png" : output.Extension;
                        var dest = Path.Combine(
                            saveDir,
                            $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}");
                        File.Copy(output.FullName, dest, false);
                        Log($"从自定义目录复制: {output.FullName} -> {dest}");
                        interactor.Poke($"图片已生成\n{dest}");
                        return;
                    }
                }

                LogError("history 中未找到图片输出");
                interactor.Poke("生图完成但未找到输出图片，请检查工作流是否包含 SaveImage 节点，或查看 ComfyUI 控制台错误");
                return;
            }

            // ZML_SaveImageV2 等自定义保存节点会把文件写到「保存路径」指定的绝对目录，
            // 不进 ComfyUI output；但 history.outputs 仍会上报 filename，导致 /view 404。
            var saved = new List<string>();
            foreach (var img in images)
            {
                // ExtraSaveCopy 关闭 + 有自定义保存目录：直接用工作流保存的文件路径
                if (!cfgConfig.ExtraSaveCopy && customDirs.Count > 0)
                {
                    var localFile = TryClaimCustomOutput(
                        customDirs, generationStartedUtc, img.Filename);
                    if (localFile != null)
                    {
                        saved.Add(localFile.FullName);
                        Log($"使用工作流保存路径: {localFile.FullName}");
                        continue;
                    }
                }

                // 标准 SaveImage：如果配置了 ComfyUI output 目录，直接引用该目录下的文件
                var comfyuiOutputDir = cfgConfig.ComfyuiOutputPath?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(comfyuiOutputDir))
                {
                    var directPath = string.IsNullOrWhiteSpace(img.Subfolder)
                        ? Path.Combine(comfyuiOutputDir, img.Filename)
                        : Path.Combine(comfyuiOutputDir, img.Subfolder, img.Filename);
                    if (File.Exists(directPath))
                    {
                        // ExtraSaveCopy 关闭：直接用 ComfyUI output 路径，不下载不复制
                        if (!cfgConfig.ExtraSaveCopy)
                        {
                            saved.Add(directPath);
                            Log($"直接引用 ComfyUI output: {directPath}");
                            continue;
                        }
                        // ExtraSaveCopy 开启：复制到 SaveDirectory（如果路径不同）
                        if (!directPath.StartsWith(saveDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var ext0 = Path.GetExtension(img.Filename);
                            if (string.IsNullOrWhiteSpace(ext0)) ext0 = ".png";
                            var dest0 = Path.Combine(saveDir,
                                $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext0}");
                            File.Copy(directPath, dest0, false);
                            saved.Add(dest0);
                            Log($"从 ComfyUI output 复制: {directPath} -> {dest0}");
                            continue;
                        }
                        // SaveDirectory 就是 ComfyUI output 目录，直接用
                        saved.Add(directPath);
                        Log($"SaveDirectory 即 ComfyUI output，直接引用: {directPath}");
                        continue;
                    }
                }

                byte[]? bytes = null;
                try
                {
                    bytes = await ViewImageAsync(
                        baseUrl, cfgConfig, img.Filename, img.Subfolder, img.Type, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogWarn($"View 异常 {img.Filename}: {ex.Message}");
                }

                // /view 失败 → 回退到 ZML 自定义保存目录按 filename 找
                if ((bytes == null || bytes.Length == 0)
                    && customDirs.Count > 0)
                {
                    var localFile = TryClaimCustomOutput(
                        customDirs, generationStartedUtc, img.Filename);
                    if (localFile != null)
                    {
                        try
                        {
                            bytes = await File.ReadAllBytesAsync(localFile.FullName, ct);
                            Log($"从自定义目录读取: {localFile.FullName}");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"读取本地文件失败 {localFile.FullName}: {ex.Message}");
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0) continue;

                // ExtraSaveCopy 关闭时，标准 SaveImage 的图片仍需下载（ComfyUI output 目录无法直接引用）
                // 但如果有 customDirs 且 /view 成功，说明图片在 ComfyUI output，仍需保存到 saveDir
                var ext2 = Path.GetExtension(img.Filename);
                if (string.IsNullOrWhiteSpace(ext2)) ext2 = ".png";
                var fileName = $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext2}";
                var path = Path.Combine(saveDir, fileName);
                await File.WriteAllBytesAsync(path, bytes, ct);
                saved.Add(path);
                Log($"保存 ({bytes.Length / 1024.0:F0}KB) -> {fileName}");
            }

            if (saved.Count == 0)
            {
                interactor.Poke("图片下载失败");
                return;
            }

            // 桌面端自动打开图片
            if (cfgConfig.AutoOpenImage)
            {
                foreach (var path in saved)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        Log($"已打开图片: {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"打开图片失败 {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }

            interactor.Poke($"图片已生成（{saved.Count} 张）\n{string.Join("\n", saved)}");
        }
        catch (OperationCanceledException)
        {
            // 含 TaskCanceledException：硬超时 / 轮询超时，必须明确结束，避免桌宠一直等
            LogWarn($"生图超时（{pollTimeoutSec}s{(priorityMode ? "，优先生图硬上限" : "")}）");
            interactor.Poke(priorityMode
                ? $"优先生图超时（{pollTimeoutSec} 秒），已结束等待。可稍后重试或检查 ComfyUI；现在可以正常说话。"
                : "生图超时，请检查 ComfyUI 是否在跑图，或增大 TimeoutSeconds。若同机开了语音优先，进程也可能被 TTS 结束，请重启 ComfyUI");
        }
        catch (HttpRequestException ex)
        {
            LogError($"网络错误: {ex.Message}");
            interactor.Poke($"无法连接 ComfyUI: {ex.Message}\n请确认地址 {cfgConfig.BaseUrl} 可访问；若同机语音优先曾腾 GPU，请重启 ComfyUI");
        }
        catch (Exception ex)
        {
            LogError($"生图失败: {ex.Message}");
            interactor.Poke($"生图失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeGens);
            // 生图结束即视为最近一次活动，空闲计时从此刻重新累计
            Volatile.Write(ref _lastActivityUtc, DateTime.UtcNow.Ticks);
            // 清理图生图下载的临时文件
            if (tempImageFile != null && tempImageFile != imagePath && File.Exists(tempImageFile))
            {
                try { File.Delete(tempImageFile); } catch { }
            }
        }
    }

    // ===================== APP-MCP 模板模式 =====================

    static bool IsAppMcpMode(ComfyuiConfig cfg)
        => string.Equals(cfg.BackendMode?.Trim(), "appmcp", StringComparison.OrdinalIgnoreCase);

    static string ResolveAppMcpApiBase(ComfyuiConfig cfg)
        => AppMcpClient.ResolveApiBase(cfg.AppMcpUrl, NormalizeBaseUrl(cfg.BaseUrl));

    /// <summary>解析「画风预设」列表（JSON 数组，字段间容错）。</summary>
    static List<StyleEntry> ParseStylePresets(string? json)
    {
        var list = new List<StyleEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return list;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr)
                return list;

            foreach (var item in arr.OfType<JsonObject>())
            {
                var name = WfJsonString(item, "n") ?? WfJsonString(item, "name") ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var template = WfJsonString(item, "t") ?? WfJsonString(item, "template") ?? "";
                var prefix = WfJsonString(item, "f") ?? WfJsonString(item, "prefix") ?? "";
                var group = WfJsonString(item, "g") ?? WfJsonString(item, "group") ?? "";

                var paramsJson = "";
                var pNode = item["p"] ?? item["params"];
                if (pNode is JsonObject po)
                    paramsJson = po.ToJsonString();
                else if (pNode is JsonValue pv && pv.GetValueKind() == JsonValueKind.String)
                    paramsJson = pv.GetValue<string>() ?? "";

                list.Add(new StyleEntry(name.Trim(), template.Trim(), prefix, paramsJson, group.Trim()));
            }
        }
        catch (Exception ex)
        {
            LogWarn($"画风预设解析失败（已忽略）: {ex.Message}");
        }
        return list;
    }

    /// <summary>按名取出画风预设；不存在时抛出可读错误（列出可用项）。</summary>
    static StyleEntry? ResolveStyleOrThrow(ComfyuiConfig cfg, string? styleName)
    {
        if (string.IsNullOrWhiteSpace(styleName))
            return null;

        var styles = ParseStylePresets(cfg.StylePresets);
        var found = styles.FirstOrDefault(s =>
            s.Name.Equals(styleName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found != null)
            return found;

        var available = string.Join("、", styles.Select(s => s.Name));
        throw new Exception(string.IsNullOrWhiteSpace(available)
            ? $"未找到画风预设 \"{styleName}\"（当前未配置画风预设）"
            : $"未找到画风预设 \"{styleName}\"；可用：{available}");
    }

    /// <summary>把 JSON 对象字符串合并进目标对象（同名覆盖）。非法 JSON 直接抛错，避免静默出错图。</summary>
    static void MergeJsonObject(JsonObject target, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject
                  ?? throw new FormatException("必须是 JSON 对象");
        }
        catch (Exception ex)
        {
            throw new Exception($"参数 JSON 无效: {ex.Message}");
        }

        foreach (var kv in obj)
            target[kv.Key] = kv.Value?.DeepClone();
    }

    /// <summary>决定提示词写入哪个模板输入：配置项优先，否则第一个 string/combo 输入。</summary>
    static string ResolveTemplatePromptParam(ComfyuiConfig cfg, AppMcpClient.AppMcpTemplate tpl)
    {
        var configured = (cfg.AppMcpPromptParam ?? "").Trim();
        if (configured.Length > 0 && tpl.HasInput(configured))
            return configured;

        foreach (var kv in tpl.Inputs)
        {
            var type = kv.Value.Type.ToUpperInvariant();
            if (type is "STRING" or "COMBO")
                return kv.Key;
        }
        return tpl.Inputs.Keys.FirstOrDefault() ?? "";
    }

    /// <summary>找到模板里最可能的图片输入（IMAGE 类型优先，其次名字含 image/图片）。</summary>
    static string? FindTemplateImageParam(AppMcpClient.AppMcpTemplate tpl)
    {
        foreach (var kv in tpl.Inputs)
        {
            var type = kv.Value.Type.ToUpperInvariant();
            if (type is "IMAGE" or "LOADIMAGE")
                return kv.Key;
        }
        foreach (var kv in tpl.Inputs)
        {
            if (kv.Key.Contains("image", StringComparison.OrdinalIgnoreCase)
                || kv.Key.Contains("图片", StringComparison.Ordinal)
                || kv.Key.Contains("图像", StringComparison.Ordinal))
                return kv.Key;
        }
        return null;
    }

    /// <summary>模板是否声明了尺寸类输入（width/height/宽/高/尺寸/分辨率等）。</summary>
    static bool HasTemplateSizeInput(AppMcpClient.AppMcpTemplate tpl)
    {
        foreach (var kv in tpl.Inputs)
        {
            var k = kv.Key;
            if (k.Contains("width", StringComparison.OrdinalIgnoreCase)
                || k.Contains("height", StringComparison.OrdinalIgnoreCase)
                || k.Contains("宽", StringComparison.Ordinal)
                || k.Contains("高", StringComparison.Ordinal)
                || k.Contains("尺寸", StringComparison.Ordinal)
                || k.Contains("分辨率", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>找到模板里代表 ZML 加载器数据的输入（控件名为 lora_loader_data）。</summary>
    static string? FindTemplateZmlDataInput(AppMcpClient.AppMcpTemplate tpl)
    {
        foreach (var kv in tpl.Inputs)
        {
            if (kv.Value.Widget.Equals("lora_loader_data", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        foreach (var kv in tpl.Inputs)
        {
            if (kv.Key.Equals("lora_loader_data", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    /// <summary>从模板自身携带的 workflow 里读出 ZML 的 lora_loader_data 原始字符串。</summary>
    static string? TryReadTemplateZmlData(AppMcpClient.AppMcpTemplate tpl)
        => AppMcpClient.ExtractZmlData(tpl.RawJson);

    /// <summary>模板声明了宽/高输入时注入分辨率；否则由模板自身决定。</summary>
    void ApplyTemplateResolution(
        JsonObject parameters, AppMcpClient.AppMcpTemplate tpl,
        string? orientation, int? width, int? height, ComfyuiConfig cfg)
    {
        var (w, h) = ResolveResolution(orientation, width, height, cfg);
        if (w.HasValue)
        {
            if (tpl.HasInput("width")) parameters["width"] = w.Value;
            if (tpl.HasInput("宽")) parameters["宽"] = w.Value;
        }
        if (h.HasValue)
        {
            if (tpl.HasInput("height")) parameters["height"] = h.Value;
            if (tpl.HasInput("高")) parameters["高"] = h.Value;
        }
    }

    /// <summary>组装模板输入：配置默认 → 画风预设 → AI 显式 → 提示词。只保留模板已声明的输入。</summary>
    JsonObject BuildTemplateParams(
        ComfyuiConfig cfg, AppMcpClient.AppMcpTemplate tpl,
        string prompt, StyleEntry? style, string? extraParams)
    {
        var raw = new JsonObject();
        MergeJsonObject(raw, cfg.AppMcpDefaultParams);
        if (style != null && !string.IsNullOrWhiteSpace(style.Params))
            MergeJsonObject(raw, style.Params);

        // 画风预设：ZML LoRA 组 → 只开该组。数据从模板自身的工作流里取，配置里只存组名。
        if (style != null && !string.IsNullOrWhiteSpace(style.Group))
        {
            var dataInput = FindTemplateZmlDataInput(tpl)
                ?? throw new Exception(
                    $"画风预设「{style.Name}」用 ZML 组切换，但模板未暴露 ZML 加载器的数据输入（lora_loader_data）。"
                    + "请在 APP-MCP 的 App Builder 中把该节点的数据内容输入框标记为输入并刷新模板；"
                    + "或改用画风预设的 p（整段 lora_loader_data）方式。");

            var baseData = TryReadTemplateZmlData(tpl)
                ?? throw new Exception(
                    $"画风预设「{style.Name}」用 ZML 组切换，但读不到模板工作流里的 lora_loader_data，无法改组开关。");

            raw[dataInput] = ComfyuiWorkflowConverter.EnableZmlLoraGroup(baseData, style.Group);
            Log($"APP-MCP 画风预设: {style.Name} → ZML 组「{style.Group}」（写入模板输入 {dataInput}）");
        }

        MergeJsonObject(raw, extraParams);

        var filtered = new JsonObject();
        var dropped = new List<string>();
        foreach (var kv in raw)
        {
            if (tpl.HasInput(kv.Key))
                filtered[kv.Key] = kv.Value?.DeepClone();
            else
                dropped.Add(kv.Key);
        }
        if (dropped.Count > 0)
        {
            LogWarn($"模板未声明这些输入，已忽略: {string.Join("、", dropped)}（如需生效，"
                    + "请在 APP-MCP App Builder 中把对应控件标记为输入，或改用 getcomfyuistyle 查看可填字段）");
        }

        var promptParam = ResolveTemplatePromptParam(cfg, tpl);
        if (string.IsNullOrWhiteSpace(promptParam))
            throw new Exception(
                $"模板 \"{tpl.Name}\" 未声明任何输入，无法注入提示词；请先在 APP-MCP 的 App Builder 中把提示词节点标记为输入并命名");

        // 前缀：画风预设自带的 f 优先；否则按开关决定是否叠加插件「固定正向提示词前缀」
        // （模板若已内置同类前缀，可在配置里关闭 AppMcpUseGlobalPrefix 避免重复）
        var stylePrefix = style?.Prefix;
        var effectivePrefix = !string.IsNullOrWhiteSpace(stylePrefix)
            ? stylePrefix
            : (cfg.AppMcpUseGlobalPrefix ? cfg.PositivePromptPrefix : null);
        filtered[promptParam] = BuildPositivePrompt(prompt, effectivePrefix);

        if (!string.IsNullOrWhiteSpace(cfg.NegativePrompt))
        {
            var negParam = tpl.HasInput("negative") ? "negative"
                : tpl.HasInput("负面提示词") ? "负面提示词" : null;
            if (negParam != null)
                filtered[negParam] = DeduplicateAndJoinPromptSegments(new[] { cfg.NegativePrompt });
        }

        return filtered;
    }

    /// <summary>下载模板返回的图片地址到本地保存目录（带图片魔数校验与大小限制）。</summary>
    async Task<List<string>> DownloadTemplateImagesAsync(
        IEnumerable<string> urls, string saveDir, CancellationToken ct)
    {
        var saved = new List<string>();
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _dlHttp.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    LogWarn($"[APP-MCP] 下载图片失败 HTTP {(int)resp.StatusCode}: {Truncate(url, 120)}");
                    continue;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0)
                    continue;
                if (bytes.Length > MaxInputImageBytes)
                {
                    LogWarn($"[APP-MCP] 图片过大，已跳过 ({bytes.Length / 1024 / 1024}MB)");
                    continue;
                }

                string ext;
                try
                {
                    ext = DetectImageExtension(bytes);
                }
                catch (Exception ex)
                {
                    LogWarn($"[APP-MCP] 非图片内容，已跳过: {ex.Message}");
                    continue;
                }

                var path = Path.Combine(
                    saveDir, $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}");
                await File.WriteAllBytesAsync(path, bytes, ct);
                saved.Add(path);
                Log($"[APP-MCP] 保存 ({bytes.Length / 1024.0:F0}KB) -> {Path.GetFileName(path)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarn($"[APP-MCP] 下载图片异常: {ex.Message}");
            }
        }
        return saved;
    }

    /// <summary>
    /// APP-MCP 模板模式生图：选模板 → 组装模板输入 → 执行 → 下载图片。
    /// 与工作流模式互不影响（由 BackendMode 分流）。
    /// </summary>
    async Task GenerateAppMcpAsync(
        string prompt, string? orientation, string? imagePath,
        int? width, int? height, string? templateName, string? styleName,
        string? templateParams, bool priorityMode)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            interactor.Poke("提示词不能为空");
            return;
        }

        var cfg = Configuration ?? new ComfyuiConfig();
        var apiBase = ResolveAppMcpApiBase(cfg);
        var name = !string.IsNullOrWhiteSpace(templateName)
            ? templateName!.Trim()
            : (cfg.AppMcpTemplate ?? "").Trim();
        if (name.Length == 0)
        {
            var hint = _appMcpTemplates.Count > 0
                ? string.Join("、", _appMcpTemplates.Select(t => t.Name))
                : "（未读取到模板）";
            interactor.Poke($"APP-MCP 模板模式：请指定模板（配置里的默认模板，或 generateimage 传 template=模板名）。当前可用：{hint}");
            return;
        }

        var saveDir = ResolveSaveDir(cfg);
        Directory.CreateDirectory(saveDir);

        var waitSeconds = Math.Clamp(cfg.AppMcpTimeoutSeconds, 30, 1800);
        if (priorityMode)
            waitSeconds = Math.Min(waitSeconds, Math.Clamp(cfg.PriorityMaxWaitSeconds, 30, 1800));

        Interlocked.Increment(ref _activeGens);
        Volatile.Write(ref _modelsLoadedFlag, 1);
        Volatile.Write(ref _lastActivityUtc, DateTime.UtcNow.Ticks);
        using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds + 60));
        var ct = hardCts.Token;

        try
        {
            if (priorityMode)
                interactor.Poke($"优先生图进行中（最长约 {waitSeconds} 秒），请稍候，期间不要语音…");

            var style = ResolveStyleOrThrow(cfg, styleName);
            if (!string.IsNullOrWhiteSpace(style?.Template))
                name = style!.Template.Trim();

            Log($"{(priorityMode ? "[优先] " : "")}[APP-MCP] 模板生图: template={name} api={apiBase}");
            var tpl = await AppMcpClient.GetTemplateAsync(apiBase, name, cfg.ApiToken, ct)
                      ?? throw new Exception($"APP-MCP 模板不存在或已禁用: {name}");

            var parameters = BuildTemplateParams(cfg, tpl, prompt, style, templateParams);
            ApplyTemplateResolution(parameters, tpl, orientation, width, height, cfg);

            // 图生图：模板声明了图片输入才上传
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var imgParam = FindTemplateImageParam(tpl);
                if (imgParam == null)
                {
                    LogWarn("[APP-MCP] 模板未声明图片输入，已忽略 imagePath（按文生图执行）");
                }
                else
                {
                    var localPath = imagePath!;
                    if (localPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || localPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || localPath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        localPath = await DownloadInputImageAsync(localPath, ct);

                    if (!File.Exists(localPath))
                        throw new Exception($"输入图片不存在: {imagePath}");

                    var uploaded = await UploadImageToComfyUIAsync(
                        NormalizeBaseUrl(cfg.BaseUrl), localPath, cfg, ct);
                    parameters[imgParam] = uploaded;
                    Log($"[APP-MCP] 已上传图片并注入模板输入 {imgParam}: {uploaded}");
                }
            }

            var result = await AppMcpClient.ExecuteTemplateAsync(
                apiBase, name, parameters, cfg.ApiToken, waitSeconds, ct);

            if (!result.Success)
            {
                LogError($"[APP-MCP] 失败: {result.Error}");
                interactor.Poke($"生图失败: {result.Error}");
                return;
            }

            if (result.ImageUrls.Count == 0)
            {
                var textNote = result.Texts.Count > 0
                    ? $"\n文本输出: {Truncate(result.Texts[0], 200)}" : "";
                interactor.Poke($"生图完成但未找到输出图片，请检查模板的「输出」是否包含保存图片节点{textNote}");
                return;
            }

            var saved = await DownloadTemplateImagesAsync(result.ImageUrls, saveDir, ct);
            if (saved.Count == 0)
            {
                interactor.Poke("图片下载失败（模板返回了图片地址，但未能下载）");
                return;
            }

            if (cfg.AutoOpenImage)
            {
                foreach (var path in saved)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        Log($"已打开图片: {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"打开图片失败 {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }

            interactor.Poke($"图片已生成（{saved.Count} 张）\n{string.Join("\n", saved)}");
        }
        catch (OperationCanceledException)
        {
            LogWarn($"[APP-MCP] 生图超时（{waitSeconds}s）");
            interactor.Poke(priorityMode
                ? $"优先生图超时（{waitSeconds} 秒），已结束等待。可稍后重试或检查 ComfyUI；现在可以正常说话。"
                : "生图超时，请检查 ComfyUI 是否在跑图，或增大 APP-MCP 超时秒数");
        }
        catch (Exception ex)
        {
            LogError($"[APP-MCP] 生图失败: {ex.Message}");
            interactor.Poke($"生图失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeGens);
            Volatile.Write(ref _lastActivityUtc, DateTime.UtcNow.Ticks);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看可用的画风预设，以及 APP-MCP 模板模式下的模板输入明细；用于切换画风或填写模板参数")]
    public async Task GetComfyuiStyle(
        [Description("可选：画风预设名；传则返回该预设的完整参数，不传则列出所有画风预设")] string? name = null)
    {
        var cfg = Configuration ?? new ComfyuiConfig();
        var styles = ParseStylePresets(cfg.StylePresets);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var style = styles.FirstOrDefault(s =>
                s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (style == null)
            {
                interactor.Poke($"status: not_found\nname: {name}\n" +
                    $"action: 画风预设不存在；可用: {string.Join("、", styles.Select(s => s.Name))}");
                return;
            }

            var sb = new StringBuilder()
                .AppendLine("status: found")
                .Append("style: ").AppendLine(style.Name);
            if (!string.IsNullOrWhiteSpace(style.Template))
                sb.Append("template: ").AppendLine(style.Template);
            if (!string.IsNullOrWhiteSpace(style.Prefix))
                sb.Append("prefix: ").AppendLine(style.Prefix);
            sb.Append("params: ").AppendLine(string.IsNullOrWhiteSpace(style.Params) ? "(空)" : style.Params);
            sb.Append("usage: generateimage 传 style=\"").Append(style.Name).AppendLine("\" 即可套用");
            interactor.Poke(sb.ToString());
            return;
        }

        var output = new StringBuilder();
        output.AppendLine("status: list");
        output.Append("画风预设: ").AppendLine(styles.Count > 0
            ? string.Join("、", styles.Select(s => s.Name))
            : "(未配置)");

        if (IsAppMcpMode(cfg))
        {
            var apiBase = ResolveAppMcpApiBase(cfg);
            var templateName = (cfg.AppMcpTemplate ?? "").Trim();
            output.Append("默认模板: ").AppendLine(templateName.Length > 0 ? templateName : "(未设置)");
            if (templateName.Length > 0)
            {
                try
                {
                    var tpl = await AppMcpClient.GetTemplateAsync(apiBase, templateName, cfg.ApiToken);
                    if (tpl != null)
                    {
                        output.Append("模板输入: ").AppendLine(tpl.Inputs.Count == 0
                            ? "(无)"
                            : string.Join("、", tpl.Inputs.Select(kv =>
                                $"{kv.Key}({kv.Value.Type.ToLowerInvariant()}" +
                                (string.IsNullOrWhiteSpace(kv.Value.DefaultJson)
                                    ? ")" : $"，默认 {Truncate(kv.Value.DefaultJson, 60)})"))));

                        var groups = ComfyuiWorkflowConverter.ListZmlLoraGroups(
                            TryReadTemplateZmlData(tpl));
                        if (groups.Count > 0)
                            output.Append("ZML LoRA 组（可用画风）: ").AppendLine(string.Join("、", groups));
                    }
                }
                catch (Exception ex)
                {
                    output.Append("模板输入读取失败: ").AppendLine(ex.Message);
                }
            }
        }
        else
        {
            // 工作流模式：顺便列出当前工作流的 ZML LoRA 组，方便配置画风预设
            try
            {
                var path = ResolveWorkflowPath(cfg);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var raw = await File.ReadAllTextAsync(path);
                    var root = JsonNode.Parse(raw);
                    if (root != null)
                    {
                        var groups = ComfyuiWorkflowConverter.ListZmlLoraGroups(
                            ComfyuiWorkflowConverter.ToApiPrompt(root));
                        output.Append("当前工作流 ZML LoRA 组（可用画风）: ").AppendLine(
                            groups.Count > 0 ? string.Join("、", groups) : "(无)");
                    }
                }
            }
            catch (Exception ex)
            {
                output.Append("ZML LoRA 组读取失败: ").AppendLine(ex.Message);
            }
        }

        interactor.Poke(output.ToString());
    }

    // ===================== 图片下载 & 上传 =====================

    /// <summary>
    /// 下载远程图片到临时目录（复用 UniversalImageGen 模式）。
    /// 校验 Content-Type 和大小，防止下载非图片或超大文件。
    /// </summary>
    async Task<string> DownloadInputImageAsync(string url, CancellationToken ct = default)
    {
        // data: URI -> 仅接受显式 Base64 图片，避免把任意数据伪装成 PNG。
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = url.IndexOf(',');
            if (commaIdx < 0)
                throw new Exception("无效的 data URI");

            var metadata = url[5..commaIdx];
            var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var mediaType = metadataParts.FirstOrDefault() ?? "";
            if (!AllowedImageContentTypes.Contains(mediaType))
                throw new Exception($"data URI 图片类型不受支持: {mediaType}");
            if (!metadataParts.Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("data URI 仅支持 Base64 编码");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(
                    Uri.UnescapeDataString(url[(commaIdx + 1)..]));
            }
            catch (FormatException ex)
            {
                throw new Exception("data URI 的 Base64 内容无效", ex);
            }
            if (bytes.Length > MaxInputImageBytes)
                throw new Exception($"图片过大 ({bytes.Length / 1024 / 1024}MB > {MaxInputImageBytes / 1024 / 1024}MB)");
            var ext = DetectImageExtension(bytes);
            var tmp = Path.Combine(Path.GetTempPath(), $"comfyui_input_{Guid.NewGuid():N}{ext}");
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            return tmp;
        }

        // HTTP/HTTPS URL
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var resp = await _dlHttp.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        // Content-Type 校验
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedImageContentTypes.Contains(contentType))
            throw new Exception($"不支持的内容类型: {contentType ?? "未知"}（仅支持 png/jpeg/webp/gif/bmp）");

        // 大小预检
        var cl = resp.Content.Headers.ContentLength;
        if (cl.HasValue && cl.Value > MaxInputImageBytes)
            throw new Exception($"图片过大 ({cl.Value / 1024 / 1024}MB > {MaxInputImageBytes / 1024 / 1024}MB)");

        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        if (data.Length > MaxInputImageBytes)
            throw new Exception($"图片过大 ({data.Length / 1024 / 1024}MB > {MaxInputImageBytes / 1024 / 1024}MB)");

        var extension = DetectImageExtension(data);
        var tmpPath = Path.Combine(Path.GetTempPath(), $"comfyui_input_{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(tmpPath, data, ct);
        return tmpPath;
    }

    /// <summary>
    /// 上传图片到 ComfyUI /upload/image。
    /// 返回 ComfyUI 分配给该图片的文件名。
    /// </summary>
    async Task<string> UploadImageToComfyUIAsync(string baseUrl, string filePath, ComfyuiConfig cfg, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxInputImageBytes)
            throw new Exception($"图片过大 ({fileInfo.Length / 1024 / 1024}MB > {MaxInputImageBytes / 1024 / 1024}MB)");

        using var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var detectedExtension = DetectImageExtension(fileBytes);
        var byteContent = new ByteArrayContent(fileBytes);
        var mime = detectedExtension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
        byteContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var uploadName = Path.ChangeExtension(Path.GetFileName(filePath), detectedExtension);
        content.Add(byteContent, "image", uploadName);
        content.Add(new StringContent("true"), "overwrite");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/upload/image")
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(cfg.ApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken);

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var result = JsonNode.Parse(raw) as JsonObject
            ?? throw new Exception($"上传图片响应格式异常: {raw}");
        var name = result["name"]?.GetValue<string>()
            ?? throw new Exception($"上传图片响应缺少 name 字段: {raw}");
        return name;
    }

    static string DetectImageExtension(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8) return ".jpg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50
            && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D
            && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A) return ".png";
        if (data.Length >= 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ".gif";
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49
            && data[2] == 0x46 && data[3] == 0x46 && data[8] == 0x57
            && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return ".webp";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return ".bmp";
        throw new InvalidDataException("文件内容不是受支持的图片格式");
    }

    // ===================== HTTP =====================

    async Task<JsonObject?> FetchObjectInfoAsync(
        string baseUrl, ComfyuiConfig cfg, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/object_info", cfg);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(raw) as JsonObject;
    }

    async Task<string> QueuePromptAsync(
        string baseUrl, ComfyuiConfig cfg, JsonObject prompt,
        string clientId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["client_id"] = clientId
        };

        using var req = CreateRequest(HttpMethod.Post, $"{baseUrl}/prompt", cfg);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var preview = raw.Length > 500 ? raw[..500] + "..." : raw;
            throw new Exception($"提交工作流失败 (HTTP {(int)resp.StatusCode}): {preview}");
        }

        var node = JsonNode.Parse(raw) as JsonObject
            ?? throw new Exception($"ComfyUI 提交响应格式异常: {raw}");
        if (node["error"] != null)
            throw new Exception($"ComfyUI 拒绝工作流: {FormatComfyError(node["error"])}");
        if (node["node_errors"] is JsonObject ne && ne.Count > 0)
            throw new Exception($"节点错误: {FormatNodeErrors(ne)}");
        var promptId = node["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(promptId))
            throw new Exception($"ComfyUI 提交响应缺少 prompt_id: {raw}");
        return promptId;
    }

    /// <summary>把 Comfy 的 error 字段整理成短可读文本（不 dump 整份 prompt）。</summary>
    static string FormatComfyError(JsonNode? error)
    {
        if (error == null) return "(无详情)";
        if (error is JsonValue jv && jv.TryGetValue<string>(out var s))
            return s;
        if (error is JsonObject obj)
        {
            var type = obj["type"]?.ToString();
            var msg = obj["message"]?.ToString() ?? obj["details"]?.ToString();
            if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(msg))
                return $"{type}: {msg}";
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        var raw = error.ToJsonString();
        return raw.Length > 300 ? raw[..300] + "..." : raw;
    }

    /// <summary>按节点汇总 node_errors，便于定位 class/字段问题。</summary>
    static string FormatNodeErrors(JsonObject nodeErrors)
    {
        var parts = new List<string>();
        foreach (var kv in nodeErrors)
        {
            var nodeId = kv.Key;
            if (kv.Value is not JsonObject detail)
            {
                parts.Add($"节点{nodeId}: {kv.Value}");
                continue;
            }

            var classType = detail["class_type"]?.ToString();
            var errors = detail["errors"] as JsonArray;
            if (errors == null || errors.Count == 0)
            {
                var one = detail.ToJsonString();
                if (one.Length > 200) one = one[..200] + "...";
                parts.Add(string.IsNullOrWhiteSpace(classType)
                    ? $"节点{nodeId}: {one}"
                    : $"节点{nodeId}({classType}): {one}");
                continue;
            }

            foreach (var err in errors)
            {
                if (err is not JsonObject e)
                {
                    parts.Add($"节点{nodeId}: {err}");
                    continue;
                }
                var et = e["type"]?.ToString() ?? "error";
                var msg = e["message"]?.ToString() ?? e["details"]?.ToString() ?? e.ToJsonString();
                var extra = e["extra_info"] as JsonObject;
                var inputName = extra?["input_name"]?.ToString();
                var loc = string.IsNullOrWhiteSpace(classType) ? $"节点{nodeId}" : $"节点{nodeId}({classType})";
                if (!string.IsNullOrWhiteSpace(inputName))
                    parts.Add($"{loc} 字段'{inputName}' {et}: {msg}");
                else
                    parts.Add($"{loc} {et}: {msg}");
            }
        }

        var joined = string.Join("; ", parts);
        return joined.Length > 800 ? joined[..800] + "..." : joined;
    }

    async Task<JsonNode> WaitHistoryAsync(
        string baseUrl, ComfyuiConfig cfg, string promptId,
        TimeSpan timeout, int intervalMs,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/history/{promptId}", cfg);
            using var resp = await Http.SendAsync(req, cancellationToken);
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(raw);
                if (node is JsonObject obj && obj.ContainsKey(promptId))
                {
                    // 检查 status
                    var status = obj[promptId]?["status"];
                    var completed = status?["completed"]?.GetValue<bool>() ?? true;
                    var statusStr = status?["status_str"]?.GetValue<string>();
                    if (statusStr == "error")
                    {
                        var msgs = status?["messages"]?.ToJsonString() ?? "";
                        throw new Exception($"ComfyUI 执行错误: {Truncate(msgs, 400)}");
                    }
                    // outputs 存在即认为完成
                    if (obj[promptId]?["outputs"] != null)
                        return obj;
                    if (completed && statusStr == "success")
                        return obj;
                }
            }

            await Task.Delay(intervalMs, cancellationToken);
        }
        throw new TaskCanceledException("等待 history 超时");
    }

    async Task<byte[]?> ViewImageAsync(
        string baseUrl, ComfyuiConfig cfg, string filename, string subfolder,
        string type, CancellationToken ct)
    {
        var qs = $"filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type={Uri.EscapeDataString(type ?? "output")}";
        using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/view?{qs}", cfg);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            LogWarn($"下载图片失败 {filename} HTTP {(int)resp.StatusCode}");
            return null;
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    static HttpRequestMessage CreateRequest(HttpMethod method, string url, ComfyuiConfig cfg)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(cfg.ApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken);
        return req;
    }

    // ===================== 解析输出 =====================

    record ImageRef(string Filename, string Subfolder, string Type);

    static List<ImageRef> ExtractImages(JsonNode history, string promptId, JsonObject? apiPrompt = null)
    {
        var list = new List<ImageRef>();
        var outputs = history[promptId]?["outputs"] as JsonObject;
        if (outputs == null) return list;

        foreach (var kv in outputs)
        {
            // 有 apiPrompt 时只取 SaveImage 类节点，过滤 PreviewImage 等预览节点
            if (apiPrompt != null
                && apiPrompt[kv.Key] is JsonObject node)
            {
                var ct = node["class_type"]?.GetValue<string>() ?? "";
                if (!IsSaveNode(ct))
                    continue;
            }

            if (kv.Value is not JsonObject outNode) continue;
            if (outNode["images"] is not JsonArray imgs) continue;
            foreach (var img in imgs.OfType<JsonObject>())
            {
                var fn = img["filename"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(fn)) continue;
                var sub = img["subfolder"]?.GetValue<string>() ?? "";
                var type = img["type"]?.GetValue<string>() ?? "output";
                list.Add(new ImageRef(fn, sub, type));
            }
        }
        return list;
    }

    /// <summary>
    /// 判断节点是否为「保存图片」类（会向 history.outputs 上报图片）：
    /// 原生 SaveImage / ZML_SaveImageV2 / 含「保存」的节点，以及
    /// Crystools 的 "Save image with extra metadata [Crystools]" 等带空格变体。
    /// PreviewImage、rgthree Image Comparer 等纯预览/画廊节点不算，避免中间预览被当成出图。
    /// </summary>
    static bool IsSaveNode(string classType)
    {
        if (string.IsNullOrEmpty(classType))
            return false;
        if (classType.Contains("SaveImage", StringComparison.OrdinalIgnoreCase)
            || classType.Contains("保存", StringComparison.OrdinalIgnoreCase))
            return true;
        // 兼容 "Save image with extra metadata [Crystools]"：去标点后仍含 saveimage
        var compact = new string(classType.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("saveimage", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> TryFindCustomSavePaths(JsonObject prompt)
    {
        var dirs = new List<string>();
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (!IsSaveNode(ct))
                continue;
            var inputs = node["inputs"] as JsonObject;
            var path = inputs?["保存路径"]?.GetValue<string>()
                       ?? inputs?["filename_prefix"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string? resolved = null;
            if (Directory.Exists(path))
                resolved = path;
            else
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        resolved = dir;
                }
                catch { }
            }

            if (resolved != null && !dirs.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                dirs.Add(resolved);
        }
        return dirs;
    }

    static FileInfo? TryClaimCustomOutput(
        List<string> directories,
        DateTime generationStartedUtc,
        string? expectedFilename = null)
    {
        foreach (var dir in directories)
        {
            var file = TryClaimCustomOutput(dir, generationStartedUtc, expectedFilename);
            if (file != null) return file;
        }
        return null;
    }

    static FileInfo? TryClaimCustomOutput(
        string directory,
        DateTime generationStartedUtc,
        string? expectedFilename = null)
    {
        if (!Directory.Exists(directory))
            return null;

        var expectedName = string.IsNullOrWhiteSpace(expectedFilename)
            ? null
            : Path.GetFileName(expectedFilename);
        var expectedStem = expectedName == null
            ? null
            : Path.GetFileNameWithoutExtension(expectedName);
        var expectedExtension = expectedName == null
            ? null
            : Path.GetExtension(expectedName);

        IEnumerable<FileInfo> candidates = new DirectoryInfo(directory)
            .GetFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(file => IsOutputImageExtension(file.Extension))
            .Where(file => file.LastWriteTimeUtc >= generationStartedUtc);

        if (expectedName != null)
        {
            candidates = candidates
                .Where(file => file.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                               || file.Extension.Equals(expectedExtension, StringComparison.OrdinalIgnoreCase)
                                  && file.Name.StartsWith(expectedStem!, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(file => file.LastWriteTimeUtc);
        }
        else
        {
            candidates = candidates.OrderByDescending(file => file.LastWriteTimeUtc);
        }

        foreach (var file in candidates)
        {
            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc < generationStartedUtc)
                continue;

            var key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
            if (ClaimedCustomOutputs.TryAdd(key, DateTime.UtcNow.Ticks))
            {
                PruneCustomOutputClaims();
                return file;
            }
        }

        return null;
    }

    static bool IsOutputImageExtension(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    static void PruneCustomOutputClaims()
    {
        if (ClaimedCustomOutputs.Count <= 4096)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-1).Ticks;
        foreach (var claim in ClaimedCustomOutputs)
        {
            if (claim.Value < cutoff)
                ClaimedCustomOutputs.TryRemove(claim.Key, out _);
        }
    }

    // ===================== 路径 =====================

    Dictionary<string, (int w, int h)> GetOrientationPresets(ComfyuiConfig cfg)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            { "portrait", (cfg.PortraitWidth, cfg.PortraitHeight) },
            { "landscape", (cfg.LandscapeWidth, cfg.LandscapeHeight) },
            { "square", (cfg.SquareWidth, cfg.SquareHeight) },
            // 中文别名
            { "竖版", (cfg.PortraitWidth, cfg.PortraitHeight) },
            { "横版", (cfg.LandscapeWidth, cfg.LandscapeHeight) },
            { "正方形", (cfg.SquareWidth, cfg.SquareHeight) },
            { "方", (cfg.SquareWidth, cfg.SquareHeight) },
        };
    }

    (int? w, int? h) ResolveResolution(string? orientation, int? width, int? height, ComfyuiConfig cfg)
    {
        int? w = width, h = height;

        // 优先级：显式 width/height > orientation 预设 > 配置默认方向 > DefaultWidth/Height 兜底
        if (!w.HasValue || !h.HasValue)
        {
            var ori = string.IsNullOrWhiteSpace(orientation) ? cfg.DefaultOrientation : orientation;
            if (!string.IsNullOrWhiteSpace(ori)
                && GetOrientationPresets(cfg).TryGetValue(ori.Trim(), out var p))
            {
                if (!w.HasValue) w = p.w;
                if (!h.HasValue) h = p.h;
            }
            else
            {
                if (!w.HasValue && cfg.DefaultWidth > 0) w = cfg.DefaultWidth;
                if (!h.HasValue && cfg.DefaultHeight > 0) h = cfg.DefaultHeight;
            }
        }

        if (w.HasValue) w = Math.Clamp(w.Value, 64, 4096);
        if (h.HasValue) h = Math.Clamp(h.Value, 64, 4096);
        return (w, h);
    }

    /// <summary>
    /// 拼接固定正向提示词前缀，并与 AI 提示词合并去重。
    /// 最终格式：segment, segment, segment（仅英文逗号 ","，且逗号后一个空格）。
    /// 前缀片段优先保留；AI 侧与前缀/自身重复的 tag 或短句会被去掉。
    /// 无前缀时也会对 AI 提示词自身做去重与格式规范化。
    /// </summary>
    static string BuildPositivePrompt(string prompt, string? prefix)
    {
        var chunks = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(prefix))
            chunks.Add(prefix);
        if (!string.IsNullOrWhiteSpace(prompt))
            chunks.Add(prompt);

        if (chunks.Count == 0)
            return "";

        return DeduplicateAndJoinPromptSegments(chunks);
    }

    /// <summary>
    /// 按逗号/换行拆段、忽略大小写与权重包装去重。
    /// 输出只使用英文逗号，例如：
    /// masterpiece, best quality, a girl on the bed, a man sit in the desk
    /// </summary>
    static string DeduplicateAndJoinPromptSegments(IEnumerable<string> chunks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var removed = 0;

        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            foreach (var raw in SplitPromptSegments(chunk))
            {
                var segment = CleanPromptSegment(raw);
                if (segment.Length == 0)
                    continue;

                var key = NormalizePromptSegmentKey(segment);
                if (key.Length == 0)
                    continue;

                if (!seen.Add(key))
                {
                    removed++;
                    continue;
                }

                result.Add(segment);
            }
        }

        if (removed > 0)
            Log($"提示词去重: 移除 {removed} 个重复片段，保留 {result.Count} 个");

        // 强制英文逗号 + 空格；绝不输出中文逗号
        return string.Join(", ", result);
    }

    /// <summary>
    /// 按英文逗号/中文逗号/顿号/换行/分号拆分。中文标点仅作输入兼容，最终全部换成英文逗号。
    /// </summary>
    static IEnumerable<string> SplitPromptSegments(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var normalized = text
            .Replace('，', ',')
            .Replace('\u3001', ',') // 顿号
            .Replace('；', ';');

        var sb = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c is ',' or ';' or '\r' or '\n')
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    static string CleanPromptSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        // 片段内残留的中文/全角标点清掉，避免进工作流
        var s = raw
            .Replace('，', ' ')
            .Replace('\u3001', ' ')
            .Replace('；', ' ')
            .Replace(';', ' ')
            .Replace(',', ' ')
            .Trim();

        s = Regex.Replace(s, @"\s+", " ");
        s = s.Trim().Trim(',', '，', ';', '；', '、');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    /// <summary>
    /// 去重键：忽略大小写、下划线/空格差异，并剥离简单权重括号如 (tag:1.2) / ((tag))。
    /// 先出现的原文形式保留（通常是前缀里的写法）。
    /// </summary>
    static string NormalizePromptSegmentKey(string segment)
    {
        var s = segment.Trim();
        if (s.Length == 0)
            return "";

        // 反复剥掉最外层权重/强调括号：(foo:1.2) / (foo) / ((foo))
        // 不处理 <lora:...>，避免误伤
        for (var guard = 0; guard < 8; guard++)
        {
            var m = Regex.Match(
                s,
                @"^\(\s*(.+?)\s*(?::\s*[\d.]+\s*)?\)$");
            if (!m.Success)
                break;
            s = m.Groups[1].Value.Trim();
        }

        s = Regex.Replace(s, @"\s+", " ");
        s = s.Replace('_', ' ');
        return s.ToLowerInvariant();
    }

    static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "http://127.0.0.1:8188";
        return url.Trim().TrimEnd('/');
    }

    static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    static string ResolveSaveDir(ComfyuiConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.SaveDirectory))
            return cfg.SaveDirectory;
        return Path.Combine(AlifePath.StorageFolderPath, "Images", "Comfyui");
    }

    string ResolveWorkflowPath(ComfyuiConfig cfg)
    {
        return ResolveWorkflowPath(cfg, null);
    }

    string ResolveWorkflowPath(ComfyuiConfig cfg, string? workflowName)
    {
        if (!string.IsNullOrWhiteSpace(workflowName))
        {
            var allWorkflows = ParseNamedWorkflows(cfg.NamedWorkflows);
            var requestedName = workflowName.Trim();
            var matches = allWorkflows
                .Where(w => w.Enabled
                            && w.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                if (allWorkflows.Any(w => !w.Enabled
                    && w.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception($"工作流 \"{requestedName}\" 已禁用");

                var available = allWorkflows
                    .Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Name))
                    .Select(w => w.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var availableText = string.Join("、", available);
                throw new Exception(string.IsNullOrWhiteSpace(availableText)
                    ? $"未找到已启用的工作流 \"{requestedName}\""
                    : $"未找到已启用的工作流 \"{requestedName}\"；可用：{availableText}");
            }

            if (matches.Count > 1)
                throw new Exception($"工作流名称 \"{requestedName}\" 重复，请在配置中保持名称唯一");

            var match = matches[0];
            if (string.IsNullOrWhiteSpace(match.Path))
                throw new Exception($"工作流 \"{requestedName}\" 未配置 JSON 路径");

            var resolved = ResolveSinglePath(cfg, match.Path);
            if (!File.Exists(resolved))
                throw new Exception($"工作流 \"{requestedName}\" 文件不存在: {resolved}");
            return resolved;
        }

        return ResolveSinglePath(cfg, cfg.WorkflowPath ?? "");
    }

    string ResolveSinglePath(ComfyuiConfig cfg, string path)
    {
        var p = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(p))
            return "";

        if (Path.IsPathRooted(p) && File.Exists(p))
            return p;

        // 相对插件目录
        var pluginDir = GetPluginDirectory();
        var candidate = Path.GetFullPath(Path.Combine(pluginDir, p));
        if (File.Exists(candidate))
            return candidate;

        // 再试原始路径
        if (File.Exists(p))
            return Path.GetFullPath(p);

        return candidate;
    }

    string ResolvePositivePromptPrefix(ComfyuiConfig cfg, string? workflowName)
    {
        if (!string.IsNullOrWhiteSpace(workflowName))
        {
            var match = ParseNamedWorkflows(cfg.NamedWorkflows)
                .Where(w => w.Enabled
                            && w.Name.Equals(workflowName.Trim(), StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (match != null && !string.IsNullOrWhiteSpace(match.Prefix))
                return match.Prefix;
        }
        return cfg.PositivePromptPrefix;
    }

    static string? WfJsonString(JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    static bool? WfJsonBool(JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<bool>(out var b))
            return b;
        return null;
    }

    static List<WorkflowEntry> ParseNamedWorkflows(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new();

        try
        {
            var arr = JsonNode.Parse(json) as JsonArray;
            if (arr == null) return new();

            return arr
                .OfType<JsonObject>()
                .Select(o => new WorkflowEntry(
                    WfJsonString(o, "n") ?? WfJsonString(o, "name") ?? "",
                    WfJsonString(o, "p") ?? WfJsonString(o, "path") ?? "",
                    WfJsonBool(o, "e") ?? true,
                    WfJsonString(o, "f") ?? WfJsonString(o, "prefix") ?? ""))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    string GetPluginDirectory()
    {
        // 优先：当前程序集/脚本所在目录
        try
        {
            var loc = typeof(ComfyuiService).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(loc))
            {
                var dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }
        }
        catch { }

        // 回退：Storage/Plugins/Alife.Plugin.Comfyui
        var fallback = Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui");
        return fallback;
    }

    string ResolveCharacterCatalogPath()
    {
        var candidates = new[]
        {
            Path.Combine(GetPluginDirectory(), "character-prompts.json"),
            Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui", "character-prompts.json"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}





