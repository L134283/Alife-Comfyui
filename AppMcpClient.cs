using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// ComfyUI-APP-MCP 的轻量 REST 客户端（不依赖 MCP 会话，直接走 ComfyUI 端口）。
///
/// 端点前缀：/mcp-server/api
///   GET  {api}/templates                          列出模板
///   GET  {api}/templates/{name}                   读取模板（inputs / outputs / workflow / api_prompt）
///   POST {api}/templates/{name}/execute           body {"params": {...}}
///   GET  {api}/templates/{name}/result/{promptId} 查询/继续等待结果
/// </summary>
internal static class AppMcpClient
{
    // 查询类请求短超时；执行类请求要等出图，给长超时
    static readonly HttpClient QueryHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly HttpClient RunHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>模板输入。<paramref name="Widget"/> 是它在节点上的真实控件名（如 lora_loader_data）。</summary>
    internal sealed record AppMcpInput(string Name, string Type, string Widget, string DefaultJson);

    internal sealed record AppMcpTemplate(
        string Name,
        string Title,
        string Description,
        IReadOnlyDictionary<string, AppMcpInput> Inputs,
        string RawJson)
    {
        public bool HasInput(string name)
            => !string.IsNullOrWhiteSpace(name) && Inputs.ContainsKey(name);
    }

    internal sealed record AppMcpResult(
        bool Success,
        string Status,
        string? Error,
        IReadOnlyList<string> ImageUrls,
        IReadOnlyList<string> Texts,
        string? PromptId,
        string RawJson);

    /// <summary>把用户填的地址归一成 APP-MCP 的 API 根（.../mcp-server/api）。</summary>
    public static string ResolveApiBase(string? configured, string comfyBaseUrl)
    {
        var raw = (configured ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0)
            raw = (comfyBaseUrl ?? "").Trim().TrimEnd('/') + "/app-mcp";

        // 去掉 MCP 入口后缀，换成 REST API 根
        if (raw.EndsWith("/app-mcp", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^"/app-mcp".Length];
        else if (raw.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^"/mcp".Length];

        if (raw.EndsWith("/mcp-server/api", StringComparison.OrdinalIgnoreCase))
            return raw;
        if (raw.EndsWith("/mcp-server", StringComparison.OrdinalIgnoreCase))
            return raw + "/api";
        return raw + "/mcp-server/api";
    }

    static HttpRequestMessage Create(HttpMethod method, string url, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return req;
    }

    static async Task<JsonNode?> SendAsync(
        HttpClient http, HttpMethod method, string url, string? token,
        HttpContent? body, CancellationToken ct)
    {
        using var req = Create(method, url, token);
        if (body != null) req.Content = body;
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"APP-MCP HTTP {(int)resp.StatusCode}: {Short(raw)}");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"APP-MCP 响应不是合法 JSON: {ex.Message}");
        }
    }

    // ===================== 模板查询 =====================

    public static async Task<List<AppMcpTemplate>> ListTemplatesAsync(
        string apiBase, string? token, CancellationToken ct = default)
    {
        var node = await SendAsync(QueryHttp, HttpMethod.Get, $"{apiBase}/templates", token, null, ct);
        var list = new List<AppMcpTemplate>();
        if (node?["templates"] is not JsonArray arr)
            return list;

        foreach (var item in arr.OfType<JsonObject>())
        {
            var name = item["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            list.Add(new AppMcpTemplate(
                name,
                item["title"]?.GetValue<string>() ?? "",
                "",
                new Dictionary<string, AppMcpInput>(StringComparer.Ordinal),
                item.ToJsonString()));
        }
        return list;
    }

    public static async Task<AppMcpTemplate?> GetTemplateAsync(
        string apiBase, string name, string? token, CancellationToken ct = default)
    {
        var node = await SendAsync(
            QueryHttp, HttpMethod.Get,
            $"{apiBase}/templates/{Uri.EscapeDataString(name)}", token, null, ct);
        return node is JsonObject obj ? ParseTemplate(obj) : null;
    }

    static AppMcpTemplate ParseTemplate(JsonObject obj)
    {
        var inputs = new Dictionary<string, AppMcpInput>(StringComparer.Ordinal);
        if (obj["inputs"] is JsonObject inObj)
        {
            foreach (var kv in inObj)
            {
                if (kv.Value is not JsonObject meta)
                {
                    inputs[kv.Key] = new AppMcpInput(kv.Key, "STRING", kv.Key, "");
                    continue;
                }
                var type = meta["type"]?.ToString() ?? "STRING";
                var widget = meta["widget"]?.ToString() ?? kv.Key;
                var def = meta["default"] is JsonNode d ? d.ToJsonString() : "";
                inputs[kv.Key] = new AppMcpInput(kv.Key, type, widget, def);
            }
        }
        return new AppMcpTemplate(
            obj["name"]?.GetValue<string>() ?? "",
            obj["title"]?.GetValue<string>() ?? "",
            obj["description"]?.GetValue<string>() ?? "",
            inputs,
            obj.ToJsonString());
    }

    /// <summary>从模板 JSON 里取出 ZML 强力 LoRA 加载器的 lora_loader_data（供提示词与 UI 共用）。</summary>
    public static string? ExtractZmlData(string? templateRawJson)
    {
        if (string.IsNullOrWhiteSpace(templateRawJson))
            return null;
        try
        {
            if (JsonNode.Parse(templateRawJson)?["workflow"]?["nodes"] is not JsonArray nodes)
                return null;

            foreach (var n in nodes.OfType<JsonObject>())
            {
                if (n["widgets_values_named"] is JsonObject named
                    && named["lora_loader_data"] is JsonValue nv
                    && nv.TryGetValue<string>(out var namedData)
                    && !string.IsNullOrWhiteSpace(namedData))
                    return namedData;

                if (n["widgets_values"] is JsonArray vals)
                {
                    foreach (var v in vals)
                    {
                        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String)
                            continue;
                        var s = jv.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(s)
                            && s.Contains("\"entries\"", StringComparison.Ordinal))
                            return s;
                    }
                }
            }
        }
        catch
        {
            // 模板结构异常时按「无 ZML」处理
        }
        return null;
    }

    /// <summary>列出模板内 ZML 加载器的组名（画风）。供 UI 展示与配置画风预设。</summary>
    public static List<string> ListTemplateZmlGroups(string? templateRawJson)
        => ComfyuiWorkflowConverter.ListZmlLoraGroups(ExtractZmlData(templateRawJson));

    /// <summary>连通性/模板列表测试。返回可读文本，供 UI 与 AI 共用。</summary>
    public static async Task<string> TestAsync(string apiBase, string? token, CancellationToken ct = default)
    {
        try
        {
            var templates = await ListTemplatesAsync(apiBase, token, ct);
            if (templates.Count == 0)
                return $"已连通，但没有可用模板。请在 ComfyUI 的 Settings → MCP Server → Templates 中创建或启用模板。\n{apiBase}";
            var names = string.Join("、", templates.Select(t =>
                string.IsNullOrWhiteSpace(t.Title) ? t.Name : $"{t.Name}（{t.Title}）"));
            return $"已连通（{templates.Count} 个模板）：{names}\n{apiBase}";
        }
        catch (OperationCanceledException)
        {
            return $"连接超时：{apiBase}";
        }
        catch (Exception ex)
        {
            return $"连接失败：{ex.Message}\n请确认 ComfyUI 正在运行且已安装 ComfyUI-APP-MCP（根地址 {apiBase}）";
        }
    }

    // ===================== 执行模板 =====================

    public static async Task<AppMcpResult> ExecuteTemplateAsync(
        string apiBase, string name, JsonObject parameters, string? token,
        int waitSeconds, CancellationToken ct = default)
    {
        var body = new JsonObject { ["params"] = parameters.DeepClone() }.ToJsonString();
        var url = $"{apiBase}/templates/{Uri.EscapeDataString(name)}/execute";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var node = await SendAsync(RunHttp, HttpMethod.Post, url, token, content, ct);
        return await ResolveResultAsync(apiBase, name, token, node, waitSeconds, ct);
    }

    static async Task<AppMcpResult> ResolveResultAsync(
        string apiBase, string name, string? token, JsonNode? node,
        int waitSeconds, CancellationToken ct)
    {
        if (node is not JsonObject obj)
            return Fail("APP-MCP 返回空响应", "");

        if (obj["error"] != null && obj["status"]?.ToString() != "timeout")
            return Fail(obj["error"]?.ToString() ?? "执行失败", obj.ToJsonString());

        var status = obj["status"]?.ToString() ?? "";
        var promptId = obj["prompt_id"]?.ToString() ?? obj["run_id"]?.ToString();

        // 完成：直接解析 outputs
        if (status == "completed")
            return ParseOutputs(obj, promptId);

        // 超时/排队/运行中：继续轮询结果端点（服务端超时不代表失败）
        if (string.IsNullOrWhiteSpace(promptId))
            return Fail(obj["error"]?.ToString() ?? "模板执行未返回 prompt_id", obj.ToJsonString());

        string? lastPollError = null;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 10, 1800));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1500, ct);

            JsonNode? polled;
            try
            {
                polled = await SendAsync(
                    QueryHttp, HttpMethod.Get,
                    $"{apiBase}/templates/{Uri.EscapeDataString(name)}/result/{Uri.EscapeDataString(promptId)}",
                    token, null, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastPollError = ex.Message;
                continue;
            }

            if (polled is not JsonObject pobj)
                continue;
            if (pobj["error"] != null)
                return Fail(pobj["error"]?.ToString() ?? "执行失败", pobj.ToJsonString());

            var pstatus = pobj["status"]?.ToString() ?? "";
            if (pstatus == "completed")
                return ParseOutputs(pobj, promptId);
        }

        return Fail($"等待出图超时（{waitSeconds} 秒）未返回结果，请检查 ComfyUI 队列"
                    + (string.IsNullOrWhiteSpace(lastPollError) ? "" : $"；轮询错误: {lastPollError}"),
                    obj.ToJsonString());
    }

    static AppMcpResult ParseOutputs(JsonObject obj, string? promptId)
    {
        var images = new List<string>();
        var texts = new List<string>();

        if (obj["outputs"] is JsonObject outputs)
        {
            foreach (var kv in outputs)
                CollectOutput(kv.Value, images, texts);
        }

        return new AppMcpResult(
            true, "completed", null,
            images.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            texts,
            promptId,
            obj.ToJsonString());
    }

    static void CollectOutput(JsonNode? node, List<string> images, List<string> texts)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                CollectOutput(item, images, texts);
            return;
        }
        if (node is not JsonObject o)
            return;

        // 列表包装：{type:"image_list", items:[...]}
        if (o["items"] is JsonArray items)
        {
            foreach (var item in items)
                CollectOutput(item, images, texts);
        }

        var type = o["type"]?.ToString() ?? "";
        var url = o["url"]?.ToString();
        if (!string.IsNullOrWhiteSpace(url)
            && (type.Equals("image", StringComparison.OrdinalIgnoreCase)
                || type.Equals("gif", StringComparison.OrdinalIgnoreCase)
                || type.Equals("video", StringComparison.OrdinalIgnoreCase)))
        {
            images.Add(url!);
        }

        if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            var value = o["value"]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                texts.Add(value!);
        }
    }

    static AppMcpResult Fail(string error, string raw)
        => new(false, "error", error, Array.Empty<string>(), Array.Empty<string>(), null, raw);

    static string Short(string s)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s[..300] + "...");
}
