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

    public ComfyuiConfig? Configuration { get; set; } = new();

    static void Log(string msg) => Console.WriteLine($"[ComfyUI] {msg}");
    static void LogWarn(string msg) => Console.WriteLine($"[ComfyUI][警告] {msg}");
    static void LogError(string msg) => Console.WriteLine($"[ComfyUI][错误] {msg}");

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
                "例：A girl with pink hair wears a uniform. She stands in a classroom and looks at the viewer.",

            "hybrid" =>
                "外貌/表情/服饰用逗号分隔的英文标签，动作/场景/氛围用自然语言追加在最后。" +
                "例：1girl, pink hair, green eyes, school uniform, smile, standing in a bright classroom, soft light coming through the window",

            _ =>
                "全小写英文标签，半角逗号分隔。禁止光线/光影标签（sunlight, warm lighting 等）。自然语言补充放所有标签最后。" +
                "例：1girl, pink hair, long hair, green eyes, school uniform, standing, smile"
        };

            var autoOpenNote = cfg.AutoOpenImage
                ? "\n- 桌面端已开启自动打开图片，生图后图片会用系统查看器打开，无需AI再发图。"
                : "";

            Prompt($$"""
            此服务通过 ComfyUI 生成图片。
            - 调用 GenerateImage(prompt, orientation?, width?, height?)，prompt 必填。
            - 分辨率：portrait=竖版832×1216 / landscape=横版1216×832 / square=正方形1216×1216。
            - 不传方向用默认{{cfg.DefaultOrientation}}；传 width/height 则覆盖。
            - 固定前缀会自动拼到 prompt 前面（若有）。
            - 地址 {{cfg.BaseUrl}} | 工作流 {{wf}} | 保存 {{saveDir}}
            - QQ环境生图完成用 <qimage image="完整路径" />。若当前在QQ聊天，生完图顺手发出去。{{autoOpenNote}}

            【提示词格式：{{styleLabel}}】
            {{styleGuide}}

            负面提示词若无特殊需求不要填写，使用工作流默认即可。
            """);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("使用 ComfyUI 工作流生成图片。传入正向提示词（必填）；可选 orientation(portrait/landscape/square) 或 width/height。固定正向提示词前缀会自动拼接，无需手动传。")]
    public void GenerateImage(
        [Description("正向提示词，描述画面内容（固定前缀会自动拼在最前）")] string prompt,
        [Description("图片方向：portrait=竖版832x1216, landscape=横版1216x832, square=正方形1216x1216。不传则用配置默认方向")] string? orientation = null,
        [Description("图片宽度，显式指定则覆盖 orientation")] int? width = null,
        [Description("图片高度，显式指定则覆盖 orientation")] int? height = null)
    {
        Poke("生图请求已发出，可以继续聊天");
        _ = GenerateImageAsync(prompt, orientation, width, height);
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

    async Task GenerateImageAsync(string prompt, string? orientation, int? width, int? height)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Poke("提示词不能为空");
            return;
        }

        var cfg = Configuration ?? new ComfyuiConfig();
        var baseUrl = NormalizeBaseUrl(cfg.BaseUrl);
        var saveDir = ResolveSaveDir(cfg);
        Directory.CreateDirectory(saveDir);

        var (w, h) = ResolveResolution(orientation, width, height, cfg);

        // 正向提示词 = 固定前缀（保留内部换行） + 换行 + 用户提示词
        var finalPositive = BuildPositivePrompt(prompt, cfg.PositivePromptPrefix);

        try
        {
            Log($"开始生图: {Truncate(finalPositive, 80)}");
            if (w.HasValue || h.HasValue)
                Log($"分辨率: {w ?? 0}x{h ?? 0}");

            // 1) 加载工作流
            var workflowPath = ResolveWorkflowPath(cfg);
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

            var rawJson = await File.ReadAllTextAsync(workflowPath);
            var root = JsonNode.Parse(rawJson)
                ?? throw new Exception("工作流 JSON 解析失败");

            // 2) 可选拉取 object_info 辅助转换
            JsonObject? objectInfo = null;
            try
            {
                objectInfo = await FetchObjectInfoAsync(baseUrl, cfg);
            }
            catch (Exception ex)
            {
                LogWarn($"获取 object_info 失败，将用本地规则转换: {ex.Message}");
            }

            // 3) 转 API
            var apiPrompt = ComfyuiWorkflowConverter.ToApiPrompt(root, objectInfo);
            Log($"工作流节点数: {apiPrompt.Count} ({(ComfyuiWorkflowConverter.IsApiFormat(root) ? "API格式" : "UI转API")})");

            // 4) 注入参数
            ComfyuiWorkflowConverter.ApplyRuntimeOverrides(
                apiPrompt,
                finalPositive,
                cfg.NegativePrompt,
                w,
                h,
                cfg.RandomizeSeed,
                string.IsNullOrWhiteSpace(cfg.PositivePromptNodeId) ? null : cfg.PositivePromptNodeId,
                string.IsNullOrWhiteSpace(cfg.PositivePromptInput) ? "positive" : cfg.PositivePromptInput,
                string.IsNullOrWhiteSpace(cfg.NegativePromptNodeId) ? null : cfg.NegativePromptNodeId,
                string.IsNullOrWhiteSpace(cfg.NegativePromptInput) ? "positive" : cfg.NegativePromptInput,
                string.IsNullOrWhiteSpace(cfg.ResolutionNodeId) ? null : cfg.ResolutionNodeId);

            // 5) 提交
            var clientId = Guid.NewGuid().ToString("N");
            var promptId = Guid.NewGuid().ToString("N");
            await QueuePromptAsync(baseUrl, cfg, apiPrompt, clientId, promptId);
            Log($"已提交 prompt_id={promptId}");

            // 6) 轮询 history
            var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 30, 1800));
            var interval = Math.Clamp(cfg.PollIntervalMs, 500, 10000);
            var history = await WaitHistoryAsync(baseUrl, cfg, promptId, timeout, interval);

            // 7) 取图
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
                    bytes = await ViewImageAsync(baseUrl, cfg, img.Filename, img.Subfolder, img.Type);
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
                            bytes = await File.ReadAllBytesAsync(localFile);
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
                                bytes = await File.ReadAllBytesAsync(fallback.FullName);
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
                await File.WriteAllBytesAsync(path, bytes);
                saved.Add(path);
                Log($"保存 ({bytes.Length / 1024.0:F0}KB) -> {fileName}");
            }

            if (saved.Count == 0)
            {
                Poke("图片下载失败");
                return;
            }

            // 桌面端自动打开图片
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

            Poke($"图片已生成（{saved.Count} 张）\n{string.Join("\n", saved)}");
        }
        catch (TaskCanceledException)
        {
            Poke("生图超时，请检查 ComfyUI 是否在跑图，或增大 TimeoutSeconds");
        }
        catch (HttpRequestException ex)
        {
            LogError($"网络错误: {ex.Message}");
            Poke($"无法连接 ComfyUI: {ex.Message}\n请确认地址 {cfg.BaseUrl} 可访问");
        }
        catch (Exception ex)
        {
            LogError($"生图失败: {ex.Message}");
            Poke($"生图失败: {ex.Message}");
        }
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

    async Task<JsonNode> WaitHistoryAsync(string baseUrl, ComfyuiConfig cfg, string promptId, TimeSpan timeout, int intervalMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var req = CreateRequest(HttpMethod.Get, $"{baseUrl}/history/{promptId}", cfg);
            using var resp = await Http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
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

            await Task.Delay(intervalMs);
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

    static readonly Dictionary<string, (int w, int h)> OrientationPresets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "portrait", (832, 1216) },
            { "landscape", (1216, 832) },
            { "square", (1216, 1216) },
            // 中文别名
            { "竖版", (832, 1216) },
            { "横版", (1216, 832) },
            { "正方形", (1216, 1216) },
            { "方", (1216, 1216) },
        };

    (int? w, int? h) ResolveResolution(string? orientation, int? width, int? height, ComfyuiConfig cfg)
    {
        int? w = width, h = height;

        // 优先级：显式 width/height > orientation 预设 > 配置默认方向 > DefaultWidth/Height 兜底
        if (!w.HasValue || !h.HasValue)
        {
            var ori = string.IsNullOrWhiteSpace(orientation) ? cfg.DefaultOrientation : orientation;
            if (!string.IsNullOrWhiteSpace(ori)
                && OrientationPresets.TryGetValue(ori.Trim(), out var p))
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
        var p = cfg.WorkflowPath?.Trim() ?? "";
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
