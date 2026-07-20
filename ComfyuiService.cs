using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Alife.Platform;

namespace Alife.Plugin.Comfyui;

[Module(
    "ComfyUI 生图",
    "连接 ComfyUI 执行工作流生图。支持 UI 工作流 JSON 自动转换，也可直接使用 API 格式。可配置固定提示词前缀与三档常用分辨率。",
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
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"
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

    record WorkflowEntry(string Name, string Path, bool Enabled);

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        functionService.RegisterHandler(new XmlHandler(this)
        {
            Description = "此服务通过 ComfyUI 本地/远程工作流生成图片。"
        });

        var cfg = Configuration ?? new ComfyuiConfig();
        var saveDir = ResolveSaveDir(cfg);
        var wf = ResolveWorkflowPath(cfg);

        // 解析命名工作流列表（仅启用的）
        var namedWorkflows = ParseNamedWorkflows(cfg.NamedWorkflows)
            .Where(w => w.Enabled)
            .ToList();
        var hasMultiWorkflow = namedWorkflows.Count > 0;

        var styleLabel = cfg.PromptStyle switch
        {
            "natural" => "纯自然语言",
            "hybrid"  => "混合模式",
            _         => "纯 Tag"
        };
        var styleGuide = cfg.PromptStyle switch
        {
            "natural" =>
                "短句束形式，每句含明确实体名词+动词。禁止复杂从句（\"的/着/了/与/和/并/而/且/于/对/从\"连接的长句）。" +
                "例：girl with pink hair wears uniform, stands in classroom.",

            "hybrid" =>
                "外貌/表情/服饰用逗号分隔的英文标签，动作/场景/氛围用自然语言追加在最后。" +
                "例：1girl, pink hair, school uniform, smile, standing in classroom, soft light.",

            _ =>
                "全小写英文标签，半角逗号分隔。禁止光线/光影标签（sunlight, warm lighting 等）。自然语言补充放所有标签最后。" +
                "例：1girl, pink hair, school uniform, standing, smile"
        };

            var autoOpenNote = cfg.AutoOpenImage
                ? "\n- 桌面端已开启自动打开图片，生图后图片会用系统查看器打开，无需AI再发图。"
                : "";

            // 优先生图：阻塞到出图结束，禁止同轮先语音
            var priorityNote = "";
            if (cfg.PriorityImageGen)
            {
                var hardCap = Math.Clamp(cfg.PriorityMaxWaitSeconds, 30, 1800);
                priorityNote = $"""

                【优先生图模式 · 已开启】
                - 调 GenerateImage 后必须等函数返回（成功/失败/超时）再继续，不要先长篇语音再调图。
                - 等图期间：禁止语音输出（不要 Speak / 不要朗读），可发极短文字说明「正在画」；结果返回后再描述。
                - 硬超时约 {hardCap} 秒，超时会返回失败，届时正常说话即可，不要空等或假装图已生成。
                """;
            }

            // 工作流列表描述
            var workflowListDesc = "";
            if (hasMultiWorkflow)
            {
                var names = namedWorkflows.Select(w => $"\"{w.Name}\"").ToList();
                workflowListDesc = $"\n\n【可用工作流】\n- {string.Join("\n- ", names)}\n" +
                    $"调用 GenerateImage 时传 workflow=\"工作流名\" 即可切换。不传则用默认工作流 \"{Path.GetFileNameWithoutExtension(wf)}\"。";
            }

            // 节点控制描述
            var nodeControlDesc = "";
            if (cfg.EnableNodeControl)
            {
                nodeControlDesc = "\n\n【高级节点控制已开启】\n" +
                    "GenerateImage 额外可选参数：\n" +
                    "- model: 更换大模型（如 meinamix_v11.safetensors）\n" +
                    "- steps: 采样步数（整数）\n" +
                    "- cfg: CFG Scale（如 7.0）\n" +
                    "- sampler: 采样器名（如 euler, dpmpp_2m, ddim）\n" +
                    "- scheduler: 调度器（如 normal, karras）\n" +
                    "- denoise: 降噪强度（0.0~1.0，图生图常用）\n" +
                    "- batch_size: 批次大小\n" +
                    "- nodeOverrides: 原始 JSON 直接控制任意节点。格式 {\"节点ID\":{\"参数名\":值}}\n" +
                    "不需要的参数请勿传入，保持工作流默认值即可。";

                // 尝试读取当前工作流的节点概览注入提示词
                try
                {
                    var wfPath = ResolveWorkflowPath(cfg);
                    if (!string.IsNullOrWhiteSpace(wfPath) && File.Exists(wfPath))
                    {
                        var rawJson = await File.ReadAllTextAsync(wfPath);
                        var root = JsonNode.Parse(rawJson);
                        if (root != null)
                        {
                            var apiPrompt = ComfyuiWorkflowConverter.ToApiPrompt(root, null);
                            var overview = ComfyuiWorkflowConverter.BuildNodeControlDescription(apiPrompt);
                            nodeControlDesc += "\n\n" + overview;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"无法生成节点概览: {ex.Message}");
                }
            }

            // denoise 智能选择指南（仅当工作流包含图生图节点时注入）
            var denoiseGuide = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(wf) && File.Exists(wf))
                {
                    var wfContent = await File.ReadAllTextAsync(wf);
                    if (wfContent.Contains("\"class_type\": \"LoadImage\""))
                    {
                        denoiseGuide = """
                        【降噪强度（denoise）】
                        - 仅图生图（传了 imagePath）时有效，文生图时不要传此参数。
                        - 控制原图保留程度：0.0=完全保留原图，1.0=完全重绘。
                        - 若用户未指定 denoise，AI 根据用户意图智能选择：
                          · 微调（换色/去瑕疵/修颜/换表情）：0.3 ~ 0.45
                          · 中等改动（改姿势/换装/换发型/加配件）：0.55 ~ 0.7
                          · 大幅改动（改构图/换背景/风格迁移）：0.7 ~ 0.85
                          · 几乎重绘（仅借原图轮廓参考）：0.85 ~ 1.0
                        """;
                    }
                }
            }
            catch { }

            var prefixNote = string.IsNullOrWhiteSpace(cfg.PositivePromptPrefix)
                ? "" : "\n- 固定提示词前缀会自动拼到正向提示词最前面。";
            var negNote = string.IsNullOrWhiteSpace(cfg.NegativePrompt)
                ? "负面提示词若无特殊需求不要填写，使用工作流默认即可。"
                : "固定负面提示词已配置，无需传此参数。";

            Prompt($$"""
            此服务通过 ComfyUI 生成图片。
            - 调用 GenerateImage(prompt, orientation?, imagePath?, width?, height?, denoise?{{(cfg.EnableNodeControl ? ", workflow?, steps?, cfg?, sampler?, scheduler?, model?, batch_size?, nodeOverrides?" : "")}})，prompt 必填。
            - 尺寸：portrait={{cfg.PortraitWidth}}×{{cfg.PortraitHeight}} / landscape={{cfg.LandscapeWidth}}×{{cfg.LandscapeHeight}} / square={{cfg.SquareWidth}}×{{cfg.SquareHeight}}。默认{{cfg.DefaultOrientation}}，传 width/height 覆盖。{{prefixNote}}
            - imagePath：本地路径/图片URL→自动下载上传图生图。不传=文生图。QQ链接可原样传入。
            - 图生图时提示词用下方【提示词格式：{{styleLabel}}】规则。不要传中文指令描述动作！应直接生成目标画面的英文提示词描述。
            - 地址 {{cfg.BaseUrl}} | 工作流 {{wf}} | 保存 {{saveDir}}
            - QQ环境用 <qimage image="完整路径" /> 发图。{{autoOpenNote}}{{priorityNote}}

            【提示词格式：{{styleLabel}}】
            {{styleGuide}}

            {{denoiseGuide}}

            {{negNote}}{{workflowListDesc}}{{nodeControlDesc}}
            """);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("使用 ComfyUI 工作流生成图片。传入正向提示词（必填）；可选 orientation(portrait/landscape/square) 或 width/height；可选 imagePath 进行图生图。固定正向提示词前缀会自动拼接，无需手动传。高级模式开启后支持 workflow/model/steps/cfg 等额外参数。开启「优先生图」时会等待出图结束再返回。")]
    public async Task GenerateImage(
        [Description("正向提示词，描述画面内容（固定前缀会自动拼在最前）")] string prompt,
        [Description("图片方向：portrait=竖版832x1216, landscape=横版1216x832, square=正方形1216x1216。不传则用配置默认方向")] string? orientation = null,
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
        [Description("【高级】批次大小，覆盖 EmptyLatentImage 中的 batch_size")] int? batchSize = null,
        [Description("【高级】原始 JSON 节点覆盖，格式 {\"节点ID\":{\"参数名\":值}}，用于控制上述参数以外的任意节点")] string? nodeOverrides = null)
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

                await GenerateImageAsync(
                    prompt, orientation, imagePath, width, height,
                    workflow, steps, cfg, sampler, scheduler, model, denoise, batchSize, nodeOverrides,
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
            _ = GenerateImageAsync(
                prompt, orientation, imagePath, width, height,
                workflow, steps, cfg, sampler, scheduler, model, denoise, batchSize, nodeOverrides,
                priorityMode: false);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查 ComfyUI 是否在线，并返回系统状态摘要")]
    public void CheckComfyuiStatus()
    {
        _ = CheckStatusAsync();
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

    async Task GenerateImageAsync(string prompt, string? orientation, string? imagePath,
        int? width, int? height,
        string? workflow, int? steps, double? cfg, string? sampler, string? scheduler,
        string? model, double? denoise, int? batchSize, string? nodeOverrides,
        bool priorityMode = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Poke("提示词不能为空");
            return;
        }

        var cfgConfig = Configuration ?? new ComfyuiConfig();
        var baseUrl = NormalizeBaseUrl(cfgConfig.BaseUrl);
        var saveDir = ResolveSaveDir(cfgConfig);
        Directory.CreateDirectory(saveDir);

        var (w, h) = ResolveResolution(orientation, width, height, cfgConfig);

        // 正向提示词 = 固定前缀（保留内部换行） + 换行 + 用户提示词
        var finalPositive = BuildPositivePrompt(prompt, cfgConfig.PositivePromptPrefix);

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
                    || imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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

            if (!string.IsNullOrWhiteSpace(workflow))
                Log($"切换工作流: {workflow} -> {Path.GetFileName(workflowPath)}");

            var rawJson = await File.ReadAllTextAsync(workflowPath, ct);
            var root = JsonNode.Parse(rawJson)
                ?? throw new Exception("工作流 JSON 解析失败");

            // 3) 可选拉取 object_info 辅助转换
            JsonObject? objectInfo = null;
            try
            {
                objectInfo = await FetchObjectInfoAsync(baseUrl, cfgConfig);
            }
            catch (Exception ex)
            {
                LogWarn($"获取 object_info 失败，将用本地规则转换: {ex.Message}");
            }

            // 4) 转 API
            var apiPrompt = ComfyuiWorkflowConverter.ToApiPrompt(root, objectInfo);
            Log($"工作流节点数: {apiPrompt.Count} ({(ComfyuiWorkflowConverter.IsApiFormat(root) ? "API格式" : "UI转API")})");

            // 5) 注入基本参数（原有简单模式不变）
            ComfyuiWorkflowConverter.ApplyRuntimeOverrides(
                apiPrompt,
                finalPositive,
                cfgConfig.NegativePrompt,
                w,
                h,
                cfgConfig.RandomizeSeed,
                string.IsNullOrWhiteSpace(cfgConfig.PositivePromptNodeId) ? null : cfgConfig.PositivePromptNodeId,
                string.IsNullOrWhiteSpace(cfgConfig.PositivePromptInput) ? "positive" : cfgConfig.PositivePromptInput,
                string.IsNullOrWhiteSpace(cfgConfig.NegativePromptNodeId) ? null : cfgConfig.NegativePromptNodeId,
                string.IsNullOrWhiteSpace(cfgConfig.NegativePromptInput) ? "positive" : cfgConfig.NegativePromptInput,
                string.IsNullOrWhiteSpace(cfgConfig.ResolutionNodeId) ? null : cfgConfig.ResolutionNodeId,
                isImg2Img: !string.IsNullOrWhiteSpace(uploadedImageName));

            // 5.5) 注入高级节点参数（如果启用了节点控制且有参数传入）
            var hasNodeOverrides = model != null || steps.HasValue || cfg.HasValue
                || sampler != null || scheduler != null || denoise.HasValue
                || batchSize.HasValue || !string.IsNullOrWhiteSpace(nodeOverrides);
            if (hasNodeOverrides)
            {
                Log("注入高级节点参数...");
                ComfyuiWorkflowConverter.ApplyNodeOverrides(
                    apiPrompt, model, steps, cfg, sampler, scheduler,
                    denoise, batchSize, nodeOverrides);
            }

            // 5.6) 图生图：注入已上传的图片文件名到 LoadImage 节点
            if (!string.IsNullOrWhiteSpace(uploadedImageName))
            {
                var loadImageNodeId = string.IsNullOrWhiteSpace(cfgConfig.LoadImageNodeId)
                    ? null : cfgConfig.LoadImageNodeId;
                var loadImageInput = string.IsNullOrWhiteSpace(cfgConfig.LoadImageInput)
                    ? "image" : cfgConfig.LoadImageInput;

                ComfyuiWorkflowConverter.InjectLoadImage(
                    apiPrompt, loadImageNodeId, loadImageInput, uploadedImageName);
                Log($"注入图片 {uploadedImageName} 到 LoadImage 节点");
            }

            // 6) 提交
            ct.ThrowIfCancellationRequested();
            var clientId = Guid.NewGuid().ToString("N");
            var promptId = Guid.NewGuid().ToString("N");
            await QueuePromptAsync(baseUrl, cfgConfig, apiPrompt, clientId, promptId);
            Log($"已提交 prompt_id={promptId}");

            // 7) 轮询 history（优先生图用硬超时上限）
            var timeout = TimeSpan.FromSeconds(pollTimeoutSec);
            var interval = Math.Clamp(cfgConfig.PollIntervalMs, 500, 10000);
            var history = await WaitHistoryAsync(baseUrl, cfgConfig, promptId, timeout, interval, ct);

            // 8) 取图
            var images = ExtractImages(history, promptId);
            if (images.Count == 0)
            {
                // 可能保存到自定义路径（ZML_SaveImageV2），history 无 images
                var customPath = TryFindCustomSavePath(apiPrompt);
                if (!string.IsNullOrWhiteSpace(customPath) && Directory.Exists(customPath))
                {
                    var latest = new DirectoryInfo(customPath)
                        .GetFiles("*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.Extension is ".png" or ".jpg" or ".jpeg" or ".webp")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (latest != null && (DateTime.UtcNow - latest.LastWriteTimeUtc).TotalMinutes < 10)
                    {
                        var dest = Path.Combine(saveDir, $"comfyui_{DateTime.Now:yyyyMMddHHmmss}_{latest.Name}");
                        File.Copy(latest.FullName, dest, true);
                        Log($"从自定义目录复制: {latest.FullName} -> {dest}");
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
            // 提前算一次自定义目录，下载失败时按 filename 回退本地读取。
            var customDir = TryFindCustomSavePath(apiPrompt);

            var saved = new List<string>();
            foreach (var img in images)
            {
                byte[]? bytes = null;
                try
                {
                    bytes = await ViewImageAsync(baseUrl, cfgConfig, img.Filename, img.Subfolder, img.Type);
                }
                catch (Exception ex)
                {
                    LogWarn($"View 异常 {img.Filename}: {ex.Message}");
                }

                // /view 失败 → 回退到 ZML 自定义保存目录按 filename 找
                if ((bytes == null || bytes.Length == 0)
                    && !string.IsNullOrWhiteSpace(customDir) && Directory.Exists(customDir))
                {
                    var localFile = Path.Combine(customDir, img.Filename);
                    if (File.Exists(localFile))
                    {
                        try
                        {
                            bytes = await File.ReadAllBytesAsync(localFile, ct);
                            Log($"从自定义目录读取: {localFile}");
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"读取本地文件失败 {localFile}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // filename 含特殊字符或前缀不一致时，按扩展名 + 最近修改时间兜底匹配
                        var ext = Path.GetExtension(img.Filename);
                        var fallback = new DirectoryInfo(customDir)
                            .GetFiles("*" + ext, SearchOption.TopDirectoryOnly)
                            .Where(f => f.Name.StartsWith(Path.GetFileNameWithoutExtension(img.Filename), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .FirstOrDefault();
                        if (fallback != null && (DateTime.UtcNow - fallback.LastWriteTimeUtc).TotalMinutes < 10)
                        {
                            try
                            {
                                bytes = await File.ReadAllBytesAsync(fallback.FullName, ct);
                                Log($"按名称前缀匹配自定义目录: {fallback.FullName}");
                            }
                            catch (Exception ex)
                            {
                                LogWarn($"读取本地文件失败 {fallback.FullName}: {ex.Message}");
                            }
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0) continue;

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
                : "生图超时，请检查 ComfyUI 是否在跑图，或增大 TimeoutSeconds");
        }
        catch (HttpRequestException ex)
        {
            LogError($"网络错误: {ex.Message}");
            Poke($"无法连接 ComfyUI: {ex.Message}\n请确认地址 {cfgConfig.BaseUrl} 可访问");
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
        // data: URI → 直接解码到临时文件
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = url.IndexOf(',');
            if (commaIdx < 0) throw new Exception("无效的 data URI");
            var b64 = url[(commaIdx + 1)..];
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch
            {
                // 可能不是 Base64，尝试 URL 解码再 Base64
                bytes = Convert.FromBase64String(Uri.UnescapeDataString(b64));
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

        var data = await resp.Content.ReadAsByteArrayAsync();
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
        using var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var byteContent = new ByteArrayContent(fileBytes);
        var ext = Path.GetExtension(filePath);
        var mime = ext?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
        byteContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(byteContent, "image", Path.GetFileName(filePath));
        content.Add(new StringContent("true"), "overwrite");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/upload/image")
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(cfg.ApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken);

        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(raw) as JsonObject
            ?? throw new Exception($"上传图片响应格式异常: {raw}");
        var name = result["name"]?.GetValue<string>()
            ?? throw new Exception($"上传图片响应缺少 name 字段: {raw}");
        return name;
    }

    static string DetectImageExtension(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8) return ".jpg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
        if (data.Length >= 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ".gif";
        if (data.Length >= 4 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46) return ".webp";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return ".bmp";
        return ".png";
    }

    // ===================== HTTP =====================

    async Task<JsonObject?> FetchObjectInfoAsync(string baseUrl, ComfyuiConfig cfg)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/object_info", cfg);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        return JsonNode.Parse(raw) as JsonObject;
    }

    async Task QueuePromptAsync(string baseUrl, ComfyuiConfig cfg, JsonObject prompt, string clientId, string promptId)
    {
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["client_id"] = clientId,
            ["prompt_id"] = promptId
        };

        using var req = CreateRequest(HttpMethod.Post, $"{baseUrl}/prompt", cfg);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var preview = raw.Length > 500 ? raw[..500] + "..." : raw;
            throw new Exception($"提交工作流失败 (HTTP {(int)resp.StatusCode}): {preview}");
        }

        var node = JsonNode.Parse(raw);
        if (node?["error"] != null)
            throw new Exception($"ComfyUI 拒绝工作流: {node["error"]}");
        if (node?["node_errors"] is JsonObject ne && ne.Count > 0)
            throw new Exception($"节点错误: {ne.ToJsonString()}");
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

    async Task<byte[]?> ViewImageAsync(string baseUrl, ComfyuiConfig cfg, string filename, string subfolder, string type)
    {
        var qs = $"filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type={Uri.EscapeDataString(type ?? "output")}";
        using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/view?{qs}", cfg);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            LogWarn($"下载图片失败 {filename} HTTP {(int)resp.StatusCode}");
            return null;
        }
        return await resp.Content.ReadAsByteArrayAsync();
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

    static List<ImageRef> ExtractImages(JsonNode history, string promptId)
    {
        var list = new List<ImageRef>();
        var outputs = history[promptId]?["outputs"] as JsonObject;
        if (outputs == null) return list;

        foreach (var kv in outputs)
        {
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

    static string? TryFindCustomSavePath(JsonObject prompt)
    {
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
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (Directory.Exists(path))
                    return path;
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        return dir;
                }
                catch { }
            }
        }
        return null;
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
    /// 拼接固定正向提示词前缀。前缀内部换行原样保留，前缀与用户提示词之间以换行分隔。
    /// </summary>
    static string BuildPositivePrompt(string prompt, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return prompt;
        var p = prefix.TrimEnd();
        if (string.IsNullOrEmpty(p))
            return prompt;
        return p + "\n" + prompt;
    }

    static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "http://127.0.0.1:8188";
        return url.Trim().TrimEnd('/');
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
        // 如果传了 workflowName，从命名工作流列表中按名称查找
        if (!string.IsNullOrWhiteSpace(workflowName))
        {
            var allWorkflows = ParseNamedWorkflows(cfg.NamedWorkflows);
            var match = allWorkflows.Find(w =>
                w.Name.Equals(workflowName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                var resolved = ResolveSinglePath(cfg, match.Path);
                if (File.Exists(resolved))
                    return resolved;
                LogWarn($"已选工作流 \"{workflowName}\" 路径无效: {match.Path}");
            }
            else
            {
                LogWarn($"未找到名为 \"{workflowName}\" 的工作流，回退默认工作流");
            }
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

    static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
