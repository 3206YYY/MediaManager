# 图片编辑器：工具「即点即用」改造方案（v1.4.10）

> 面向实现者。本文件只描述要改什么、改哪里、怎么验，**不含代码实现**。
> 任务来源：用户 2026-11-24 提出，AI 只做现状核对 + 出方案，不改业务代码。

## 0. 一句话目标（大白话）

现在右侧那排图标（基础 / 裁剪 / 调色 / 文字 / 标注 / 打码 / 水印）点下去，**大多只是换了一块参数面板**，还得在面板里再点一次「开始裁剪」「添加文字」才真的能动手。
改成：**点哪个图标，画布上立刻进入那个工具的编辑状态**；旁边那块参数面板保留，用来改数值。
另外给马赛克加一个跟随鼠标的画笔大小圆圈，再补一句说明，告诉用户「打码边缘那圈淡描边是给你看的，不是马赛克坏了，成品里不会有」。

## 1. 现状核对（先别重复造轮子）

主体是 `src/MediaManager.App/Views/ViewerWindow.xaml`（985 行）与 `ViewerWindow.xaml.cs`（4430 行），
渲染在 `src/MediaManager.App/Editing/`（`EditorDocument` / `EditorRenderer` / `EditorOverlayFactory`）。

| 现状 | 位置 |
|---|---|
| 分类轨 7 个 `RadioButton`（`CatBasic/CatCrop/CatColor/CatText/CatDraw/CatPrivacy/CatMark`），`Checked="OnCategoryChecked"` | `ViewerWindow.xaml:439-491` |
| `ShowCategory(tag)` 只切换 7 个 `StackPanel` 可见性 | `ViewerWindow.xaml.cs:3328` |
| **已有**：点「标注」自动 `EnterInkMode()`、点「打码」自动 `EnterPrivacyMode()`（注释写着「省掉面板里再点一次」） | `ViewerWindow.xaml.cs:3363-3381` |
| **缺**：点「裁剪」只显示 `CropPanel`，画布没有裁剪框，必须再点面板里的 `CropTool`（「✂ 开始裁剪」） | `ViewerWindow.xaml:567` |
| **缺**：点「文字」只显示 `TextPanel`，必须再点 `TextTool`（「🔤 添加文字」） | `ViewerWindow.xaml:645` |
| **Bug**：`ShowCategory` 的 `default` 分支只退出 Ink/Privacy/Shape/Erase，**没退出 Crop** → 从「裁剪」切到别的分类，裁剪框和 `CropBar` 残留在画布上 | `ViewerWindow.xaml.cs:3365-3379` |
| 裁剪模式一进入就 `ApplyZoom(1.0)` + 重置平移，`ExitCropMode()` 不恢复 → 点一下裁剪再切走，用户的缩放/位置被改了 | `ViewerWindow.xaml.cs:1331-1366` |
| `AddTextItem()` **立刻** `_doc.Mutate("添加文字", …Texts.Add(item))`，默认文本是 `"输入文字"`（非空）；`EndTextEditing()` 只在文本无内容时移除 → **用户啥都不填就切走，成品里会留着「输入文字」四个字**，撤销栈里还多一条记录 | `ViewerWindow.xaml.cs:2031-2058`、`:2114-2140`、`EditorDocument.cs:460` |
| 打码已有 `ToolHint` 操作提示；`_draftHint` 是**落笔之后的轨迹草稿**，不是悬停时的画笔光标 | `ViewerWindow.xaml.cs:2651-2674`、`:2730-2760` |
| 画笔大小 `_privacyWidthDisplay`（默认 40）是**画布坐标像素**，归一化时 `_privacyWidthDisplay / CanvasW`；屏幕直径应为 `width × AbsoluteScale`（`AbsoluteScale = DisplayScale × Zoom`） | `ViewerWindow.xaml.cs:124`、`:2808`、`:3454` |
| 「保护覆盖」= `OutlineDark`(`#30000000`) + `OutlineLight`(`#48FFFFFF`) 双色淡描边，**预览**传 `showOutline: true`，**导出**不传（默认 false），成品已不带 | `EditorOverlayFactory.cs:223-254`、`:389-460`；`ViewerWindow.xaml.cs:554,560,2786`；`EditorRenderer.cs:193,199` |
| 顶部 `ToolHint` 是单行（各工具会覆盖它），默认 `HintText` | `ViewerWindow.xaml:260`、`ViewerWindow.xaml.cs:268` |
| 撤销是快照式：`BeginEdit/Commit/CancelEdit`，`Mutate = BeginEdit + Commit`，会 `History.Push` | `EditorDocument.cs:440-467` |

## 2. 三条红线（写进实现说明，别踩）

1. **只丢「没动手的临时状态」，绝不丢已完成的编辑。**
   用户说的「原来没保存的编辑就消失」，指的是「裁剪框弹出来但我啥都没拖」「文本框弹出来但我一个字没打」「笔按下去还没抬起来就切走」。
   **已确认的裁剪、已写好的文字、已画好的马赛克笔迹 / 涂鸦 / 形状 / 水印必须原样保留**，撤销栈里的记录也保留。
   做成「切工具 = 清空当前所有编辑」是灾难：用户画了 10 笔马赛克，点一下文字全没了。
2. **纯 UI 提示层永远不进 `EditorDocument` / `EditorRenderer`。**
   画笔圆圈、说明文案、临时框的辅助线都只是屏幕上的东西。项目硬规则是「预览与导出共用同一份 `EditorDocument` + 同一套绘制代码」，
   `showOutline` 已经把辅助线隔离在预览侧 —— 新加的圆圈和文案**只能挂在 ViewerWindow 的独立层**，否则会被烧进成品。
3. **保存规则不动**：编辑器只写副本、原图永不改；副本命名 `Core/Editing/EditedFileNaming.cs`；保存后 `InvalidateThumbnailAsync` 刷新缩略图。本改造一行都不碰。

## 3. 改造项 A：分类轨变成「即点即用」

在 `ShowCategory(tag)` 里把 `switch` 补全，右侧面板照旧切换，只是顺带把工具一起拉起来：

| 分类 Tag | 点下去立刻发生 | 实现 |
|---|---|---|
| `basic` 基础 | 只换面板；退出所有活动模式 | 现状 + 补退出 Crop |
| `crop` 裁剪 | **立刻出现裁剪框**（8 手柄 + 确认/取消条），`ToolHint` 提示怎么拖 | 调 `EnterCropMode()` |
| `color` 调色 | 只换面板；退出所有活动模式 | 现状 + 补退出 Crop |
| `text` 文字 | **立刻出现一个可输入的文本框**（旁边 `TextPanel` 改字号/颜色/角度） | 调 `AddTextItem(isWatermark:false)`，需先按 §4 改造 |
| `draw` 标注 | 立刻进涂鸦 | 已有 `EnterInkMode()` |
| `privacy` 打码 | 立刻进打码 + 显示画笔圆圈 + 显示 §6 说明 | 已有 `EnterPrivacyMode()` + 新增 C/D |
| `mark` 水印 | 只换面板（水印要选图/输文字，**不自动放一个空水印**） | 现状 + 补退出 Crop |

- `default` 分支补 `if (_mode == ToolMode.Crop) ExitCropMode();`，修掉裁剪框残留。
- 面板里原来的 `CropTool` / `TextTool` / `MosaicTool` 按钮**保留**（作为「退出/重新进入」的开关），但不再是必经步骤；`EnterXxxMode` 里的 `EndOtherModes(except:)` 继续负责互斥。
- 「旁边改数值」已经是现成的：裁剪比例、文字角度字号、画笔大小/颗粒都在右侧面板，不用重写。

## 4. 改造项 B：切工具时的状态清理（最容易出事的一段）

`ShowCategory` 切分类前，按下面这张表清一遍；顺序建议「先清旧工具，再进新工具」：

| 旧状态 | 切走时怎么处理 | 现状 |
|---|---|---|
| 裁剪框未确认 | `ExitCropMode()`：隐藏框、不写 doc、不进撤销栈 | 已有，但没被切分类调用 |
| 自动弹出的文本框，用户一个字没改 | **整项移除 + 撤销栈里那条「添加文字」一并撤掉** | ❌ 现状会留下 `"输入文字"` |
| 打码/涂鸦草稿未抬笔 | `CancelPrivacyDraft()` → `_doc.CancelEdit()` | 已有 |
| 已确认裁剪 / 已输入文字 / 已提交马赛克等 | **保留** | 已有 |

**文字占位符怎么修（推荐做法）**：自动进入文字工具时，**不要立刻写 `EditorDocument`**。
改成先在 ViewerWindow 里建一个「待提交的文本框」（空 `Text` + 占位提示文字，提示层不进 doc），等用户真的敲了第一个字再 `Mutate` 落库；
用户直接切走就把这个待提交框丢掉 —— 文档和撤销栈都不留痕。
备选做法（改动小但更绕）：仍走 `AddTextItem`，但记一个 `autoCreated && !_textDirty` 标记，切走时移除该项并回退 `History` 那条记录。**不如前者干净**。

**顺带修缩放副作用**：`EnterCropMode()` 现在会 `ApplyZoom(1.0)` 且重置平移，`ExitCropMode()` 不恢复。
即点即用之后用户会更频繁地「点进去看一眼又切走」，每次都把视图缩放重置会很烦。
建议进入时记下 `_viewModel.Zoom` 与 `PanTranslate.X/Y`，退出时恢复（已确认裁剪的除外 —— 那时画布尺寸变了，按现有逻辑走）。

## 5. 改造项 C：打码画笔光标（带明显边框的圆圈）

- 新增独立层，例如 `BrushCursorLayer`（`Canvas`），放在 `MosaicLayer` 之上、`IsHitTestVisible = false`、`Panel.SetZIndex` 高于所有叠加层。
  ⚠ 复用 `MosaicLayer` 会挡住鼠标事件（`ShowCategory` 注释里踩过「注水层一直挡着鼠标」的坑）。
- 内容：一个 `Ellipse`，**双色描边**保证深浅底图都看得见 —— 外圈 `OutlineDark`（约 2px）+ 内圈 `OutlineLight`（约 1px），或用 `PrimaryBrush` 实色 + 白色内描边；中间透明，`IsHitTestVisible = false`。
- 直径 = `_privacyWidthDisplay × AbsoluteScale`（画布坐标 → 屏幕 DIP）。**缩放、拖「画笔大小」滑块时实时重算**，否则放大后圆圈和实际笔刷对不上。
- 显隐：仅当 `_mode == ToolMode.Privacy` 且 `PrivacyToolCombo` 选「画笔涂抹」时，鼠标在画布上才显示；鼠标离开画布、切分类、按 Esc、切到「拖一个矩形」模式时立刻隐藏。矩形模式可换成虚线十字/矩形预览。
- 落笔后已有 `_draftHint` 轨迹草稿，两者别叠成一团：可让圆圈在按下鼠标时降透明度或隐藏，抬笔后恢复。
- 给圆圈补 `AutomationProperties.Name`（如「画笔大小提示圈」），实机脚本要按名字找它。
- **不得**出现在 `EditorDocument.cs` / `EditorRenderer.cs` / 导出产物的任何路径里。

## 6. 改造项 D：保护覆盖说明（只在打码界面可见）

- 位置：`ViewerWindow.xaml` 的 `PrivacyPanel`（`StackPanel`，`:804` 起）里加一行小字 `TextBlock`：
  `TextWrapping="Wrap"`、`Foreground="{DynamicResource TextSecondaryBrush}"`、用现有 `FontSize.Small`。
- 建议文案（开头别绕弯）：
  > 「打码边缘的淡色描边是为了让你在纯色背景上也能看清涂过哪里，**不是马赛克没生效**。保存、导出或切到别的页面后，成品里不会有这圈描边。」
- 「只在马赛克界面显示」靠现有面板互斥天然成立（`PrivacyPanel` 只在 `CatPrivacy` 选中时 `Visibility=Visible`）；查看器、导出图、其他分类都看不到。
- 可选增强：第一次进打码时在画布角落弹一个可关闭的小气泡（`AppSetting` 记「只提示一次」），别每次都弹，也别塞进顶部 `ToolHint`（单行会被工具提示覆盖）。
- 可选：给 `PrivacyPanel` 加一个「预览时显示辅助描边」开关（默认开），让用户能自己关掉看真实效果。**导出侧 `showOutline` 默认值不许动**，成品永远不带。
- 守卫钉死：`EditorRenderer.cs` 调 `BuildMosaicRect` / `BuildPrivacyLayer` 时不得传 `showOutline: true`。

## 7. 验收（新增 `build/verify/verify_editor_tool_modes.py`）

`build/verify/run_all_regress.py` 是「目录即清单」，新脚本落地即被收录，不用改它。
实机段沿用公共约定：`uiawin.force_foreground`（别直接 `SetForegroundWindow`）、先 `SetProcessDpiAwareness(2)`、
库文件备份 + `try/finally` 还原、进程 `try/finally` 关干净、`collect` 深度 ≥18。
解释器用 `C:/Users/upmen/.workbuddy/binaries/python/envs/default/Scripts/python.exe`（bash 里的 `python` 没装 `uiautomation`）。

**静态段**（约 8 项）
1. 7 个分类 `Tag` 齐全，且 `ShowCategory` 的 `switch` 覆盖全部 7 个。
2. `crop` 分支调 `EnterCropMode()`、`text` 分支调 `AddTextItem(...)`。
3. `ShowCategory` 的 `default` 分支含 `ExitCropMode()`（裁剪框残留守卫）。
4. 画笔圆圈相关类型/名字**不出现**在 `EditorDocument.cs` 与 `EditorRenderer.cs`。
5. `EditorRenderer.cs` 里 `BuildMosaicRect` / `BuildPrivacyLayer` 调用不传 `showOutline: true`。
6. §6 说明文案位于 `PrivacyPanel` 的 XAML 块内（不在 `ToolHint`、不在其他面板）。
7. 自动文字工具不再 `Mutate` 写入 `"输入文字"` 占位串（或存在明确的「未改动即丢弃」路径）。
8. `Enter/ExitCropMode` 成对保存/恢复缩放与平移。

**UIA 实机段**（约 8 项）
1. 点「裁剪」→ 画布上立刻出现裁剪框（补 `AutomationProperties.Name`），无需再点面板按钮。
2. 点「文字」→ 立刻出现可输入文本框，且 `EditorDocument` 里还没有文字项。
3. 一个字不打字切到「打码」→ 文本框消失、文档里没有 `"输入文字"`、撤销栈不新增记录。
4. 裁剪切到「基础」→ 裁剪框消失，且缩放/平移恢复到进入前。
5. 点「打码」→ 画笔圆圈出现；拖「画笔大小」滑块 → 圆圈直径跟着变；切到「矩形打码」→ 圆圈消失。
6. 画一笔马赛克 → 切到「文字」→ 再切回「打码」→ 那笔马赛克**还在**（红线 1）。
7. §6 说明只在打码面板可见；切到「基础」「文字」后按名字找不到它。
8. 按住 `\` 看原图 / 保存副本后，辅助线不出现在画布与产物里。

**逐像素导出比对**：确认导出的马赛克区域**不含**双色淡描边（`showOutline` 仍为 false）。

> ✅ 已落实（2026-11-25）：工具已从 `artifacts/overlaycheck/` 挪进 `build/overlaycheck/` 并入库
> （`artifacts/` 被忽略，原位置 Git 里根本看不到）；同时按上面第一种路径补了
> 「`showOutline: true` vs 不传」的 PNG 逐像素断言，并真的调用 `EditorRenderer.Render(doc)` 走导出路径。
> 守卫脚本现在每轮**实跑**该工具（工具段 4 项）：预览描边 vs 无描边 = 0.705（> 0），
> 导出 vs 无描边 = 0.003（≈ 0）。
>
> 口径说明：这是**渲染器口径**的像素比对，不是「点保存副本、拿产物文件逐像素比」的端到端；
> §7 UIA 8 里的「保存副本产物」目前仍只有静态兜底（静态段第 6 项：导出不传 `showOutline:true`），
> 别把它理解成已经点过保存副本。
> 其余 UIA 第 5 项后半、第 6 项、第 8 项已在 §11 补齐，见下方 54/54。

## 8. 明确不做

- 不抽控件、不重构 `ViewerWindow.xaml.cs` 的 4430 行；只在 `ShowCategory` / 三个 `Enter/ExitXxxMode` / `AddTextItem` 附近做小改。
- 不改 `EditorDocument` 数据模型、不改归一化坐标规则（`Core/Editing/OverlayGeometry`）、不改 `MosaicCache` / `BlurCache` 的键（底图实例，别换成路径弱键）。
- 不改预览/导出共用同一份状态与绘制代码的架构，不改导出 `showOutline` 默认值。
- 不碰删除策略、网格/列表切换、播放器；`verify_grid_selection` / `verify_video_player` 守的东西本版一律不动。

## 9. 分步建议（可拆两版）

1. **第一步（低风险，先发）**：§4 的裁剪框残留 + 文字占位符陷阱 + 缩放恢复，加 §6 说明。都是「修既有别扭」，不引入新交互。
2. **第二步**：§3 即点即用 + §5 画笔光标，配 §7 守卫。
3. 发版按既有约定：`dotnet test` + 全量回归 + ISCC 出包 + GitHub Release/tag + 官网/README/更新说明同步；
   更新说明**开头先写一段概括**（提示窗扒的就是这段）。

## 10. 待用户确认的一处理解

用户原话「再点其他的编辑功能原来没保存的编辑就消失」。本方案按「只丢没动手的临时状态」实现（§2 红线 1）。
若实际想要的是「每次切换工具都清空该工具本次会话的全部编辑」，需要重新定义与撤销栈的关系 —— 不建议，会破坏连续打码的体验。

## 11. 复核后补充清单（2026-11-24）

> 面向实现者。首轮实现 31/31（静态 9 + UIA 22）；本节欠账补齐后为 **54/54**
> （静态 13 + 工具段 4 + UIA 37），本节是复核后追加的欠账，
> **不改渲染、不改导出、不改数据模型**。`build/verify/` 是「目录即清单」，
> 断言加进 `verify_editor_tool_modes.py` 即可，不用动 `run_all_regress.py`。
> **版本：本版就是 v1.4.10**，编辑器改动并入 1.4.10，不另开 1.4.11；
> 守卫脚本 docstring 已统一为 v1.4.10。

### 补充 1-A（真 bug，优先修）：按 `\` 看原图时画笔圈没被隐藏

`ViewerWindow.xaml.cs:979-1008` 的 `BeginCompare()` / `EndCompare()` 只折叠 / 恢复
Mosaic、Watermark、Shape、Text、Ink、Tile 六层，**没有 `BrushCursorLayer`**（ZIndex 80，最高层）；
`OnImageMouseMove` 的打码分支也不看 `_comparing`。结果：打码时按住 `\` 看原图，
画笔圈仍浮在原图上，鼠标一动还会被 `UpdateBrushCursor()` 重画 —— 原图对比不干净。

修法（两处都要，只做一处挡不住后续 MouseMove）：
- `BeginCompare()` 里加 `HideBrushCursor()`；
- `UpdateBrushCursor()` 开头加「`_comparing` 时先 `HideBrushCursor()` 再 return」。
  `EndCompare()` 不用恢复，下次移动鼠标自然会重新出现。

顺带核对（既有问题，本轮放大了概率）：`CropLayer`（ZIndex 70）也不在这两份列表里。
`case "crop"` 现在一点就进裁剪，按住 `\` 时裁剪框同样会浮在原图上。
若 §7 UIA 8 要求「看原图时画布上没有任何辅助线」，这里也要一并折叠 / 恢复。

### 补充 1-B：把 §7 里漏做的四组实机断言补齐

现状（已补）：U6–U9 已落地 —— 覆盖层计数、切矩形工具时圆圈消失、按 `\` 隐藏辅助线、
导出无描边由工具段实跑像素比对。代码上 `_doc` 没被清空，红线 1 成立，且已有实机证据。
下面保留的是当初的补法说明，供以后改断言时参考。复用现有 helper
（`select` / `hover` / `collect` / `probe_named` / `undo_button` / `force_foreground`），
建议插在 U4 之后、U5 之前（U6 会动撤销栈，注意与 U3 / U5 的顺序）。

**U6 已画好的马赛克切工具后还在（红线 1，§7 UIA 6）**
- 前置：给**预览侧**的打码覆盖层元素加 UIA 名字。只在 `ViewerWindow.RebuildMosaics()`
  （`:549-574`）里、元素生成之后 `AutomationProperties.SetName(element, "打码覆盖层 N")`；
  **不要改 `EditorOverlayFactory`** —— 它同时被 `EditorRenderer` 导出调用，红线 2 要求
  预览 / 导出共用同一套绘制代码，别把导出路径牵进来（附加属性虽不进 PNG，口径要干净）。
- 步骤：选「打码」→ 在画面上按住拖一笔（`SetCursorPos` + `mouse_event(2/4)`，
  每步 `sleep(0.05)`；`MosaicLayer.IsHitTestVisible=False`，事件从 `ImageBox` 冒泡，
  `PrivacyToolCombo` 默认就是「画笔涂抹」）→ 断言覆盖层计数 ≥1 → 切「文字」→
  切回「打码」→ 断言**计数不变**，且撤销按钮仍可用。
- 不想加 UIA 名字时的备选：只断言两次切换后撤销按钮仍可用、按一次撤销后覆盖层消失。间接但比没有强。
- 收尾把这一笔撤销掉，别把测试笔迹留在查看器里（库有 finally 还原，但 doc 是内存态）。

**U7 切「拖一个矩形」圆圈消失（§7 UIA 5 后半）**
- `PrivacyToolCombo` 已有 `AutomationProperties.Name="打码工具"`。找 ListItem 名字
  「拖一个矩形」，用 `GetSelectionItemPattern().Select()`；收起状态扫不到就先
  `GetExpandCollapsePattern().Expand()`，选完 `Collapse()`。
- `hover(img)` 后断言 `probe_named(viewer, "画笔大小提示圈") is None or offscreen(...)`；
  选回「画笔涂抹」再断言圆圈回来。

**U8 保存副本后成品不带描边（§7 UIA 8 后半）**
- 点「💾 保存副本」（`SaveButton`，XAML `:254`），等 `SaveResultBar` 出现、
  `SaveResultName` 有文件名，按 `Core/Editing/EditedFileNaming.cs` 的规则在图片同目录找新文件。
- Pillow 打开，断言笔迹边缘没有 `OutlineDark`(#30000000) / `OutlineLight`(#48FFFFFF)
  那圈半透明描边；更稳的是与「同参数、不传 `showOutline` 的渲染」逐像素一致。
- 用 `try/finally` 清掉这个临时产物，别往用户图片目录留垃圾。

**U9 按住 `\` 时辅助线不在画布上（§7 UIA 8 前半）**
- 必须 KeyDown / KeyUp 成对（`BeginCompare` 绑 KeyDown、`EndCompare` 绑 KeyUp，
  别只发按下不松手；用 `keybd_event` 比 `SendKeys` 稳），断言「画笔大小提示圈」
  与「裁剪框」两个探针都不可见，松开后恢复。

**公共约定别忘**：`force_foreground`（别直接 `SetForegroundWindow`）、
`SetProcessDpiAwareness(2)`、库备份 + `try/finally` 还原、进程 `try/finally` 关干净、
`collect` 深度 ≥18；解释器用
`C:/Users/upmen/.workbuddy/binaries/python/envs/default/Scripts/python.exe`
（bash 里的 `python` 没装 `uiautomation`，用错只会得到静态段假绿）。

### 补充 2：敲字后的两条撤销合并成一条

**现状（精确）**：`CreateTextEditor` 的 `TextChanged` 先 `CommitPendingText()` →
`_doc.Mutate("添加文字", Texts.Add(item))`（before = 无 item，after = **空文本** item），
并把 `_textEditSnapshot` 挪到「添加之后」；用户输入后 `_textDirty = true`；
`EndTextEditing()` 再 `History.Push(SnapshotCommand(..., "编辑文字"))`。
撤销栈两条，按一次 Ctrl+Z 回到「有 item 但 Text 为空」——画面上什么都没了，
文档里却留个看不见的空框，得按两次才真删干净。与 `EndTextEditing` 注释
「一次编辑会话 = 一条可撤销记录」不符。

**推荐改法：把「添加」延到编辑结束，一次性 `Mutate`**
1. 删掉 `CommitPendingText()` 及其在 `TextChanged` 里的调用；`TextChanged` 只留
   `_activeText.Text = box.Text; _textDirty = true; MeasureText(...)`。
2. 挂起期间 `_activeText` 不在 `_doc.Texts` 里：`RebuildTexts()` 现有的待提交分支
   （`:723-728`）会一直画这个框，`foreach (_doc.Texts)` 不会重复创建，**不用改**；
   `EnsureSelectionsValid()` 的 `!_textPending` 保护也继续留着。
3. `EndTextEditing()` 开头取 `bool wasPending = _textPending`，分三条路径：
   - `wasPending && !OverlayTextRules.HasContent(item.Text)` → 直接丢弃（现状），
     不落库、不 Push，也**跳过**后面那句 `Texts.Remove` 与 `History.Push`。
   - `wasPending && HasContent` → `_doc.Mutate("添加文字", () => _doc.Texts.Add(item))`。
     **此时 `item.Text` 已是最终内容**，`Mutate` 的 after 快照含完整文字，
     一条撤销直接回到「无文字」。同样跳过 `History.Push("编辑文字")`。
   - `!wasPending`（点已有文字项编辑）→ 原样保留 `_textEditSnapshot` + Push 路径。
4. 收尾清理（`_textEditSnapshot = null`、`_textDirty = false`、`_activeText / _activeTextBox`
   置空、`TextProps.Visibility`、`SyncTextAngleFromDocument()`）**两条路径都要走到**，
   别在挂起分支提前 return 漏掉。

**不要选「`BeginEdit()` 不 `Commit`、延到 End 再 `Commit`」**：`EditorDocument._pending`
是单例字段，编辑期间任何别的 `Mutate`（改颜色、旋转、裁剪、打码）都会覆盖快照，
撤销栈会错乱。上面这种做法编辑期间 doc 完全不动，最干净。

**必须一起核对的入口**（挂起时 doc 里没有 item，所有读 doc 的地方都要先 `EndTextEditing()`）：
`Undo()` / `Redo()` / `OnSaveClick` / `OnSaveAsClick` / `BeginCompare()` /
Esc 键 / 切换图片。现有代码在 `:958, 967, 981, 1028, 1159` 等处已调用，逐个确认没漏。

**验收**（写进 `verify_editor_tool_modes.py`）：
- 静态段加一条：`TextChanged` 路径里不再有 `CommitPendingText` / `Texts.Add`，
  挂起会话的添加走 `Mutate("添加文字")`。
- UIA 段在 U5 之后加：敲「验证文字」→ 切走 → **按一次撤销** → 断言画面上
  找不到那段文字对应的文字层元素、撤销按钮变灰。**当前实现下这一步会红**
  （要按两次），正好当回归守卫。
- 别破坏 U3（没敲字不产生撤销记录）与 U5（敲了字留下可撤销的添加）。

### 顺带两笔小账（同一版里做掉）

- `BrushCursorProbe` 每次 `MouseMove` 都被 `SetName` 改成「画笔大小提示圈 40 / 80 …」，
  屏幕阅读器会持续播报。直径改放一个 `_brushCursorDiameter` 变量，探针名字固定；
  **若改了名字格式，U4 的 `probe_size()` 要同步改**，否则 54 项会假红。
- 守卫脚本 docstring 与本文档标题的版本口径统一为 **v1.4.10**。

### 完成标准

`verify_editor_tool_modes.py` 静态段 13 项、工具段 4 项（实跑 overlaycheck 比像素）、
UIA 段 37 项，共 **54/54** 全绿；
`verify_design_tokens` / `verify_toolbar_anchors` 不受影响。
按约定**日常不跑全量回归**，等喊「打包 v1.4.10」时再跑 `run_all_regress.py`。
