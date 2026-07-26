# Alife.Plugin.Comfyui

通过 ComfyUI 工作流生图的 Alife 插件。适配任意 ComfyUI 工作流，不绑定特定工作流。

## 功能

- 可配置 ComfyUI 地址（默认 `http://127.0.0.1:8188`）
- 支持 **UI 工作流 JSON** 自动转 API 格式，也支持官方 **Export (API)** 导出的 API JSON
- 三档常用分辨率预设（竖版 / 横版 / 正方形），AI 可智能选择，也可由用户指定宽高
- **固定正向提示词前缀**：生图时自动拼到正向提示词最前面，内部换行原样保留
- **固定负面提示词**：可选，留空则使用工作流自带负面
- **多工作流**：可配置多个命名工作流，AI 通过 `workflow` 参数切换
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

手动节点 ID 只属于默认工作流。AI 通过 `workflow` 切换到命名工作流时，插件会忽略默认工作流的节点 ID，并根据所选工作流的采样器连接关系重新识别正向、负面、分辨率和 LoadImage 节点。因此，不同工作流可以使用完全不同的节点编号。

工作流名称必须唯一，并且只有已启用的命名工作流可以调用。名称不存在、重复、已禁用或路径无效时会明确返回错误，不会静默回退到默认工作流。

## 注意

- 自定义节点（WeiLin / ZML / LoRA 等）必须在 ComfyUI 侧已安装
- UI 工作流中的 **Anything Everywhere** 会尽量通过 `ue_links` 还原；若失败，请改用 API 导出或显式连线
- 若 history 无图但 ZML 保存到了自定义目录，插件会尝试从该目录复制最新图片
- `workflows/艾芙.json` 仅作为示例工作流，可替换为任意你自己的工作流

## 与本地 TTS 同机（可选）

本插件**不依赖**任何语音插件，可单独使用。

若同机还跑本地 TTS（如 CosyVoice2），建议：

| 配置 | 建议 | 说明 |
|------|------|------|
| **优先生图** `PriorityImageGen` | **开** | 生图请求开始后阻塞到出图结束，同轮不会先 Speak 抢 GPU |
| 默认 | 关 | 纯生图 / 边聊边画用户保持异步 |

策略对齐（个人用法示例）：

1. 平时以说话为优先  
2. **一旦开始生图** → 等图完成再允许桌宠说话（靠本插件「优先生图」）  
3. 图完后正常说话；若 TTS 一直出不来，语音侧可能 soft interrupt / 结束 Comfy 进程腾 GPU  
4. 能正常说话则两者互不打扰  

超时或连接失败时，提示中会提醒：若同机开了语音优先，Comfy 进程可能已被结束，需手动重启 ComfyUI。

## 版本历史

### v1.0.5（2026-07-27）

- 修复命名工作流复用默认节点 ID 后出现「未找到节点/未明确节点」：AI 传 `workflow` 切换时不再套用默认工作流节点 ID，按所选工作流采样器连接图重新识别
- 配置节点不存在或类型不适用时回退当前工作流自动识别
- 多层 Conditioning 连接递归追踪，区分正负分支
- LoadImage 节点与实际字符串输入字段支持自动回退
- 未知、禁用、重名或路径无效的工作流明确失败，不再静默使用默认工作流
- 使用 ComfyUI `/prompt` 响应返回的真实 `prompt_id` 轮询 `/history/{prompt_id}`
- API 格式工作流跳过不必要的 `object_info` 请求
- 节点控制关闭时忽略高级参数，`denoise` 仍可单独使用
- 上传、提交、history 轮询和图片下载均接入任务取消
- 修复 data URI 分支不可达，并增加 20 MB 限制和图片魔数校验
- UI 自动识别失败时清空旧节点 ID，避免残留旧配置

### v1.0.4（2026-07-22）

- 分辨率预设支持 UI 手工编辑（三档各宽高可直接在卡片上修改）
- 工作流目录扫描：输入 ComfyUI 路径后点「扫描」，列出所有工作流 JSON 下拉选择
- 高级模式开关：开启后展示节点概览表（节点 ID / 类型 / 关键参数），方便排查问题
- AI 节点控制开关：开启后 AI 可直接操控模型/步数/CFG/采样器/调度器/denoise/batch_size 参数
- 图生图 ImageResize 自动识别与分辨率注入
- 多工作流卡片增加「浏览」按钮，展开已扫描的工作流列表供选

### v1.0.3（2026-07-21）

- 新增多工作流支持：命名工作流卡片，含启用/禁用开关，AI 可传 `workflow` 参数切换
- 多工作流默认关闭，不传 `workflow` 时使用默认工作流，保持向下兼容
- 预设三张工作流卡片（文生图 / 图生图 / 其他），默认关闭，需手动启用
- 默认参数 UI 排版优化：复选框独立为自适应全宽行，条件输入不再挤在网格内

### v1.0.2（2026-07-19）

- 图生图提示词复用 PromptStyle 规则，不再直接传中文指令
- denoise 降噪强度从高级模式拆出，简单模式始终可用
- AI 可智能选择降噪强度

### v1.0.1（2026-07-18）

- 新增桌面端「生完图自动打开图片」开关（`AutoOpenImage`），开启后调用系统默认图片查看器
- AI 提示词优化：QQ 环境自动发图 + 桌面端智能判断
- --gpu-only 模式低内存启动支持

### v1.0.0（2026-07-17）

- 初始版本
- 支持 UI 工作流 JSON 自动转 API 格式，也支持官方 API JSON
- 三档分辨率预设（portrait/landscape/square），AI 可智能选择或由用户指定宽高
- 固定正向提示词前缀（保留内部换行）与可选固定负面提示词
- 基于采样器连接关系的正/负面节点自动识别（兼容原生 CLIPTextEncode 与第三方节点）
- UI「自动识别节点」按钮一键扫描工作流并回填节点 ID
- ZML_SaveImageV2 自定义保存目录兜底读取（/view 404 时自动回退本地文件）
