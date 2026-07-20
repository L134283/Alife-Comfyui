using System.ComponentModel;

namespace Alife.Plugin.Comfyui;

public class ComfyuiConfig
{
    [Description("ComfyUI 服务地址，例如 http://127.0.0.1:8188")]
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    [Description("工作流 JSON 路径（支持 UI 格式或 API 格式）。可填绝对路径，或相对插件目录的路径。必填")]
    public string WorkflowPath { get; set; } = "";

    [Description("正向提示词节点 ID。留空则自动识别（优先 WeiLinPromptUI / CLIPTextEncode）")]
    public string PositivePromptNodeId { get; set; } = "";

    [Description("正向提示词输入字段名，默认 positive；标准 CLIPTextEncode 用 text")]
    public string PositivePromptInput { get; set; } = "positive";

    [Description("固定正向提示词前缀。生图时会自动拼到正向提示词最前面，内部换行会被原样保留。留空则不拼接")]
    public string PositivePromptPrefix { get; set; } = "";

    [Description("固定负面提示词。留空则使用工作流自带的负面提示词")]
    public string NegativePrompt { get; set; } = "";

    [Description("负面提示词节点 ID。留空则自动识别（正向节点之外的另一个 WeiLinPromptUI / CLIPTextEncode）")]
    public string NegativePromptNodeId { get; set; } = "";

    [Description("负面提示词输入字段名，默认 positive；标准 CLIPTextEncode 用 text")]
    public string NegativePromptInput { get; set; } = "positive";

    [Description("分辨率节点 ID（如 ZML_PresetResolutionV2 / EmptyLatentImage）。留空则自动识别")]
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

    [Description("可选：ComfyUI API 鉴权 Token（部分反代需要）")]
    public string ApiToken { get; set; } = "";

    [Description("提示词种类：tag=纯标签, natural=纯自然语言, hybrid=混合模式")]
    public string PromptStyle { get; set; } = "tag";

    [Description("桌面端：生完图后自动用系统默认图片查看器打开图片")]
    public bool AutoOpenImage { get; set; } = false;

    /// <summary>
    /// 优先生图：GenerateImage 会阻塞到出图结束（或超时）才返回，
    /// 避免同轮对话里桌宠先 TTS 说话、与 Comfy 抢 GPU 导致卡死无声。
    /// </summary>
    [Description("优先生图：生图请求发出后等待结果返回再继续（期间勿语音）。超时后自动结束，避免桌宠一直卡住")]
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

    [Description("命名工作流列表（JSON 数组）。格式：[{\"n\":\"文生图\",\"p\":\"workflows/txt2img.json\",\"e\":true}]。n=名称 p=路径 e=启用")]
    public string NamedWorkflows { get; set; } = "[{\"n\":\"文生图\",\"p\":\"\",\"e\":false},{\"n\":\"图生图\",\"p\":\"\",\"e\":false},{\"n\":\"其他\",\"p\":\"\",\"e\":false}]";

    [Description("图生图 LoadImage 节点 ID。留空则自动识别（class_type 含 LoadImage 的节点）")]
    public string LoadImageNodeId { get; set; } = "";

    [Description("图生图 LoadImage 节点的图片输入字段名，默认 image")]
    public string LoadImageInput { get; set; } = "image";
}
