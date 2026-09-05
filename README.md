# Alife.Plugin.Comfyui

通过 ComfyUI 工作流生图的 Alife 插件。适配任意 ComfyUI 工作流，不绑定特定工作流。

## 功能

- 可配置 ComfyUI 地址（默认 `http://127.0.0.1:8188`）
- 支持 **UI 工作流 JSON** 自动转 API 格式，也支持官方 **Export (API)** 导出的 API JSON（seed 的 control 与 combo 合法值 `fixed` 等可区分，避免 FBCache 等节点参数错位）
- 三档常用分辨率预设（竖版 / 横版 / 正方形），AI 可智能选择，也可由用户指定宽高
- **固定正向提示词前缀**：默认工作流与每个命名工作流都可单独配置前缀，与 AI 提示词合并后自动去重，统一为英文逗号+空格
- **固定负面提示词**：可选；填写则覆盖工作流负面并规范为英文逗号分隔
- **多工作流**：可配置多个命名工作流，AI 通过 `workflow` 参数切换；每个工作流可独立设置固定提示词前缀
- **角色提示词检索**：内置 8906 个角色的中文/英文/别名索引，支持错字、部分名称与作品名消歧
- **在线角色检索扩充（可选，默认关）**：本地索引未收录或仍歧义时，`findcharacterprompt` 自动联网查 AnimaDex 在线角色库（约 3.6 万角色）；本地命中/消歧仍优先，失败自动回纯本地
- **提示词预设**：可保存常用提示词片段（角色人设/动作/背景），AI 按需调用 `getpromptpreset` 检索复用；数据存于 `Storage/Config/Alife.Plugin.Comfyui/`，**插件更新不会清空**
- **在线标签检索（可选，默认关）**：自然语言 → 标准 Danbooru 标签（`searchdanboorutags`）、关联标签、可选画师推荐；多源故障转移
- **隐式注入（4.0 新特性，UI 可切换）**：默认显式注入（函数文档直接进系统提示词）；开启隐式注入后文档不直接注入，AI 先调用 `<comfyuiimagegeneration/>` 按需加载（省 token，渐进式）
- **复杂工作流兼容**：支持 GetNode/SetNode 跨图引用、多采样器（一采+二采+局部重绘）、多保存节点目录
- **模型卸载与显存释放**：UI 一键卸载已加载模型释放显存/内存；可设置空闲 N 小时 N 分钟后自动卸载；AI 可调 `unloadcomfyuimodels` 立即卸载、`setcomfyuiidleunload` 查看/设置空闲自动卸载（设置会持久化）
- AI 调用参数：正向提示词（必填）、方向 / 宽高（可选）

## 角色提示词检索

插件启动时会一次性加载 `character-prompts.json`，角色数据不会注入系统提示词。只有用户明确要画某个已有动漫、游戏、漫画或 VTuber 角色时，模型才应调用：

```xml
<findcharacterprompt name="初音未来" work="VOCALOID"/>
```

普通人物、真人、原创角色、Bot 自己的人设，以及只指定服装、动作或画风的请求都不检索，直接由 Bot 编写提示词。

匹配成功会返回三段英文 Tag：

- `trigger_tags_xml`：角色触发词与作品标识，必须保留
- `appearance_tags_xml`：稳定外貌特征，必须保留
- `default_outfit_tags_xml`：默认服装；用户没有指定服装时才使用

返回字段已经过 XML 属性转义。组合进函数调用时应保留 `&amp;` 等转义写法。若结果为 `status: ambiguous`，函数只返回候选、不返回任何 Tag；应带作品名 `work` 重新查询，不能使用"最像"的候选猜测生图。

例如"雷姆穿哥特萝莉服"：保留雷姆的 `trigger_tags_xml` 和 `appearance_tags_xml`，舍弃默认女仆装，追加哥特萝莉服、当前动作、构图与场景。这样角色身份稳定，但服装和画面仍可自由变化。

若本地未收录该角色（返回 `status: not_found`）、或带作品名仍歧义，可在插件 UI 开启「在线角色检索扩充」：`findcharacterprompt` 会自动联网查 AnimaDex 在线角色库（约 3.6 万角色），返回 `source: animadex` 的 trigger 与特征标签（无默认服装段，换装需自写）。本地命中/消歧仍优先，不影响离线质量。

- 中文常用名、英文 Danbooru Tag 和当前两个词库可验证的别名都会参与搜索
- 无当前来源佐证的旧译名不进入运行时索引；已验证别名的权重仍低于规范名
- 支持少量错字、名称片段和括号前的角色本名
- 1–2 字短名称不做单字纠错，避免把不同角色强行猜成同一人
- 短名称、同名角色或多个版本建议同时传 `work`
- 精简 JSON 运行时索引约 3.0 MiB，加载后在本地搜索，不消耗对话 token

校订版工作簿位于 `outputs/character-prompt-index/角色提示词校订版.xlsx`，包含筛选状态、别名与逐行来源。

### 角色数据来源

- 译名主词库：<https://github.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table>
- 译名交叉校验：<https://github.com/sw1313/danbooru-tags-translation>
- 结构化外貌/服装：<https://github.com/tcpassos/mcp-danbooru-characters>

外貌 Tag 反映 Danbooru/模型训练分布，不等同于官方角色设定。性转或同人占比高的角色可能出现与原作不同的性别或服装 Tag。上述译名库还包含各自的上游数据与许可要求；发布含数据文件的插件包前应再次核对来源许可。

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

## 提示词预设存储位置

用户预设**不再**放在插件安装目录（`Storage/Plugins/Alife.Plugin.Comfyui/`），以免 Alife 更新插件时整目录替换把文件冲掉。

| 路径 | 说明 |
|------|------|
| `Storage/Config/Alife.Plugin.Comfyui/prompt-presets.json` | **正式位置**，升级保留 |
| `Storage/Plugins/.../prompt-presets.json` | 旧位置；新版本首次加载若发现会自动迁到 Config |

备份/换机时复制 `Config/Alife.Plugin.Comfyui/` 即可。

## 固定提示词前缀与自动去重

在插件配置 UI 的「提示词前缀与负面」区域填写。例如：

```
masterpiece, best quality, score_9, score_8, newest, highres
(@buran buta), (@baonu de zhanshen caibuto)
```

生图注入工作流前会：

1. 合并 **固定前缀** + **AI 的 prompt**
2. 按逗号/换行拆成 tag 或小短句，**忽略大小写去重**（前缀优先保留）
3. 统一输出为 **英文逗号 + 空格**，例如：

```
masterpiece, best quality, a girl on the bed, a man sit in the desk
```

中文逗号 `，`、顿号、多余换行仅作输入兼容，**最终不会进入工作流**。AI 提示词里与前缀重复的 `masterpiece` 等会被去掉。

>

AI 默认不再写画风/质量类提示词（如 masterpiece、best quality、style、画师名等）：这些由前缀或工作流决定，用户明确指定画风/画师时除外。

## 提示词模式

- `natural`：检索到的角色 Tag 原样置前，后接 2–4 个简洁英文短句描述服装、动作、构图、场景和光线；多人时每个角色各用一句完整短句，人物关系与互动单独一句
- `hybrid`：静态元素（身份/外貌/服装/表情/光线）用精准英文 Tag，动态内容（动作/互动/关系/构图/氛围）用简洁自然短句；多人时先写人数（1girl, 1boy 等），每个角色各自一个独立短语单元（自然语言串联其外貌/服装/表情），角色间逗号分隔，最后一句写人物关系与互动，禁止特征混写
- `tag`：全程使用去重、无冲突的 Danbooru 风格英文 Tag，按身份、人数、外貌、服装、动作、构图、背景、光线排序；多人时每个角色特征 Tag 各自连续排列

三种模式都使用英文提示词，不把用户中文命令原句直接塞进工作流。

## 在线 Danbooru 语义标签检索（可选）

默认**关闭**（零外网）。开启后，AI 在用户描述含服装/姿势/场景等细节时可调用在线检索，把自然语言转成更准的英文 Tag，再按当前「提示词种类」写入 `generateimage`。

| 函数 | 作用 | 条件 |
|------|------|------|
| `searchdanboorutags` | 自然语言 → 标准 tag | 总开关开 |
| `getrelateddanboorutags` | 已有英文 tag → 共现关联 | 总开关开 |
| `getdanbooruartists` | 画师推荐 | 总开关 + 画师独立开关 |

**优先级（防冲突）**：本地角色 `findcharacterprompt` > 用户预设 > 在线标签 > 画师（可选）> AI 自写。在线结果不得覆盖角色 trigger/appearance。

**质量优先**：三种提示词模式调用积极性相同；差别只在落笔——`natural` 用短句消化检索语义，禁止把大段 tag 列表原样当整段 prompt。

**源与大陆访问**：

| 配置 | 默认 |
|------|------|
| 主源 | `https://sakizuki-danboorusearchonline.ms.show`（官方备份，通常更快） |
| 备用 | `https://sakizuki-danboorusearch.hf.space`（HF Space，可能冷启 30–60s） |
| 自建 | 填自定义主源后**只打自定义**，不自动回退 HF |

失败 soft-fail：超时/熔断后 AI 应自写英文并仍可 `generateimage`，不阻塞生图。

上游项目（MIT）：[DanbooruSearchOnline](https://github.com/SuzumiyaAkizuki/DanbooruSearchOnline)。使用公开服务时请友情链接：[Hugging Face Space](https://huggingface.co/spaces/SAkizuki/DanbooruSearch)。

## 使用

1. 启动 ComfyUI
2. 在 Alife 中启用模块 `ComfyuiService`
3. 配置 ComfyUI 地址与工作流路径（可填相对插件目录的路径或绝对路径）
4. 按需填写固定提示词前缀、默认方向（多工作流时可在每个工作流卡片单独设置前缀）
5. 让 AI 调用 `GenerateImage`

## AI 调用示例

```xml
<!-- 竖版立绘 -->
<generateimage prompt="1girl, hololive, tokoyami towa, demon tail, ..." orientation="portrait"/>

<!-- 横版风景，自定义宽高 -->
<generateimage prompt="a beautiful landscape, mountains, sunset" width="1216" height="832"/>
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
- UI→API 时：KSampler 等节点 seed 后的 `fixed` / `randomize` 等是前端控件，**不会**写入 API；若自定义节点（如 `ApplyFBCacheOnModel`）的 combo 选项本身就是 `fixed`，会作为真实参数保留。含此类节点的复杂流建议在网页与插件各验证一次
- 若 history 无图但 ZML 保存到了自定义目录，插件只会认领本次任务开始后新写入且尚未被其他任务认领的图片
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

### v4.4.1（2026-09-05）

- **修复**：DLSS5Settings、RTXVideoSuperResolution 等新式自定义节点的 UI→API widget 错位。这类节点把下拉声明为 `["COMBO", {options:[...]}]`（含 `COMFY_DYNAMICCOMBO_V3` 动态下拉，选中后带 `resize_type.scale` 子控件），不再用旧式选项数组；转换器此前把这些字段误判为连线输入跳过，并按 object_info `input_order` 重排 widget，导致参数整体错位（如「动漫4」`scene_change_threshold` 错拿 `local_structure_strength` 的 1.5 报「大于最大值 1」）
- widget 映射顺序改为以 UI 保存的 widget 槽位顺序（`widgets_values`）为准，object_info 仅补齐 UI 缺失的当前新增字段；combo 选项读取、可选值校验与 seed 后 control token 判定同步支持新式 `COMBO` 声明
- 新增回归自检：新式 `COMBO` 声明节点转换不得丢字段、不得错位

### v4.4.0（2026-09-03）

- **新增「在线角色检索扩充」（可选，默认关）**：本地角色索引（`character-prompts.json`）未命中、或带作品名仍歧义时，`findcharacterprompt` 自动联网查 AnimaDex 在线角色库（约 3.6 万角色，数据基于 Danbooru 标签聚合），把返回的 trigger/特征标签包装回原有三段格式（无默认服装段）；本地命中与中文/别名/作品消歧仍优先，保持离线可用，在线失败自动回纯本地提示
- 中文→英文转换复用本地索引别名表（AnimaDex 搜索接口只接受英文/罗马字）；UI 新增「在线角色检索扩充」配置：开关、服务地址、超时秒数与连通测试
- 定位说明：实测本地中文「亚丝娜」会优先命中 SAO 版本（在线按热度会把 Blue Archive 版本排第一），因此本功能是**本地未收录时的在线补充**而非替代
- **修复**：Crystools「Save image with extra metadata」保存节点因 class_type 含空格（`Save image`）未被识别为保存类节点，导致该工作流报「history 中未找到图片输出」；保存类节点判定改为兼容 `SaveImage`/`保存`/去标点含 `saveimage` 的变体

### v4.3.1（2026-08-30）

- **修复**：UI 工作流转 API 时，部分新版本 ComfyUI/UE 插件导出的工作流（如含 BooruGalleryNode 的「动漫3」）会把 `extra.ue_links` 中本应是数字的节点 ID 序列化成字符串，直接 `GetValue<int>` 会抛「String 无法转换为 Int32」导致生图失败；链接 / ue_links / 节点 id / mode 的整数读取统一兼容数字与数字字符串

### v4.3.0（2026-08-30）

- **新增「模型卸载与显存释放」**：UI「模型卸载与显存释放」区域提供「立即卸载模型（释放显存）」按钮，一键调用 ComfyUI `/free` 卸载已加载模型并释放显存/内存
- **空闲自动卸载**：可设置空闲 N 小时 N 分钟后自动卸载（默认关；默认 30 分钟）。从最近一次生图结束开始计时，生图中不会误卸
- **AI 自主调用**：`unloadcomfyuimodels` 立即卸载（生图中拒绝）；`setcomfyuiidleunload` 查看/设置空闲自动卸载时长，修改后持久化到配置（重载/重启不丢失）

### v4.2.0（2026-08-10）

- **适配 Alife 4.2.0 框架**：`XmlHandler` API 变更（`Name` 改为只读、无 0 参构造函数），主 handler 与 status handler 改用 `new XmlHandler("ComfyuiImageGeneration"){...}` / `new XmlHandler("ComfyuiStatusInternal"){...}` 初始化器写法，修复插件重载编译失败

### v4.0.0（2026-08-08）

- **适配 Alife 4.0.0 框架**：模块基类从 `InteractiveModule` 迁移到 `ChatBehaviour`，`Prompt`/`Poke` 改为 `IInteractor` 注入，消除 4.0 废弃警告；新增插件清单 `manifest.json`（Version 4.0.0）
- **新增「隐式注入」开关**（UI「高级模式」区域）：显式（默认）直接把函数文档注入系统提示词；隐式模式下函数文档不直接注入，AI 需先调用 `<comfyuiimagegeneration/>` 按需加载（省 token，渐进式）。系统提示词同步给出引导
- **隐式注入瘦身**：开启隐式注入时「功能说明」只常时注入硬规则（英文/质量词/方向/提示词模式/发图格式），角色检索、提示词预设、在线标签、优先生图等详细规则移入 `handler.Explanation`，AI 调用 `<comfyuiimagegeneration/>` 后随文档一并加载，省 token 且效果不变
- **提示词模式升级**：tag 管静态元素（身份/外貌/服装/表情/光线）、自然短句管动态内容（动作/互动/关系/构图/氛围）；多人场景先写人数（1girl, 1boy 等），每个角色各自一个独立描述单元、角色间逗号分隔，最后一句写人物关系与互动，禁止多角色特征混写；不使用括号分组（SD 权重语法）；natural/tag 模式同步补充多人规则
- **系统提示词去重**：删除与函数文档重复的内容（图生图传参、角色检索触发条件、预设检索用法、在线检索触发条件等），尺寸行不再注入像素值；显式模式注入量减少约 1/4
- **热重载健壮性**：模块销毁时自动注销已注册的 `XmlHandler` 与 status 处理器（`ComfyuiStatusInternal`），避免旧处理器在函数表中累积导致函数被重复执行

### v1.3.3（2026-08-02）

- 新增：每个命名工作流可单独配置固定正向提示词前缀（工作流卡片新增前缀输入，JSON 字段 `f`，留空沿用全局前缀），生图时按所选工作流自动选择
- 优化：精简系统提示词（合并图生图说明、压缩角色检索/预设/在线标签与 `generateimage` 描述），AI 不再自写画风/质量类提示词（由固定前缀或工作流决定，用户明确指定画师时可例外）
- 修复：`NamedWorkflows` 与工作流卡片 JSON 解析逐项容错（字段缺失/null/类型不符单独回退，不再整批失效）

### v1.3.2（2026-08-01）

- 修复 UI→API：将 combo 合法值 `fixed`（如 ApplyFBCacheOnModel 的 `threshold_schedule`）误当作 seed 的 `control_after_generate` 跳过，导致参数错位
- 仅在 seed 上下文跳过 control token；有 object_info 时按类型/选项判断
- 生图转换后轻量校验 + `/prompt` 节点错误更可读；模块启动自检 FBCache/KSampler 映射

### v1.3.1（2026-07-31）

- 正向提示词注入前自动去重（前缀 + AI prompt），统一为英文逗号+空格
- 提示词预设改存 `Storage/Config/Alife.Plugin.Comfyui/`，插件更新不再清空；旧路径自动迁移

### v1.3.0（2026-07-30）

- 新增可选在线 Danbooru 语义标签检索（REST）：`searchdanboorutags` / `getrelateddanboorutags` / 独立开关画师推荐
- 默认主源官方备份域、HF 回退、总超时预算、短时缓存与熔断；UI「测试连通」
- 质量优先系统提示词：三种 PromptStyle 同等积极检索，落笔方式不同；默认关=零外网

### v1.2.1（2026-07-29）

- 修复多采样器工作流（如脸部修复流）出多张图问题：`ExtractImages` 只提取 SaveImage 类节点，过滤 PreviewImage 等预览节点
- 新增「额外保存副本」开关：关闭时直接用工作流内保存节点的路径，不额外复制或下载
- 新增「ComfyUI output 目录」配置：填写后标准 SaveImage 的图片直接引用该目录，不再重复下载

### v1.2.0（2026-07-28）

- 修复复杂工作流（如 530 流）正向节点识别错误：多采样器场景收集全部启用采样器，按文本长度择优（正向取最长、负向取最短）
- GetNode/SetNode 跨图引用解析：变量名取自 `widgets_values[0]`，连接重定向到 SetNode 源节点，消除「缺少节点」报错
- 多保存节点目录收集认领：同时支持有元数据和无元数据两个保存路径（如 `output` 与 `D:/ANIMA出图/`）
- 新增提示词预设功能：AI 可调用 `getpromptpreset` 按需检索、`savepromptpreset` 保存常用提示词片段（角色人设/动作/背景）
- UI 提供预设卡片管理：输入保存区 + 名字卡片列表 + 点击展开编辑/删除

### v1.1.0（2026-07-28）

- 新增 8906 个二次元角色的本地中英文、别名、错字与作品名模糊检索
- 返回角色触发词、稳定外貌和默认服装三段 Tag；指定新服装时保留角色身份与外貌、舍弃默认服装
- 原创角色、Bot 自身人设、真人和普通人物不调用角色检索；歧义结果只返候选、不返猜测 Tag
- 精简系统提示词并按配置动态裁剪函数文档；高级节点参数改为按需查询
- 限制节点覆盖范围，修复 `batch_size`、XML 转义、重复 Tag、自定义输出并发误匹配和后台启动异常

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
