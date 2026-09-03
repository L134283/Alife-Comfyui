using System.ComponentModel;

namespace Alife.Plugin.Comfyui;

public class ComfyuiConfig
{
    [Description("ComfyUI 服务地址，例如 http://127.0.0.1:8188")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    [Description("工作流 JSON 路径（支持 UI 格式或 API 格式）。可填绝对路径，或相对插件目录的路径。必填")]
    public string WorkflowPath { get; set; } = "";

    [Description("默认工作流的正向提示词节点 ID。留空则自动识别；命名工作流始终按各自连接关系识别")]
    public string PositivePromptNodeId { get; set; } = "";

    [Description("正向提示词输入字段名，默认 positive；标准 CLIPTextEncode 用 text")]
    public string PositivePromptInput { get; set; } = "positive";

    [Description("固定正向提示词前缀（默认工作流用）。命名工作流可在各自卡片里单独设置前缀，留空则沿用此项。生图时与 AI 提示词合并后自动去重，统一为「英文逗号+空格」分隔；此项留空则不拼接")]
    public string PositivePromptPrefix { get; set; } = "";

    [Description("固定负面提示词。填写后覆盖工作流负面，并规范为英文逗号分隔；留空则使用工作流自带负面")]
    public string NegativePrompt { get; set; } = "";

    [Description("默认工作流的负面提示词节点 ID。留空则自动识别；命名工作流始终按各自连接关系识别")]
    public string NegativePromptNodeId { get; set; } = "";

    [Description("负面提示词输入字段名，默认 positive；标准 CLIPTextEncode 用 text")]
    public string NegativePromptInput { get; set; } = "positive";

    [Description("默认工作流的分辨率节点 ID。留空则自动识别；命名工作流始终独立识别")]
    public string ResolutionNodeId { get; set; } = "";

    [Description("默认方向：portrait=竖版(832×1216)、landscape=横版(1216×832)、square=正方形(1216×1216)。AI 调用时可传 orientation 覆盖，也可直接指定 width/height")]
    public string DefaultOrientation { get; set; } = "portrait";

    [Description("默认宽度（兜底用：未指定方向、也未传 width 时使用）")]
    public int DefaultWidth { get; set; } = 832;

    [Description("默认高度（兜底用：未指定方向、也未传 height 时使用）")]
    public int DefaultHeight { get; set; } = 1216;

    [Description("是否每次随机 seed（推荐开启）")]
    public bool RandomizeSeed { get; set; } = true;

    [Description("等待出图超时秒数")]
    public int TimeoutSeconds { get; set; } = 300;

    [Description("轮询间隔毫秒")]
    public int PollIntervalMs { get; set; } = 1500;

    [Description("图片保存目录。留空则使用 Alife 存储目录/Images/Comfyui")]
    public string SaveDirectory { get; set; } = "";

    [Description("额外保存副本：开启时将工作流输出的图片额外复制到上方「图片保存目录」；关闭时直接使用工作流内保存节点的路径，不再额外复制")]
    public bool ExtraSaveCopy { get; set; } = true;

    [Description("ComfyUI output 目录绝对路径（如 D:\\AI\\ComfyUI-aki-v1.6\\ComfyUI\\output）。填写后标准 SaveImage 的图片直接引用该目录，不再重复下载")]
    public string ComfyuiOutputPath { get; set; } = "";

    [Description("可选：ComfyUI API 鉴权 Token（部分反代需要）")]
    public string ApiToken { get; set; } = "";

    [Description("提示词种类：tag=纯标签，natural=角色 Tag + 英文短句，hybrid=标签与自然语言混合")]
    public string PromptStyle { get; set; } = "tag";

    [Description("桌面端：生完图后自动用系统默认图片查看器打开图片")]
    public bool AutoOpenImage { get; set; } = false;

    /// <summary>
    /// 优先生图：GenerateImage 会阻塞到出图结束（或超时）才返回，
    /// 避免同轮对话里桌宠先 TTS 说话、与 Comfy 抢 GPU 导致卡死无声。
    /// 默认关（纯生图用户不受影响）；与本地 TTS 同机时建议开启。
    /// 本插件不依赖任何语音插件。
    /// </summary>
    [Description("优先生图：生图请求开始后阻塞到出图结束再返回（期间勿语音）。同机有本地 TTS 时建议开；纯生图可关。超时自动结束")]
    public bool PriorityImageGen { get; set; } = false;

    /// <summary>
    /// 优先生图模式下的硬超时上限（秒）。实际等待 = min(TimeoutSeconds, 本值)。
    /// 防止 Comfy 彻底卡死时桌宠/对话一直挂起。
    /// </summary>
    [Description("优先生图硬超时上限（秒）。实际等待取 min(超时秒数, 本值)，防止生图卡死拖死桌宠")]
    public int PriorityMaxWaitSeconds { get; set; } = 180;

    [Description("竖版预设宽度")]
    public int PortraitWidth { get; set; } = 832;

    [Description("竖版预设高度")]
    public int PortraitHeight { get; set; } = 1216;

    [Description("横版预设宽度")]
    public int LandscapeWidth { get; set; } = 1216;

    [Description("横版预设高度")]
    public int LandscapeHeight { get; set; } = 832;

    [Description("正方形预设宽度")]
    public int SquareWidth { get; set; } = 1216;

    [Description("正方形预设高度")]
    public int SquareHeight { get; set; } = 1216;

    [Description("高级模式开关：开启后显示工作流节点概览，供进阶用户使用")]
    public bool AdvancedMode { get; set; } = false;

    [Description("AI 节点控制开关：开启后 AI 可操控模型/步数/采样器等节点参数。不影响原有简单模式")]
    public bool EnableNodeControl { get; set; } = false;

    [Description("隐式注入（4.0 新特性）：开启后函数文档不直接注入系统提示词，AI 需先调用 <comfyuiimagegeneration/> 按需加载（省 token，渐进式）；关闭则显式注入，功能说明直接可用")]
    public bool ImplicitInjection { get; set; } = false;

    [Description("命名工作流列表（JSON 数组）。格式：[{\"n\":\"名称\",\"p\":\"路径\",\"e\":true,\"f\":\"前缀(可空)\"}]。n=名称 p=路径 e=启用 f=固定正向提示词前缀(留空沿用全局前缀)")]
    public string NamedWorkflows { get; set; } = "[{\"n\":\"文生图\",\"p\":\"\",\"e\":false},{\"n\":\"图生图\",\"p\":\"\",\"e\":false},{\"n\":\"其他\",\"p\":\"\",\"e\":false}]";

    [Description("提示词预设列表（JSON 数组）。格式：[{\"n\":\"预设名\",\"c\":\"提示词内容\"}]。AI 可通过 getpromptpreset 按需检索复用")]
    public string PromptPresets { get; set; } = "[]";

    [Description("默认工作流的图生图 LoadImage 节点 ID。留空则自动识别；命名工作流始终独立识别")]
    public string LoadImageNodeId { get; set; } = "";

    [Description("图生图 LoadImage 节点的图片输入字段名，默认 image")]
    public string LoadImageInput { get; set; } = "image";

    // ---------- 在线 Danbooru 语义标签检索（默认关，避免外网依赖） ----------

    [Description("启用在线 Danbooru 语义标签检索（search/related）。默认关；开启后 AI 有画面细节时可检索标准 tag 提升出图质量")]
    public bool EnableDanbooruSearch { get; set; } = false;

    [Description("启用画师推荐（依赖在线标签检索总开关）。默认关；开启后 AI 可在用户明确要画风/画师时查询")]
    public bool EnableDanbooruArtistRecommend { get; set; } = false;

    [Description("标签检索主源 BaseUrl。默认官方备份域（大陆通常更快）。可填自建地址")]
    public string DanbooruSearchPrimaryUrl { get; set; } = "https://sakizuki-danboorusearchonline.ms.show";

    [Description("标签检索备用 BaseUrl。主源失败时回退；主源为自定义非官方域时不自动回退。可空=不回退")]
    public string DanbooruSearchFallbackUrl { get; set; } = "https://sakizuki-danboorusearch.hf.space";

    [Description("单次在线检索总超时秒数（含主备源尝试预算）")]
    public int DanbooruSearchTimeoutSeconds { get; set; } = 45;

    [Description("在线检索是否返回/优先 NSFW 标签。默认关，使用 SFW 结果")]
    public bool DanbooruSearchShowNsfw { get; set; } = false;

    // ---------- 在线角色检索扩充（AnimaDex，默认关，保持零外网） ----------

    [Description("启用在线角色检索扩充：本地角色索引（character-prompts.json）未命中或带作品名仍歧义时，自动联网查 AnimaDex 在线角色库（约 3.6 万角色）。仅扩充不替换：本地命中/歧义处理仍优先")]
    public bool EnableAnimadexCharacterSearch { get; set; } = false;

    [Description("AnimaDex 在线角色库服务地址。默认官方站点（大陆直连通常约 1s）；可填本地自建地址")]
    public string AnimadexBaseUrl { get; set; } = "https://animadex.net";

    [Description("单次 AnimaDex 在线查询超时秒数。失败会自动降级回纯本地行为，不阻塞生图")]
    public int AnimadexTimeoutSeconds { get; set; } = 15;

    // ---------- 模型卸载与空闲自动释放 ----------

    [Description("空闲自动卸载总开关：开启后距最近一次生图结束空闲超过设定时长，自动卸载已加载模型释放显存/内存。默认关")]
    public bool EnableAutoUnload { get; set; } = false;

    [Description("空闲自动卸载时长-小时部分（0~720），与分钟部分相加为总空闲时长")]
    public int AutoUnloadIdleHours { get; set; } = 0;

    [Description("空闲自动卸载时长-分钟部分（0~59），与小时部分相加为总空闲时长。默认 30 分钟")]
    public int AutoUnloadIdleMinutes { get; set; } = 30;
}
