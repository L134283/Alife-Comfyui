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
}
