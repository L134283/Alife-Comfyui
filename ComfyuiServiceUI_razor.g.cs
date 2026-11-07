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
/// 结构（2026-11 精简版）：
/// 1) 4 个页签：生图 / 后端 · 画风 / 提示词 / 进阶，**只渲染当前页签**；
/// 2) 页签内分区用原生 &lt;details&gt; 折叠：默认只展开主分区，标题右侧带当前值摘要，
///    长解释收进「?」折叠块，避免一屏铺满说明文字；
/// 3) 默认精简模式：节点映射、Token、图目录、兜底宽高、轮询、在线地址/超时等低频项
///    由「进阶 → 显示专家选项」控制；
/// 4) 文本输入 onchange（失焦/回车提交），避免每敲一个字符整页重建；
/// 5) 纯静态轻量 CSS（无动画/滤镜/毛玻璃/多层阴影），避免宿主每帧重绘闪烁；
/// 6) 卡片列表进入页签时惰性加载，不在渲染过程中改状态。
/// 业务方法（扫描/识别/连通测试/卡片增删改/节点概览等）保持原实现不变。
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

    // 画风预设卡片管理
    List<StyleCard> _styleCards = new();
    int _activeStyleIndex = -1;
    string _newStyleName = "";
    bool _loadedStyleCards;

    // 顶部状态：有未保存改动 / 刚切过后端（需重载）
    bool _dirty;
    bool _backendSwitched;

    // 在线标签检索连通测试
    string? _danbooruTestMessage;
    bool _danbooruTesting;

    // 在线角色检索（AnimaDex）连通测试
    string? _animadexTestMessage;
    bool _animadexTesting;

    // 在线角色检索（Danbooru）连通测试
    string? _booruCharTestMessage;
    bool _booruCharTesting;

    // 在线角色检索（DanbooruSearch 镜像）连通测试
    string? _mirrorCharTestMessage;
    bool _mirrorCharTesting;

    // 模型卸载与空闲自动释放
    bool _unloadingModel;
    string? _unloadMessage;

    // APP-MCP 模板模式连通测试
    string? _appMcpTestMessage;
    bool _appMcpTesting;

    // 页签（只渲染当前页签）。4 个页签：生图 / 后端·画风 / 提示词 / 进阶
    int _tab = 0;
    static readonly (int Id, string Title)[] AllTabs =
    {
        (0, "生图"), (1, "后端 · 画风"), (2, "提示词"), (3, "进阶")
    };
    bool _loadedWorkflowCards;
    bool _loadedPresetCards;

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

    record StyleCard
    {
        public string Name { get; set; } = "";
        public string Template { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Group { get; set; } = "";
        public string Params { get; set; } = "";
    }

    // ============================================================
    // 样式：纯静态、无动画、无滤镜、无毛玻璃。避免宿主每帧重绘导致闪烁/掉帧。
    // ============================================================
    const string Css = @"
.cu-root { width:100%; box-sizing:border-box; color:#3f2a38; font-size:13px;
  --pink:#ec4899; --pink-d:#9d174d; --line:#f2d9e7; --line-2:#f9ecf3; --soft:#fff9fc;
  --hint:#8a4f6b; --muted:#a8728f; }
.cu-card { background:#fff; border:1px solid #f2d7e5; border-radius:14px; box-shadow:0 1px 2px rgba(190,24,93,.06); overflow:hidden; }
.cu-head { display:flex; align-items:center; gap:8px; flex-wrap:wrap; padding:11px 16px; border-bottom:1px solid #f7e3ee; background:#fff8fb; }
.cu-title { font-weight:800; font-size:14.5px; color:#9d174d; }
.cu-spacer { flex:1; }
.cu-state { font-size:11px; font-weight:700; color:#8a4f6b; background:#fff; border:1px dashed #f0cfe1; border-radius:999px; padding:2px 9px; }
.cu-state.warn { color:#b45309; background:#fffbeb; border-color:#fbd38d; }
.cu-chip { font-size:11px; font-weight:700; padding:2px 9px; border-radius:999px; border:1px solid #f4c8dd; color:#9d174d; background:#fff; white-space:nowrap; }
.cu-chip.on { background:#ec4899; border-color:#ec4899; color:#fff; }
.cu-tabs { display:flex; gap:4px; flex-wrap:wrap; padding:7px 12px; border-bottom:1px solid #f7e3ee; background:#fff; }
.cu-tab { border:1px solid transparent; background:transparent; color:#9d4b74; font-weight:700; font-size:12.5px; padding:5px 13px; border-radius:999px; cursor:pointer; font-family:inherit; }
.cu-tab:hover { background:#fff0f7; }
.cu-tab.active { background:#ec4899; border-color:#ec4899; color:#fff; }
.cu-body { padding:12px 14px 16px; }
/* 折叠分区：标题常显，内容收起 */
.cu-sec { border:1px solid var(--line); border-radius:12px; background:#fff; margin:0 0 9px; overflow:hidden; }
.cu-sec:last-child { margin-bottom:0; }
.cu-sec > summary { list-style:none; cursor:pointer; display:flex; align-items:center; gap:8px;
  padding:9px 12px; font-size:12.5px; font-weight:800; color:#9d174d; background:var(--soft); }
.cu-sec > summary::-webkit-details-marker { display:none; }
.cu-sec > summary::before { content:'▸'; font-size:10px; color:var(--pink); flex:none; }
.cu-sec[open] > summary::before { content:'▾'; }
.cu-sec > summary:hover { background:#fff0f7; }
.cu-sec-hint { margin-left:auto; font-size:11px; font-weight:600; color:var(--hint); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; max-width:55%; }
.cu-sec-body { padding:11px 12px 12px; }
/* 面板只作语义分组，不再画边框（减少嵌套盒子） */
.cu-panel { border:none; background:transparent; padding:0; margin:0; }
.cu-grid2 { display:grid; grid-template-columns:repeat(auto-fit,minmax(240px,1fr)); gap:10px; }
.cu-grid3 { display:grid; grid-template-columns:repeat(auto-fit,minmax(165px,1fr)); gap:8px; }
.cu-row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; }
.cu-col { display:flex; flex-direction:column; gap:2px; }
.cu-label { font-size:11.5px; font-weight:700; color:#9d4b74; margin:9px 0 4px; }
.cu-input, .cu-textarea, .cu-select { width:100%; box-sizing:border-box; border:1px solid #efccdd; border-radius:8px; padding:6px 9px; font-size:12.5px; color:#3f2a38; background:#fff; outline:none; font-family:inherit; }
.cu-input:focus, .cu-textarea:focus, .cu-select:focus { border-color:#ec4899; }
.cu-textarea { resize:vertical; line-height:1.5; font-family:Consolas,ui-monospace,monospace; }
.cu-hint { font-size:11.5px; color:var(--hint); margin:5px 0 2px; line-height:1.6; white-space:pre-wrap; }
/* 长解释折叠：详情默认收起，只看一行摘要 */
.cu-help { margin:6px 0 2px; font-size:11.5px; color:var(--hint); }
.cu-help > summary { list-style:none; cursor:pointer; display:inline-flex; align-items:center; gap:6px; color:#9d4b74; font-weight:700; }
.cu-help > summary::-webkit-details-marker { display:none; }
.cu-help > summary::before { content:'?'; display:inline-flex; align-items:center; justify-content:center;
  width:14px; height:14px; border-radius:50%; background:var(--pink); color:#fff; font-size:10px; font-weight:800; flex:none; }
.cu-help[open] > summary { color:#9d174d; }
.cu-help .cu-hint { margin:6px 0 0; padding:7px 9px; background:var(--soft); border:1px dashed var(--line); border-radius:8px; }
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
.cfy-reso-card { border:1px solid var(--line); border-radius:10px; padding:8px 10px; background:#fff; }
.cfy-reso-shine { display:none; }
.cfy-reso-tag { font-size:10px; font-weight:800; color:#db2777; letter-spacing:.8px; }
.cfy-reso-input-row { display:flex; align-items:center; gap:5px; margin:5px 0 3px; }
.cfy-reso-input { width:62px; box-sizing:border-box; border:1px solid #efccdd; border-radius:7px; padding:3px 6px; font-size:12px; font-family:inherit; color:#3f2a38; }
.cfy-reso-sep { color:var(--muted); font-weight:700; }
.cfy-reso-name { font-size:12px; font-weight:700; color:#9d174d; }
.cfy-reso-hint { font-size:10.5px; color:var(--hint); margin-top:1px; }
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
        if (!_loadedStyleCards) { LoadStyleCards(); _loadedStyleCards = true; }

        int i = 0;

        b.OpenElement(i++, "style");
        b.AddContent(i++, Css);
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-root");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-card");

        // ---------- 头部：只留模式/配置状态 + 一条统一提示 ----------
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
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "cu-spacer");
        b.CloseElement();
        if (_backendSwitched)
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cu-state warn");
            b.AddContent(i++, "已切换后端：需重载模块");
            b.CloseElement();
        }
        else if (_dirty)
        {
            // 框架配置面板由宿主保存：这里只提示「有改动」，不新增保存按钮
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "cu-state");
            b.AddContent(i++, "有改动：记得保存");
            b.CloseElement();
        }
        b.CloseElement(); // head

        // ---------- 页签 ----------
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-tabs");
        foreach (var (tabId, tabTitle) in AllTabs)
        {
            var id = tabId;
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", _tab == id ? "cu-tab active" : "cu-tab");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async e =>
            {
                _tab = id;
                if (id == 3 && Configuration.AdvancedMode)
                    await LoadNodeOverview();
            }));
            b.AddContent(i++, tabTitle);
            b.CloseElement();
        }
        b.CloseElement(); // tabs

        // ---------- 内容：只渲染当前页签 ----------
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-body");
        switch (_tab)
        {
            case 1: RenderBackendTab(b); break;
            case 2: RenderPromptTab(b); break;
            case 3: RenderAdvancedTab(b); break;
            default: RenderBasicTab(b); break;
        }
        b.CloseElement(); // body

        b.CloseElement(); // card
        b.CloseElement(); // root
    }

    bool IsAppMcp()
        => string.Equals((Configuration?.BackendMode ?? "").Trim(), "appmcp", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // 页签 0：生图（连接 / 工作流或模板 / 分辨率 / 生图行为）
    // ============================================================
    void RenderBasicTab(RenderTreeBuilder b)
    {
        int i = 1000;
        var appMcp = IsAppMcp();
        var expert = Configuration.ShowExpertOptions;

        // ---------- 连接 ----------
        AddSection(b, ref i, "连接", open: true,
            note: string.IsNullOrWhiteSpace(Configuration.BaseUrl) ? "未填地址" : Configuration.BaseUrl.Trim());
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        AddInput(b, ref i, "ComfyUI 地址", Configuration.BaseUrl, v => Configuration.BaseUrl = v);
        if (expert)
        {
            AddInput(b, ref i, "API Token（反代鉴权用，可空）", Configuration.ApiToken,
                v => Configuration.ApiToken = v);
            AddInput(b, ref i, "图片保存目录（额外副本用，可空）", Configuration.SaveDirectory,
                v => Configuration.SaveDirectory = v);
            AddInput(b, ref i, "ComfyUI output 目录（可空）", Configuration.ComfyuiOutputPath,
                v => Configuration.ComfyuiOutputPath = v);
            AddInput(b, ref i, "自定义出图目录（可空）", Configuration.CustomOutputPath,
                v => Configuration.CustomOutputPath = v);
        }
        b.CloseElement();
        if (expert)
        {
            AddCheck(b, ref i, "额外保存副本（复制到「图片保存目录」）", Configuration.ExtraSaveCopy,
                v => Configuration.ExtraSaveCopy = v);
            AddHint(b, ref i, "关闭则直接用工作流/模板自带的保存路径。");
        }
        else
        {
            var resolvedSaveDir = string.IsNullOrWhiteSpace(Configuration.SaveDirectory)
                ? Path.Combine(AlifePath.StorageFolderPath, "Images", "Comfyui")
                : Configuration.SaveDirectory;
            AddHelp(b, ref i, "图片存在哪？",
                $"当前保存目录：{resolvedSaveDir}\n"
                + "想「电脑只留一份」：在「进阶」页打开「显示专家选项」，再填 ComfyUI output / 自定义出图目录，"
                + "插件会直接引用该目录里的原图，不再复制。\n"
                + "地址同时用于状态检查、图生图上传、图片下载；模板模式未填 APP-MCP 地址时也会由它推导。");
        }
        AddHelp(b, ref i, "第一次用？三步走",
            "1) 启动 ComfyUI，确认上面的地址与它一致（默认 http://127.0.0.1:8188）。\n"
            + (appMcp
                ? "2) 在下面「APP-MCP 模板」填默认模板名，点「测试连通并列出模板」。\n"
                : "2) 在下面「工作流」填工作流 JSON 路径（可点「扫描目录」选），再点「自动识别节点」。\n")
            + "3) 直接让角色生图。想要一个工作流/模板切换多种画风，去「后端 · 画风 → 画风预设」。");
        EndSection(b, ref i);

        if (appMcp)
            RenderAppMcpSections(b, ref i, expert);
        else
        {
            RenderWorkflowSections(b, ref i, expert);
            RenderResolutionSection(b, ref i, expert);
        }

        // ---------- 生图行为 ----------
        AddSection(b, ref i, "生图行为", note: Configuration.PriorityImageGen ? "已开优先生图" : null);
        AddCheck(b, ref i, "生完后用系统默认看图器打开（仅桌面端）", Configuration.AutoOpenImage,
            v => Configuration.AutoOpenImage = v);
        AddCheck(b, ref i, "优先生图（语音避让）：本轮语音播完后再提交（同机 TTS 建议开）", Configuration.PriorityImageGen,
            v => Configuration.PriorityImageGen = v);
        if (Configuration.PriorityImageGen)
        {
            AddInput(b, ref i, "硬超时上限（秒）", Configuration.PriorityMaxWaitSeconds.ToString(),
                SetInt(v => Configuration.PriorityMaxWaitSeconds = Math.Clamp(v, 30, 1800)));
        }
        AddHelp(b, ref i, "优先生图是怎么工作的？",
            "AI 调用 generateimage 时先排队，等本轮语音合成/播放结束再提交 ComfyUI；出图期间下一轮消息会等待，"
            + "这样 TTS 与 Comfy 不会同时抢 GPU。\n"
            + "实际等待 = min(等待出图超时, 硬超时上限)，超时强制结束，避免卡死对话。\n"
            + "本插件不依赖任何语音插件（靠框架「本轮结束」时机判断语音已收尾）。");
        EndSection(b, ref i);
    }

    // ---------- 工作流模式：默认工作流 + 节点映射 + 多工作流 ----------
    void RenderWorkflowSections(RenderTreeBuilder b, ref int i, bool expert)
    {
        var wfName = string.IsNullOrWhiteSpace(Configuration.WorkflowPath)
            ? "未选择文件"
            : Path.GetFileName(Configuration.WorkflowPath);
        AddSection(b, ref i, "工作流", open: true, note: wfName);

        AddInput(b, ref i, "工作流 JSON 路径（绝对路径或相对插件目录）", Configuration.WorkflowPath,
            v => Configuration.WorkflowPath = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.AddAttribute(i++, "style", "margin-top:8px;");
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
                    MarkDirty();
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

        if (expert)
        {
            AddHint(b, ref i, "节点映射（专家，留空自动识别；仅作用于默认工作流）");
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-grid2");
            AddInput(b, ref i, "正向提示词节点 ID", Configuration.PositivePromptNodeId,
                v => Configuration.PositivePromptNodeId = v);
            AddInput(b, ref i, "正向提示词字段名", Configuration.PositivePromptInput,
                v => Configuration.PositivePromptInput = v);
            AddInput(b, ref i, "分辨率节点 ID", Configuration.ResolutionNodeId,
                v => Configuration.ResolutionNodeId = v);
            AddInput(b, ref i, "负面提示词节点 ID", Configuration.NegativePromptNodeId,
                v => Configuration.NegativePromptNodeId = v);
            AddInput(b, ref i, "负面提示词字段名", Configuration.NegativePromptInput,
                v => Configuration.NegativePromptInput = v);
            AddInput(b, ref i, "图生图 LoadImage 节点 ID", Configuration.LoadImageNodeId,
                v => Configuration.LoadImageNodeId = v);
            b.CloseElement();
        }

        AddHelp(b, ref i, "支持哪些工作流文件？",
            "· ComfyUI 网页里直接保存的 UI 工作流 JSON（插件自动转 API 格式）\n"
            + "· 官方 Export (API) 导出的 API JSON\n"
            + "「扫描目录」会在路径所在目录 / ComfyUI 的 workflows 目录列出可用工作流；"
            + "「自动识别节点」会扫描正/负/分辨率节点并回填。相对路径按插件目录解析，绝对路径最稳妥。");
        EndSection(b, ref i);

        // ---------- 多工作流 ----------
        var enabledCount = _workflowCards.Count(c => c.Enabled);
        AddSection(b, ref i, "多工作流（可选）",
            note: _workflowCards.Count == 0 ? "未添加" : $"已启用 {enabledCount}/{_workflowCards.Count}");

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
        b.CloseElement();
        AddHint(b, ref i, "启用后 AI 可用 workflow 参数切换；名称唯一、路径有效。");
        EndSection(b, ref i);
    }

    // ---------- 分辨率 + 默认参数 ----------
    void RenderResolutionSection(RenderTreeBuilder b, ref int i, bool expert)
    {
        AddSection(b, ref i, "分辨率",
            note: $"竖 {Configuration.PortraitWidth}×{Configuration.PortraitHeight}");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid3");
        AddEditableResoCard(b, ref i, "PORTRAIT", "竖版", "人物 · 手机壁纸",
            Configuration.PortraitWidth, v => Configuration.PortraitWidth = v,
            Configuration.PortraitHeight, v => Configuration.PortraitHeight = v);
        AddEditableResoCard(b, ref i, "LANDSCAPE", "横版", "风景 · 横构图",
            Configuration.LandscapeWidth, v => Configuration.LandscapeWidth = v,
            Configuration.LandscapeHeight, v => Configuration.LandscapeHeight = v);
        AddEditableResoCard(b, ref i, "SQUARE", "正方形", "头像 · 对称构图",
            Configuration.SquareWidth, v => Configuration.SquareWidth = v,
            Configuration.SquareHeight, v => Configuration.SquareHeight = v);
        b.CloseElement();
        AddHelp(b, ref i, "AI 怎么选尺寸？",
            "AI 传 orientation=portrait/landscape/square 智能选档，也可直接指定 width/height 覆盖。"
            + "改完数值点空白处生效。");
        EndSection(b, ref i);

        if (!expert)
            return;

        AddSection(b, ref i, "默认参数（专家）");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        AddSelect(b, ref i, "默认方向", Configuration.DefaultOrientation, v => Configuration.DefaultOrientation = v, new[]
        {
            ("portrait", $"竖版 {Configuration.PortraitWidth}×{Configuration.PortraitHeight}"),
            ("landscape", $"横版 {Configuration.LandscapeWidth}×{Configuration.LandscapeHeight}"),
            ("square", $"正方形 {Configuration.SquareWidth}×{Configuration.SquareHeight}")
        });
        AddInput(b, ref i, "兜底宽度", Configuration.DefaultWidth.ToString(), SetInt(v => Configuration.DefaultWidth = v));
        AddInput(b, ref i, "兜底高度", Configuration.DefaultHeight.ToString(), SetInt(v => Configuration.DefaultHeight = v));
        AddInput(b, ref i, "等待出图超时（秒）", Configuration.TimeoutSeconds.ToString(), SetInt(v => Configuration.TimeoutSeconds = v));
        AddInput(b, ref i, "轮询间隔（毫秒，500~10000）", Configuration.PollIntervalMs.ToString(), SetInt(v => Configuration.PollIntervalMs = v));
        b.CloseElement();
        EndSection(b, ref i);
    }

    // ---------- 模板模式：APP-MCP 模板 ----------
    void RenderAppMcpSections(RenderTreeBuilder b, ref int i, bool expert)
    {
        var tplName = string.IsNullOrWhiteSpace(Configuration.AppMcpTemplate)
            ? "未选模板"
            : Configuration.AppMcpTemplate.Trim();
        AddSection(b, ref i, "APP-MCP 模板", open: true, note: tplName);

        AddInput(b, ref i, "APP-MCP 地址（留空自动推导）", Configuration.AppMcpUrl,
            v => Configuration.AppMcpUrl = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        AddInput(b, ref i, "默认模板名", Configuration.AppMcpTemplate, v => Configuration.AppMcpTemplate = v);
        AddInput(b, ref i, "提示词输入参数名", Configuration.AppMcpPromptParam,
            v => Configuration.AppMcpPromptParam = v);
        b.CloseElement();

        AddTextArea(b, ref i, "模板默认参数（JSON 对象）", Configuration.AppMcpDefaultParams,
            v => Configuration.AppMcpDefaultParams = v, 3);
        AddTextArea(b, ref i, "模板自定义说明（会注入 AI 提示词）", Configuration.AppMcpTemplateNotes,
            v => Configuration.AppMcpTemplateNotes = v, 4);

        if (expert)
        {
            AddInput(b, ref i, "等待出图超时（秒）", Configuration.AppMcpTimeoutSeconds.ToString(),
                SetInt(v => Configuration.AppMcpTimeoutSeconds = Math.Clamp(v, 30, 1800)));
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.AddAttribute(i++, "style", "margin-top:8px;");
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

        AddHelp(b, ref i, "参数与尺寸怎么填？",
            "模板默认参数写 {\"参数名\": 值}，只有模板已声明的输入才生效，AI 可用 params 覆盖；"
            + "改完模板记得点「测试连通并列出模板」核对输入名。\n"
            + "画面尺寸由模板决定；模板若声明了 width / height / 宽 / 高 输入，AI 可用 params 传。"
            + "「模板自定义说明」会随系统提示词一起给 AI，用来说明各开关用法。");
        EndSection(b, ref i);
    }

    // ============================================================
    // 页签 1：后端 · 画风（后端切换 / 画风预设 / 模型卸载 / 安装教程）
    // ============================================================
    void RenderBackendTab(RenderTreeBuilder b)
    {
        int i = 3000;
        var appMcp = IsAppMcp();

        AddSection(b, ref i, "生图后端", open: true, note: appMcp ? "APP-MCP 模板" : "工作流");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", appMcp ? "cu-btn" : "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            MarkDirty();
            if (Configuration.BackendMode != "workflow")
                _backendSwitched = true;
            Configuration.BackendMode = "workflow";
        }));
        b.AddContent(i++, "工作流模式（直连 /prompt）");
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", appMcp ? "cu-btn primary" : "cu-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            MarkDirty();
            if (Configuration.BackendMode != "appmcp")
                _backendSwitched = true;
            Configuration.BackendMode = "appmcp";
        }));
        b.AddContent(i++, "APP-MCP 模板模式");
        b.CloseElement();
        b.CloseElement();
        AddHint(b, ref i, "切换后需重载本模块；模板参数在「生图」页填。");
        AddHelp(b, ref i, "模板模式还要准备什么？",
            "模板模式依赖 ComfyUI 先安装第三方开源节点 ComfyUI-APP-MCP（免费开源），"
            + "安装步骤、新手避坑与文档链接在本页下方「安装 APP-MCP」里。"
            + "\n重载后「生图」页会变成 APP-MCP 模板设置（地址 / 默认模板 / 参数 / 测试连通）。");
        EndSection(b, ref i);

        RenderStylePresetsSection(b, ref i);
        RenderModelUnloadSection(b, ref i);

        // 第三方节点教程：整块默认收起，不占主流程
        if (appMcp)
        {
            AddSection(b, ref i, "安装 APP-MCP 节点（第三方，首次使用看这里）", note: "6 步 + 避坑");
            AddNote(b, ref i, "本插件只是「调用方」：模板模式必须让 ComfyUI 先装上 ComfyUI-APP-MCP，否则「测试连通」一定失败。");
            AddSteps(b, ref i,
                "装节点（二选一）：① ComfyUI Manager → 搜索 app mode mcp → 安装（推荐）；② 手动：进 ComfyUI/custom_nodes 执行 git clone https://github.com/Lotus0614/ComfyUI-APP-MCP.git，再按该仓库 README 安装 requirements.txt 依赖（Windows 便携包要用包内 python_embeded\\python.exe）。",
                "重启 ComfyUI（必须），确认启动日志没有报错。",
                "打开你要用的工作流 → 左上角菜单进 App Builder → 把 AI 要填的控件标记为「输入」并起清晰参数名（例如 positive）→ 把保存图片的节点标记为「输出」。",
                "在该工作流里加一个 Markdown Note：title 填模板短名（例如 913流），description 填说明（可留空）。",
                "工作流用 Save 保存（不是 Export）；再进 ComfyUI 的 Settings → MCP Server → Templates → Create from Workflow 创建模板。",
                "回到本插件：填「默认模板名」→ 点「测试连通并列出模板」，能看到模板与输入明细就算成功。");
            AddImage(b, ref i, UiAssets.AppBuilderEntryPng,
                "找不到 App Builder？看这张图：ComfyUI 左上角「图形」下拉左边的那个方框图标就是 App Builder 入口（对应上面第 3 步）。");

            AddNote(b, ref i,
                "新手避坑（最常见的 6 个坑）：\n"
                + "1) 参数名必须完全一致：AI 传的名字要和模板输入名一字不差（含中文、大小写）；改名后要 Refresh 模板并重载本模块。\n"
                + "2) 自定义 UI 选项不能当输入：要标记的是它上方的「数据内容输入框」。例如 ZML 强力 LoRA 加载器需要暴露 lora_loader_data（本插件正是靠它切换画风组）。\n"
                + "3) 模板的「输出」必须包含保存图片节点，否则插件拿不到图（会提示「未找到输出图片」）。\n"
                + "4) 模板列表为空？工作流要用 Save 保存，且模板不能在 Templates 里被禁用。\n"
                + "5) 改过工作流：在 Templates 里对同名模板点 Refresh，然后重载本模块。\n"
                + "6) 想让每次自动随机 seed：把那个输入命名为 seed，运行时自动填随机值，AI 不用传。");

            AddLinkRow(b, ref i, "ComfyUI-APP-MCP 仓库（GitHub，含安装说明）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP");
            AddLinkRow(b, ref i, "《工具参考》中文文档（模板/参数/输出格式）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/tools.md");
            AddLinkRow(b, ref i, "《故障排查》中文文档（装不上/没输出/参数报错）",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/troubleshooting.md");
            AddLinkRow(b, ref i, "《独立部署与远程访问》文档",
                "https://github.com/Lotus0614/ComfyUI-APP-MCP/blob/master/docs/zh/standalone.md");
            EndSection(b, ref i);
        }
    }

    /// <summary>画风预设：卡片式编辑器（工作流模式=节点覆盖，模板模式=模板输入）。</summary>
    void RenderStylePresetsSection(RenderTreeBuilder b, ref int i)
    {
        var appMcp = IsAppMcp();
        AddSection(b, ref i, "画风预设",
            note: _styleCards.Count == 0 ? "未配置" : $"{_styleCards.Count} 个");

        if (_styleCards.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-empty");
            b.AddContent(i++, appMcp
                ? "暂无画风。填名称后点「添加画风」，再在卡片里填 ZML 组名即可。"
                : "暂无画风。可在卡片里填节点覆盖参数，让同一工作流切换画风。");
            b.CloseElement();
        }
        else
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-list");
            for (var si = 0; si < _styleCards.Count; si++)
            {
                var styleIndex = si;
                var card = _styleCards[si];
                var expanded = _activeStyleIndex == si;

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-item");
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-itemhead");
                b.AddAttribute(i++, "style", "cursor:pointer;");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => ToggleStyleExpand(styleIndex)));
                b.OpenElement(i++, "span");
                b.AddAttribute(i++, "class", "cu-label");
                b.AddAttribute(i++, "style", "margin:0;");
                b.AddContent(i++, string.IsNullOrWhiteSpace(card.Name) ? "(未命名)" : card.Name);
                b.CloseElement();
                b.OpenElement(i++, "span");
                b.AddAttribute(i++, "class", "cu-empty");
                b.AddAttribute(i++, "style", "flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;");
                var preview = !string.IsNullOrWhiteSpace(card.Group) ? $"组 {card.Group}"
                    : !string.IsNullOrWhiteSpace(card.Template) ? $"模板 {card.Template}" : "未填参数";
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
                    AddInput(b, ref i, "画风名（AI 用 style=这个名字）", card.Name, v => UpdateStyleCardName(styleIndex, v));
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-grid2");
                    AddInput(b, ref i, "模板名（可空，仅模板模式）", card.Template, v => UpdateStyleCardTemplate(styleIndex, v));
                    AddInput(b, ref i, "ZML LoRA 组名（可空）", card.Group, v => UpdateStyleCardGroup(styleIndex, v));
                    b.CloseElement();
                    AddTextArea(b, ref i, "附加正向前缀（可空）", card.Prefix, v => UpdateStyleCardPrefix(styleIndex, v), 2);
                    AddTextArea(b, ref i, appMcp ? "模板输入参数（JSON，可空）" : "节点覆盖参数（JSON，可空）",
                        card.Params, v => UpdateStyleCardParams(styleIndex, v), 3);
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-row");
                    b.OpenElement(i++, "button");
                    b.AddAttribute(i++, "type", "button");
                    b.AddAttribute(i++, "class", "cu-del");
                    b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => RemoveStyleCard(styleIndex)));
                    b.AddContent(i++, "删除");
                    b.CloseElement();
                    b.CloseElement();
                    b.CloseElement();
                }
                b.CloseElement();
            }
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        b.AddAttribute(i++, "style", "margin-top:10px;");
        AddInput(b, ref i, "新画风名", _newStyleName, v => _newStyleName = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;align-items:flex-end;");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => AddStyleCard()));
        b.AddContent(i++, "+ 添加画风");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();

        AddHelp(b, ref i, "画风预设怎么用？",
            "AI 在 generateimage 里传 style=\"画风名\" 即可切换；也能用 getcomfyuistyle 查看已配画风与模板可用的 ZML 组名。\n"
            + (appMcp
                ? "最简用法：只填「ZML LoRA 组名」——插件会从当前模板读出该组并只开它。\n"
                  + "精细用法：再填模板名、附加前缀与模板输入 JSON（如 {\"lora_loader_data\":\"……\"}）。"
                : "工作流模式下，「节点覆盖参数」是 JSON：键为节点 ID，值为该节点要覆盖的 inputs，例如 {\"11\":{\"lora_loader_data\":\"…\"}}。"));
        EndSection(b, ref i);
    }

    /// <summary>模型卸载与显存释放区块（两种后端模式共用）。</summary>
    void RenderModelUnloadSection(RenderTreeBuilder b, ref int i)
    {
        AddSection(b, ref i, "模型卸载与显存释放",
            note: Configuration.EnableAutoUnload
                ? $"空闲 {Configuration.AutoUnloadIdleHours}h{Configuration.AutoUnloadIdleMinutes}m"
                : "仅手动");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-row");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn");
        b.AddAttribute(i++, "disabled", _unloadingModel);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, UnloadModels));
        b.AddContent(i++, _unloadingModel ? "卸载中…" : "立即卸载模型（释放显存）");
        b.CloseElement();
        b.CloseElement();
        if (!string.IsNullOrWhiteSpace(_unloadMessage))
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "cu-detect");
            b.AddContent(i++, _unloadMessage);
            b.CloseElement();
        }

        AddCheck(b, ref i, "空闲自动卸载：距上次生图空闲超时后自动卸载", Configuration.EnableAutoUnload,
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
        }
        AddHint(b, ref i, "生图中不会自动卸载；时长改完需保存配置。");
        EndSection(b, ref i);
    }

    // ============================================================
    // 页签 2：提示词（种类 / 前缀与负面 / 预设）
    // ============================================================
    void RenderPromptTab(RenderTreeBuilder b)
    {
        int i = 4000;
        var appMcp = IsAppMcp();

        AddSection(b, ref i, "提示词种类", open: true,
            note: Configuration.PromptStyle switch
            {
                "natural" => "自然语言",
                "hybrid" => "混合",
                _ => "纯 Tag"
            });
        AddSelect(b, ref i, "提示词种类", Configuration.PromptStyle, v => Configuration.PromptStyle = v, new[]
        {
            ("tag", "纯 Tag — 全小写英文标签，逗号分隔"),
            ("natural", "自然语言 — 角色 Tag 置前 + 英文短句"),
            ("hybrid", "混合模式 — 静态用标签，动作/关系用短句")
        });
        AddHint(b, ref i, "只影响 AI 的写法；提示词原样进工作流（不拆段、不去重）。");
        EndSection(b, ref i);

        var hasPrefix = !string.IsNullOrWhiteSpace(Configuration.PositivePromptPrefix);
        AddSection(b, ref i, "固定前缀与负面", open: !hasPrefix,
            note: hasPrefix ? "已设前缀" : null);

        if (appMcp)
        {
            AddCheck(b, ref i, "模板模式下也拼接上面的固定正向提示词前缀", Configuration.AppMcpUseGlobalPrefix,
                v => Configuration.AppMcpUseGlobalPrefix = v);
        }

        AddTextArea(b, ref i, "固定正向提示词前缀（逗号或换行均可）",
            Configuration.PositivePromptPrefix, v => Configuration.PositivePromptPrefix = v, 4);
        AddHint(b, ref i, "原样拼在 AI 提示词最前，用「, 」连接；留空则不拼接。");

        AddTextArea(b, ref i, "固定负面提示词", Configuration.NegativePrompt, v => Configuration.NegativePrompt = v, 3);
        AddHint(b, ref i, appMcp
            ? "模板声明了 negative / 负面提示词 输入时才会写入；留空则不发送。"
            : "留空=用工作流自带负面；填写则原样覆盖。");
        AddHelp(b, ref i, "命名工作流的前缀在哪设？",
            "「生图 → 多工作流」里每张卡片都能单独设固定正向前缀，留空则沿用这里的全局前缀。"
            + "模板模式若模板内部已带画风/质量前缀（如 913流 的节点 52），可关掉上面的开关避免重复。");
        EndSection(b, ref i);

        AddSection(b, ref i, "提示词预设",
            note: _presetCards.Count == 0 ? "未配置" : $"{_presetCards.Count} 个");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "cu-grid2");
        AddInput(b, ref i, "预设名称", _newPresetName, v => _newPresetName = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;align-items:flex-end;");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "cu-btn primary");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => AddPresetCard()));
        b.AddContent(i++, "+ 保存预设");
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
        AddHelp(b, ref i, "预设存在哪？AI 怎么用？",
            "数据存于 Storage/Config/Alife.Plugin.Comfyui/prompt-presets.json，插件更新不会清空。\n"
            + "AI 用 getpromptpreset 按名检索复用；同名保存会覆盖。");
        EndSection(b, ref i);
    }

    // ============================================================
    // 进阶页 · 在线检索（标签 / 角色）
    // ============================================================
    void RenderOnlineSections(RenderTreeBuilder b, ref int i)
    {
        AddSection(b, ref i, "在线标签检索", note: Configuration.EnableDanbooruSearch ? "已开" : "未开");
        AddCheck(b, ref i, "启用语义标签检索（search / related）", Configuration.EnableDanbooruSearch,
            v => Configuration.EnableDanbooruSearch = v);
        AddCheck(b, ref i, "启用画师推荐（依赖总开关）", Configuration.EnableDanbooruArtistRecommend,
            v => Configuration.EnableDanbooruArtistRecommend = v,
            enabled: Configuration.EnableDanbooruSearch);
        AddHint(b, ref i, "默认关=零外网；开启后需重载模块函数才注册。");

        if (Configuration.EnableDanbooruSearch)
        {
            if (Configuration.ShowExpertOptions)
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
            }

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
            AddHint(b, ref i, "使用公开服务时请友情链接上游：");
            AddLinkRow(b, ref i, "DanbooruSearch 在线服务（Hugging Face Space）",
                "https://huggingface.co/spaces/SAkizuki/DanbooruSearch");
        }
        AddHelp(b, ref i, "为什么要开？",
            "有服装/姿势/场景等细节时，AI 可把自然语言转成标准 Danbooru tag，出图更准；"
            + "检索失败会自动回到自写英文，仍能正常生图。");
        EndSection(b, ref i);

        AddSection(b, ref i, "在线角色检索",
            note: Configuration.EnableAnimadexCharacterSearch ? "已开" : "未开");
        AddCheck(b, ref i, "启用在线角色检索（本地未命中时联网补查）", Configuration.EnableAnimadexCharacterSearch,
            v => Configuration.EnableAnimadexCharacterSearch = v);
        AddHint(b, ref i, "仅补充本地索引：本地命中仍优先，默认关=零外网。");

        if (Configuration.EnableAnimadexCharacterSearch)
        {
            var provider = (Configuration.CharacterLookupProvider ?? "").Trim().ToLowerInvariant();
            if (provider is not ("mirror" or "danbooru" or "auto" or "animadex"))
                provider = "mirror";
            AddSelect(b, ref i, "数据源", provider,
                v => Configuration.CharacterLookupProvider = v,
                new[]
                {
                    ("mirror", "DanbooruSearch 国内镜像（推荐：0.3~1.5s，支持中文角色名）"),
                    ("danbooru", "Danbooru 官方 API（国内常超时，需代理/反代）"),
                    ("auto", "自动：镜像 → 官方 → AnimaDex 依次回退"),
                    ("animadex", "AnimaDex（部分网络无法直连）")
                });

            var useMirror = provider is "mirror" or "auto";
            var useBooru = provider is "danbooru" or "auto";
            var useAnimadex = provider is "animadex" or "auto";

            if (useMirror)
            {
                if (Configuration.ShowExpertOptions)
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-grid2");
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "镜像地址", Configuration.CharacterMirrorUrl, v => Configuration.CharacterMirrorUrl = v);
                    AddHint(b, ref i, "默认与「在线标签检索」同一个 DanbooruSearch 服务（国内可直连）。");
                    b.CloseElement();
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "超时秒数", Configuration.CharacterMirrorTimeoutSeconds.ToString(),
                        SetInt(v => Configuration.CharacterMirrorTimeoutSeconds = Math.Clamp(v, 3, 60)));
                    AddInput(b, ref i, "最少图片数（过滤冷门/背景角色）", Configuration.DanbooruCharacterMinPosts.ToString(),
                        SetInt(v => Configuration.DanbooruCharacterMinPosts = Math.Clamp(v, 0, 100000)));
                    b.CloseElement();
                    b.CloseElement();
                }

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-row");
                b.AddAttribute(i++, "style", "margin-top:10px;");
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "cu-btn");
                b.AddAttribute(i++, "disabled", _mirrorCharTesting);
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestMirrorCharacterConnectivity));
                b.AddContent(i++, _mirrorCharTesting ? "测试中…" : "测试镜像连通");
                b.CloseElement();
                b.CloseElement();
                if (!string.IsNullOrWhiteSpace(_mirrorCharTestMessage))
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-detect");
                    b.AddContent(i++, _mirrorCharTestMessage);
                    b.CloseElement();
                }
                AddHint(b, ref i,
                    "镜像返回带中文角色名与作品名，中文查询可直接命中（如「薇薇安」「酒寄彩葉」）；"
                    + "同时给出英文 trigger / 外貌 / 默认服装，10 分钟缓存。");
            }

            if (useBooru)
            {
                if (Configuration.ShowExpertOptions)
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-grid2");
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "Danbooru API 地址", Configuration.DanbooruCharacterBaseUrl,
                        v => Configuration.DanbooruCharacterBaseUrl = v);
                    AddHint(b, ref i, "默认 https://danbooru.donmai.us。连不上可填自建反向代理；不要填网页版路径。");
                    b.CloseElement();
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "超时秒数", Configuration.DanbooruCharacterTimeoutSeconds.ToString(),
                        SetInt(v => Configuration.DanbooruCharacterTimeoutSeconds = Math.Clamp(v, 5, 60)));
                    AddInput(b, ref i, "最少图片数（过滤冷门/背景角色）", Configuration.DanbooruCharacterMinPosts.ToString(),
                        SetInt(v => Configuration.DanbooruCharacterMinPosts = Math.Clamp(v, 0, 100000)));
                    b.CloseElement();
                    b.CloseElement();
                }

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-row");
                b.AddAttribute(i++, "style", "margin-top:10px;");
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "cu-btn");
                b.AddAttribute(i++, "disabled", _booruCharTesting);
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestBooruCharacterConnectivity));
                b.AddContent(i++, _booruCharTesting ? "测试中…" : "测试 Danbooru 连通");
                b.CloseElement();
                b.CloseElement();
                if (!string.IsNullOrWhiteSpace(_booruCharTestMessage))
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-detect");
                    b.AddContent(i++, _booruCharTestMessage);
                    b.CloseElement();
                }
                AddHint(b, ref i,
                    "Danbooru 数据实时更新，新番/新游角色基本都能查到；返回 source: danbooru 时会带外貌与默认服装。\n"
                    + "单次查询约 2 个请求（名字搜索 + 特征标签），有 10 分钟缓存；不支持中文角色名，中文会先查本地库。");
            }

            if (useAnimadex)
            {
                if (Configuration.ShowExpertOptions)
                {
                    b.OpenElement(i++, "div");
                    b.AddAttribute(i++, "class", "cu-grid2");
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "AnimaDex 服务地址", Configuration.AnimadexBaseUrl, v => Configuration.AnimadexBaseUrl = v);
                    AddHint(b, ref i, "默认 animadex.net。部分地区/网络无法直连时会超时，建议改用 Danbooru。");
                    b.CloseElement();
                    b.OpenElement(i++, "div");
                    AddInput(b, ref i, "超时秒数", Configuration.AnimadexTimeoutSeconds.ToString(),
                        SetInt(v => Configuration.AnimadexTimeoutSeconds = Math.Clamp(v, 5, 60)));
                    b.CloseElement();
                    b.CloseElement();
                }

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "cu-row");
                b.AddAttribute(i++, "style", "margin-top:10px;");
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "cu-btn");
                b.AddAttribute(i++, "disabled", _animadexTesting);
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create(this, TestAnimadexConnectivity));
                b.AddContent(i++, _animadexTesting ? "测试中…" : "测试 AnimaDex 连通");
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
        }
        EndSection(b, ref i);
    }

    // ============================================================
    // 页签 3：进阶（显示选项 / AI 注入 / 节点 / 在线检索 / 关于）
    // ============================================================
    void RenderAdvancedTab(RenderTreeBuilder b)
    {
        int i = 6000;
        var appMcp = IsAppMcp();
        var expert = Configuration.ShowExpertOptions;

        AddSection(b, ref i, "显示选项", open: true, note: expert ? "专家模式" : "精简模式");
        AddToggle(b, ref i, "显示专家选项", "EXPERT", Configuration.ShowExpertOptions,
            v => Configuration.ShowExpertOptions = v);
        AddHint(b, ref i, "默认只显示必填项；打开后可微调节点 ID、轮询间隔、在线地址与超时等。");
        EndSection(b, ref i);

        AddSection(b, ref i, "AI 注入方式", note: Configuration.ImplicitInjection ? "隐式" : "显式");
        AddToggle(b, ref i, "隐式注入（省 token）", "4.0", Configuration.ImplicitInjection,
            v => Configuration.ImplicitInjection = v);
        AddHint(b, ref i, "改动需重载模块后生效。");
        AddHelp(b, ref i, "显式 / 隐式有什么区别？",
            "显式（默认）：函数文档直接注入系统提示词，AI 立刻会用。\n"
            + "隐式：文档不直接注入，AI 需先调用 <comfyuiimagegeneration/> 按需加载，省 token 但多一次调用。");
        EndSection(b, ref i);

        if (!appMcp)
        {
            AddSection(b, ref i, "节点控制与概览",
                note: Configuration.AdvancedMode ? "概览已开" : null);
            AddToggle(b, ref i, "AI 节点控制", "ADVANCED", Configuration.EnableNodeControl,
                v => Configuration.EnableNodeControl = v);
            AddToggle(b, ref i, "工作流节点概览", "BETA", Configuration.AdvancedMode,
                v => Configuration.AdvancedMode = v,
                afterToggleAsync: async () =>
                {
                    if (Configuration.AdvancedMode)
                        await LoadNodeOverview();
                });
            AddHint(b, ref i, "节点控制让 AI 能换模型/步数/采样器；概览用于排查工作流。");

            if (Configuration.AdvancedMode)
            {
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
                    b.AddContent(i++, "尚未加载。切换一次本页签或重开上面开关即可加载。");
                    b.CloseElement();
                }
                else
                {
                    AddNodeOverview(b, ref i);
                }
            }
            EndSection(b, ref i);
        }

        RenderOnlineSections(b, ref i);

        AddSection(b, ref i, "关于与依赖");
        AddHint(b, ref i,
            "ComfyUI × Alife · Doro 的妙妙工具，可在插件市场搜索安装/更新。\n"
            + "配置改完用面板的保存入口应用；切换后端需重载模块。");
        AddHelp(b, ref i, "依赖与数据目录",
            "可选依赖（装完请重载本模块）：\n"
            + "· 模板模式 → ComfyUI 侧需装第三方节点 ComfyUI-APP-MCP（「后端 · 画风」页有教程）。\n"
            + "· 工作流用到的自定义节点（ZML / WeiLin / Crystools / DLSS 等）需在 ComfyUI 侧已安装。\n"
            + "提示词预设目录（插件更新不会清空）：" + ComfyuiService.GetUserDataDirectory());
        AddLinkRow(b, ref i, "ComfyUI-APP-MCP（模板模式依赖，GitHub）",
            "https://github.com/Lotus0614/ComfyUI-APP-MCP");
        AddLinkRow(b, ref i, "在线标签检索上游 DanbooruSearchOnline（MIT）",
            "https://github.com/SuzumiyaAkizuki/DanbooruSearchOnline");
        AddLinkRow(b, ref i, "在线角色检索上游 AnimaDex", "https://animadex.net");
        EndSection(b, ref i);
    }

    // ============================================================
    // 轻量渲染辅助
    // ============================================================
    /// <summary>
    /// 可折叠分区：标题常显、内容默认收起（open=true 的分区默认展开）。
    /// 用原生 &lt;details&gt;，不依赖 JS；展开状态由浏览器自己保存，重渲染不会把用户展开的分区收回去。
    /// 必须与 <see cref="EndSection"/> 成对使用。
    /// </summary>
    void AddSection(RenderTreeBuilder b, ref int seq, string title, bool open = false, string? note = null)
    {
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "cu-sec");
        if (open)
            b.AddAttribute(seq++, "open", true);

        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, title);
        if (!string.IsNullOrWhiteSpace(note))
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "cu-sec-hint");
            b.AddAttribute(seq++, "title", note);
            b.AddContent(seq++, note);
            b.CloseElement();
        }
        b.CloseElement(); // summary

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-sec-body");
    }

    void EndSection(RenderTreeBuilder b, ref int seq)
    {
        b.CloseElement(); // cu-sec-body
        b.CloseElement(); // details
    }

    /// <summary>长解释折叠块：默认只显示一行摘要，点开才看详情。</summary>
    void AddHelp(RenderTreeBuilder b, ref int seq, string summary, string detail)
    {
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "cu-help");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, summary);
        b.CloseElement();
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "cu-hint");
        b.AddContent(seq++, detail);
        b.CloseElement();
        b.CloseElement();
    }

    void MarkDirty() => _dirty = true;

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
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                MarkDirty();
                onChange(e.Value?.ToString() ?? "");
            }));
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
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                MarkDirty();
                onChange(e.Value?.ToString() ?? "");
            }));
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
            EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                MarkDirty();
                onChange(e.Value?.ToString() ?? "");
            }));
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
            MarkDirty();
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
            MarkDirty();
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

    async Task TestMirrorCharacterConnectivity()
    {
        if (_mirrorCharTesting) return;
        _mirrorCharTesting = true;
        _mirrorCharTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var baseUrl = Configuration.CharacterMirrorUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _mirrorCharTestMessage = "未填写镜像地址";
            }
            else
            {
                var (ok, msg, ms) = await MirrorCharacterClient.HealthAsync(baseUrl, 20);
                _mirrorCharTestMessage = $"{(ok ? "OK" : "FAIL")} {ms}ms — {msg}";
            }
        }
        catch (Exception ex)
        {
            _mirrorCharTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _mirrorCharTesting = false;
            StateHasChanged();
        }
    }

    async Task TestBooruCharacterConnectivity()
    {
        if (_booruCharTesting) return;
        _booruCharTesting = true;
        _booruCharTestMessage = "探测中…";
        StateHasChanged();
        try
        {
            var baseUrl = Configuration.DanbooruCharacterBaseUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _booruCharTestMessage = "未填写 API 地址";
            }
            else
            {
                var (ok, msg, ms) = await BooruCharacterClient.HealthAsync(baseUrl, 20);
                _booruCharTestMessage = $"{(ok ? "OK" : "FAIL")} {ms}ms — {msg}";
            }
        }
        catch (Exception ex)
        {
            _booruCharTestMessage = "测试异常: " + ex.Message;
        }
        finally
        {
            _booruCharTesting = false;
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
        MarkDirty();
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

    // ===================== 画风预设卡片管理 =====================

    void LoadStyleCards()
    {
        _styleCards = new();
        var json = Configuration?.StylePresets ?? "[]";
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr)
                return;
            foreach (var item in arr.OfType<JsonObject>())
            {
                var name = WfJsonString(item, "n") ?? WfJsonString(item, "name") ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var template = WfJsonString(item, "t") ?? WfJsonString(item, "template") ?? "";
                var prefix = WfJsonString(item, "f") ?? WfJsonString(item, "prefix") ?? "";
                var group = WfJsonString(item, "g") ?? WfJsonString(item, "group") ?? "";

                var pNode = item["p"] ?? item["params"];
                var paramsJson = pNode switch
                {
                    JsonObject po => po.ToJsonString(),
                    JsonValue pv when pv.GetValueKind() == System.Text.Json.JsonValueKind.String
                        => pv.GetValue<string>() ?? "",
                    _ => ""
                };

                _styleCards.Add(new StyleCard
                {
                    Name = name,
                    Template = template,
                    Prefix = prefix,
                    Group = group,
                    Params = paramsJson
                });
            }
        }
        catch
        {
            // 配置里的 JSON 坏了：当作没有画风，避免整个面板打不开
        }
    }

    void SaveStyleCards()
    {
        var arr = new JsonArray();
        foreach (var card in _styleCards)
        {
            if (string.IsNullOrWhiteSpace(card.Name))
                continue;

            var obj = new JsonObject { ["n"] = card.Name.Trim() };
            if (!string.IsNullOrWhiteSpace(card.Template))
                obj["t"] = card.Template.Trim();
            if (!string.IsNullOrWhiteSpace(card.Prefix))
                obj["f"] = card.Prefix;
            if (!string.IsNullOrWhiteSpace(card.Group))
                obj["g"] = card.Group.Trim();
            if (!string.IsNullOrWhiteSpace(card.Params))
            {
                var p = card.Params.Trim();
                try { obj["p"] = JsonNode.Parse(p)?.DeepClone(); }
                catch { obj["p"] = p; } // 非法 JSON 原样保留，调用时会给出可读报错
            }
            arr.Add(obj);
        }

        Configuration.StylePresets = arr.ToJsonString();
        MarkDirty();
    }

    void AddStyleCard()
    {
        var name = _newStyleName?.Trim() ?? "";
        if (name.Length == 0)
        {
            StateHasChanged();
            return;
        }
        if (name.Length > 60) name = name[..60];

        _styleCards = _styleCards
            .Where(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 模板模式最省事的用法是「组名 = 画风名」，默认帮用户填上
        _styleCards.Add(new StyleCard { Name = name, Group = IsAppMcp() ? name : "" });
        _newStyleName = "";
        SaveStyleCards();
        _activeStyleIndex = _styleCards.Count - 1;
        StateHasChanged();
    }

    void RemoveStyleCard(int index)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards.RemoveAt(index);
        _activeStyleIndex = -1;
        SaveStyleCards();
        StateHasChanged();
    }

    void UpdateStyleCardName(int index, string name)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards[index] = _styleCards[index] with { Name = name };
        SaveStyleCards();
    }

    void UpdateStyleCardTemplate(int index, string template)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards[index] = _styleCards[index] with { Template = template };
        SaveStyleCards();
    }

    void UpdateStyleCardPrefix(int index, string prefix)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards[index] = _styleCards[index] with { Prefix = prefix };
        SaveStyleCards();
    }

    void UpdateStyleCardGroup(int index, string group)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards[index] = _styleCards[index] with { Group = group };
        SaveStyleCards();
    }

    void UpdateStyleCardParams(int index, string parameters)
    {
        if (index < 0 || index >= _styleCards.Count) return;
        _styleCards[index] = _styleCards[index] with { Params = parameters };
        SaveStyleCards();
    }

    void ToggleStyleExpand(int index)
    {
        _activeStyleIndex = _activeStyleIndex == index ? -1 : index;
        StateHasChanged();
    }
}
