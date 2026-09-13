using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Platform;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using AntDesign;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// ComfyUI 配置面板。
///
/// 重构要点（v4.5.0）：
/// 1) 分页签，**只渲染当前页签**——原来一次性构建全部区块，任何交互都整页重建；
/// 2) 文本输入改为 onchange（失焦/回车提交），不再每敲一个字符整页重建（原为 oninput）；
/// 3) 去掉毛玻璃(backdrop-filter)、keyframes 动画、超大 conic-gradient、模糊滤镜与多层阴影——
///    这些是面板闪烁/掉帧的主因，且与渲染树无关；
/// 4) 事件回调不再手写 StateHasChanged（Blazor 事件后自动重渲染）；
/// 5) 卡片列表改为进入页签时惰性加载，不在渲染过程中改状态。
/// 所有业务方法（扫描/识别/连通测试/卡片增删改/节点概览等）保持原实现不变。
/// </summary>
public partial class ComfyuiServiceUI : ModuleUIBase<ComfyuiService, ComfyuiConfig>
{
    string? detectMessage; string? _scanDetectMessage;
    string _scanDir = "";
    List<string> _scannedWorkflows = new();
    bool _showWorkflowDropdown;
    List<(string NodeId, string ClassType, string KeyParams)> _nodeOverview = new();
    string? _nodeOverviewError;

    // 命名工作流卡片管理
    List<WorkflowCard> _workflowCards = new();
    int _activeScanCardIndex = -1; // 当前展开浏览下拉的卡片索引

    // 提示词预设卡片管理
    List<PresetCard> _presetCards = new();
    int _activePresetIndex = -1; // 当前展开编辑的预设索引
    string _newPresetName = "";
    string _newPresetContent = "";

    // 在线标签检索连通测试
    string? _danbooruTestMessage;
    bool _danbooruTesting;

    // 在线角色检索（AnimaDex）连通测试
    string? _animadexTestMessage;
    bool _animadexTesting;

    // 模型卸载与空闲自动释放
    bool _unloadingModel;
    string? _unloadMessage;

    // APP-MCP 模板模式连通测试
    string? _appMcpTestMessage;
    bool _appMcpTesting;

    // 页签（只渲染当前页签）。页签列表按后端模式生成：模板模式隐藏「工作流」整页
    int _tab = 0;
    static readonly (int Id, string Title)[] AllTabs =
    {
        (0, "基础"), (1, "工作流"), (2, "后端 · 画风"), (3, "提示词"), (4, "检索"), (5, "高级")
    };
    bool _loadedWorkflowCards;
    bool _loadedPresetCards;

    IEnumerable<(int Id, string Title)> EffectiveTabs()
    {
        var appMcp = IsAppMcp();
        foreach (var t in AllTabs)
        {
            if (appMcp && t.Id == 1)
                continue; // 模板模式：工作流配置整体不可用，隐藏该页签
            yield return t;
        }
    }

    record WorkflowCard
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Prefix { get; set; } = "";
    }

    record PresetCard
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
    }

    // ============================================================
    // 样式：纯静态、无动画、无滤镜、无毛玻璃。避免宿主每帧重绘导致闪烁/掉帧。
    // ============================================================
    const string Css = @"
.cu-root { width:100%; box-sizing:border-box; color:#3f2a38; font-size:13px; }
.cu-card { background:#fff; border:1px solid #f2d7e5; border-radius:14px; box-shadow:0 1px 2px rgba(190,24,93,.06); overflow:hidden; }
.cu-head { display:flex; align-items:center; gap:10px; flex-wrap:wrap; padding:13px 16px; border-bottom:1px solid #f7e3ee; background:#fff8fb; }
.cu-title { font-weight:800; font-size:15px; color:#9d174d; }
.cu-spacer { flex:1; }
.cu-chip { font-size:11px; font-weight:700; padding:2px 9px; border-radius:999px; border:1px solid #f4c8dd; color:#9d174d; background:#fff; white-space:nowrap; }
.cu-chip.on { background:#ec4899; border-color:#ec4899; color:#fff; }
.cu-tabs { display:flex; gap:4px; flex-wrap:wrap; padding:8px 10px; border-bottom:1px solid #f7e3ee; background:#fff; }
.cu-tab { border:1px solid transparent; background:transparent; color:#9d4b74; font-weight:700; font-size:12.5px; padding:6px 13px; border-radius:999px; cursor:pointer; font-family:inherit; }
.cu-tab:hover { background:#fff0f7; }
.cu-tab.active { background:#ec4899; border-color:#ec4899; color:#fff; }
.cu-body { padding:14px 16px 18px; }
.cu-sec { font-size:11.5px; font-weight:800; color:#9d174d; letter-spacing:.8px; margin:18px 0 6px; }
.cu-sec:first-child { margin-top:0; }
.cu-panel { border:1px solid #f7e3ee; border-radius:10px; padding:12px; background:#fffdfe; }
.cu-grid2 { display:grid; grid-template-columns:repeat(auto-fit,minmax(230px,1fr)); gap:12px; }
.cu-row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; }
.cu-col { display:flex; flex-direction:column; gap:2px; }
.cu-label { font-size:11.5px; font-weight:700; color:#9d4b74; margin:9px 0 4px; }
.cu-input, .cu-textarea, .cu-select { width:100%; box-sizing:border-box; border:1px solid #efccdd; border-radius:8px; padding:6px 9px; font-size:12.5px; color:#3f2a38; background:#fff; outline:none; font-family:inherit; }
.cu-input:focus, .cu-textarea:focus, .cu-select:focus { border-color:#ec4899; }
.cu-textarea { resize:vertical; line-height:1.5; font-family:Consolas,ui-monospace,monospace; }
.cu-hint { font-size:11px; color:#b06a8c; margin:4px 0 10px; line-height:1.55; white-space:pre-wrap; }
.cu-detect { font-size:11.5px; color:#9d174d; background:#fff5fa; border:1px solid #f7d3e6; border-radius:8px; padding:8px 10px; margin:8px 0; white-space:pre-wrap; line-height:1.5; }
.cu-btn { border:1px solid #efccdd; background:#fff; color:#9d174d; font-weight:700; font-size:12px; padding:6px 12px; border-radius:8px; cursor:pointer; font-family:inherit; }
.cu-btn:hover { background:#fff0f7; }
.cu-btn.primary { background:#ec4899; border-color:#ec4899; color:#fff; }
.cu-btn.primary:hover { background:#db2777; }
.cu-btn:disabled { opacity:.5; cursor:default; }
.cu-check { display:inline-flex; align-items:center; gap:7px; cursor:pointer; font-size:12.5px; font-weight:700; color:#9d174d; }
.cu-check input { accent-color:#ec4899; width:15px; height:15px; flex:none; }
.cu-check.off { opacity:.45; cursor:not-allowed; }
.cu-list { display:flex; flex-direction:column; gap:8px; }
.cu-item { border:1px solid #f7e3ee; border-radius:10px; padding:10px; background:#fffdfe; }
.cu-item.off { background:#faf7f9; }
.cu-itemhead { display:flex; align-items:center; gap:8px; flex-wrap:wrap; }
.cu-mini { flex:1; min-width:110px; box-sizing:border-box; border:1px solid #efccdd; border-radius:7px; padding:5px 8px; font-size:12px; font-family:inherit; }
.cu-del { margin-left:auto; border:1px solid #f3c9da; background:#fff; color:#be185d; border-radius:7px; cursor:pointer; padding:2px 9px; font-size:12px; }
.cu-mini2 { display:flex; gap:6px; margin-top:7px; align-items:center; }
.cu-drop { margin-top:7px; border:1px solid #efccdd; border-radius:8px; max-height:180px; overflow:auto; background:#fff; }
.cu-dropitem { padding:6px 9px; font-size:12px; cursor:pointer; }
.cu-dropitem:hover { background:#fff0f7; }
.cu-empty { font-size:12px; color:#b06a8c; padding:8px 2px; }
.cu-code { font-family:Consolas,ui-monospace,monospace; word-break:break-all; }
.cu-toggle { display:flex; align-items:center; gap:10px; padding:9px 11px; border:1px solid #f7e3ee; border-radius:10px; background:#fffdfe; cursor:pointer; margin-bottom:6px; }
.cu-toggle:hover { background:#fff7fb; }
.cu-toggle .t { font-size:12.5px; font-weight:700; color:#9d174d; display:flex; align-items:center; gap:6px; }
.cu-toggle .b { font-size:10px; font-weight:700; color:#db2777; border:1px solid #f4c8dd; border-radius:999px; padding:1px 6px; }
.cu-sw { margin-left:auto; width:38px; height:20px; border-radius:999px; background:#edd3e0; position:relative; flex:none; }
.cu-sw::after { content:''; position:absolute; top:2px; left:2px; width:16px; height:16px; border-radius:50%; background:#fff; }
.cu-sw.on { background:#ec4899; }
.cu-sw.on::after { left:20px; }
/* 分辨率卡片（沿用被保留的 AddEditableResoCard 的类名） */
.cfy-reso-card { border:1px solid #f7e3ee; border-radius:12px; padding:11px 12px; background:#fffdfe; }
.cfy-reso-shine { display:none; }
.cfy-reso-tag { font-size:10.5px; font-weight:800; color:#db2777; letter-spacing:1px; }
.cfy-reso-input-row { display:flex; align-items:center; gap:6px; margin:7px 0 5px; }
.cfy-reso-input { width:76px; box-sizing:border-box; border:1px solid #efccdd; border-radius:7px; padding:4px 7px; font-size:12.5px; font-family:inherit; color:#3f2a38; }
.cfy-reso-sep { color:#b06a8c; font-weight:700; }
.cfy-reso-name { font-size:12.5px; font-weight:700; color:#9d174d; }
.cfy-reso-hint { font-size:11px; color:#b06a8c; margin-top:2px; }
/* 节点概览（沿用被保留的 AddNodeOverview 的类名） */
.cfy-node-overview { margin-top:8px; }
.cfy-node-count { font-size:12px; font-weight:700; color:#9d174d; margin-bottom:6px; }
.cfy-node-table { width:100%; border-collapse:collapse; font-size:11.5px; }
.cfy-node-table th, .cfy-node-table td { border-bottom:1px solid #f7e3ee; padding:5px 7px; text-align:left; vertical-align:top; }
.cfy-node-table th { color:#9d174d; font-weight:800; }
.cfy-node-table td.nid { font-family:Consolas,ui-monospace,monospace; color:#db2777; }
.cfy-node-table td.params { font-family:Consolas,ui-monospace,monospace; word-break:break-all; color:#6b4459; }
/* 教程 / 说明 / 链接 */
.cu-note { font-size:11.5px; color:#5b3a4d; background:#fff8fb; border:1px dashed #f6c6dc; border-radius:8px; padding:9px 11px; margin:6px 0; line-height:1.65; white-space:pre-wrap; }
.cu-note b { color:#9d174d; }
.cu-steps { margin:6px 0 10px; padding-left:20px; font-size:12px; color:#5b3a4d; line-height:1.75; }
.cu-steps li { margin-bottom:3px; }
.cu-linkrow { display:flex; align-items:baseline; gap:8px; flex-wrap:wrap; margin:5px 0; font-size:12px; }
.cu-link { color:#db2777; font-weight:700; text-decoration:none; border-bottom:1px dashed #f3c9da; }
.cu-link:hover { color:#9d174d; }
.cu-url { font-family:Consolas,ui-monospace,monospace; font-size:10.5px; color:#9d4b74; background:#fff5fa; border:1px solid #f7d3e6; border-radius:6px; padding:1px 6px; }
.cu-tag { display:inline-block; font-size:10.5px; font-weight:800; color:#db2777; border:1px solid #f4c8dd; border-radius:6px; padding:0 5px; margin-right:5px; }
.cu-figure { margin:8px 0 10px; }
.cu-img { display:block; max-width:100%; height:auto; border:1px solid #f2d7e5; border-radius:10px; background:#fff; }
.cu-cap { font-size:11px; color:#9d4b74; margin-top:5px; line-height:1.55; }
";

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        // 卡片列表惰性加载：不在渲染过程中改 Configuration，避免额外重渲染
        if (!_loadedWorkflowCards) { LoadWorkflowCards(); _loadedWorkflowCards = true; }
        if (!_loadedPresetCards) { LoadPresetCards(); _loadedPresetCards = true; }

        int i = 0;

        b.OpenElement(i++, "style");
        b.AddContent(i++, Css);
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-root");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-card");

        // ---------- 头部 ----------
        var appMcp = IsAppMcp();
        var configured = !string.IsNullOrWhiteSpace(Configuration.BaseUrl)
                         && (appMcp || !string.IsNullOrWhiteSpace(Configuration.WorkflowPath));

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-head");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cu-title");
        b.AddContent(i++, "ComfyUI 生图");
        b.CloseElement();
        AddChip(b, ref i, configured ? "已配置" : "未配置", configured);
        AddChip(b, ref i, appMcp ? "APP-MCP 模板" : "工作流模式", appMcp);
        var styleCount = 0;
        try
        {
            if (JsonNode.Parse(Configuration.StylePresets ?? "[]") is JsonArray styleArr)
                styleCount = styleArr.Count;
        }
        catch { }
        if (styleCount > 0)
            AddChip(b, ref i, $"画风 {styleCount}", true);
        if (Configuration.EnableDanbooruSearch)
            AddChip(b, ref i, "在线标签", true);
        if (Configuration.EnableAnimadexCharacterSearch)
            AddChip(b, ref i, "在线角色", true);
        b.CloseElement(); // head

        // ---------- 页签（按后端模式生成） ----------
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-tabs");
        foreach (var (tabId, tabTitle) in EffectiveTabs())
        {
            var id = tabId;
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", _tab == id ? "cu-tab active" : "cu-tab");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async e =>
            {
                _tab = id;
                if (id == 5 && Configuration.AdvancedMode)
                    await LoadNodeOverview();
            }));
            b.AddContent(i++, tabTitle);
            b.CloseElement();
        }
        b.CloseElement(); // tabs

        // ---------- 内容：只渲染当前页签（模式切换后若停在已隐藏页签，回落到基础页） ----------
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-body");
        var workflowTabUsable = !appMcp;
        switch (_tab)
        {
            case 1: if (workflowTabUsable) RenderWorkflowTab(b); else RenderBasicTab(b); break;
            case 2: RenderBackendTab(b); break;
            case 3: RenderPromptTab(b); break;
            case 4: RenderSearchTab(b); break;
            case 5: RenderAdvancedTab(b); break;
            default: RenderBasicTab(b); break;
        }
        b.CloseElement(); // body

        b.CloseElement(); // card
        b.CloseElement(); // root
    }

    bool IsAppMcp()
        => string.Equals((Configuration?.BackendMode ?? "").Trim(), "appmcp", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // 页签 1：基础（连接 / 保存 / 分辨率 / 默认参数 / 生图行为）
    // ============================================================
    void RenderBasicTab(RenderTreeBuilder b)
    {
        int i = 1000;
        var appMcp = IsAppMcp();

        AddSection(b, ref i, "快速开始（新手看这里）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddSteps(b, ref i,
            "启动 ComfyUI（默认地址 http://127.0.0.1:8188），确认下面「连接」里的地址与它一致。",
            appMcp
                ? "「后端 · 画风」页填好 APP-MCP 的默认模板名，点「测试连通并列出模板」；若提示连接失败，多半是还没装 APP-MCP 节点——该页有安装教程和链接。"
                : "「工作流」页填工作流 JSON 路径（可点「扫描目录」从列表里选），再点「自动识别节点」。",
            "让角色生图即可。想要一个工作流出多种画风，就去「后端 · 画风」页配「画风预设」。");
        AddHint(b, ref i, "改完任何配置记得保存（用框架提供的保存入口）；切换生图后端后需要重载本模块，新设置与新提示词才会生效。");
        b.CloseElement();

        AddSection(b, ref i, "连接");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "ComfyUI 地址", Configuration.BaseUrl, v => Configuration.BaseUrl = v);
        AddInput(b, ref i, "API Token（可选）", Configuration.ApiToken, v => Configuration.ApiToken = v);
        b.CloseElement();
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "图片保存目录（额外副本时用）", Configuration.SaveDirectory, v => Configuration.SaveDirectory = v);
        AddInput(b, ref i, "ComfyUI output 目录（可选）", Configuration.ComfyuiOutputPath, v => Configuration.ComfyuiOutputPath = v);
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i,
            "地址例如 http://127.0.0.1:8188（端口不是 8188 就填你自己的）。\n"
            + "这个地址同时用于：在线状态检查、图生图上传、图片下载；开启模板模式且未填 APP-MCP 地址时，也会由它自动推导。\n"
            + "「ComfyUI output 目录」填绝对路径后，标准 SaveImage 的图片直接引用该文件，不再重复下载。\n"
            + "「API Token」只有你的 ComfyUI 藏在带鉴权的反代后面才需要，普通本地使用留空即可。");
        b.CloseElement();

        AddSection(b, ref i, "图片落盘");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddCheck(b, ref i, "额外保存副本（复制到「图片保存目录」；关闭则直接用工作流保存路径）",
            Configuration.ExtraSaveCopy, v => Configuration.ExtraSaveCopy = v);
        var resolvedSaveDir = string.IsNullOrWhiteSpace(Configuration.SaveDirectory)
            ? Path.Combine(AlifePath.StorageFolderPath, "Images", "Comfyui")
            : Configuration.SaveDirectory;
        AddHint(b, ref i, Configuration.ExtraSaveCopy
            ? $"开启：出图后会把图片复制一份到「图片保存目录」（留空则用默认目录）。\n当前实际目录：{resolvedSaveDir}"
            : $"关闭：直接使用工作流/模板自带的保存路径，不额外复制。\n「图片保存目录」当前为：{resolvedSaveDir}");
        AddHint(b, ref i, "提示：图片路径会被插件交给 AI 用于发图（QQ 环境用 qimage 标签），所以目录别设成需要权限的位置。");
        b.CloseElement();

        if (appMcp)
        {
            // 模板模式：尺寸与等待由模板 / APP-MCP 决定 —— 工作流模式的这些项自动隐藏
            AddSection(b, ref i, "尺寸与等待（模板模式）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddHint(b, ref i,
                "以下工作流模式专用项在模板模式下不参与，已自动隐藏：分辨率预设、默认方向、兜底宽高、等待出图超时、轮询间隔。\n"
                + "· 画面尺寸由模板决定；若模板声明了 width / height / 宽 / 高 输入，可用 generateimage 的 params 传。\n"
                + "· 等待出图超时请在「后端 · 画风」页签的 APP-MCP 设置里调整。");
            b.CloseElement();
        }
        else
        {
            AddSection(b, ref i, "分辨率预设");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            AddEditableResoCard(b, ref i, "PORTRAIT", "竖版", "全身立绘 · 人物 · 手机壁纸",
                Configuration.PortraitWidth, v => Configuration.PortraitWidth = v,
                Configuration.PortraitHeight, v => Configuration.PortraitHeight = v);
            AddEditableResoCard(b, ref i, "LANDSCAPE", "横版", "风景 · 场景 · 横构图",
                Configuration.LandscapeWidth, v => Configuration.LandscapeWidth = v,
                Configuration.LandscapeHeight, v => Configuration.LandscapeHeight = v);
            AddEditableResoCard(b, ref i, "SQUARE", "正方形", "头像 · 图标 · 对称构图",
                Configuration.SquareWidth, v => Configuration.SquareWidth = v,
                Configuration.SquareHeight, v => Configuration.SquareHeight = v);
            b.CloseElement();
            AddHint(b, ref i, "AI 传 orientation=portrait/landscape/square 智能选档；也可直接指定 width/height 覆盖。数值改完点空白处生效。");

            AddSection(b, ref i, "默认参数");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            b.OpenElement(i++, "div");
            AddSelect(b, ref i, "默认方向", Configuration.DefaultOrientation, v => Configuration.DefaultOrientation = v, new[]
            {
                ("portrait", $"竖版 {Configuration.PortraitWidth}×{Configuration.PortraitHeight}"),
                ("landscape", $"横版 {Configuration.LandscapeWidth}×{Configuration.LandscapeHeight}"),
                ("square", $"正方形 {Configuration.SquareWidth}×{Configuration.SquareHeight}")
            });
            AddHint(b, ref i, "AI 未传 orientation 时使用");
            AddInput(b, ref i, "兜底宽度", Configuration.DefaultWidth.ToString(), SetInt(v => Configuration.DefaultWidth = v));
            AddInput(b, ref i, "兜底高度", Configuration.DefaultHeight.ToString(), SetInt(v => Configuration.DefaultHeight = v));
            b.CloseElement();
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "等待出图超时（秒）", Configuration.TimeoutSeconds.ToString(), SetInt(v => Configuration.TimeoutSeconds = v));
            AddHint(b, ref i, "普通模式等待上限");
            AddInput(b, ref i, "轮询间隔（毫秒）", Configuration.PollIntervalMs.ToString(), SetInt(v => Configuration.PollIntervalMs = v));
            AddHint(b, ref i, "500~10000");
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }

        AddSection(b, ref i, "生图行为");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddCheck(b, ref i, "生完图自动用系统默认图片查看器打开（仅桌面端）", Configuration.AutoOpenImage,
            v => Configuration.AutoOpenImage = v);
        AddCheck(b, ref i, "优先生图：生图开始后等图完再返回（同机本地 TTS 建议开）", Configuration.PriorityImageGen,
            v => Configuration.PriorityImageGen = v);
        if (Configuration.PriorityImageGen)
        {
            AddInput(b, ref i, "硬超时上限（秒）", Configuration.PriorityMaxWaitSeconds.ToString(),
                SetInt(v => Configuration.PriorityMaxWaitSeconds = Math.Clamp(v, 30, 1800)));
            AddHint(b, ref i, "实际等待 = min(等待出图超时, 本上限)，超时强制结束避免卡死对话。");
        }
        AddHint(b, ref i, "本插件不依赖任何语音插件；优先生图只是避免同机 TTS 与 Comfy 抢 GPU。");
        b.CloseElement();
    }

    // ============================================================
    // 页签 2：工作流（默认工作流 / 扫描 / 识别 / 多工作流）
    // ============================================================
    void RenderWorkflowTab(RenderTreeBuilder b)
    {
        int i = 2000;

        if (IsAppMcp())
        {
            AddHint(b, ref i, "当前后端是「APP-MCP 模板模式」：本页签的工作流配置不参与生图（保留供切回工作流模式使用）。");
        }

        AddSection(b, ref i, "默认工作流");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddInput(b, ref i, "工作流 JSON 路径（绝对路径或相对插件目录）", Configuration.WorkflowPath,
            v => Configuration.WorkflowPath = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, ScanWorkflows));
        b.AddContent(i++, "扫描目录");
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, AutoDetectNodes));
        b.AddContent(i++, "自动识别节点");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i,
            "「扫描目录」会在路径所在目录 / ComfyUI 的 workflows 目录中列出可用工作流；「自动识别节点」扫描当前工作流的正/负/分辨率节点并回填。\n"
            + "支持两种文件：ComfyUI 网页里直接保存的 UI 工作流 JSON（插件会自动转成 API 格式），以及官方 Export (API) 导出的 API JSON。\n"
            + "填相对路径时按「插件目录」解析；填绝对路径最稳妥。");

        if (!string.IsNullOrWhiteSpace(_scanDetectMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-detect");
            b.AddContent(i++, _scanDetectMessage);
            b.CloseElement();
        }
        if (_showWorkflowDropdown && _scannedWorkflows.Count > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-drop");
            foreach (var wf in _scannedWorkflows)
            {
                var picked = wf;
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-dropitem");
                b.AddAttribute(i++, "title", picked);
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                {
                    Configuration.WorkflowPath = picked;
                    _showWorkflowDropdown = false;
                }));
                b.AddContent(i++, GetWorkflowDisplayName(picked, _scanDir));
                b.CloseElement();
            }
            b.CloseElement();
        }
        if (!string.IsNullOrWhiteSpace(detectMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-detect");
            b.AddContent(i++, detectMessage);
            b.CloseElement();
        }
        b.CloseElement();

        AddSection(b, ref i, "节点映射（留空自动识别）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "正向提示词节点 ID", Configuration.PositivePromptNodeId, v => Configuration.PositivePromptNodeId = v);
        AddInput(b, ref i, "正向提示词字段名", Configuration.PositivePromptInput, v => Configuration.PositivePromptInput = v);
        AddInput(b, ref i, "分辨率节点 ID", Configuration.ResolutionNodeId, v => Configuration.ResolutionNodeId = v);
        b.CloseElement();
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "负面提示词节点 ID", Configuration.NegativePromptNodeId, v => Configuration.NegativePromptNodeId = v);
        AddInput(b, ref i, "负面提示词字段名", Configuration.NegativePromptInput, v => Configuration.NegativePromptInput = v);
        AddInput(b, ref i, "图生图 LoadImage 节点 ID", Configuration.LoadImageNodeId, v => Configuration.LoadImageNodeId = v);
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "手动节点 ID 仅作用于默认工作流；命名工作流会按各自连接关系重新识别。");
        b.CloseElement();

        AddSection(b, ref i, "多工作流（可选）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddHint(b, ref i, "启用后 AI 可通过 workflow 参数切换；每个工作流可单独设固定正向前缀（留空沿用全局）。名称需唯一且路径有效。");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-list");
        for (var ci = 0; ci < _workflowCards.Count; ci++)
        {
            var cardIndex = ci;
            var card = _workflowCards[ci];
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", card.Enabled ? "cu-item" : "cu-item off");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-itemhead");

            b.OpenElement(i++, "label");
            b.AddAttribute(i++, "class", "cu-check");
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "type", "checkbox");
            b.AddAttribute(i++, "checked", card.Enabled);
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                ToggleWorkflowCardEnabled(cardIndex)));
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddContent(i++, "启用");
            b.CloseElement();
            b.CloseElement();

            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cu-mini");
            b.AddAttribute(i++, "value", card.Name);
            b.AddAttribute(i++, "placeholder", "名称");
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                UpdateWorkflowCardName(cardIndex, e.Value?.ToString() ?? "")));
            b.CloseElement();

            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cu-del");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                RemoveWorkflowCard(cardIndex)));
            b.AddContent(i++, "✕");
            b.CloseElement();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-mini2");
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cu-mini");
            b.AddAttribute(i++, "value", card.Path);
            b.AddAttribute(i++, "placeholder", "工作流 JSON 路径…");
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                UpdateWorkflowCardPath(cardIndex, e.Value?.ToString() ?? "")));
            b.CloseElement();
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cu-btn");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                ToggleWorkflowScan(cardIndex)));
            b.AddContent(i++, "浏览");
            b.CloseElement();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-mini2");
            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "class", "cu-mini");
            b.AddAttribute(i++, "value", card.Prefix);
            b.AddAttribute(i++, "placeholder", "固定正向前缀（留空沿用全局）");
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                UpdateWorkflowCardPrefix(cardIndex, e.Value?.ToString() ?? "")));
            b.CloseElement();
            b.CloseElement();

            if (_activeScanCardIndex == ci && _scannedWorkflows.Count > 0)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-drop");
                foreach (var wf in _scannedWorkflows)
                {
                    var wfPath = wf;
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-dropitem");
                    b.AddAttribute(i++, "title", wfPath);
                    b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
                    {
                        UpdateWorkflowCardPath(cardIndex, wfPath);
                        _activeScanCardIndex = -1;
                    }));
                    b.AddContent(i++, Path.GetFileName(wfPath));
                    b.CloseElement();
                }
                b.CloseElement();
            }

            b.CloseElement(); // item
        }
        if (_workflowCards.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-empty");
            b.AddContent(i++, "暂无命名工作流（不添加则只用默认工作流）");
            b.CloseElement();
        }
        b.CloseElement(); // list

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.AddAttribute(i++, "style", "margin-top:10px;");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => AddWorkflowCard()));
        b.AddContent(i++, "+ 添加工作流");
        b.CloseElement();
        if (_scannedWorkflows.Count == 0)
            AddHint(b, ref i, "提示：先点上方「扫描目录」可把工作流列出来直接选。");
        b.CloseElement();
        b.CloseElement();
    }

    // ============================================================
    // 页签 3：后端 · 画风（工作流/APP-MCP、画风预设、显存）
    // ============================================================
    void RenderBackendTab(RenderTreeBuilder b)
    {
        int i = 3000;
        var appMcp = IsAppMcp();

        AddSection(b, ref i, "生图后端");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", appMcp ? "cu-btn" : "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => Configuration.BackendMode = "workflow"));
        b.AddContent(i++, "工作流模式（原有，默认）");
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", appMcp ? "cu-btn primary" : "cu-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => Configuration.BackendMode = "appmcp"));
        b.AddContent(i++, "APP-MCP 模板模式");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "切换后端后请「重载本模块」（面板的重载入口，或让 AI 调 reload_plugin），AI 提示词与函数说明才会按新模式生效。模板模式还依赖 ComfyUI 先装上第三方节点 APP-MCP，见下方教程。");
        b.CloseElement();

        if (appMcp)
        {
            AddSection(b, ref i, "APP-MCP 模板设置");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddInput(b, ref i, "APP-MCP 地址", Configuration.AppMcpUrl, v => Configuration.AppMcpUrl = v);
            AddHint(b, ref i, "可填 http://127.0.0.1:8188/app-mcp 或 http://127.0.0.1:8188/mcp-server/api；留空自动由「ComfyUI 地址」推导。");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "默认模板名", Configuration.AppMcpTemplate, v => Configuration.AppMcpTemplate = v);
            AddHint(b, ref i, "如 913流；AI 也可用 template 参数切换。");
            b.CloseElement();
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "提示词输入参数名", Configuration.AppMcpPromptParam, v => Configuration.AppMcpPromptParam = v);
            AddHint(b, ref i, "默认 positive；留空则自动取第一个字符串输入。");
            b.CloseElement();
            b.CloseElement();

            AddTextArea(b, ref i, "模板默认参数（JSON 对象）", Configuration.AppMcpDefaultParams,
                v => Configuration.AppMcpDefaultParams = v, 3);
            AddHint(b, ref i, "示例 {\"参数名\": 值}；只有模板已声明的输入才生效，AI 可用 params 覆盖。");

            AddInput(b, ref i, "等待出图超时（秒）", Configuration.AppMcpTimeoutSeconds.ToString(),
                SetInt(v => Configuration.AppMcpTimeoutSeconds = Math.Clamp(v, 30, 1800)));

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-row");
            b.AddAttribute(i++, "style", "margin-top:10px;");
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cu-btn");
            b.AddAttribute(i++, "disabled", _appMcpTesting);
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestAppMcp));
            b.AddContent(i++, _appMcpTesting ? "测试中…" : "测试连通并列出模板");
            b.CloseElement();
            b.CloseElement();
            if (!string.IsNullOrWhiteSpace(_appMcpTestMessage))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-detect");
                b.AddContent(i++, _appMcpTestMessage);
                b.CloseElement();
            }
            b.CloseElement();
        }
        else
        {
            AddHint(b, ref i,
                "原有模式：直连 ComfyUI /prompt，读取「工作流」页签里的 JSON，行为完全不变。\n"
                + "想用「APP-MCP 模板模式」？它需要先在 ComfyUI 里安装第三方开源节点 ComfyUI-APP-MCP（免费开源）。点上面那个「APP-MCP 模板模式」按钮，本页就会出现完整安装教程、新手避坑清单与官方文档链接。");
        }

        // 模板模式的第三方节点：安装教程 + 避坑 + 文档链接（小白向，只在模板模式显示）
        if (appMcp)
        {
            AddSection(b, ref i, "① 安装 APP-MCP 节点（第三方，模板模式依赖它）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddNote(b, ref i, "本插件只是「调用方」：模板模式必须让 ComfyUI 先装上第三方开源节点 ComfyUI-APP-MCP，否则下面的「测试连通」一定失败。");
            AddSteps(b, ref i,
                "装节点（二选一）：① ComfyUI Manager → 搜索 app mode mcp → 安装（推荐）；② 手动：进 ComfyUI/custom_nodes 执行 git clone https://github.com/Lotus0614/ComfyUI-APP-MCP.git，再按该仓库 README 安装 requirements.txt 依赖（Windows 便携包要用包内 python_embeded\\python.exe）。",
                "重启 ComfyUI（必须），确认启动日志没有报错。",
                "打开你要用的工作流 → 左上角菜单进 App Builder → 把 AI 要填的控件标记为「输入」并起清晰参数名（例如 positive）→ 把保存图片的节点标记为「输出」。",
                "在该工作流里加一个 Markdown Note：title 填模板短名（例如 913流），description 填说明（可留空）。",
                "工作流用 Save 保存（不是 Export）；再进 ComfyUI 的 Settings → MCP Server → Templates → Create from Workflow 创建模板。",
                "回到本插件：填「默认模板名」→ 点「测试连通并列出模板」，能看到模板与输入明细就算成功。");
            AddImage(b, ref i, UiAssets.AppBuilderEntryPng,
                "找不到 App Builder？看这张图：ComfyUI 左上角「图形」下拉左边的那个方框图标就是 App Builder 入口（对应上面第 3 步，点它展开输入/输出标记面板）。");
            b.CloseElement();

            AddSection(b, ref i, "② 新手避坑（最常见的 6 个坑）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddNote(b, ref i,
                "1) 参数名必须完全一致：AI 传的名字要和模板输入名一字不差（含中文、大小写）；改名后要 Refresh 模板并重载本模块。\n"
                + "2) 自定义 UI 选项不能当输入：要标记的是它上方的「数据内容输入框」。例如 ZML 强力 LoRA 加载器需要暴露 lora_loader_data（本插件正是靠它切换画风组）。\n"
                + "3) 模板的「输出」必须包含保存图片节点，否则插件拿不到图（会提示「未找到输出图片」）。\n"
                + "4) 模板列表为空？工作流要用 Save 保存，且模板不能在 Templates 里被禁用。\n"
                + "5) 改过工作流：在 Templates 里对同名模板点 Refresh，然后重载本模块。\n"
                + "6) 想让每次自动随机 seed：把那个输入命名为 seed，运行时自动填随机值，AI 不用传。");
            b.CloseElement();

            AddSection(b, ref i, "③ 官方文档与源码（点不开可直接复制地址）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddLinkRow(b, ref i, "ComfyUI-APP-MCP 仓库（GitHub，含安装说明）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP");
            AddLinkRow(b, ref i, "《工具参考》中文文档（模板/参数/输出格式）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/tools.md");
            AddLinkRow(b, ref i, "《故障排查》中文文档（装不上/没输出/参数报错）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/troubleshooting.md");
            AddLinkRow(b, ref i, "《独立部署与远程访问》文档",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/standalone.md");
            AddHint(b, ref i,
                "· 仓库地址以 Lotus0614 为准（旧链接 Luo-Lotus 会自动跳转，能打开但不是规范地址）。\n"
                + "· 本插件走它的 REST 接口（/mcp-server/api），不需要你再配任何 MCP 客户端。\n"
                + "· ComfyUI 端口不是 8188 也能用：地址填 http://127.0.0.1:<你的端口>/app-mcp 即可。");
            b.CloseElement();
        }

        // 画风预设只在 APP-MCP 模板模式下有意义（一个模板切换多种画风）；工作流模式不显示。
        if (appMcp)
        {
            AddSection(b, ref i, "画风预设（一个模板切换多种画风）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddTextArea(b, ref i, "画风预设（JSON 数组）", Configuration.StylePresets,
                v => Configuration.StylePresets = v, 8);
            AddHint(b, ref i,
                "最简用法：只写 ZML 组名 —— [{\"n\":\"示例组名\",\"g\":\"示例组名\"}]（只开该组即一种画风，数据由插件从当前模板读取）。\n"
                + "精细用法：可选 \"t\" 模板名、\"f\" 附加前缀、\"p\" 模板输入（如 {\"lora_loader_data\":\"……\"}）。\n"
                + "AI 在 generateimage 里传 style=\"画风名\" 切换；它也能用 getcomfyuistyle 查看可用的 ZML 组名。");
            b.CloseElement();
        }

        AddSection(b, ref i, "模型卸载与显存释放");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn");
        b.AddAttribute(i++, "disabled", _unloadingModel);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, UnloadModels));
        b.AddContent(i++, _unloadingModel ? "卸载中…" : "立即卸载模型（释放显存）");
        b.CloseElement();
        if (!string.IsNullOrWhiteSpace(_unloadMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-detect");
            b.AddAttribute(i++, "style", "margin:0;");
            b.AddContent(i++, _unloadMessage);
            b.CloseElement();
        }
        b.CloseElement();

        AddCheck(b, ref i, "空闲自动卸载：距上次生图空闲超时后自动卸载模型", Configuration.EnableAutoUnload,
            v => Configuration.EnableAutoUnload = v);
        if (Configuration.EnableAutoUnload)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-row");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "width:150px;");
            AddInput(b, ref i, "空闲小时数", Configuration.AutoUnloadIdleHours.ToString(),
                SetInt(v => Configuration.AutoUnloadIdleHours = Math.Clamp(v, 0, 720)));
            b.CloseElement();
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "style", "width:150px;");
            AddInput(b, ref i, "空闲分钟数", Configuration.AutoUnloadIdleMinutes.ToString(),
                SetInt(v => Configuration.AutoUnloadIdleMinutes = Math.Clamp(v, 0, 59)));
            b.CloseElement();
            b.CloseElement();
            AddHint(b, ref i, $"合计空闲 {Configuration.AutoUnloadIdleHours} 小时 {Configuration.AutoUnloadIdleMinutes} 分钟后自动卸载（需保存配置后生效）。");
        }
        b.CloseElement();
    }

    // ============================================================
    // 页签 4：提示词（种类 / 前缀 / 负面 / 预设）
    // ============================================================
    void RenderPromptTab(RenderTreeBuilder b)
    {
        int i = 4000;
        var appMcp = IsAppMcp();

        AddSection(b, ref i, "提示词种类");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddSelect(b, ref i, "提示词种类", Configuration.PromptStyle, v => Configuration.PromptStyle = v, new[]
        {
            ("tag", "纯 Tag — 全小写英文标签，逗号分隔"),
            ("natural", "自然语言 — 角色 Tag 置前 + 英文短句"),
            ("hybrid", "混合模式 — 静态用标签，动作/关系用短句")
        });
        AddHint(b, ref i, "控制 AI 落笔格式；与固定前缀叠加时仍会自动去重。AI 默认不写画风/质量词（由前缀或工作流决定）。");
        b.CloseElement();

        AddSection(b, ref i, "固定前缀与负面（两个模式通用）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");

        if (appMcp)
        {
            AddCheck(b, ref i, "模板模式下也拼接上面的固定正向提示词前缀", Configuration.AppMcpUseGlobalPrefix,
                v => Configuration.AppMcpUseGlobalPrefix = v);
            AddHint(b, ref i, "默认开（两个模式行为一致）。若模板内部已带画风/质量前缀（如 913流 的节点 52），关闭可避免重复。");
        }

        AddTextArea(b, ref i, "固定正向提示词前缀（逗号或换行均可）",
            Configuration.PositivePromptPrefix, v => Configuration.PositivePromptPrefix = v, 5);
        AddHint(b, ref i, appMcp
            ? "拼在 prompt 最前，与 AI 提示词合并去重后统一英文逗号+空格；留空则不拼接。"
            : "与 AI 提示词合并后自动去重，统一英文逗号+空格。命名工作流可在「工作流」页签各自设前缀；留空则不拼接。");

        AddTextArea(b, ref i, "固定负面提示词", Configuration.NegativePrompt, v => Configuration.NegativePrompt = v, 3);
        AddHint(b, ref i, appMcp
            ? "模板模式下仅当模板声明了 negative / 负面提示词 输入时才会写入；留空则不发送。"
            : "留空=用工作流自带负面；填写则覆盖并规范为英文逗号分隔。");
        b.CloseElement();

        AddSection(b, ref i, "提示词预设");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddHint(b, ref i, "保存常用片段（角色人设/动作/背景），AI 用 getpromptpreset 检索复用。数据存于 Storage/Config/Alife.Plugin.Comfyui/（插件更新不会清空）。");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        b.OpenElement(i++, "div");
        AddInput(b, ref i, "预设名称", _newPresetName, v => _newPresetName = v);
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;align-items:flex-end;");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => AddPresetCard()));
        b.AddContent(i++, "保存预设");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();
        AddTextArea(b, ref i, "预设内容", _newPresetContent, v => _newPresetContent = v, 3);

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-list");
        for (var pi = 0; pi < _presetCards.Count; pi++)
        {
            var presetIndex = pi;
            var preset = _presetCards[pi];
            var expanded = _activePresetIndex == pi;

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-item");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-itemhead");
            b.AddAttribute(i++, "style", "cursor:pointer;");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => TogglePresetExpand(presetIndex)));
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cu-label");
            b.AddAttribute(i++, "style", "margin:0;");
            b.AddContent(i++, preset.Name);
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cu-empty");
            b.AddAttribute(i++, "style", "flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;");
            var preview = preset.Content.Length > 70 ? preset.Content[..70] + "…" : preset.Content;
            b.AddContent(i++, preview);
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cu-label");
            b.AddAttribute(i++, "style", "margin:0;");
            b.AddContent(i++, expanded ? "收起" : "展开");
            b.CloseElement();
            b.CloseElement();

            if (expanded)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "style", "margin-top:8px;");
                AddInput(b, ref i, "名称", preset.Name, v => UpdatePresetCardName(presetIndex, v));
                AddTextArea(b, ref i, "内容", preset.Content, v => UpdatePresetCardContent(presetIndex, v), 5);
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-row");
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "cu-del");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => RemovePresetCard(presetIndex)));
                b.AddContent(i++, "删除");
                b.CloseElement();
                b.CloseElement();
                b.CloseElement();
            }
            b.CloseElement();
        }
        if (_presetCards.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-empty");
            b.AddContent(i++, "暂无预设。填名称+内容后点「保存预设」，或让 AI 用 savepromptpreset 存。");
            b.CloseElement();
        }
        b.CloseElement();
        b.CloseElement();
    }

    // ============================================================
    // 页签 5：检索（在线标签 / 在线角色）
    // ============================================================
    void RenderSearchTab(RenderTreeBuilder b)
    {
        int i = 5000;

        AddSection(b, ref i, "在线 Danbooru 语义标签检索");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddCheck(b, ref i, "启用语义标签检索（search / related）", Configuration.EnableDanbooruSearch,
            v => Configuration.EnableDanbooruSearch = v);
        AddCheck(b, ref i, "启用画师推荐（额外外网调用，依赖总开关）", Configuration.EnableDanbooruArtistRecommend,
            v => Configuration.EnableDanbooruArtistRecommend = v,
            enabled: Configuration.EnableDanbooruSearch);
        AddHint(b, ref i, "默认关=零外网。有服装/姿势/场景等细节时 AI 可检索标准 tag；失败会自写英文仍可生图。需重载模块后函数才注册。");

        if (Configuration.EnableDanbooruSearch)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "主源 URL", Configuration.DanbooruSearchPrimaryUrl, v => Configuration.DanbooruSearchPrimaryUrl = v);
            AddHint(b, ref i, "默认官方备份域（大陆通常更快）；自建后不再自动回退备用。");
            AddInput(b, ref i, "超时秒数（总预算）", Configuration.DanbooruSearchTimeoutSeconds.ToString(),
                SetInt(v => Configuration.DanbooruSearchTimeoutSeconds = Math.Clamp(v, 10, 120)));
            b.CloseElement();
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "备用 URL（可空）", Configuration.DanbooruSearchFallbackUrl, v => Configuration.DanbooruSearchFallbackUrl = v);
            AddHint(b, ref i, "默认 HF Space，可能冷启 30–60s。");
            AddCheck(b, ref i, "包含 NSFW 标签（默认关，用 SFW）", Configuration.DanbooruSearchShowNsfw,
                v => Configuration.DanbooruSearchShowNsfw = v);
            b.CloseElement();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-row");
            b.AddAttribute(i++, "style", "margin-top:10px;");
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cu-btn");
            b.AddAttribute(i++, "disabled", _danbooruTesting);
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestDanbooruConnectivity));
            b.AddContent(i++, _danbooruTesting ? "测试中…" : "测试连通");
            b.CloseElement();
            b.CloseElement();
            if (!string.IsNullOrWhiteSpace(_danbooruTestMessage))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-detect");
                b.AddContent(i++, _danbooruTestMessage);
                b.CloseElement();
            }
            AddHint(b, ref i, "使用公开服务时请友情链接上游（点不开可直接复制地址）：");
            AddLinkRow(b, ref i, "DanbooruSearch 在线服务（Hugging Face Space）",
                "https://huggingface.co/spaces/SAkizuki/DanbooruSearch");
        }
        b.CloseElement();

        AddSection(b, ref i, "在线角色检索扩充（AnimaDex）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddCheck(b, ref i, "启用 AnimaDex 在线角色检索", Configuration.EnableAnimadexCharacterSearch,
            v => Configuration.EnableAnimadexCharacterSearch = v);
        AddHint(b, ref i, "仅作本地索引补充：本地未命中、或带作品名仍歧义时联网查约 3.6 万角色。本地命中仍优先，默认关=零外网。");

        if (Configuration.EnableAnimadexCharacterSearch)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "服务地址", Configuration.AnimadexBaseUrl, v => Configuration.AnimadexBaseUrl = v);
            AddHint(b, ref i, "默认 animadex.net（大陆直连通常约 1s）。");
            b.CloseElement();
            b.OpenElement(i++, "div");
            AddInput(b, ref i, "超时秒数", Configuration.AnimadexTimeoutSeconds.ToString(),
                SetInt(v => Configuration.AnimadexTimeoutSeconds = Math.Clamp(v, 5, 60)));
            b.CloseElement();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-row");
            b.AddAttribute(i++, "style", "margin-top:10px;");
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", "cu-btn");
            b.AddAttribute(i++, "disabled", _animadexTesting);
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestAnimadexConnectivity));
            b.AddContent(i++, _animadexTesting ? "测试中…" : "测试连通");
            b.CloseElement();
            b.CloseElement();
            if (!string.IsNullOrWhiteSpace(_animadexTestMessage))
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-detect");
                b.AddContent(i++, _animadexTestMessage);
                b.CloseElement();
            }
            AddHint(b, ref i, "AnimaDex 不支持中文检索：中文角色名本地未收录时会提示改用英文/罗马字。");
        }
        b.CloseElement();
    }

    // ============================================================
    // 页签 6：高级（注入方式 / 节点控制 / 节点概览）
    // ============================================================
    void RenderAdvancedTab(RenderTreeBuilder b)
    {
        int i = 6000;

        var appMcp = IsAppMcp();

        AddSection(b, ref i, "AI 注入方式");
        AddToggle(b, ref i, "隐式注入（省 token）", "4.0", Configuration.ImplicitInjection,
            v => Configuration.ImplicitInjection = v);
        AddHint(b, ref i, "开启后函数文档不直接注入系统提示词，AI 先调用 <comfyuiimagegeneration/> 按需加载；关闭为显式注入（默认）。改动需重载模块后生效。");

        if (appMcp)
        {
            AddSection(b, ref i, "工作流节点相关");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-panel");
            AddHint(b, ref i,
                "「AI 节点控制」与「工作流节点概览」在 APP-MCP 模板模式下不适用（模板不使用工作流节点），"
                + "已自动隐藏，相关规则也已从 AI 提示词中移除；切回工作流模式即恢复。");
            b.CloseElement();
        }
        else
        {
            AddToggle(b, ref i, "AI 节点控制", "ADVANCED", Configuration.EnableNodeControl,
                v => Configuration.EnableNodeControl = v);
            AddHint(b, ref i, "开启后 AI 可直接操控工作流节点参数（换模型/步数/CFG/采样器等）。不开启则保持原有简单模式。");

            AddToggle(b, ref i, "工作流节点概览", "BETA", Configuration.AdvancedMode,
                v => Configuration.AdvancedMode = v,
                afterToggleAsync: async () =>
                {
                    if (Configuration.AdvancedMode)
                        await LoadNodeOverview();
                });
            AddHint(b, ref i, "开启后展示工作流全部节点类型与关键参数，便于排查问题或手动配置节点映射。");

            if (Configuration.AdvancedMode)
            {
                AddSection(b, ref i, "节点概览");
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-panel");
                if (!string.IsNullOrWhiteSpace(_nodeOverviewError))
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-detect");
                    b.AddContent(i++, _nodeOverviewError);
                    b.CloseElement();
                }
                else if (_nodeOverview.Count == 0)
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-empty");
                    b.AddContent(i++, "尚未加载。切换一次本页签或关闭再开启上面开关即可加载。");
                    b.CloseElement();
                }
                AddNodeOverview(b, ref i);
                b.CloseElement();
            }
        }

        AddSection(b, ref i, "关于与依赖");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-panel");
        AddHint(b, ref i,
            "ComfyUI × Alife · Doro 的妙妙工具。\n"
            + "配置改完请用面板的保存入口应用；切换生图后端后需要重载本模块才会生效。\n"
            + "本插件可在 Alife 的「插件市场」里搜索安装/更新。\n"
            + "提示词预设存放目录（插件更新不会清空）：\n" + ComfyuiService.GetUserDataDirectory());
        AddHint(b, ref i,
            "可选依赖（按需，装完请重载本模块）：\n"
            + "· 模板模式 → ComfyUI 侧需装第三方节点 ComfyUI-APP-MCP（「后端 · 画风」页有安装教程与链接）。\n"
            + "· 工作流用到的自定义节点（ZML / WeiLin / Crystools / DLSS 等）必须在 ComfyUI 侧已安装，工作流才能在那边跑通。");
        AddLinkRow(b, ref i, "ComfyUI-APP-MCP（模板模式依赖，GitHub）",
            "https://github.com/Lotus0614/ComfyUI-APP-MCP");
        AddLinkRow(b, ref i, "在线标签检索上游 DanbooruSearchOnline（MIT）",
            "https://github.com/SuzumiyaAkizuki/DanbooruSearchOnline");
        AddLinkRow(b, ref i, "在线角色检索上游 AnimaDex", "https://animadex.net");
        b.CloseElement();
    }

    // ============================================================
    // 轻量渲染辅助
    // ============================================================
    void AddSection(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-sec");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddHint(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-hint");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddChip(RenderTreeBuilder b, ref int seq, string text, bool on)
    {
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", on ? "cu-chip on" : "cu-chip");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    /// <summary>文本输入：onchange 提交（失焦/回车），避免每敲一字符整页重渲染。</summary>
    void AddInput(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "cu-input");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => onChange(e.Value?.ToString() ?? "")));
        b.CloseElement();
    }

    /// <summary>多行文本输入：onchange 提交。</summary>
    void AddTextArea(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange, int rows = 4)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenElement(seq++, "textarea");
        b.AddAttribute(seq++, "class", "cu-textarea");
        b.AddAttribute(seq++, "rows", rows);
        b.AddAttribute(seq++, "spellcheck", "false");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => onChange(e.Value?.ToString() ?? "")));
        b.CloseElement();
    }

    void AddSelect(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> onChange, (string val, string text)[] options)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-label");
        b.AddContent(seq++, label);
        b.CloseElement();

        b.OpenElement(seq++, "select");
        b.AddAttribute(seq++, "class", "cu-select");
        b.AddAttribute(seq++, "value", value ?? "");
        b.AddAttribute(seq++, "onchange",
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => onChange(e.Value?.ToString() ?? "")));
        foreach (var opt in options)
        {
            b.OpenElement(seq++, "option");
            b.AddAttribute(seq++, "value", opt.val);
            if ((value ?? "") == opt.val)
                b.AddAttribute(seq++, "selected", "selected");
            b.AddContent(seq++, opt.text);
            b.CloseElement();
        }
        b.CloseElement();
    }

    void AddCheck(RenderTreeBuilder b, ref int seq, string label, bool value, Action<bool> onChanged, bool enabled = true)
    {
        b.OpenElement(seq++, "label");
        b.AddAttribute(seq++, "class", enabled ? "cu-check" : "cu-check off");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "checkbox");
        b.AddAttribute(seq++, "checked", value);
        if (!enabled)
            b.AddAttribute(seq++, "disabled", true);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (!enabled) return;
            onChanged((bool)(e.Value ?? false));
        }));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddContent(seq++, label);
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>开关行（整行可点）。afterToggleAsync 在状态变更后执行（用于按需加载）。</summary>
    void AddToggle(RenderTreeBuilder b, ref int seq, string label, string badge, bool value,
        Action<bool> onChanged, Func<Task>? afterToggleAsync = null)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-toggle");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async e =>
        {
            onChanged(!value);
            if (afterToggleAsync != null)
                await afterToggleAsync();
        }));
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "t");
        b.AddContent(seq++, label);
        if (!string.IsNullOrWhiteSpace(badge))
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "b");
            b.AddContent(seq++, badge);
            b.CloseElement();
        }
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", value ? "cu-sw on" : "cu-sw");
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>编号步骤列表（教程用）。</summary>
    void AddSteps(RenderTreeBuilder b, ref int seq, params string[] steps)
    {
        b.OpenElement(seq++, "ol");
        b.AddAttribute(seq++, "class", "cu-steps");
        foreach (var step in steps)
        {
            b.OpenElement(seq++, "li");
            b.AddContent(seq++, step);
            b.CloseElement();
        }
        b.CloseElement();
    }

    /// <summary>突出说明块（小白避坑用）。</summary>
    void AddNote(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-note");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    /// <summary>外链行：可点击 + 旁边给出可复制的地址（照顾不方便点链接的用户）。</summary>
    void AddLinkRow(RenderTreeBuilder b, ref int seq, string text, string url)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-linkrow");
        b.OpenElement(seq++, "a");
        b.AddAttribute(seq++, "class", "cu-link");
        b.AddAttribute(seq++, "href", url);
        b.AddAttribute(seq++, "target", "_blank");
        b.AddAttribute(seq++, "rel", "noreferrer noopener");
        b.AddContent(seq++, text);
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "cu-url");
        b.AddContent(seq++, url);
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>教程插图：内嵌 data URI，避免外部文件路径与打包问题。</summary>
    void AddImage(RenderTreeBuilder b, ref int seq, string dataUri, string caption)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-figure");
        b.OpenElement(seq++, "img");
        b.AddAttribute(seq++, "class", "cu-img");
        // UiAssets 里存的是纯 base64，这里补全 data URI 头，否则会被浏览器当成相对路径导致裂图。
        b.AddAttribute(seq++, "src", "data:image/png;base64," + dataUri);
        b.AddAttribute(seq++, "alt", caption ?? "");
        b.CloseElement();
        if (!string.IsNullOrWhiteSpace(caption))
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "cu-cap");
            b.AddContent(seq++, caption);
            b.CloseElement();
        }
        b.CloseElement();
    }

    static Action<string> SetInt(Action<int> setter)
        => v => { if (int.TryParse(v, out var n)) setter(n); };

    async Task TestAppMcp()
    {
        if (_appMcpTesting) return;
        _appMcpTesting = true;
        _appMcpTestMessage = "正在连接 APP-MCP…";
        StateHasChanged();
        try
        {
            var cfg = Configuration ?? new ComfyuiConfig();
            var comfy = string.IsNullOrWhiteSpace(cfg.BaseUrl)
                ? "http://127.0.0.1:8188" : cfg.BaseUrl.Trim().TrimEnd('/');
            var apiBase = AppMcpClient.ResolveApiBase(cfg.AppMcpUrl, comfy);
            var message = await AppMcpClient.TestAsync(apiBase, cfg.ApiToken);

            // 顺带把「默认模板」的输入明细与可用画风组列出来，省得去 ComfyUI 那边翻
            var templateName = (cfg.AppMcpTemplate ?? "").Trim();
            if (templateName.Length > 0)
            {
                var tpl = await AppMcpClient.GetTemplateAsync(apiBase, templateName, cfg.ApiToken);
                if (tpl == null)
                {
                    message += $"\n默认模板「{templateName}」读取失败或不存在：请检查模板名，或刷新 APP-MCP 的 Templates 列表。";
                }
                else
                {
                    var inputs = tpl.Inputs.Count == 0
                        ? "(无输入)"
                        : string.Join("、", tpl.Inputs.Select(kv => $"{kv.Key}({kv.Value.Type.ToLowerInvariant()})"));
                    message += $"\n默认模板「{templateName}」输入：{inputs}";

                    var hasImageInput = tpl.Inputs.Values.Any(v =>
                                            v.Type.ToUpperInvariant() is "IMAGE" or "LOADIMAGE")
                                        || tpl.Inputs.Keys.Any(k =>
                                            k.Contains("图", StringComparison.Ordinal)
                                            || k.Contains("image", StringComparison.OrdinalIgnoreCase));
                    message += hasImageInput
                        ? "\n该模板支持图生图（可用 imagepath）"
                        : "\n该模板未声明图片输入：imagepath 会被忽略";

                    var zmlGroups = AppMcpClient.ListTemplateZmlGroups(tpl.RawJson);
                    message += zmlGroups.Count > 0
                        ? $"\nZML LoRA 组（可填进「画风预设」的 g）：{string.Join("、", zmlGroups)}"
                        : "\n未发现 ZML LoRA 组（画风预设的 g 对该模板不适用）";
                }
            }

            _appMcpTestMessage = message;
        }
        catch (Exception ex)
        {
            _appMcpTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _appMcpTesting = false;
            StateHasChanged();
        }
    }

    async Task UnloadModels()
    {
        if (_unloadingModel) return;
        _unloadingModel = true;
        _unloadMessage = "正在请求 ComfyUI 卸载模型…";
        StateHasChanged();
        try
        {
            _unloadMessage = await ComfyuiService.UnloadModelsNowAsync(Configuration);
        }
        catch (Exception ex)
        {
            _unloadMessage = "卸载异常: " + ex.Message;
        }
        finally
        {
            _unloadingModel = false;
            StateHasChanged();
        }
    }

    async Task TestDanbooruConnectivity()
    {
        if (_danbooruTesting) return;
        _danbooruTesting = true;
        _danbooruTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var primary = Configuration.DanbooruSearchPrimaryUrl?.Trim() ?? "";
            var fallback = Configuration.DanbooruSearchFallbackUrl?.Trim() ?? "";
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(primary))
            {
                var (ok, msg, ms) = await DanbooruSearchClient.HealthAsync(primary, 20);
                parts.Add($"主源 {(ok ? "OK" : "FAIL")} {ms}ms — {msg}");
            }
            else
            {
                parts.Add("主源未填写");
            }

            if (!string.IsNullOrWhiteSpace(fallback)
                && !string.Equals(primary?.TrimEnd('/'), fallback.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                var (ok, msg, ms) = await DanbooruSearchClient.HealthAsync(fallback, 25);
                parts.Add($"备用 {(ok ? "OK" : "FAIL")} {ms}ms — {msg}");
            }

            _danbooruTestMessage = string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            _danbooruTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _danbooruTesting = false;
            StateHasChanged();
        }
    }

    async Task TestAnimadexConnectivity()
    {
        if (_animadexTesting) return;
        _animadexTesting = true;
        _animadexTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var baseUrl = Configuration.AnimadexBaseUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _animadexTestMessage = "未填写服务地址";
            }
            else
            {
                var (ok, msg, ms) = await AnimadexClient.HealthAsync(baseUrl, 20);
                _animadexTestMessage = $"{(ok ? "OK" : "FAIL")} {ms}ms — {msg}";
            }
        }
        catch (Exception ex)
        {
            _animadexTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _animadexTesting = false;
            StateHasChanged();
        }
    }

    async Task AutoDetectNodes()
    {
        _scanDetectMessage = null;
        try
        {
            var path = Configuration.WorkflowPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                detectMessage = "请先填写工作流路径";
                StateHasChanged();
                return;
            }

            string full = path;
            if (!File.Exists(full))
            {
                var pluginDir = Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui");
                try { full = Path.GetFullPath(Path.Combine(pluginDir, path)); } catch { }
            }
            if (!File.Exists(full))
            {
                detectMessage = $"找不到工作流文件: {path}";
                StateHasChanged();
                return;
            }

            var raw = await File.ReadAllTextAsync(full);
            if (JsonNode.Parse(raw) is not JsonObject root)
            {
                detectMessage = "工作流 JSON 解析失败";
                StateHasChanged();
                return;
            }

            var api = ComfyuiWorkflowConverter.ToApiPrompt(root, null);
            var (posId, negId) = ComfyuiWorkflowConverter.FindPositiveAndNegativeIds(api);
            var resId = ComfyuiWorkflowConverter.FindResolutionNodeId(api);

            // 先清空旧结果，避免更换工作流后识别失败却继续使用上一份节点 ID。
            Configuration.PositivePromptNodeId = posId ?? "";
            Configuration.NegativePromptNodeId = negId ?? "";
            Configuration.ResolutionNodeId = resId ?? "";

            if (!string.IsNullOrEmpty(posId) && api[posId] is JsonObject posNode)
            {
                var posInputs = posNode["inputs"] as JsonObject ?? new JsonObject();
                Configuration.PositivePromptInput = ComfyuiWorkflowConverter.ResolvePromptField(posInputs, Configuration.PositivePromptInput);
            }
            if (!string.IsNullOrEmpty(negId) && api[negId] is JsonObject negNode)
            {
                var negInputs = negNode["inputs"] as JsonObject ?? new JsonObject();
                Configuration.NegativePromptInput = ComfyuiWorkflowConverter.ResolvePromptField(negInputs, Configuration.NegativePromptInput);
            }

            detectMessage =
                $"已识别（共 {api.Count} 节点）：\n" +
                $"· 正向 #{posId ?? "?"}（字段 {Configuration.PositivePromptInput}）\n" +
                $"· 负面 #{negId ?? "?"}（字段 {Configuration.NegativePromptInput}）\n" +
                $"· 分辨率 #{resId ?? "?"}" +
                (string.IsNullOrEmpty(posId) ? "\n提示：未识别到正向节点，请手动指定" : "");
        }
        catch (Exception ex)
        {
            detectMessage = $"识别失败: {ex.Message}";
        }
        StateHasChanged();
    }

    void AddEditableResoCard(RenderTreeBuilder b, ref int seq, string tag, string name, string hint,
        int width, Action<int> onWidthChanged, int height, Action<int> onHeightChanged)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-card");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-shine");
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-tag");
        b.AddContent(seq++, tag);
        b.CloseElement();
        // editable input row
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-input-row");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "cfy-reso-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "value", width);
        b.AddAttribute(seq++, "min", "64");
        b.AddAttribute(seq++, "max", "4096");
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out var n))
                onWidthChanged(Math.Clamp(n, 64, 4096));
        }));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "cfy-reso-sep");
        b.AddContent(seq++, "×");
        b.CloseElement();
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "cfy-reso-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "value", height);
        b.AddAttribute(seq++, "min", "64");
        b.AddAttribute(seq++, "max", "4096");
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out var n))
                onHeightChanged(Math.Clamp(n, 64, 4096));
        }));
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-name");
        b.AddContent(seq++, name);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-reso-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
        b.CloseElement();
    }

    async Task ScanWorkflows()
    {
        detectMessage = null;
        var path = (Configuration?.WorkflowPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _scanDetectMessage = "请先在路径框中输入 ComfyUI 目录或工作流目录路径";
            StateHasChanged();
            return;
        }

        var dir = ResolveWorkflowScanDir(path);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _scanDetectMessage = $"路径无效或目录不存在: {path}";
            StateHasChanged();
            return;
        }

        _scanDir = dir;

        try
        {
            var allFiles = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            var workflows = new List<string>();
            foreach (var file in allFiles)
            {
                if (await IsComfyuiWorkflow(file))
                    workflows.Add(file);
            }

            _scannedWorkflows = workflows.OrderBy(f => f).ToList();
            _showWorkflowDropdown = _scannedWorkflows.Count > 0;

            _scanDetectMessage = _scannedWorkflows.Count > 0
                ? $"扫描 {dir}\n找到 {_scannedWorkflows.Count} 个工作流，请在下方选择"
                : $"在 {dir}\n未找到有效工作流 .json 文件";
        }
        catch (Exception ex)
        {
            _scanDetectMessage = $"扫描失败: {ex.Message}";
        }
        StateHasChanged();
    }

    /// <summary>
    /// 智能定位工作流扫描目录：识别 ComfyUI / aki 版，自动进 workflows 子目录
    /// </summary>
    static string ResolveWorkflowScanDir(string enteredPath)
    {
        if (!Directory.Exists(enteredPath))
        {
            var parent = Path.GetDirectoryName(enteredPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                return parent;
            return enteredPath;
        }

        // 直接是 ComfyUI 根目录（有 main.py）
        if (File.Exists(Path.Combine(enteredPath, "main.py")))
        {
            var wf = TryGetWorkflowsDir(enteredPath);
            if (wf != null) return wf;
        }

        // aki 版：ComfyUI/main.py 在子目录里
        var inner = Path.Combine(enteredPath, "ComfyUI");
        if (Directory.Exists(inner) && File.Exists(Path.Combine(inner, "main.py")))
        {
            var wf = TryGetWorkflowsDir(inner);
            if (wf != null) return wf;
        }

        // 非 ComfyUI 根目录，直接用用户输入的目录
        return enteredPath;
    }

    static string? TryGetWorkflowsDir(string comfyuiRoot)
    {
        var candidates = new[]
        {
            Path.Combine(comfyuiRoot, "user", "default", "workflows"),
            Path.Combine(comfyuiRoot, "workflows"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// 判断 JSON 文件是否为 ComfyUI 工作流（非配置文件等）
    /// </summary>
    static async Task<bool> IsComfyuiWorkflow(string filePath)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(filePath);
            // API 格式：顶层数字键 + class_type
            if (raw.Contains("\"class_type\""))
                return true;
            // UI 格式：last_node_id + nodes + links
            if (raw.Contains("\"last_node_id\"") && raw.Contains("\"nodes\"") && raw.Contains("\"links\""))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下拉列表显示名：仅取文件名，hover 显示完整路径
    /// </summary>
    static string GetWorkflowDisplayName(string fullPath, string scanDir)
    {
        return Path.GetFileName(fullPath);
    }

    async Task LoadNodeOverview()
    {
        _nodeOverview = new();
        _nodeOverviewError = null;
        try
        {
            var path = (Configuration?.WorkflowPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                _nodeOverviewError = "请先配置工作流路径";
                return;
            }
            var full = path;
            if (!File.Exists(full))
            {
                var pluginDir = Path.Combine(AlifePath.StorageFolderPath, "Plugins", "Alife.Plugin.Comfyui");
                try { full = Path.GetFullPath(Path.Combine(pluginDir, path)); } catch { }
            }
            if (!File.Exists(full))
            {
                _nodeOverviewError = $"找不到工作流文件: {path}";
                return;
            }

            var raw = await File.ReadAllTextAsync(full);
            if (JsonNode.Parse(raw) is not JsonObject root)
            {
                _nodeOverviewError = "工作流 JSON 解析失败";
                return;
            }

            var api = ComfyuiWorkflowConverter.ToApiPrompt(root, null);
            foreach (var kv in api)
            {
                if (kv.Value is not JsonObject node) continue;
                var ct = node["class_type"]?.GetValue<string>() ?? "?";
                var inputs = node["inputs"] as JsonObject ?? new JsonObject();
                var keyParams = string.Join(", ", inputs
                    .Select(i =>
                    {
                        var valStr = i.Value switch
                        {
                            JsonValue jv => jv.GetValue<object>()?.ToString() ?? "null",
                            JsonArray => "[…]",
                            JsonObject => "{…}",
                            null => "null",
                            _ => "…"
                        };
                        var s = $"{i.Key}: {valStr}";
                        return s.Length > 50 ? s[..47] + "…" : s;
                    })
                    .Take(3));
                if (string.IsNullOrWhiteSpace(keyParams))
                    keyParams = $"{inputs.Count} 个输入";
                _nodeOverview.Add((kv.Key, ct, keyParams));
            }
        }
        catch (Exception ex)
        {
            _nodeOverviewError = $"解析失败: {ex.Message}";
        }
    }

    void AddNodeOverview(RenderTreeBuilder b, ref int seq)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cfy-node-overview");

        if (!string.IsNullOrWhiteSpace(_nodeOverviewError))
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "padding:12px 16px;color:#be185d;font-size:12px;");
            b.AddContent(seq++, _nodeOverviewError);
            b.CloseElement();
        }
        else if (_nodeOverview.Count == 0)
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "padding:12px 16px;color:#b06a8c;font-size:12px;font-style:italic;");
            b.AddContent(seq++, "正在加载节点信息...");
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "cfy-node-count");
            b.AddContent(seq++, $"共 {_nodeOverview.Count} 个节点");
            b.CloseElement();

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "max-height:320px;overflow:auto;");
            b.OpenElement(seq++, "table");
            b.AddAttribute(seq++, "class", "cfy-node-table");
            // header
            b.OpenElement(seq++, "thead");
            b.OpenElement(seq++, "tr");
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "节点 ID"); b.CloseElement();
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "类型"); b.CloseElement();
            b.OpenElement(seq++, "th"); b.AddContent(seq++, "关键参数"); b.CloseElement();
            b.CloseElement();
            b.CloseElement();
            // body
            b.OpenElement(seq++, "tbody");
            foreach (var n in _nodeOverview)
            {
                b.OpenElement(seq++, "tr");
                b.OpenElement(seq++, "td");
                b.AddAttribute(seq++, "class", "nid");
                b.AddContent(seq++, n.NodeId);
                b.CloseElement();
                b.OpenElement(seq++, "td");
                b.AddContent(seq++, n.ClassType);
                b.CloseElement();
                b.OpenElement(seq++, "td");
                b.AddAttribute(seq++, "class", "params");
                b.AddContent(seq++, n.KeyParams);
                b.CloseElement();
                b.CloseElement();
            }
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement();
    }

    // ===================== 命名工作流卡片管理 =====================

    static string? WfJsonString(System.Text.Json.Nodes.JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    static bool? WfJsonBool(System.Text.Json.Nodes.JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<bool>(out var b))
            return b;
        return null;
    }

    void LoadWorkflowCards()
    {
        _workflowCards = new();
        var json = Configuration?.NamedWorkflows ?? "[]";
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
            {
                foreach (var item in arr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    _workflowCards.Add(new WorkflowCard
                    {
                        Name = WfJsonString(item, "n") ?? WfJsonString(item, "name") ?? "",
                        Path = WfJsonString(item, "p") ?? WfJsonString(item, "path") ?? "",
                        Enabled = WfJsonBool(item, "e") ?? true,
                        Prefix = WfJsonString(item, "f") ?? WfJsonString(item, "prefix") ?? ""
                    });
                }
            }
        }
        catch { }
    }

    void SaveWorkflowCards()
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var card in _workflowCards)
        {
            arr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["n"] = card.Name,
                ["p"] = card.Path,
                ["e"] = card.Enabled,
                ["f"] = card.Prefix
            });
        }
        Configuration.NamedWorkflows = arr.ToJsonString();
    }

    void AddWorkflowCard()
    {
        _workflowCards.Add(new WorkflowCard { Name = "新工作流", Path = "", Enabled = true });
        SaveWorkflowCards();
        StateHasChanged();
    }

    void RemoveWorkflowCard(int index)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards.RemoveAt(index);
        _activeScanCardIndex = -1;
        SaveWorkflowCards();
        StateHasChanged();
    }

    void UpdateWorkflowCardName(int index, string name)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Name = name };
        SaveWorkflowCards();
    }

    void UpdateWorkflowCardPath(int index, string path)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Path = path };
        SaveWorkflowCards();
    }

    void UpdateWorkflowCardPrefix(int index, string prefix)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Prefix = prefix };
        SaveWorkflowCards();
    }

    void ToggleWorkflowCardEnabled(int index)
    {
        if (index < 0 || index >= _workflowCards.Count) return;
        _workflowCards[index] = _workflowCards[index] with { Enabled = !_workflowCards[index].Enabled };
        SaveWorkflowCards();
        StateHasChanged();
    }

    void ToggleWorkflowScan(int index)
    {
        _activeScanCardIndex = _activeScanCardIndex == index ? -1 : index;
        StateHasChanged();
    }

    // ===================== 提示词预设卡片管理 =====================

    void LoadPresetCards()
    {
        _presetCards = new();
        var path = ComfyuiService.GetPresetFilePath();
        if (!File.Exists(path)) return;
        try
        {
            var json = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
            var arr = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
            {
                foreach (var item in arr.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    _presetCards.Add(new PresetCard
                    {
                        Name = item["n"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? "",
                        Content = item["c"]?.GetValue<string>() ?? item["content"]?.GetValue<string>() ?? ""
                    });
                }
            }
        }
        catch { }
    }

    void SavePresetCards()
    {
        // 始终写入用户数据目录，避免写回 Plugins 后被市场更新清掉
        var path = System.IO.Path.Combine(
            ComfyuiService.GetUserDataDirectory(), "prompt-presets.json");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var card in _presetCards)
            {
                arr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["n"] = card.Name,
                    ["c"] = card.Content
                });
            }
            System.IO.File.WriteAllText(path, arr.ToJsonString(), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    void AddPresetCard()
    {
        var name = _newPresetName?.Trim() ?? "";
        var content = _newPresetContent?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            StateHasChanged();
            return;
        }
        if (name.Length > 60) name = name[..60];
        if (content.Length > 4000) content = content[..4000];

        // 同名覆盖
        _presetCards = _presetCards
            .Where(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _presetCards.Add(new PresetCard { Name = name, Content = content });
        _newPresetName = "";
        _newPresetContent = "";
        SavePresetCards();
        StateHasChanged();
    }

    void RemovePresetCard(int index)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards.RemoveAt(index);
        _activePresetIndex = -1;
        SavePresetCards();
        StateHasChanged();
    }

    void UpdatePresetCardName(int index, string name)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards[index] = _presetCards[index] with { Name = name };
        SavePresetCards();
    }

    void UpdatePresetCardContent(int index, string content)
    {
        if (index < 0 || index >= _presetCards.Count) return;
        _presetCards[index] = _presetCards[index] with { Content = content };
        SavePresetCards();
    }

    void TogglePresetExpand(int index)
    {
        _activePresetIndex = _activePresetIndex == index ? -1 : index;
        StateHasChanged();
    }
}
