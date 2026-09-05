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
            "Seed Everywhere", "Simple String",
            // GetNode/SetNode 是 KJNodes 的跨图引用辅助节点，
            // 变量名在 widgets_values[0]；在 ToApiPrompt 中已把指向 GetNode 的
            // 连接重定向到 SetNode 的源节点，故两者本身不进 API prompt。
            "GetNode", "SetNode"
        };

    static readonly HashSet<string> ConnectionTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "MODEL", "CLIP", "VAE", "CONDITIONING", "LATENT", "IMAGE", "MASK",
            "CONTROL_NET", "STYLE_MODEL", "CLIP_VISION", "GLIGEN",
            "UPSCALE_MODEL", "AUDIO", "WEBCAM", "PHOTOMAKER", "*"
        };

    public     static bool IsApiFormat(JsonNode root)
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

    /// <summary>
    /// 从 JsonNode 安全读取 int。部分新版本 ComfyUI / UE 插件导出的 UI 工作流会把
    /// 本应是数字的字段（如 extra.ue_links 的 downstream/downstream_slot）序列化成字符串，
    /// 直接 GetValue&lt;int&gt; 会抛「String 无法转换为 Int32」导致生图失败。
    /// 这里兼容数字、布尔与数字字符串；解析不了返回 false。
    /// </summary>
    static bool TryGetNodeInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue jv)
            return false;
        try
        {
            if (jv.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                value = jv.GetValue<int>();
                return true;
            }
        }
        catch
        {
            // 非整型数字等，走字符串解析
        }
        return jv.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out value);
    }

    /// <summary>
    /// UI 工作流转 API prompt。
    /// <paramref name="validate"/> 为 true 且提供 object_info 时，转换后做 required/combo 轻量校验。
    /// </summary>
    public static JsonObject ToApiPrompt(JsonNode root, JsonObject? objectInfo = null, bool validate = false)
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
            var linkId = TryGetNodeInt(link[0], out var lid) ? lid : -1;
            var srcNode = TryGetNodeInt(link[1], out var sn) ? sn : -1;
            var srcSlot = TryGetNodeInt(link[2], out var ss) ? ss : -1;
            if (linkId < 0 || srcNode < 0 || srcSlot < 0)
                continue;
            linkMap[linkId] = (srcNode, srcSlot);
        }

        var ueMap = new Dictionary<(int node, int slot), (int src, int srcSlot)>();
        if (ui["extra"]?["ue_links"] is JsonArray ueLinks)
        {
            foreach (var ue in ueLinks.OfType<JsonObject>())
            {
                var down = TryGetNodeInt(ue["downstream"], out var d) ? d : -1;
                var downSlot = TryGetNodeInt(ue["downstream_slot"], out var ds) ? ds : -1;
                var upStr = ue["upstream"]?.ToString();
                var upSlot = TryGetNodeInt(ue["upstream_slot"], out var us) ? us : 0;
                if (down < 0 || downSlot < 0 || string.IsNullOrWhiteSpace(upStr))
                    continue;
                if (!int.TryParse(upStr, out var up))
                    continue;
                ueMap[(down, downSlot)] = (up, upSlot);
            }
        }

        // GetNode/SetNode 跨图引用解析（KJNodes）
        // SetNode: widgets_values[0]=变量名, inputs[0].link=源连接
        // GetNode: widgets_values[0]=变量名, 输出连接需重定向到 SetNode 的源节点
        var setVarMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var getRedirectMap = new Dictionary<int, int>();
        foreach (var nodeNode in nodes.OfType<JsonObject>())
        {
            var classType = nodeNode["type"]?.GetValue<string>() ?? "";
            var id = TryGetNodeInt(nodeNode["id"], out var nid) ? nid : -1;
            if (id < 0) continue;

            if (classType.Equals("SetNode", StringComparison.OrdinalIgnoreCase))
            {
                var widgets = nodeNode["widgets_values"] as JsonArray;
                var varName = widgets?[0]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(varName)) continue;
                var inputs = nodeNode["inputs"] as JsonArray;
                var firstInput = inputs?.FirstOrDefault();
                int? link = null;
                if (firstInput is JsonObject fi && TryGetNodeInt(fi["link"], out var linkVal))
                    link = linkVal;
                if (link.HasValue && linkMap.TryGetValue(link.Value, out var src))
                    setVarMap[varName] = src.srcNode;
            }
            else if (classType.Equals("GetNode", StringComparison.OrdinalIgnoreCase))
            {
                var widgets = nodeNode["widgets_values"] as JsonArray;
                var varName = widgets?[0]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(varName)) continue;
                if (setVarMap.TryGetValue(varName, out var srcNode))
                    getRedirectMap[id] = srcNode;
            }
        }

        // 重定向 linkMap 中指向 GetNode 的连接到 SetNode 的源节点
        if (getRedirectMap.Count > 0)
        {
            var keysToUpdate = linkMap
                .Where(kv => getRedirectMap.ContainsKey(kv.Value.srcNode))
                .ToList();
            foreach (var kv in keysToUpdate)
                linkMap[kv.Key] = (getRedirectMap[kv.Value.srcNode], kv.Value.srcSlot);
        }

        var prompt = new JsonObject();

        foreach (var nodeNode in nodes.OfType<JsonObject>())
        {
            var id = TryGetNodeInt(nodeNode["id"], out var nid) ? nid : -1;
            if (id < 0) continue;

            var mode = TryGetNodeInt(nodeNode["mode"], out var nmode) ? nmode : 0;
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

            try
            {
                ApplyWidgets(classType, widgetInputNames, widgets, inputsObj, objectInfo);
                ApplySpecialNodeInputs(classType, nodeNode, widgets, inputsObj);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"节点 {id} ({classType}) widget 映射失败: {ex.Message}", ex);
            }

            prompt[id.ToString()] = new JsonObject
            {
                ["class_type"] = classType,
                ["inputs"] = inputsObj
            };
        }

        if (validate && objectInfo != null)
            ValidateConvertedPrompt(prompt, objectInfo);

        return prompt;
    }

    /// <summary>
    /// 对照 object_info 做轻量完整性检查：对已写入的标量做 combo 选项与数值 min/max 校验。
    /// 不因「缺省 required」失败（多数 UI 流依赖 Comfy 默认值）；错位映射常表现为 combo 非法值。
    /// 失败时抛出带节点 id / class_type / 字段名的异常，避免只看到远端 Comfy 校验。
    /// </summary>
    public static void ValidateConvertedPrompt(JsonObject prompt, JsonObject objectInfo)
    {
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(classType)) continue;
            if (objectInfo[classType] is not JsonObject info) continue;

            var inputs = node["inputs"] as JsonObject ?? new JsonObject();
            void CheckGroup(JsonObject? group)
            {
                if (group == null) return;
                foreach (var entry in group)
                {
                    var field = entry.Key;
                    if (string.IsNullOrWhiteSpace(field) || !inputs.ContainsKey(field))
                        continue;
                    if (IsConnectionOnlyInput(info, field))
                        continue;
                    ValidateFieldValue(kv.Key, classType, field, entry.Value, inputs[field]);
                }
            }

            var inputRoot = info["input"] as JsonObject;
            CheckGroup(inputRoot?["required"] as JsonObject);
            CheckGroup(inputRoot?["optional"] as JsonObject);
        }
    }

    static void ValidateFieldValue(
        string nodeId, string classType, string field, JsonNode? schema, JsonNode? value)
    {
        if (schema is not JsonArray arr || arr.Count == 0 || value == null)
            return;
        if (value is JsonArray)
            return; // 连线 [nodeId, slot]

        // combo：旧式第一项为选项数组；新式类型名为 "COMBO"、选项在 meta.options
        var options = GetSchemaOptions(schema);
        if (options != null)
        {
            if (value is not JsonValue jv || !jv.TryGetValue<string>(out var s))
                return;
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var opt in options)
            {
                if (opt is JsonValue ov && ov.TryGetValue<string>(out var os))
                    allowed.Add(os);
                else if (opt != null)
                    allowed.Add(opt.ToString());
            }
            if (allowed.Count > 0 && !allowed.Contains(s))
            {
                throw new Exception(
                    $"转换校验失败: 节点 {nodeId} ({classType}) 参数 '{field}' 值 '{s}' 不在可选列表中");
            }
            return;
        }

        if (arr[0] is not JsonValue typeVal || !typeVal.TryGetValue<string>(out var typeName))
            return;

        JsonObject? meta = arr.Count > 1 ? arr[1] as JsonObject : null;
        if (typeName is "INT" or "FLOAT" && value is JsonValue numVal)
        {
            double d;
            try
            {
                d = numVal.GetValueKind() switch
                {
                    JsonValueKind.Number => numVal.GetValue<double>(),
                    JsonValueKind.String when double.TryParse(numVal.GetValue<string>(), out var parsed) => parsed,
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    _ => double.NaN
                };
            }
            catch
            {
                return;
            }
            if (double.IsNaN(d)) return;

            if (meta?["min"] != null)
            {
                var min = meta["min"]!.GetValue<double>();
                if (d < min)
                    throw new Exception(
                        $"转换校验失败: 节点 {nodeId} ({classType}) 参数 '{field}'={d} 小于最小值 {min}");
            }
            if (meta?["max"] != null)
            {
                var max = meta["max"]!.GetValue<double>();
                if (d > max)
                    throw new Exception(
                        $"转换校验失败: 节点 {nodeId} ({classType}) 参数 '{field}'={d} 大于最大值 {max}");
            }
        }
    }

    static void ApplyWidgets(
        string classType,
        List<string> widgetInputNames,
        JsonArray widgets,
        JsonObject inputsObj,
        JsonObject? objectInfo)
    {
        var nodeInfo = objectInfo != null && objectInfo[classType] is JsonObject info
            ? info
            : null;
        var ordered = ResolveWidgetOrder(classType, widgetInputNames, objectInfo);
        int wi = 0;
        string? previousWritten = null;

        foreach (var name in ordered)
        {
            if (inputsObj.ContainsKey(name))
                continue;
            if (wi >= widgets.Count)
                break;

            // 仅在 seed 控件上下文跳过 control_after_generate；
            // 禁止把 combo 合法值（如 threshold_schedule="fixed"）全局跳过。
            while (wi < widgets.Count
                   && ShouldSkipControlAfterGenerate(name, previousWritten, widgets[wi], nodeInfo))
            {
                wi++;
            }

            if (wi >= widgets.Count)
                break;

            var val = widgets[wi];
            wi++;

            if (val == null || (val is JsonValue jn && jn.GetValueKind() == JsonValueKind.Null))
                continue;
            if (IsLikelyUiOnlyTokenJson(val))
                continue;

            inputsObj[name] = val.DeepClone();
            previousWritten = name;
        }
    }

    /// <summary>
    /// 判断 widgets_values 中的 token 是否应作为 UI-only 的 control_after_generate 跳过。
    /// 仅当：上一写入字段是 seed 类，或（极少见）当前目标字段是 seed 类却读到 control 字符串。
    /// 有 object_info 时：当前字段若是 combo/STRING 且选项含该值，绝不跳过。
    /// </summary>
    static bool ShouldSkipControlAfterGenerate(
        string currentInputName,
        string? previousWrittenInputName,
        JsonNode? token,
        JsonObject? nodeObjectInfo)
    {
        if (token is not JsonValue jv || !jv.TryGetValue<string>(out var ctrl))
            return false;
        if (string.IsNullOrWhiteSpace(ctrl) || !ControlAfterGenerate.Contains(ctrl))
            return false;

        // 当前字段 schema 表明这是合法 combo/STRING 值（含 fixed 等）→ 必须保留
        if (nodeObjectInfo != null)
        {
            var schema = GetInputSchema(nodeObjectInfo, currentInputName);
            if (IsWidgetScalarSchema(schema) && SchemaAcceptsStringOption(schema, ctrl))
                return false;

            // 上一字段是 INT seed，当前 token 是 control → 跳过 UI 控件
            if (IsSeedLikeField(previousWrittenInputName, nodeObjectInfo))
                return true;

            // 当前字段是 seed 类 INT，却读到 control 字符串：跳过以免把 randomize 写入 seed
            if (IsSeedLikeField(currentInputName, nodeObjectInfo))
                return true;

            return false;
        }

        // 无 object_info：仅靠字段名启发式 + seed 上下文
        if (IsSeedLikeFieldName(previousWrittenInputName))
            return true;
        if (IsSeedLikeFieldName(currentInputName))
            return true;
        return false;
    }

    static bool IsSeedLikeField(string? name, JsonObject? nodeInfo)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (IsSeedLikeFieldName(name))
            return true;
        if (nodeInfo == null)
            return false;
        // 名称以 seed 结尾且类型为 INT
        if (name.EndsWith("seed", StringComparison.OrdinalIgnoreCase))
        {
            var schema = GetInputSchema(nodeInfo, name);
            return IsIntSchema(schema);
        }
        return false;
    }

    static bool IsSeedLikeFieldName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return name.Equals("seed", StringComparison.OrdinalIgnoreCase)
               || name.Equals("noise_seed", StringComparison.OrdinalIgnoreCase);
    }

    static JsonNode? GetInputSchema(JsonObject nodeInfo, string name)
    {
        var input = nodeInfo["input"] as JsonObject;
        if (input?["required"] is JsonObject req && req[name] != null)
            return req[name];
        if (input?["optional"] is JsonObject opt && opt[name] != null)
            return opt[name];
        if (input?["hidden"] is JsonObject hid && hid[name] != null)
            return hid[name];
        return null;
    }

    /// <summary>
    /// 取 combo 选项数组。兼容旧式 [["opt", ...], {...}] 与新式 ["COMBO", {options:[...]}]。
    /// 不是 combo 或取不到选项时返回 null。
    /// </summary>
    static JsonArray? GetSchemaOptions(JsonNode? schema)
    {
        if (schema is not JsonArray arr || arr.Count == 0)
            return null;
        if (arr[0] is JsonArray options)
            return options;
        if (arr[0] is JsonValue tv && tv.TryGetValue<string>(out var t)
            && t.Equals("COMBO", StringComparison.OrdinalIgnoreCase)
            && arr.Count > 1 && arr[1] is JsonObject meta)
            return meta["options"] as JsonArray;
        return null;
    }

    static bool IsWidgetScalarSchema(JsonNode? schema)
    {
        if (schema is not JsonArray arr || arr.Count == 0)
            return false;
        if (GetSchemaOptions(schema) != null)
            return true; // combo（旧式选项数组 / 新式 COMBO）
        if (arr[0] is JsonValue tv && tv.TryGetValue<string>(out var t))
            return t is "INT" or "FLOAT" or "STRING" or "BOOLEAN" or "COMBO";
        return false;
    }

    static bool IsIntSchema(JsonNode? schema)
    {
        if (schema is not JsonArray arr || arr.Count == 0)
            return false;
        return arr[0] is JsonValue tv
               && tv.TryGetValue<string>(out var t)
               && t.Equals("INT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// combo 选项含该字符串，或字段类型为 STRING（可接受任意含 fixed 的合法值）。
    /// </summary>
    static bool SchemaAcceptsStringOption(JsonNode? schema, string value)
    {
        if (schema is not JsonArray arr || arr.Count == 0)
            return false;
        var options = GetSchemaOptions(schema);
        if (options != null)
        {
            foreach (var opt in options)
            {
                if (opt is JsonValue ov && ov.TryGetValue<string>(out var os)
                    && os.Equals(value, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (opt != null && opt.ToString().Equals(value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        if (arr[0] is JsonValue tv && tv.TryGetValue<string>(out var t)
            && t.Equals("STRING", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static List<string> ResolveWidgetOrder(string classType, List<string> fallback, JsonObject? objectInfo)
    {
        if (objectInfo == null || objectInfo[classType] is not JsonObject info)
            return fallback;

        // widgets_values 始终按 UI 保存时的 widget 槽位顺序排列（= fallback/widgetInputNames 顺序），
        // 这是唯一与取值对齐的可靠顺序。object_info 的 input_order 只反映“当前节点定义”，
        // 节点升级换序、漏掉动态 combo 子控件（如 resize_type.scale）时会造成位置错位
        // （DLSS5Settings scene_change_threshold 错拿 1.5、RTXVideoSuperResolution quality 错拿缩放值）。
        // 因此顺序以 UI 的 fallback 为主；object_info 仅用于补齐 UI 中缺失的当前新增 widget 字段。
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fallback)
        {
            if (seen.Add(f))
                order.Add(f);
        }

        var inputOrder = info["input_order"] as JsonObject;
        void AddFrom(JsonArray? arr)
        {
            if (arr == null) return;
            foreach (var n in arr)
            {
                var name = n?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsConnectionOnlyInput(info, name)) continue;
                if (seen.Add(name))
                    order.Add(name);
            }
        }

        AddFrom(inputOrder?["required"] as JsonArray);
        AddFrom(inputOrder?["optional"] as JsonArray);

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
            // 新式自定义节点把下拉声明为 ["COMBO", {options:[...]}] 或 ["COMFY_DYNAMICCOMBO_V3", {...}]，
            // 等价旧式选项数组，是 widget 不是连线；若被误判为连线，widget 顺序会错位。
            if (typeName is "INT" or "FLOAT" or "STRING" or "BOOLEAN"
                || typeName.Contains("COMBO", StringComparison.OrdinalIgnoreCase))
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
        string? resolutionNodeId,
        bool isImg2Img = false)
    {
        // 优先用采样器连接关系识别正/负面节点（通用，兼容原生 CLIPTextEncode 与第三方节点）；
        // 连接关系未命中再回退启发式（WeiLinPromptUI 文本最长者 / CLIPTextEncode）。
        var (autoPosId, autoNegId) = FindPositiveAndNegativeIds(prompt);

        var posId = ResolvePromptNodeId(
            prompt, positiveNodeId, positiveInputName, autoPosId);

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
            var negId = ResolvePromptNodeId(
                prompt, negativeNodeId, negativeInputName, autoNegId);

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
            // 图生图模式：缩放输入图片到目标分辨率，不动 EmptyLatentImage
            if (isImg2Img)
            {
                var resizeId = FindImageResizeNodeId(prompt);
                if (!string.IsNullOrWhiteSpace(resizeId) && prompt[resizeId] is JsonObject resizeNode)
                {
                    var inputs = resizeNode["inputs"] as JsonObject ?? new JsonObject();
                    resizeNode["inputs"] = inputs;
                    // ImageResize / ImageScale 常见字段名
                    if (width.HasValue)
                    {
                        if (inputs.ContainsKey("width")) inputs["width"] = width.Value;
                        else if (inputs.ContainsKey("target_width")) inputs["target_width"] = width.Value;
                        else if (inputs.ContainsKey("W")) inputs["W"] = width.Value;
                    }
                    if (height.HasValue)
                    {
                        if (inputs.ContainsKey("height")) inputs["height"] = height.Value;
                        else if (inputs.ContainsKey("target_height")) inputs["target_height"] = height.Value;
                        else if (inputs.ContainsKey("H")) inputs["H"] = height.Value;
                    }
                }
            }
            else
            {
            var resId = resolutionNodeId;
            if (string.IsNullOrWhiteSpace(resId)
                || prompt[resId] is not JsonObject configuredResolution
                || !CanApplyResolution(configuredResolution))
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
            } // end else (not img2img)
        } // end if (width || height)

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

    static bool IsUsablePromptNode(JsonObject prompt, string? nodeId, string? inputName)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || prompt[nodeId] is not JsonObject node)
            return false;

        var classType = node["class_type"]?.GetValue<string>() ?? "";
        var inputs = node["inputs"] as JsonObject;
        if (inputs == null)
            return false;

        if (IsPromptTextNode(classType))
            return true;

        if (!string.IsNullOrWhiteSpace(inputName)
            && inputs[inputName] is JsonValue configuredValue
            && configuredValue.TryGetValue<string>(out _))
            return true;

        return inputs["text"] is JsonValue text && text.TryGetValue<string>(out _)
               || inputs["positive"] is JsonValue positive && positive.TryGetValue<string>(out _);
    }

    static string? ResolvePromptNodeId(
        JsonObject prompt,
        string? configuredId,
        string? configuredInputName,
        string? autoId)
    {
        if (IsUsablePromptNode(prompt, configuredId, configuredInputName))
            return configuredId;
        return IsUsablePromptNode(prompt, autoId, null) ? autoId : null;
    }

    static bool CanApplyResolution(JsonObject node)
    {
        var classType = node["class_type"]?.GetValue<string>() ?? "";
        if (classType.Contains("PresetResolution", StringComparison.OrdinalIgnoreCase)
            || classType.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase))
            return true;

        var inputs = node["inputs"] as JsonObject;
        return inputs != null
               && (inputs.ContainsKey("width") || inputs.ContainsKey("height")
                   || inputs.ContainsKey("自定义宽") || inputs.ContainsKey("自定义高"));
    }

    static bool IsPromptTextNode(string ct)
        => ct.Contains("TextEncode", StringComparison.OrdinalIgnoreCase)
           || ct.Contains("PromptUI", StringComparison.OrdinalIgnoreCase)
           || ct.Contains("PromptText", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 通过采样器 positive/negative 输入的连接关系识别正/负面提示词节点。
    /// 多采样器工作流（一采+二采+局部重绘）时收集全部启用采样器，
    /// 正向取追溯到的提示词节点中文本最长者（主提示词通常最长），
    /// 负向取文本最短者（负向通常较短）且排除已选正向节点。
    /// 连接关系未命中时回退到启发式。
    /// </summary>
    public static (string? positiveId, string? negativeId) FindPositiveAndNegativeIds(JsonObject prompt)
    {
        // 收集所有启用采样器的 positive/negative 源节点 ID
        var posCandidates = new List<string>();
        var negCandidates = new List<string>();
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (!IsSamplerType(ct)) continue;
            var inputs = node["inputs"] as JsonObject;
            if (inputs == null) continue;

            if (inputs["positive"] is JsonArray pArr && pArr.Count > 0
                && pArr[0]?.GetValue<string>() is string pId)
                posCandidates.Add(pId);
            if (inputs["negative"] is JsonArray nArr && nArr.Count > 0
                && nArr[0]?.GetValue<string>() is string nId)
                negCandidates.Add(nId);
        }

        // 对每个候选追溯所有可达提示词节点，按文本长度择优
        var posPromptNodes = new List<(string id, int textLen)>();
        foreach (var pid in posCandidates.Distinct(StringComparer.Ordinal))
        {
            foreach (var found in TraceToPromptNodes(prompt, pid))
                posPromptNodes.Add((found, GetPromptTextLen(prompt, found)));
        }
        var negPromptNodes = new List<(string id, int textLen)>();
        foreach (var nid in negCandidates.Distinct(StringComparer.Ordinal))
        {
            foreach (var found in TraceToPromptNodes(prompt, nid))
                negPromptNodes.Add((found, GetPromptTextLen(prompt, found)));
        }

        string? posId = null, negId = null;
        if (posPromptNodes.Count > 0)
            posId = posPromptNodes.OrderByDescending(n => n.textLen).First().id;
        if (negPromptNodes.Count > 0)
            negId = negPromptNodes
                .Where(n => n.id != posId)
                .OrderBy(n => n.textLen)
                .FirstOrDefault().id;

        // 连接关系未命中，回退启发式
        posId ??= FindPositiveNodeId(prompt);
        negId ??= FindNegativeNodeId(prompt, posId);
        return (posId, negId);
    }

    /// <summary>获取提示词节点的文本长度（用于多采样器择优）。</summary>
    static int GetPromptTextLen(JsonObject prompt, string nodeId)
    {
        if (prompt[nodeId] is not JsonObject node) return 0;
        var inputs = node["inputs"] as JsonObject;
        if (inputs == null) return 0;
        var text = inputs["positive"]?.ToString() ?? inputs["text"]?.ToString() ?? "";
        return text.Length;
    }

    /// <summary>
    /// BFS 追溯连接图，返回所有可达的提示词文本节点 ID 列表（去重）。
    /// 用于多采样器场景下收集全部候选，由调用方按文本长度择优。
    /// </summary>
    static List<string> TraceToPromptNodes(JsonObject prompt, string? startId)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(startId))
            return result;

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!visited.Add(currentId) || prompt[currentId] is not JsonObject node)
                continue;

            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (IsPromptTextNode(classType))
                result.Add(currentId);

            if (node["inputs"] is not JsonObject inputs)
                continue;

            // Conditioning 组合节点常同时包含 positive/negative，优先沿语义相符分支追溯
            var linkedInputs = inputs
                .Where(kv => kv.Value is JsonArray arr
                             && arr.Count > 0
                             && arr[0] is JsonValue)
                .OrderByDescending(kv => IsPreferredConditioningInput(kv.Key, preferNegative: false));

            foreach (var input in linkedInputs)
            {
                if (input.Value is not JsonArray arr || arr.Count == 0)
                    continue;
                string? linkedId = null;
                try { linkedId = arr[0]?.GetValue<string>(); }
                catch { }
                if (!string.IsNullOrWhiteSpace(linkedId) && !visited.Contains(linkedId))
                    queue.Enqueue(linkedId);
            }
        }

        return result;
    }

    /// <summary>兼容旧调用：返回第一个追溯到的提示词节点。</summary>
    static string? TraceToPromptNode(
        JsonObject prompt, string? startId, bool preferNegative)
    {
        if (string.IsNullOrWhiteSpace(startId))
            return null;

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!visited.Add(currentId) || prompt[currentId] is not JsonObject node)
                continue;

            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (IsPromptTextNode(classType))
                return currentId;

            if (node["inputs"] is not JsonObject inputs)
                continue;

            // Conditioning 组合节点常同时包含 positive/negative，优先沿语义相符的分支追踪。
            var linkedInputs = inputs
                .Where(kv => kv.Value is JsonArray arr
                             && arr.Count > 0
                             && arr[0] is JsonValue)
                .OrderByDescending(kv => IsPreferredConditioningInput(kv.Key, preferNegative));

            foreach (var input in linkedInputs)
            {
                if (input.Value is not JsonArray arr || arr.Count == 0)
                    continue;
                string? linkedId = null;
                try { linkedId = arr[0]?.GetValue<string>(); }
                catch { }
                if (!string.IsNullOrWhiteSpace(linkedId) && !visited.Contains(linkedId))
                    queue.Enqueue(linkedId);
            }
        }

        return null;
    }

    static bool IsPreferredConditioningInput(string name, bool preferNegative)
    {
        if (preferNegative)
            return name.Contains("negative", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("负", StringComparison.OrdinalIgnoreCase);
        return name.Contains("positive", StringComparison.OrdinalIgnoreCase)
               || name.Contains("正", StringComparison.OrdinalIgnoreCase);
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
        string? promptTextNode = null;

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
            else if (IsPromptTextNode(ct) && promptTextNode == null)
            {
                promptTextNode = kv.Key;
            }
        }

        return bestWeiLin ?? promptTextNode;
    }

    /// <summary>
    /// 找负面提示词节点：优先正向节点之外的另一个 WeiLinPromptUI（文本最短者），
    /// 其次是另一个 CLIPTextEncode。
    /// </summary>
    public static string? FindNegativeNodeId(JsonObject prompt, string? excludePosId)
    {
        string? bestWeiLin = null;
        int bestLen = int.MaxValue;
        string? promptTextNode = null;

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
            else if (IsPromptTextNode(ct) && promptTextNode == null)
            {
                promptTextNode = kv.Key;
            }
        }

        return bestWeiLin ?? promptTextNode;
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

    // ===================== AI 节点控制 =====================

    static readonly HashSet<string> AiVisibleNodeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ckpt_name", "steps", "cfg", "cfg_scale", "sampler_name", "scheduler",
        "denoise", "batch_size", "width", "height"
    };

    static readonly string[] SensitiveOverrideFragments =
    {
        "path", "save", "directory", "url", "command", "script", "token", "password",
        "filename", "file_name", "保存", "路径", "目录", "地址", "命令", "脚本", "令牌", "密码"
    };

    /// <summary>按需描述允许 AI 操作的安全标量参数。</summary>
    public static string BuildNodeControlDescription(JsonObject prompt)
    {
        var lines = new List<string>();

        foreach (var kv in prompt)
        {
            if (lines.Count >= 40) break;
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            var inputs = node["inputs"] as JsonObject;
            if (inputs == null || inputs.Count == 0) continue;

            var widgets = new List<string>();
            foreach (var ikv in inputs)
            {
                if (widgets.Count >= 10) break;
                if (!AiVisibleNodeFields.Contains(ikv.Key) || ikv.Value is not JsonValue)
                    continue;
                var valStr = ikv.Value.ToString();
                if (valStr.Length > 80) valStr = valStr[..77] + "...";
                widgets.Add($"{ikv.Key}={valStr}");
            }

            if (widgets.Count == 0) continue;

            lines.Add($"· 节点 #{kv.Key} [{ct}]: {string.Join(", ", widgets)}");
        }

        if (lines.Count == 0) return "当前工作流无可调节点。";

        return "允许调整的节点参数：\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 将 AI 传入的语义化参数注入到工作流对应节点中。
    /// 支持 model / steps / cfg / sampler / scheduler / denoise / batch_size，
    /// 以及 rawOverrides JSON（兜底：按节点 ID 直接覆盖 input 值）。
    /// </summary>
    public static void ApplyNodeOverrides(
        JsonObject prompt,
        string? model = null,
        int? steps = null,
        double? cfg = null,
        string? sampler = null,
        string? scheduler = null,
        double? denoise = null,
        int? batch_size = null,
        string? rawOverrides = null)
    {
        ValidateSemanticOverrides(model, steps, cfg, sampler, scheduler, denoise, batch_size);

        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            var inputs = node["inputs"] as JsonObject;
            if (inputs == null) continue;

            // 模型切换：CheckpointLoader / CheckpointLoaderSimple
            if (!string.IsNullOrWhiteSpace(model) && IsCheckpointLoader(ct))
            {
                var key = inputs.ContainsKey("ckpt_name") ? "ckpt_name" : null;
                if (key == null)
                {
                    foreach (var ikv in inputs)
                    {
                        if (ikv.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                            && (s.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
                                || s.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)))
                        { key = ikv.Key; break; }
                    }
                }
                if (key != null) inputs[key] = model;
            }

            // 采样器参数：KSampler / KSamplerAdvanced / 等
            if (IsSamplerType(ct))
            {
                if (steps.HasValue && inputs.ContainsKey("steps"))
                    inputs["steps"] = steps.Value;
                if (cfg.HasValue)
                {
                    if (inputs.ContainsKey("cfg")) inputs["cfg"] = cfg.Value;
                    else if (inputs.ContainsKey("cfg_scale")) inputs["cfg_scale"] = cfg.Value;
                }
                if (!string.IsNullOrWhiteSpace(sampler) && inputs.ContainsKey("sampler_name"))
                    inputs["sampler_name"] = sampler;
                if (!string.IsNullOrWhiteSpace(scheduler) && inputs.ContainsKey("scheduler"))
                    inputs["scheduler"] = scheduler;
                if (denoise.HasValue && inputs.ContainsKey("denoise"))
                    inputs["denoise"] = denoise.Value;
            }

            // 批次大小：EmptyLatentImage
            if (batch_size.HasValue && ct.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase))
            {
                if (inputs.ContainsKey("batch_size"))
                    inputs["batch_size"] = batch_size.Value;
            }
        }

        // 原始覆盖仅允许现有节点的现有标量字段，禁止连线和路径类参数。
        if (!string.IsNullOrWhiteSpace(rawOverrides))
            ApplyValidatedRawOverrides(prompt, rawOverrides);
    }

    static void ValidateSemanticOverrides(
        string? model, int? steps, double? cfg, string? sampler, string? scheduler,
        double? denoise, int? batch_size)
    {
        if (model?.Length > 260)
            throw new ArgumentOutOfRangeException(nameof(model), "模型名称过长");
        if (sampler?.Length > 80)
            throw new ArgumentOutOfRangeException(nameof(sampler), "采样器名称过长");
        if (scheduler?.Length > 80)
            throw new ArgumentOutOfRangeException(nameof(scheduler), "调度器名称过长");
        if (steps is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(steps), "steps 必须在 1~200 之间");
        if (cfg is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(cfg), "cfg 必须在 0~30 之间");
        if (denoise is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(denoise), "denoise 必须在 0~1 之间");
        if (batch_size is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(batch_size), "batch_size 必须在 1~16 之间");
    }

    static void ApplyValidatedRawOverrides(JsonObject prompt, string rawOverrides)
    {
        if (rawOverrides.Length > 8192)
            throw new ArgumentException("nodeOverrides 不能超过 8192 个字符", nameof(rawOverrides));

        JsonObject overrides;
        try
        {
            overrides = JsonNode.Parse(rawOverrides) as JsonObject
                ?? throw new ArgumentException("nodeOverrides 顶层必须是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"nodeOverrides JSON 无效: {ex.Message}", nameof(rawOverrides));
        }

        var pending = new List<(JsonObject Inputs, string Field, JsonNode Value)>();
        var overrideCount = 0;
        foreach (var nodeOverride in overrides)
        {
            if (prompt[nodeOverride.Key] is not JsonObject targetNode)
                throw new ArgumentException($"nodeOverrides 包含不存在的节点 #{nodeOverride.Key}");
            if (targetNode["inputs"] is not JsonObject targetInputs)
                throw new ArgumentException($"节点 #{nodeOverride.Key} 没有可覆盖的 inputs");
            if (nodeOverride.Value is not JsonObject fields)
                throw new ArgumentException($"节点 #{nodeOverride.Key} 的覆盖值必须是 JSON 对象");

            foreach (var fieldOverride in fields)
            {
                if (++overrideCount > 32)
                    throw new ArgumentException("nodeOverrides 最多允许 32 个字段");
                if (IsSensitiveOverrideField(fieldOverride.Key))
                    throw new ArgumentException($"禁止覆盖敏感字段 #{nodeOverride.Key}.{fieldOverride.Key}");
                if (!targetInputs.TryGetPropertyValue(fieldOverride.Key, out var currentValue))
                    throw new ArgumentException($"节点 #{nodeOverride.Key} 不存在字段 {fieldOverride.Key}");
                if (currentValue is not JsonValue || fieldOverride.Value is not JsonValue newValue)
                    throw new ArgumentException(
                        $"#{nodeOverride.Key}.{fieldOverride.Key} 只允许标量值，不能修改连线、对象或数组");
                if (!AreCompatibleScalarTypes(currentValue, newValue))
                    throw new ArgumentException($"#{nodeOverride.Key}.{fieldOverride.Key} 的值类型不匹配");
                if (newValue.GetValueKind() == JsonValueKind.String
                    && (newValue.GetValue<string>()?.Length ?? 0) > 512)
                    throw new ArgumentException($"#{nodeOverride.Key}.{fieldOverride.Key} 的字符串值过长");

                ValidateKnownRawRange(fieldOverride.Key, newValue);
                pending.Add((targetInputs, fieldOverride.Key, newValue.DeepClone()));
            }
        }

        foreach (var change in pending)
            change.Inputs[change.Field] = change.Value;
    }

    static bool IsSensitiveOverrideField(string field)
        => SensitiveOverrideFragments.Any(fragment =>
            field.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    static bool AreCompatibleScalarTypes(JsonNode currentValue, JsonValue newValue)
    {
        var currentKind = currentValue.GetValueKind();
        var newKind = newValue.GetValueKind();
        if (currentKind is JsonValueKind.True or JsonValueKind.False)
            return newKind is JsonValueKind.True or JsonValueKind.False;
        return currentKind == newKind
               && currentKind is JsonValueKind.String or JsonValueKind.Number;
    }

    static void ValidateKnownRawRange(string field, JsonValue value)
    {
        if (value.GetValueKind() != JsonValueKind.Number)
            return;

        var number = value.GetValue<double>();
        if (field.Equals("steps", StringComparison.OrdinalIgnoreCase) && number is < 1 or > 200)
            throw new ArgumentOutOfRangeException(field, "steps 必须在 1~200 之间");
        if ((field.Equals("cfg", StringComparison.OrdinalIgnoreCase)
             || field.Equals("cfg_scale", StringComparison.OrdinalIgnoreCase))
            && number is < 0 or > 30)
            throw new ArgumentOutOfRangeException(field, "cfg 必须在 0~30 之间");
        if (field.Equals("denoise", StringComparison.OrdinalIgnoreCase) && number is < 0 or > 1)
            throw new ArgumentOutOfRangeException(field, "denoise 必须在 0~1 之间");
        if (field.Equals("batch_size", StringComparison.OrdinalIgnoreCase) && number is < 1 or > 16)
            throw new ArgumentOutOfRangeException(field, "batch_size 必须在 1~16 之间");
        if ((field.Equals("width", StringComparison.OrdinalIgnoreCase)
             || field.Equals("height", StringComparison.OrdinalIgnoreCase))
            && number is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(field, "width/height 必须在 64~4096 之间");
    }

    static bool IsCheckpointLoader(string classType)
        => classType.Contains("CheckpointLoader", StringComparison.OrdinalIgnoreCase)
           || classType.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
               && classType.Contains("Loader", StringComparison.OrdinalIgnoreCase);

    // ===================== 图生图图片输入 =====================

    /// <summary>
    /// 自动识别工作流中的 LoadImage 节点 ID。
    /// class_type 包含 "LoadImage" 或 "Load Image" 即命中。
    /// </summary>
    public static string? FindLoadImageNodeId(JsonObject prompt)
    {
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (ct.Contains("LoadImage", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("Load Image", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    /// <summary>
    /// 将已上传的图片文件名注入到 LoadImage 节点。
    /// </summary>
    public static string? InjectLoadImage(JsonObject prompt, string? nodeId, string inputName, string filename)
    {
        var nid = IsUsableLoadImageNode(prompt, nodeId, inputName)
            ? nodeId
            : FindLoadImageNodeId(prompt);
        if (string.IsNullOrWhiteSpace(nid) || prompt[nid] is not JsonObject node)
            return null;

        var inputs = node["inputs"] as JsonObject ?? new JsonObject();
        node["inputs"] = inputs;
        var field = ResolveLoadImageField(inputs, inputName);
        inputs[field] = filename;
        return nid;
    }

    static bool IsUsableLoadImageNode(JsonObject prompt, string? nodeId, string? inputName)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || prompt[nodeId] is not JsonObject node)
            return false;

        var classType = node["class_type"]?.GetValue<string>() ?? "";
        if (classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase)
            || classType.Contains("Load Image", StringComparison.OrdinalIgnoreCase))
            return true;

        var inputs = node["inputs"] as JsonObject;
        return inputs != null
               && !string.IsNullOrWhiteSpace(inputName)
               && inputs[inputName] is JsonValue value
               && value.TryGetValue<string>(out _);
    }

    static string ResolveLoadImageField(JsonObject inputs, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName)
            && inputs[configuredName] is JsonValue configuredValue
            && configuredValue.TryGetValue<string>(out _))
            return configuredName;

        foreach (var candidate in new[] { "image", "filename", "image_path", "path" })
        {
            if (inputs[candidate] is JsonValue value && value.TryGetValue<string>(out _))
                return candidate;
        }

        foreach (var input in inputs)
        {
            if (input.Value is JsonValue value && value.TryGetValue<string>(out _))
                return input.Key;
        }

        return string.IsNullOrWhiteSpace(configuredName) ? "image" : configuredName;
    }

    /// <summary>
    /// 自动识别工作流中的 ImageResize / ImageScale 节点 ID。
    /// 用于图生图模式时将输出分辨率注入到缩放节点。
    /// </summary>
    public static string? FindImageResizeNodeId(JsonObject prompt)
    {
        foreach (var kv in prompt)
        {
            if (kv.Value is not JsonObject node) continue;
            var ct = node["class_type"]?.GetValue<string>() ?? "";
            if (ct.Contains("ImageResize", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("ImageScale", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("ImageResizeToTotalPixels", StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    /// <summary>
    /// 最小回归：ApplyFBCacheOnModel 的 threshold_schedule=fixed 不得被跳过；
    /// KSampler 的 seed 后 control_after_generate 不得写入 API inputs。
    /// 返回 null 表示通过，否则为失败说明（不抛异常，便于 Awake 自检）。
    /// </summary>
    public static string? RunWidgetMappingSelfCheck()
    {
        try
        {
            // 1) FBCache：widgets 含合法 "fixed" combo，无 object_info 也应保留
            var fbCacheUi = JsonNode.Parse("""
            {
              "nodes": [
                {
                  "id": 45,
                  "type": "ApplyFBCacheOnModel",
                  "mode": 0,
                  "inputs": [
                    { "name": "model", "type": "MODEL", "link": null },
                    { "name": "object_to_patch", "type": "COMBO", "widget": { "name": "object_to_patch" }, "link": null },
                    { "name": "residual_diff_threshold", "type": "FLOAT", "widget": { "name": "residual_diff_threshold" }, "link": null },
                    { "name": "start", "type": "FLOAT", "widget": { "name": "start" }, "link": null },
                    { "name": "end", "type": "FLOAT", "widget": { "name": "end" }, "link": null },
                    { "name": "max_consecutive_cache_hits", "type": "INT", "widget": { "name": "max_consecutive_cache_hits" }, "link": null },
                    { "name": "threshold_step", "type": "INT", "widget": { "name": "threshold_step" }, "link": null },
                    { "name": "threshold_schedule", "type": "COMBO", "widget": { "name": "threshold_schedule" }, "link": null },
                    { "name": "num_always_run_blocks", "type": "INT", "widget": { "name": "num_always_run_blocks" }, "link": null },
                    { "name": "enable_diagnostics", "type": "BOOLEAN", "widget": { "name": "enable_diagnostics" }, "link": null }
                  ],
                  "widgets_values": ["diffusion_model", 0.12, 0, 1, -1, 0, "fixed", 1, false]
                }
              ],
              "links": []
            }
            """)!;

            var fbPrompt = ToApiPrompt(fbCacheUi);
            if (fbPrompt["45"] is not JsonObject fbNode
                || fbNode["inputs"] is not JsonObject fbIn)
                return "FBCache: 节点 45 未进入 prompt";

            var schedule = fbIn["threshold_schedule"]?.ToString();
            if (!string.Equals(schedule, "fixed", StringComparison.Ordinal))
                return $"FBCache: threshold_schedule 应为 fixed，实际为 '{schedule}'（疑似 control token 误跳）";

            var alwaysBlocks = fbIn["num_always_run_blocks"];
            if (alwaysBlocks is not JsonValue ab
                || !(ab.GetValueKind() == JsonValueKind.Number && ab.GetValue<int>() == 1
                     || ab.GetValueKind() == JsonValueKind.String && ab.GetValue<string>() == "1"))
                return $"FBCache: num_always_run_blocks 应为 1，实际为 '{alwaysBlocks}'";

            if (fbIn["enable_diagnostics"] is not JsonValue diag
                || diag.GetValueKind() != JsonValueKind.False)
                return $"FBCache: enable_diagnostics 应为 false，实际为 '{fbIn["enable_diagnostics"]}'";

            // 2) KSampler：seed 后 randomize 必须跳过，seed 数值保留
            var samplerUi = JsonNode.Parse("""
            {
              "nodes": [
                {
                  "id": 3,
                  "type": "KSampler",
                  "mode": 0,
                  "inputs": [
                    { "name": "model", "type": "MODEL", "link": null },
                    { "name": "positive", "type": "CONDITIONING", "link": null },
                    { "name": "negative", "type": "CONDITIONING", "link": null },
                    { "name": "latent_image", "type": "LATENT", "link": null },
                    { "name": "seed", "type": "INT", "widget": { "name": "seed" }, "link": null },
                    { "name": "steps", "type": "INT", "widget": { "name": "steps" }, "link": null },
                    { "name": "cfg", "type": "FLOAT", "widget": { "name": "cfg" }, "link": null },
                    { "name": "sampler_name", "type": "COMBO", "widget": { "name": "sampler_name" }, "link": null },
                    { "name": "scheduler", "type": "COMBO", "widget": { "name": "scheduler" }, "link": null },
                    { "name": "denoise", "type": "FLOAT", "widget": { "name": "denoise" }, "link": null }
                  ],
                  "widgets_values": [12345, "randomize", 20, 7.5, "euler", "normal", 1.0]
                }
              ],
              "links": []
            }
            """)!;

            var sp = ToApiPrompt(samplerUi);
            if (sp["3"] is not JsonObject sNode || sNode["inputs"] is not JsonObject sIn)
                return "KSampler: 节点 3 未进入 prompt";

            if (sIn["seed"] is not JsonValue seedVal
                || seedVal.GetValueKind() != JsonValueKind.Number
                || seedVal.GetValue<long>() != 12345L)
                return $"KSampler: seed 应为 12345，实际为 '{sIn["seed"]}'";

            // control token 不得作为任何 input 值
            foreach (var input in sIn)
            {
                if (input.Value is JsonValue jv
                    && jv.TryGetValue<string>(out var str)
                    && ControlAfterGenerate.Contains(str))
                    return $"KSampler: inputs['{input.Key}'] 不应写入 control token '{str}'";
            }

            if (sIn["steps"] is not JsonValue stepsVal
                || stepsVal.GetValueKind() != JsonValueKind.Number
                || stepsVal.GetValue<int>() != 20)
                return $"KSampler: steps 应为 20（seed 后跳过 randomize），实际为 '{sIn["steps"]}'";

            // 3) 有 object_info 时：combo 含 fixed 不得跳过；seed 后 control 仍跳过
            var objectInfo = JsonNode.Parse("""
            {
              "ApplyFBCacheOnModel": {
                "input": {
                  "required": {
                    "model": ["MODEL", {}],
                    "object_to_patch": [["diffusion_model"], {}],
                    "residual_diff_threshold": ["FLOAT", {"default": 0.12}],
                    "start": ["FLOAT", {"default": 0}],
                    "end": ["FLOAT", {"default": 1}],
                    "max_consecutive_cache_hits": ["INT", {"default": -1}],
                    "threshold_step": ["INT", {"default": 0}],
                    "threshold_schedule": [["fixed", "linear"], {}],
                    "num_always_run_blocks": ["INT", {"default": 1}],
                    "enable_diagnostics": ["BOOLEAN", {"default": false}]
                  }
                },
                "input_order": {
                  "required": [
                    "model", "object_to_patch", "residual_diff_threshold", "start", "end",
                    "max_consecutive_cache_hits", "threshold_step", "threshold_schedule",
                    "num_always_run_blocks", "enable_diagnostics"
                  ]
                }
              },
              "KSampler": {
                "input": {
                  "required": {
                    "model": ["MODEL", {}],
                    "seed": ["INT", {"default": 0}],
                    "steps": ["INT", {"default": 20}],
                    "cfg": ["FLOAT", {"default": 8}],
                    "sampler_name": [["euler", "dpmpp_2m"], {}],
                    "scheduler": [["normal", "karras"], {}],
                    "positive": ["CONDITIONING", {}],
                    "negative": ["CONDITIONING", {}],
                    "latent_image": ["LATENT", {}],
                    "denoise": ["FLOAT", {"default": 1.0}]
                  }
                },
                "input_order": {
                  "required": [
                    "model", "seed", "steps", "cfg", "sampler_name", "scheduler",
                    "positive", "negative", "latent_image", "denoise"
                  ]
                }
              }
            }
            """) as JsonObject;

            var fb2 = ToApiPrompt(fbCacheUi, objectInfo, validate: true);
            var sched2 = fb2["45"]?["inputs"]?["threshold_schedule"]?.ToString();
            if (!string.Equals(sched2, "fixed", StringComparison.Ordinal))
                return $"FBCache+object_info: threshold_schedule 应为 fixed，实际 '{sched2}'";

            var sp2 = ToApiPrompt(samplerUi, objectInfo, validate: false);
            var seed2 = sp2["3"]?["inputs"]?["seed"];
            if (seed2 is not JsonValue sv2
                || sv2.GetValueKind() != JsonValueKind.Number
                || sv2.GetValue<long>() != 12345L)
                return $"KSampler+object_info: seed 应为 12345，实际 '{seed2}'";

            var steps2 = sp2["3"]?["inputs"]?["steps"];
            if (steps2 is not JsonValue st2
                || st2.GetValueKind() != JsonValueKind.Number
                || st2.GetValue<int>() != 20)
                return $"KSampler+object_info: steps 应为 20，实际 '{steps2}'";

            // 4) 新式 COMBO 声明 ["COMBO", {options:[...]}] 不得被当作连线输入丢弃，
            //    否则 ResolveWidgetOrder 漏字段、widget 位置整体左移（DLSS5Settings 回归）
            var comboUi = JsonNode.Parse("""
            {
              "nodes": [
                {
                  "id": 20,
                  "type": "DLSS5Settings",
                  "mode": 0,
                  "inputs": [
                    { "name": "upscaling_mode", "type": "COMBO", "widget": { "name": "upscaling_mode" }, "link": null },
                    { "name": "nr_intensity", "type": "FLOAT", "widget": { "name": "nr_intensity" }, "link": null },
                    { "name": "scene_change_threshold", "type": "FLOAT", "widget": { "name": "scene_change_threshold" }, "link": null },
                    { "name": "warmup_frames", "type": "INT", "widget": { "name": "warmup_frames" }, "link": null },
                    { "name": "runtime_dir", "type": "STRING", "widget": { "name": "runtime_dir" }, "link": null }
                  ],
                  "widgets_values": ["1x (DLAA / native)", 1, 0.24, 0, ""]
                }
              ],
              "links": []
            }
            """)!;
            var comboObjectInfo = JsonNode.Parse("""
            {
              "DLSS5Settings": {
                "input": {
                  "required": {
                    "upscaling_mode": ["COMBO", {"options": ["1x (DLAA / native)", "2x (Performance)"]}],
                    "nr_intensity": ["FLOAT", {"default": 1.0, "min": 0.0, "max": 2.0}],
                    "scene_change_threshold": ["FLOAT", {"default": 0.24, "min": 0.01, "max": 1.0}],
                    "warmup_frames": ["INT", {"default": 0, "min": 0, "max": 16}],
                    "runtime_dir": ["STRING", {"default": ""}]
                  }
                },
                "input_order": {
                  "required": [
                    "upscaling_mode", "nr_intensity", "scene_change_threshold", "warmup_frames", "runtime_dir"
                  ]
                }
              }
            }
            """) as JsonObject;
            var cp = ToApiPrompt(comboUi, comboObjectInfo, validate: true);
            if (cp["20"] is not JsonObject cn || cn["inputs"] is not JsonObject ci)
                return "COMBO 声明: 节点 20 未进入 prompt";
            var um = ci["upscaling_mode"]?.ToString();
            if (!string.Equals(um, "1x (DLAA / native)", StringComparison.Ordinal))
                return $"COMBO 声明: upscaling_mode 应为 '1x (DLAA / native)'，实际 '{um}'（COMBO 字段被误当连线跳过导致错位）";
            var sct = ci["scene_change_threshold"];
            if (sct is not JsonValue sctv
                || sctv.GetValueKind() != JsonValueKind.Number
                || Math.Abs(sctv.GetValue<double>() - 0.24) > 0.0001)
                return $"COMBO 声明: scene_change_threshold 应为 0.24，实际 '{sct}'";
            var wf = ci["warmup_frames"];
            if (wf is not JsonValue wfv
                || wfv.GetValueKind() != JsonValueKind.Number
                || wfv.GetValue<int>() != 0)
                return $"COMBO 声明: warmup_frames 应为 0，实际 '{wf}'";

            return null;
        }
        catch (Exception ex)
        {
            return $"自检异常: {ex.Message}";
        }
    }
}
