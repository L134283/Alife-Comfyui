using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// 将 ComfyUI UI 工作流 JSON 转为 /prompt 可用的 API 格式。
/// 也兼容已经是 API 格式的 JSON。
/// </summary>
public static class ComfyuiWorkflowConverter
{
    static readonly HashSet<string> ControlAfterGenerate =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "fixed", "increment", "decrement", "randomize"
        };

    static readonly HashSet<string> SkipNodeTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Note", "Reroute", "PrimitiveNode",
            "Anything Everywhere", "Anything Everywhere3",
            "Anything Everywhere?", "Prompts Everywhere",
            "Seed Everywhere", "Simple String"
        };

    static readonly HashSet<string> ConnectionTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "MODEL", "CLIP", "VAE", "CONDITIONING", "LATENT", "IMAGE", "MASK",
            "CONTROL_NET", "STYLE_MODEL", "CLIP_VISION", "GLIGEN",
            "UPSCALE_MODEL", "AUDIO", "WEBCAM", "PHOTOMAKER", "*"
        };

    public static bool IsApiFormat(JsonNode root)
    {
        if (root is not JsonObject obj)
            return false;

        if (obj["nodes"] is JsonArray)
            return false;

        foreach (var kv in obj)
        {
            if (!int.TryParse(kv.Key, out _))
                continue;
            if (kv.Value is JsonObject node && node["class_type"] != null)
                return true;
        }
        return false;
    }

    public static JsonObject ToApiPrompt(JsonNode root, JsonObject? objectInfo = null)
    {
        if (IsApiFormat(root))
            return (JsonObject)root.DeepClone();

        if (root is not JsonObject ui)
            throw new Exception("工作流 JSON 格式无效");

        var nodes = ui["nodes"] as JsonArray
            ?? throw new Exception("UI 工作流缺少 nodes 数组");
        var linksArr = ui["links"] as JsonArray ?? new JsonArray();

        var linkMap = new Dictionary<int, (int srcNode, int srcSlot)>();
        foreach (var linkNode in linksArr)
        {
            if (linkNode is not JsonArray link || link.Count < 5)
                continue;
            var linkId = link[0]!.GetValue<int>();
            var srcNode = link[1]!.GetValue<int>();
            var srcSlot = link[2]!.GetValue<int>();
            linkMap[linkId] = (srcNode, srcSlot);
        }

        var ueMap = new Dictionary<(int node, int slot), (int src, int srcSlot)>();
        if (ui["extra"]?["ue_links"] is JsonArray ueLinks)
        {
            foreach (var ue in ueLinks.OfType<JsonObject>())
            {
                var down = ue["downstream"]?.GetValue<int>() ?? -1;
                var downSlot = ue["downstream_slot"]?.GetValue<int>() ?? -1;
                var upStr = ue["upstream"]?.ToString();
                var upSlot = ue["upstream_slot"]?.GetValue<int>() ?? 0;
                if (down < 0 || downSlot < 0 || string.IsNullOrWhiteSpace(upStr))
                    continue;
                if (!int.TryParse(upStr, out var up))
                    continue;
                ueMap[(down, downSlot)] = (up, upSlot);
            }
        }

        var prompt = new JsonObject();

        foreach (var nodeNode in nodes.OfType<JsonObject>())
        {
            var id = nodeNode["id"]?.GetValue<int>() ?? -1;
            if (id < 0) continue;

            var mode = nodeNode["mode"]?.GetValue<int>() ?? 0;
            if (mode is 2 or 4) continue;

            var classType = nodeNode["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(classType))
                continue;
            if (SkipNodeTypes.Contains(classType))
                continue;
            if (classType.StartsWith("markdown", StringComparison.OrdinalIgnoreCase))
                continue;

            var inputsObj = new JsonObject();
            var nodeInputs = nodeNode["inputs"] as JsonArray ?? new JsonArray();
            var widgets = nodeNode["widgets_values"] as JsonArray ?? new JsonArray();

            var widgetInputNames = new List<string>();
            for (int i = 0; i < nodeInputs.Count; i++)
            {
                if (nodeInputs[i] is not JsonObject inp)
                    continue;
                var name = inp["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (inp["widget"] != null)
                    widgetInputNames.Add(name);

                if (inp["link"] is JsonValue linkVal)
                {
                    int linkId;
                    try { linkId = linkVal.GetValue<int>(); }
                    catch { continue; }
                    if (linkMap.TryGetValue(linkId, out var src))
                        inputsObj[name] = new JsonArray { src.srcNode.ToString(), src.srcSlot };
                }
            }

            // UE 隐式连线
            foreach (var (key, src) in ueMap)
            {
                if (key.node != id) continue;
                if (key.slot < 0 || key.slot >= nodeInputs.Count) continue;
                if (nodeInputs[key.slot] is not JsonObject slotInp) continue;
                var slotName = slotInp["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(slotName)) continue;
                if (inputsObj.ContainsKey(slotName)) continue;
                inputsObj[slotName] = new JsonArray { src.src.ToString(), src.srcSlot };
            }

            ApplyWidgets(classType, widgetInputNames, widgets, inputsObj, objectInfo);
            ApplySpecialNodeInputs(classType, nodeNode, widgets, inputsObj);

            prompt[id.ToString()] = new JsonObject
            {
                ["class_type"] = classType,
                ["inputs"] = inputsObj
            };
        }

        return prompt;
    }

    static void ApplyWidgets(
        string classType,
        List<string> widgetInputNames,
        JsonArray widgets,
        JsonObject inputsObj,
        JsonObject? objectInfo)
    {
        var ordered = ResolveWidgetOrder(classType, widgetInputNames, objectInfo);
        int wi = 0;

        foreach (var name in ordered)
        {
            if (inputsObj.ContainsKey(name))
                continue;
            if (wi >= widgets.Count)
                break;

            var val = widgets[wi];
            if (val is JsonValue jvCtrl && jvCtrl.TryGetValue<string>(out var ctrl)
                && ControlAfterGenerate.Contains(ctrl))
            {
                wi++;
                if (wi >= widgets.Count) break;
                val = widgets[wi];
            }

            wi++;

            if (val == null || (val is JsonValue jn && jn.GetValueKind() == JsonValueKind.Null))
                continue;
            if (IsLikelyUiOnlyTokenJson(val))
                continue;

            inputsObj[name] = val.DeepClone();
        }
    }

    static List<string> ResolveWidgetOrder(string classType, List<string> fallback, JsonObject? objectInfo)
    {
        if (objectInfo == null || objectInfo[classType] is not JsonObject info)
            return fallback;

        var order = new List<string>();
        var inputOrder = info["input_order"] as JsonObject;
        var required = inputOrder?["required"] as JsonArray;
        var optional = inputOrder?["optional"] as JsonArray;

        void AddFrom(JsonArray? arr)
        {
            if (arr == null) return;
            foreach (var n in arr)
            {
                var name = n?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsConnectionOnlyInput(info, name)) continue;
                order.Add(name);
            }
        }

        AddFrom(required);
        AddFrom(optional);

        if (order.Count == 0)
            return fallback;

        var set = new HashSet<string>(order, StringComparer.Ordinal);
        foreach (var f in fallback)
        {
            if (!set.Contains(f) && !IsConnectionOnlyInput(info, f))
                order.Add(f);
        }
        return order;
    }

    static bool IsConnectionOnlyInput(JsonObject nodeInfo, string name)
    {
        var input = nodeInfo["input"] as JsonObject;
        JsonNode? def = null;
        if (input?["required"] is JsonObject req && req[name] != null)
            def = req[name];
        else if (input?["optional"] is JsonObject opt && opt[name] != null)
            def = opt[name];
        else if (input?["hidden"] is JsonObject hid && hid[name] != null)
            return true;

        if (def is not JsonArray arr || arr.Count == 0)
            return false;

        // combo 选项数组 => widget
        if (arr[0] is JsonArray)
            return false;

        if (arr[0] is JsonValue typeVal && typeVal.TryGetValue<string>(out var typeName))
        {
            if (typeName is "INT" or "FLOAT" or "STRING" or "BOOLEAN")
                return false;
            if (ConnectionTypes.Contains(typeName))
                return true;
            // 全大写自定义类型通常是连线
            if (typeName == typeName.ToUpperInvariant() && typeName.Length > 1)
                return true;
        }
        return false;
    }

    static bool IsLikelyUiOnlyTokenJson(JsonNode? val)
    {
        if (val is not JsonValue jv || !jv.TryGetValue<string>(out var s))
            return false;
        s = s.TrimStart();
        return s.StartsWith("[{\"id\"", StringComparison.Ordinal)
               || s.StartsWith("[{\"id\":", StringComparison.Ordinal);
    }

    static void ApplySpecialNodeInputs(
        string classType,
        JsonObject nodeNode,
        JsonArray widgets,
        JsonObject inputsObj)
    {
        if (classType.Equals("ZmlPowerLoraLoader", StringComparison.OrdinalIgnoreCase))
        {
            if (!inputsObj.ContainsKey("lora_loader_data"))
            {
                string? data = null;
                if (nodeNode["properties"]?["powerLoraLoader_data"] != null)
                    data = nodeNode["properties"]!["powerLoraLoader_data"]!.ToJsonString();
                else if (widgets.Count > 0 && widgets[0] != null)
                {
                    data = widgets[0] is JsonValue v && v.TryGetValue<string>(out var s)
                        ? s
                        : widgets[0]!.ToJsonString();
                }

                if (!string.IsNullOrWhiteSpace(data))
                    inputsObj["lora_loader_data"] = data;
            }
            return;
        }

        if (classType.Equals("WeiLinPromptUI", StringComparison.OrdinalIgnoreCase))
        {
            if (!inputsObj.ContainsKey("positive") && widgets.Count > 0)
            {
                var p = widgets[0];
                if (p != null && !IsLikelyUiOnlyTokenJson(p))
                    inputsObj["positive"] = p.DeepClone();
            }
            if (!inputsObj.ContainsKey("auto_random") && widgets.Count > 1 && widgets[1] != null)
                inputsObj["auto_random"] = widgets[1]!.DeepClone();
        }
    }

    public static void ApplyRuntimeOverrides(
        JsonObject prompt,
        string positivePrompt,
        string? negativePrompt,
        int? width,
        int? height,
        bool randomizeSeed,
        string? positiveNodeId,
        string positiveInputName,
        string? negativeNodeId,
        string negativeInputName,
        string? resolutionNodeId)
    {
        // 优先用采样器连接关系识别正/负面节点（通用，兼容原生 CLIPTextEncode 与第三方节点）；
        // 连接关系未命中再回退启发式（WeiLinPromptUI 文本最长者 / CLIPTextEncode）。
        var (autoPosId, autoNegId) = FindPositiveAndNegativeIds(prompt);

        var posId = string.IsNullOrWhiteSpace(positiveNodeId) ? autoPosId : positiveNodeId;

        if (!string.IsNullOrWhiteSpace(posId) && prompt[posId] is JsonObject posNode)
        {
            var inputs = posNode["inputs"] as JsonObject ?? new JsonObject();
            posNode["inputs"] = inputs;
            var field = ResolvePromptField(inputs, positiveInputName);
            inputs[field] = positivePrompt;
        }
        else
        {
            throw new Exception("未找到正向提示词节点，请在配置中指定 PositivePromptNodeId，或点击 UI 的「自动识别节点」");
        }

        // 负面提示词（可选）。留空则保留工作流自带的负面
        if (!string.IsNullOrWhiteSpace(negativePrompt))
        {
            var negId = string.IsNullOrWhiteSpace(negativeNodeId) ? autoNegId : negativeNodeId;

            if (!string.IsNullOrWhiteSpace(negId) && prompt[negId] is JsonObject negNode)
            {
                var inputs = negNode["inputs"] as JsonObject ?? new JsonObject();
                negNode["inputs"] = inputs;
                var field = ResolvePromptField(inputs, negativeInputName);
                inputs[field] = negativePrompt;
            }
        }

        if (width.HasValue || height.HasValue)
        {
            var resId = resolutionNodeId;
            if (string.IsNullOrWhiteSpace(resId))
                resId = FindResolutionNodeId(prompt);

            if (!string.IsNullOrWhiteSpace(resId) && prompt[resId] is JsonObject resNode)
            {
                var inputs = resNode["inputs"] as JsonObject ?? new JsonObject();
                resNode["inputs"] = inputs;
                var classType = resNode["class_type"]?.GetValue<string>() ?? "";

                if (classType.Contains("PresetResolution", StringComparison.OrdinalIgnoreCase)
                    || classType.Equals("ZML_PresetResolutionV2", StringComparison.OrdinalIgnoreCase))
                {
                    inputs["预设"] = "自定义";
                    if (width.HasValue) inputs["自定义宽"] = width.Value;
                    if (height.HasValue) inputs["自定义高"] = height.Value;
                }
                else if (classType.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase))
                {
                    if (width.HasValue) inputs["width"] = width.Value;
                    if (height.HasValue) inputs["height"] = height.Value;
                }
                else
                {
                    if (width.HasValue)
                    {
                        if (inputs.ContainsKey("width")) inputs["width"] = width.Value;
                        else if (inputs.ContainsKey("自定义宽")) inputs["自定义宽"] = width.Value;
                    }
                    if (height.HasValue)
                    {
                        if (inputs.ContainsKey("height")) inputs["height"] = height.Value;
                        else if (inputs.ContainsKey("自定义高")) inputs["自定义高"] = height.Value;
                    }
                }
            }
        }

        if (randomizeSeed)
        {
            foreach (var kv in prompt)
            {
                if (kv.Value is not JsonObject node) continue;
                if (node["inputs"] is not JsonObject inputs) continue;
                if (inputs.ContainsKey("seed"))
                    inputs["seed"] = Random.Shared.NextInt64(0, long.MaxValue);
                if (inputs.ContainsKey("noise_seed"))
                    inputs["noise_seed"] = Random.Shared.NextInt64(0, long.MaxValue);
            }
        }
    }

    static readonly HashSet<string> SamplerTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "KSampler", "KSamplerAdvanced", "KSampler (Efficient)",
            "SamplerCustom", "SamplerCustomAdvanced", "KSampler Turbo",
            "KSampler Select", "KSampler (Advanced)"
        };

    static bool IsSamplerType(string ct)
        => SamplerTypes.Contains(ct) || ct.Contains("Sampler", StringComparison.OrdinalIgnoreCase);

    static bool IsPromptTextNode(string ct)
        => ct.Contains("TextEncode", StringComparison.OrdinalIgnoreCase)
           || ct.Contains("PromptUI", StringComparison.OrdinalIgnoreCase)
           || ct.Contains("PromptText", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 通过采样器 positive/negative 输入的连接关系识别正/负面提示词节点。
    /// 对原生 ComfyUI 工作流（CLIPTextEncode）与第三方节点（WeiLinPromptUI 等）均通用。
    /// 连接关系未命中时回退到启发式。
    /// </summary>
    public static (string? positiveId, string? negativeId) FindPositiveAndNegativeIds(JsonObject prompt)
    {
        string? posId = null, negId = null;

        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (!IsSamplerType(ct)) continue;
            var inputs = node["inputs"] as JsonObject;
            if (inputs == null) continue;

            if (posId == null && inputs["positive"] is JsonArray pArr && pArr.Count > 0)
                posId = pArr[0]?.GetValue<string>();
            if (negId == null && inputs["negative"] is JsonArray nArr && nArr.Count > 0)
                negId = nArr[0]?.GetValue<string>();
        }

        // 采样器直连的节点可能不是文本节点（如 ConditionCombine），沿 CONDITIONING 链追溯一层
        posId = TraceToPromptNode(prompt, posId);
        negId = TraceToPromptNode(prompt, negId);

        // 连接关系未命中，回退启发式
        posId ??= FindPositiveNodeId(prompt);
        negId ??= FindNegativeNodeId(prompt, posId);
        return (posId, negId);
    }

    static string? TraceToPromptNode(JsonObject prompt, string? startId)
    {
        if (string.IsNullOrWhiteSpace(startId)) return null;
        if (prompt[startId] is not JsonObject node) return startId;
        var ct = node["class_type"]?.GetValue<string>() ?? "";
        if (IsPromptTextNode(ct)) return startId;

        // 沿 conditioning/clip/text 输入追溯
        var inputs = node["inputs"] as JsonObject;
        if (inputs == null) return startId;
        foreach (var ikv in inputs)
        {
            if (ikv.Value is JsonArray arr && arr.Count > 0
                && arr[0]?.GetValue<string>() is string linkedId
                && prompt[linkedId] is JsonObject linked
                && IsPromptTextNode(linked["class_type"]?.GetValue<string>() ?? ""))
            {
                return linkedId;
            }
        }
        return startId;
    }

    /// <summary>
    /// 解析提示词文本字段名。配置留空时按节点 inputs 自动选择：
    /// 优先 text（CLIPTextEncode），其次 positive（WeiLinPromptUI），再取第一个字符串字段。
    /// </summary>
    public static string ResolvePromptField(JsonObject inputs, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName) && inputs.ContainsKey(configuredName))
            return configuredName;

        if (inputs.ContainsKey("text")) return "text";
        if (inputs.ContainsKey("positive")) return "positive";
        foreach (var kv in inputs)
        {
            if (kv.Value is JsonValue v && v.TryGetValue<string>(out _))
                return kv.Key;
        }
        return !string.IsNullOrWhiteSpace(configuredName) ? configuredName : "positive";
    }

    public static string? FindPositiveNodeId(JsonObject prompt)
    {
        string? bestWeiLin = null;
        int bestLen = -1;
        string? clipEncode = null;

        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            var inputs = node["inputs"] as JsonObject;

            if (ct.Equals("WeiLinPromptUI", StringComparison.OrdinalIgnoreCase))
            {
                var text = inputs?["positive"]?.ToString() ?? "";
                if (text.Length > bestLen)
                {
                    bestLen = text.Length;
                    bestWeiLin = kv.Key;
                }
            }
            else if (ct.Equals("CLIPTextEncode", StringComparison.OrdinalIgnoreCase) && clipEncode == null)
            {
                clipEncode = kv.Key;
            }
        }

        return bestWeiLin ?? clipEncode;
    }

    /// <summary>
    /// 找负面提示词节点：优先正向节点之外的另一个 WeiLinPromptUI（文本最短者），
    /// 其次是另一个 CLIPTextEncode。
    /// </summary>
    public static string? FindNegativeNodeId(JsonObject prompt, string? excludePosId)
    {
        string? bestWeiLin = null;
        int bestLen = int.MaxValue;
        string? clipEncode = null;

        foreach (var kv in prompt)
        {
            if (!string.IsNullOrEmpty(excludePosId) && kv.Key == excludePosId) continue;
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            var inputs = node["inputs"] as JsonObject;

            if (ct.Equals("WeiLinPromptUI", StringComparison.OrdinalIgnoreCase))
            {
                var text = inputs?["positive"]?.ToString() ?? "";
                if (text.Length < bestLen)
                {
                    bestLen = text.Length;
                    bestWeiLin = kv.Key;
                }
            }
            else if (ct.Equals("CLIPTextEncode", StringComparison.OrdinalIgnoreCase) && clipEncode == null)
            {
                clipEncode = kv.Key;
            }
        }

        return bestWeiLin ?? clipEncode;
    }

    public static string? FindResolutionNodeId(JsonObject prompt)
    {
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (ct.Contains("PresetResolution", StringComparison.OrdinalIgnoreCase)
                || ct.Equals("ZML_PresetResolutionV2", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }
}
