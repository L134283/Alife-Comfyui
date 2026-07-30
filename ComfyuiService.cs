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
    XmlFunctionCaller functionService
) : InteractiveModule<ComfyuiService>, IConfigurable<ComfyuiConfig>
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
    // 优先生图串行，避免叠多个阻塞任务拖死对话
    static readonly SemaphoreSlim PriorityGate = new(1, 1);
    static readonly ConcurrentDictionary<string, long> ClaimedCustomOutputs = new(StringComparer.OrdinalIgnoreCase);

    record WorkflowEntry(string Name, string Path, bool Enabled);
    CharacterPromptIndex? _characterPromptIndex;

    record PromptPresetEntry(string Name, string Content);

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        var cfg = Configuration ?? new ComfyuiConfig();
        var wf = ResolveWorkflowPath(cfg);

        try
        {
            var catalogPath = ResolveCharacterCatalogPath();
            if (File.Exists(catalogPath))
            {
                _characterPromptIndex = CharacterPromptIndex.Load(catalogPath);
                Log($"已加载角色提示词索引: {_characterPromptIndex.Count} 个角色");
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

        // 只向 AI 暴露名称唯一且文件存在的工作流。
        var configuredWorkflows = ParseNamedWorkflows(cfg.NamedWorkflows)
            .Where(w => w.Enabled
                        && !string.IsNullOrWhiteSpace(w.Name)
                        && !string.IsNullOrWhiteSpace(w.Path))
            .ToList();
        var namedWorkflows = new List<WorkflowEntry>();
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
        var hasNamedWorkflows = namedWorkflows.Count > 0;
        var workflowPaths = new[] { wf }.Concat(namedWorkflows.Select(w => w.Path));
        var supportsImageInput = await AnyWorkflowSupportsImageInputAsync(workflowPaths);

        RegisterFunctionHandlers(cfg, hasNamedWorkflows, supportsImageInput);

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
                "避免故事叙述、否定句和互相冲突的描述。",

            "hybrid" =>
                "角色身份、外貌、服装和表情使用逗号分隔的英文 Tag；复杂动作、人物关系、构图和场景用 1~2 个简洁英文短句追加在末尾。",

            _ =>
                "使用具体的 Danbooru 风格英文 Tag，半角逗号分隔。顺序为主体身份、人数、外貌、服装、表情动作、构图、背景与光线；去重并避免矛盾。"
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

        var workflowListDesc = hasNamedWorkflows
            ? $"\n- 可选 workflow：{string.Join("、", namedWorkflows.Select(w => $"\"{w.Name}\""))}；不传则使用默认工作流。"
            : "";

        var nodeControlDesc = cfg.EnableNodeControl
            ? "\n- 高级节点控制已开启；仅在用户明确要求时修改。需要节点信息时先调用 getcomfyuicontrols。"
            : "";

        var denoiseGuide = supportsImageInput
            ? "\n- 图生图 denoise：微调 0.30~0.45，换装/姿势 0.55~0.70，大改 0.70~0.85；文生图不要传。"
            : "";
        var imageInputNote = supportsImageInput
            ? "\n- imagepath 仅在图生图时传本地路径或图片 URL；文生图不要传。"
            : "";

        var prefixNote = string.IsNullOrWhiteSpace(cfg.PositivePromptPrefix)
            ? "" : "\n- 固定正向前缀由插件自动添加，并与 AI 提示词合并后自动去重（统一为「英文逗号+空格」）；不要把前缀里已有的 tag/短句再写进 prompt。";
        var characterLookupNote = _characterPromptIndex == null
            ? ""
            : """

            【具体角色检索】
            - 仅当主体是有明确名字的既有动漫、游戏、漫画或虚拟主播角色时，先调用 findcharacterprompt；作品已知时同时传 work。
            - 画你自己的人设、原创角色、普通人物、真人，或只指定服装/动作/画风时，直接组织提示词，不要检索。
            - 仅使用 status: matched 的结果。prompt 必须保留 trigger_tags_xml 与 appearance_tags_xml；动作、构图和场景按当前请求自由组合。
            - 用户指定了新服装时舍弃 default_outfit_tags_xml，只写新服装；未指定服装时可使用默认服装。
            - 返回字段已按 XML 属性转义，组合进 generateimage 的 prompt 属性时保持 &amp; 等转义文本原样，不要还原或翻译。
            """;

        var presetNote = """

            【提示词预设】
            - 可调用 getpromptpreset 检索已保存的提示词预设（角色人设/复杂动作/完整背景等），不传 name 返回列表，传 name 返回完整内容。
            - 拿到预设内容后组合到 generateimage 的 prompt 中，不要原样塞入，需结合当前请求调整。
            - 用户要求保存常用提示词时调用 savepromptpreset（name=名称, content=内容）。
            """;

        var danbooruNote = "";
        if (cfg.EnableDanbooruSearch)
        {
            var styleUseLine = cfg.PromptStyle switch
            {
                "natural" =>
                    "- 自然语言模式同样积极 search（可用 full_scene 一次取概念）；落笔时角色 Tag 按规则放开头，其后用 2~4 个英文短句消化检索结果（可嵌入关键英文词），禁止把返回 tag 列表原样当作整段 prompt。",
                "hybrid" =>
                    "- 混合模式：检索 tag 进身份/外貌/服装/表情的 Tag 段；动作、关系、场景仍用短句。",
                _ =>
                    "- 纯 Tag 模式：充分吸收返回英文 tag（去重去矛盾）；复杂画面可用 full_scene。"
            };

            var artistLine = cfg.EnableDanbooruArtistRecommend
                ? "\n- 仅当用户明确要画风/画师时，可再调用 getdanbooruartists（tags=已确定的英文 tag）；单次生图最多 1 次；未要求不要调用。"
                : "";

            danbooruNote = $"""

            【在线标签检索】（质量增强：有画面细节时积极使用）
            - 用户描述含服装/道具/姿势/场景/光影/风格等细节时，优先调用 searchdanboorutags 再 generateimage，以提升提示词准确度；三种提示词模式均适用，不要因为自然语言模式而少用。
            - 极简且无额外画面细节时可直接 generateimage。
            - 已有英文 tag 需补搭配时 getrelateddanboorutags。
            - 优先级：findcharacterprompt（具名角色）> 用户预设 > 在线标签 > 自写。在线不得覆盖角色 trigger/appearance；用户新服装以用户为准。
            - 单次生图 search≤1、related≤1；一次 query 写清关键画面即可；禁止中文原句当 prompt。
            - 仅用 status: ok；unavailable/超时则按当前模式自写英文并 generateimage，勿声称已检索、勿反复重试检索。
            {styleUseLine}{artistLine}
            """;
        }

        Prompt($$"""
        【ComfyUI 生图】
        - 所有 prompt 使用英文，直接描述目标画面，不要把用户的中文命令原句塞进 prompt。
        - 尺寸：portrait={{cfg.PortraitWidth}}×{{cfg.PortraitHeight}}，landscape={{cfg.LandscapeWidth}}×{{cfg.LandscapeHeight}}，square={{cfg.SquareWidth}}×{{cfg.SquareHeight}}；默认 {{cfg.DefaultOrientation}}。
        - 提示词模式：{{styleLabel}}。{{styleGuide}}{{prefixNote}}{{workflowListDesc}}{{imageInputNote}}{{denoiseGuide}}{{nodeControlDesc}}{{autoOpenNote}}
        - QQ 环境需要发图时使用 <qimage type="Private/Group" targetid="QQ号或群号" image="完整路径" />。私聊 type=Private、群聊 type=Group，targetid 填当前会话的 QQ 号或群号。{{characterLookupNote}}{{presetNote}}{{danbooruNote}}{{priorityNote}}
        """);
    }

    void RegisterFunctionHandlers(
        ComfyuiConfig cfg, bool hasNamedWorkflows, bool supportsImageInput)
    {
        var discovered = new XmlHandler(this);
        var functions = discovered.Functions.ToDictionary(
            function => function.Name, StringComparer.OrdinalIgnoreCase);

        if (functions.TryGetValue("checkcomfyuistatus", out var statusFunction))
        {
            functionService.RegisterHandlerWithoutDocument(new XmlHandler
            {
                Name = "ComfyuiStatusInternal",
                Instance = this,
                Functions = new List<XmlFunction> { statusFunction }
            });
        }

        var exposed = new List<XmlFunction>();
        if (functions.TryGetValue("generateimage", out var generateFunction))
        {
            var hiddenParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var description = supportsImageInput
                ? "使用 ComfyUI 生成图片；传 imagepath 时执行图生图。"
                : "使用 ComfyUI 生成图片。";
            exposed.Add(CloneFunctionDocument(
                generateFunction, description, hiddenParameters));
        }

        if (_characterPromptIndex != null
            && functions.TryGetValue("findcharacterprompt", out var characterFunction))
        {
            exposed.Add(CloneFunctionDocument(
                characterFunction,
                "仅在明确生成某个既有动漫、游戏、漫画或虚拟主播角色时调用；返回身份、稳定外貌和默认服装 Tag。"));
        }

        if (cfg.EnableNodeControl
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

        var handlerDesc = "ComfyUI 生图";
        if (_characterPromptIndex != null)
            handlerDesc += "与具体二次元角色提示词检索";
        if (cfg.EnableDanbooruSearch)
            handlerDesc += "、在线标签检索";
        handlerDesc += "。";

        functionService.RegisterHandler(new XmlHandler
        {
            Name = "ComfyuiImageGeneration",
            Description = handlerDesc,
            Instance = this,
            Functions = exposed
        });
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
    [Description("使用 ComfyUI 工作流生成图片。传入正向提示词（必填）；可选 orientation(portrait/landscape/square) 或 width/height；可选 imagePath 进行图生图。配置多个工作流后可传 workflow 切换；AI 节点控制开启后可传 model/steps/cfg 等高级参数。开启「优先生图」时会等待出图结束再返回。")]
    public async Task GenerateImage(
        [Description("正向提示词，描述画面内容（固定前缀会自动拼在最前）")] string prompt,
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
        [Description("【高级】安全节点覆盖，格式 {\"节点ID\":{\"参数名\":标量值}}")] string? nodeOverrides = null)
    {
        var cfgConfig = Configuration ?? new ComfyuiConfig();
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
                    Poke("已有生图任务进行中，请稍后再试（优先生图模式不叠任务）");
                    return;
                }

                await RunGenerationSafelyAsync(
                    prompt, orientation, imagePath, width, height,
                    workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size, nodeOverrides,
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
            Poke("生图请求已发出，可以继续聊天");
            _ = RunGenerationSafelyAsync(
                prompt, orientation, imagePath, width, height,
                workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size, nodeOverrides,
                priorityMode: false);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查 ComfyUI 是否在线，并返回系统状态摘要")]
    public void CheckComfyuiStatus()
    {
        _ = CheckStatusAsync();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("仅检索明确指定的既有二次元角色；支持中文、英文、别名、部分名称和少量错字。")]
    public void FindCharacterPrompt(
        [Description("角色名，支持中文、英文、别名或不完整名称")] string name,
        [Description("可选作品名；同名角色或短名称时建议填写")] string? work = null)
    {
        if (_characterPromptIndex == null)
        {
            Poke("角色提示词索引未加载，请检查 character-prompts.json 是否位于插件目录");
            return;
        }

        var matches = _characterPromptIndex.Search(name, work, 3);
        if (matches.Count == 0)
        {
            Poke("status: not_found\nquery: " + name
                 + (string.IsNullOrWhiteSpace(work) ? "" : $"\nwork: {work}")
                 + "\naction: 不要编造角色 Tag；检查角色名或作品名后再查询");
            return;
        }

        var best = matches[0];
        var strongWorkMatch = !string.IsNullOrWhiteSpace(work) && best.WorkScore >= 0.85;
        var ambiguous = best.Score < 0.80
            || matches.Count > 1
               && best.Score - matches[1].Score < 0.04
               && !strongWorkMatch;

        if (ambiguous)
        {
            var candidates = string.Join("\n", matches.Select((match, index) =>
                $"{index + 1}. {match.Entry.ChineseName} [{match.Entry.EnglishName}] | " +
                $"{DisplayWork(match.Entry)} | match_score: {(int)Math.Round(match.Score * 100)}/100"));
            Poke($"status: ambiguous\nquery: {name}\ncandidates:\n{candidates}\n" +
                 "action: 不要使用任何角色 Tag；请结合上下文选定角色后，带作品名 work 重新查询");
            Log($"角色检索存在歧义: {name}");
            return;
        }

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

        Poke(result.ToString());
        Log($"角色检索: {name} -> {best.Entry.EnglishName} ({matchScore}/100)");
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
            Poke("status: unavailable\nreason: 在线标签检索未开启\naction: 请直接按当前提示词模式写英文并 generateimage");
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
            Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 搜索异常: {ex.Message}");
            Poke("status: unavailable\nreason: " + ex.Message
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
            Poke("status: unavailable\nreason: 在线标签检索未开启\naction: 用已有 tag 直接生图");
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
            Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 关联异常: {ex.Message}");
            Poke("status: unavailable\nreason: " + ex.Message
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
            Poke("status: unavailable\nreason: 画师推荐未开启\naction: 跳过画师，直接生图");
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
            Poke(text);
        }
        catch (Exception ex)
        {
            LogWarn($"Danbooru 画师异常: {ex.Message}");
            Poke("status: unavailable\nreason: " + ex.Message
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
            Poke("预设名称不能为空");
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

        Poke($"status: saved\nname: {name}\naction: 下次可用 getpromptpreset name=\"{name}\" 检索复用");
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
            Poke("status: empty\naction: 暂无提示词预设，可调用 savepromptpreset 保存");
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var names = string.Join("\n", presets.Select((p, i) =>
                $"{i + 1}. {p.Name}"));
            Poke($"status: list\ncount: {presets.Count}\npresets:\n{names}");
            return;
        }

        name = name.Trim();
        var match = presets.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            Poke($"status: not_found\nname: {name}\naction: 预设不存在，可调用 getpromptpreset 查看列表");
            return;
        }

        Poke($"status: found\nname: {match.Name}\ncontent:\n{match.Content}");
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
                Poke("AI 节点控制未开启");
                return;
            }

            var workflowPath = ResolveWorkflowPath(cfg, workflow);
            if (string.IsNullOrWhiteSpace(workflowPath) || !File.Exists(workflowPath))
                throw new FileNotFoundException("工作流文件不存在", workflowPath);

            var raw = await File.ReadAllTextAsync(workflowPath);
            var root = JsonNode.Parse(raw) ?? throw new InvalidDataException("工作流 JSON 为空");
            var prompt = ComfyuiWorkflowConverter.ToApiPrompt(root);
            var label = string.IsNullOrWhiteSpace(workflow) ? "默认工作流" : workflow.Trim();
            Poke($"workflow: {label}\n{ComfyuiWorkflowConverter.BuildNodeControlDescription(prompt)}");
        }
        catch (Exception ex)
        {
            LogWarn($"读取节点控制信息失败: {ex.Message}");
            Poke($"读取节点控制信息失败: {ex.Message}");
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
                Poke($"ComfyUI 不可用 (HTTP {(int)resp.StatusCode})");
                return;
            }
            var node = JsonNode.Parse(raw);
            var ver = node?["system"]?["comfyui_version"]?.ToString() ?? "?";
            var device = node?["devices"]?[0]?["name"]?.ToString() ?? "未知设备";
            Poke($"ComfyUI 在线\n版本: {ver}\n设备: {device}");
            Log($"状态正常 {ver} / {device}");
        }
        catch (Exception ex)
        {
            LogError($"状态检查失败: {ex.Message}");
            Poke($"ComfyUI 连接失败: {ex.Message}");
        }
    }

    async Task RunGenerationSafelyAsync(
        string prompt, string? orientation, string? imagePath,
        int? width, int? height,
        string? workflow, int? steps, double? cfg, string? sampler, string? scheduler,
        string? model, double? denoise, int? batch_size, string? nodeOverrides,
        bool priorityMode)
    {
        try
        {
            await GenerateImageAsync(
                prompt, orientation, imagePath, width, height,
                workflow, steps, cfg, sampler, scheduler, model, denoise, batch_size,
                nodeOverrides, priorityMode);
        }
        catch (Exception ex)
        {
            LogError($"生图任务启动失败: {ex.Message}");
            Poke($"生图失败: {ex.Message}");
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
        bool priorityMode = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Poke("提示词不能为空");
            return;
        }

        var generationStartedUtc = DateTime.UtcNow;
        var cfgConfig = Configuration ?? new ComfyuiConfig();
        var baseUrl = NormalizeBaseUrl(cfgConfig.BaseUrl);
        var saveDir = ResolveSaveDir(cfgConfig);
        Directory.CreateDirectory(saveDir);

        var (w, h) = ResolveResolution(orientation, width, height, cfgConfig);

        // 正向提示词 = 固定前缀 + AI 提示词，合并后自动去重；分隔符强制英文逗号+空格 ", "
        var finalPositive = BuildPositivePrompt(prompt, cfgConfig.PositivePromptPrefix);
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
        using var hardCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(pollTimeoutSec + 30)); // 比轮询多 30s 余量（上传/下载）
        var ct = hardCts.Token;

        try
        {
            if (priorityMode)
                Poke($"优先生图进行中（最长约 {pollTimeoutSec} 秒），请稍候，期间不要语音…");
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
                    Poke($"输入图片不存在: {imagePath}");
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
                Poke("请先在配置中填写工作流 JSON 路径");
                LogError("工作流路径为空，请在配置 UI 中设置 WorkflowPath");
                return;
            }
            if (!File.Exists(workflowPath))
            {
                Poke($"工作流文件不存在: {workflowPath}");
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

            // 4) 转 API
            var apiPrompt = ComfyuiWorkflowConverter.ToApiPrompt(root, objectInfo);
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
                            Poke($"图片已生成\n{output.FullName}");
                            return;
                        }
                        var ext = string.IsNullOrWhiteSpace(output.Extension) ? ".png" : output.Extension;
                        var dest = Path.Combine(
                            saveDir,
                            $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}");
                        File.Copy(output.FullName, dest, false);
                        Log($"从自定义目录复制: {output.FullName} -> {dest}");
                        Poke($"图片已生成\n{dest}");
                        return;
                    }
                }

                LogError("history 中未找到图片输出");
                Poke("生图完成但未找到输出图片，请检查工作流是否包含 SaveImage 节点，或查看 ComfyUI 控制台错误");
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
                Poke("图片下载失败");
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

            Poke($"图片已生成（{saved.Count} 张）\n{string.Join("\n", saved)}");
        }
        catch (OperationCanceledException)
        {
            // 含 TaskCanceledException：硬超时 / 轮询超时，必须明确结束，避免桌宠一直等
            LogWarn($"生图超时（{pollTimeoutSec}s{(priorityMode ? "，优先生图硬上限" : "")}）");
            Poke(priorityMode
                ? $"优先生图超时（{pollTimeoutSec} 秒），已结束等待。可稍后重试或检查 ComfyUI；现在可以正常说话。"
                : "生图超时，请检查 ComfyUI 是否在跑图，或增大 TimeoutSeconds。若同机开了语音优先，进程也可能被 TTS 结束，请重启 ComfyUI");
        }
        catch (HttpRequestException ex)
        {
            LogError($"网络错误: {ex.Message}");
            Poke($"无法连接 ComfyUI: {ex.Message}\n请确认地址 {cfgConfig.BaseUrl} 可访问；若同机语音优先曾腾 GPU，请重启 ComfyUI");
        }
        catch (Exception ex)
        {
            LogError($"生图失败: {ex.Message}");
            Poke($"生图失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeGens);
            // 清理图生图下载的临时文件
            if (tempImageFile != null && tempImageFile != imagePath && File.Exists(tempImageFile))
            {
                try { File.Delete(tempImageFile); } catch { }
            }
        }
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
            throw new Exception($"ComfyUI 拒绝工作流: {node["error"]}");
        if (node["node_errors"] is JsonObject ne && ne.Count > 0)
            throw new Exception($"节点错误: {ne.ToJsonString()}");
        var promptId = node["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(promptId))
            throw new Exception($"ComfyUI 提交响应缺少 prompt_id: {raw}");
        return promptId;
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
                if (!ct.Contains("SaveImage", StringComparison.OrdinalIgnoreCase)
                    && !ct.Contains("保存", StringComparison.OrdinalIgnoreCase))
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

    static List<string> TryFindCustomSavePaths(JsonObject prompt)
    {
        var dirs = new List<string>();
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (!ct.Contains("SaveImage", StringComparison.OrdinalIgnoreCase)
                && !ct.Contains("保存", StringComparison.OrdinalIgnoreCase))
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
                    o["n"]?.GetValue<string>() ?? o["name"]?.GetValue<string>() ?? "",
                    o["p"]?.GetValue<string>() ?? o["path"]?.GetValue<string>() ?? "",
                    o["e"]?.GetValue<bool>() ?? true))
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
