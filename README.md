# Alife.Plugin.Comfyui

通过 ComfyUI 工作流生图的 Alife 插件。适配任意 ComfyUI 工作流，不绑定特定工作流。

## 功能

- 可配置 ComfyUI 地址（默认 `http://127.0.0.1:8188`）
- 支持 **UI 工作流 JSON** 自动转 API 格式，也支持官方 **Export (API)** 导出的 API JSON
- 三档常用分辨率预设（竖版 / 横版 / 正方形），AI 可智能选择，也可由用户指定宽高
- **固定正向提示词前缀**：生图时自动拼到正向提示词最前面，内部换行原样保留
- **固定负面提示词**：可选，留空则使用工作流自带负面
- AI 调用参数：正向提示词（必填）、方向 / 宽高（可选）

## 分辨率三档

| orientation | 方向 | 宽 × 高 | 适合场景 |
|-------------|------|---------|----------|
| `portrait`  | 竖版 | 832 × 1216 | 全身立绘、人物、手机壁纸 |
| `landscape` | 横版 | 1216 × 832 | 风景、场景、横构图 |
| `square`    | 正方形 | 1216 × 1216 | 头像、图标、对称构图 |

调用约定（优先级从高到低）：
1. 显式传 `width` + `height` → 按指定值（仍 clamp 到 64–4096）
2. 只传 `orientation` → 用对应预设
3. 都不传 → 用配置里的「默认方向」
4. 默认方向也无效 → 用配置里的兜底宽高

## 固定提示词前缀

在插件配置 UI 的「提示词前缀与负面」区域填写。例如：

```
masterpiece, best quality, score_9, score_8, newest, highres,

(@buran buta), (@baonu de zhanshen caibuto),
```

生图时实际发送给 ComfyUI 的正向提示词为：`前缀` + 换行 + `AI 传入的 prompt`。
前缀内部的空行会被原样保留（分段作用不受影响）。

## 使用

1. 启动 ComfyUI
2. 在 Alife 中启用模块 `ComfyuiService`
3. 配置 ComfyUI 地址与工作流路径（可填相对插件目录的路径或绝对路径）
4. 按需填写固定提示词前缀、默认方向
5. 让 AI 调用 `GenerateImage`

## AI 调用示例

```
<!-- 竖版立绘 -->
<function>GenerateImage</function>
<arg name="prompt">1girl, hololive, tokoyami towa, demon tail, ...</arg>
<arg name="orientation">portrait</arg>

<!-- 横版风景，自定义宽高 -->
<function>GenerateImage</function>
<arg name="prompt">a beautiful landscape, mountains, sunset</arg>
<arg name="width">1216</arg>
<arg name="height">832</arg>
```

## 节点自动识别

插件通过**采样器连接关系**识别正/负面提示词节点（KSampler 的 `positive`/`negative` 输入分别连哪个节点），对原生 ComfyUI 工作流（`CLIPTextEncode`）与第三方节点（`WeiLinPromptUI` 等）均通用，不依赖特定节点类型。

- **UI「自动识别节点」按钮**：点击后会读取工作流 JSON、转换为 API 格式、自动扫描并回填正向/负面/分辨率节点 ID 与字段名，并显示识别结果。
- 连接关系未命中时回退启发式：
  - 正向：`WeiLinPromptUI`（文本最长者）→ `CLIPTextEncode`
  - 负面：正向节点之外的另一个提示词节点
  - 分辨率：`ZML_PresetResolutionV2` / `EmptyLatentImage` 等
- 字段名按节点类型自动选择：`CLIPTextEncode` → `text`，`WeiLinPromptUI` → `positive`，其他 → 第一个字符串字段

如自动识别有误，可在配置中手动指定节点 ID。

## 注意

- 自定义节点（WeiLin / ZML / LoRA 等）必须在 ComfyUI 侧已安装
- UI 工作流中的 **Anything Everywhere** 会尽量通过 `ue_links` 还原；若失败，请改用 API 导出或显式连线
- 若 history 无图但 ZML 保存到了自定义目录，插件会尝试从该目录复制最新图片
- `workflows/艾芙.json` 仅作为示例工作流，可替换为任意你自己的工作流

## 版本历史

### v1.0.0（2026-07-17）

- 初始版本
- 支持 UI 工作流 JSON 自动转 API 格式，也支持官方 API JSON
- 三档分辨率预设（portrait/landscape/square），AI 可智能选择或由用户指定宽高
- 固定正向提示词前缀（保留内部换行）与可选固定负面提示词
- 基于采样器连接关系的正/负面节点自动识别（兼容原生 CLIPTextEncode 与第三方节点）
- UI「自动识别节点」按钮一键扫描工作流并回填节点 ID
- ZML_SaveImageV2 自定义保存目录兜底读取（/view 404 时自动回退本地文件）
