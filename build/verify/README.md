# 实机验收脚本（UIA）

这些脚本用 Windows UI Automation 真的去点真的窗口，验证"单元测试碰不到的那 68%"——
WPF 视图、窗口状态机、弹层、主题渲染。

放在仓库里的目的：**让"实机 N/N 通过"这句话可以被别人复现**，而不是只有出图机器上能跑。

## 前置条件

```bash
# 1. 先编出一份 Debug 版（脚本默认跑 src/MediaManager.App/bin/Debug/net8.0-windows）
export PATH="/c/Users/<你>/.dotnet:$PATH"
dotnet build MediaManager.sln

# 2. 依赖（Python 3.8+）
pip install uiautomation pillow

# 3. 关闭正在运行的媒体管理器 —— 脚本会 taskkill，但先手动关能避免误杀
```

脚本用 `__file__` 自己推导仓库根目录，**没有写死路径**，clone 到任何位置都能跑。
仓库根目录下每个脚本都按 `python build/verify/<脚本>.py` 调用。

## ⚠ 会动你的开发库

脚本操作的是**本机开发库** `src/MediaManager.App/bin/Debug/net8.0-windows/Data/library.db`
（便携模式），会真的写数据、改设置、设密码：

- `verify_diary_calendar.py` 会通过界面保存一篇日记
- `verify_diary_attachment.py` 会直接往库里插 1 篇日记 + 3 个媒体（标题「自动化验证：点开附件」、
  文件名 `diary_shot_*.png`），跑完**连同临时图片一起删掉**，脚本中途崩了下次运行也会先清干净
- `verify_vault_password.py` 会设密码 → 改密码 → 取消密码，结束时回到"无密码"
- `verify_light_theme.py` 会切主题，跑完切回去
- `verify_first_run.py` **会把整个库临时挪走**（模拟全新安装），跑完再原样放回；
  中途崩了也会在 `finally` 里恢复，实在不行备份在 `Data/_verify_first_run_bak/`
- `verify_plugin_breaker.py` 会往 `Data/plugins/faulty/` 丢一个「必炸插件」
  （编译产物在 `build/TestPlugins/FaultyPlugin/`），跑完删掉该目录

**跑之前建议先备份 `library.db`**。截图落在同级 `shots_*/`，已 gitignore。

## 脚本清单

| 脚本 | 验什么 | 判据 | 上次结果 |
|---|---|---|---|
| `verify_external_open.py` | 非核心类型外放：右键菜单有「用系统默认程序打开」、双击 txt 起的是记事本而不是内置窗、图片仍然出内置查看器 | 进程列表出现外部程序 + 无内置预览窗口 | 4/4 |
| `verify_vault_password.py` | 密码全生命周期：未设密码直接进（不弹窗）→ 设 1234 → 关窗重开要密码 → 输错原地重试 → 改 5678 后旧密码失效 → 取消密码回"一点就进" | 每步的窗口状态 + 库里无残留凭据 | 20/20 |
| `verify_diary_calendar.py` | 日历弹窗在深色下不是白板、点日期能写回日期框、年视图 12 个方块、点月份能跳转 | 弹窗截图**平均亮度 < 150**（深色底约 44，白板约 240+） | 17/17 |
| `verify_light_theme.py` | 同一套模板在浅色下没被弄坏 | 日历平均亮度 **> 200**（正常浅底约 247） | 通过 |
| `verify_diary_attachment.py` | 日记附件缩略图：能看到图 → 点开内置查看器 → 打开的是**被点的那张** → 按 → 翻的仍是本篇日记的图；缩略图缺失时自己补生成 | 查看器窗口出现且标题含被点文件名；缩略图区域**像素标准差 ≥ 20**（空灰框 ≈ 0，真图 ≈ 90+） | 9/9 |
| `verify_diagnostic_info.py` | 设置页「遇到问题」：日志目录显示的是真实存在的目录、复制出来的诊断信息含版本与路径、**不含用户名**（会被贴到公开论坛） | 目录 `os.path.isdir` 为真；剪贴板文本含「版本：」「日志目录：」，不含 `%USERNAME%` | 11/11 |
| `verify_first_run.py` | 首启二选一：空库启动 → 弹选择窗（非模态，主窗口同时在）→ 选「只看图」→ 侧栏收起日记/私密/回收站/标签、保留发现/全部/收藏；重启不再问；**老库（27 条媒体）升级不弹窗**，只静默补标记且不改用户原设置 | 侧栏文本按 `IsOffscreen` 判可见性（WPF 的 Collapsed 控件仍在 UIA 树里）；设置值回查 SQLite | 30/30 |
| `verify_plugin_breaker.py` | 插件动作熔断：必炸插件的动作点 3 次 —— 前两次弹「插件出错」、第 3 次起**不再弹框**，状态栏出「已停用」，「🧩 插件」入口自动隐藏 | 对话框按 **Win32 标题轮询**（不能用 UIA 根枚举，见坑 7）；反向验证过：阈值 3→30 后三项全红 | 14/14 |
| `verify_video_player.py` | 视频播放器（LibVLC）：命令行带视频路径启动 → 窗口出现、无「无法播放此视频」面板、顶栏是文件名、总时长已加载、**时间在走**；空格暂停后时间停住、暂停时按→步进 5 秒、再按空格恢复；关播放器主窗口还在 | 全程只读 UIA 时间文本（`mm:ss`）+ 键盘驱动，**不碰视频区截图**（HwndHost 截出来是黑的）；视频画面本身仍靠人眼 | 10/10 |
| `verify_memory.py` | 内存实测（可复跑）：空载 / 打开查看器 / 关掉查看器三个场景各采样 10 次左右，写 `mem_result.json` | psapi `GetProcessMemoryInfo`，不用第三方库；**只记数字不下结论** | 空载 218MB / 开查看器 303MB / 关后 301MB（Debug，27 条库；两轮复跑都在 ±5MB 内） |

## 脚本自身的九个坑（都真踩过，别再踩）

1. **数据库路径要跟程序同一套判断。** exe 旁边有 `portable.marker` 时数据在 `Data/library.db`，
   否则在 `%LOCALAPPDATA%\MediaManager\library.db`。第一版 `verify_diary_attachment.py` 按
   后者播种，程序读的是前者 —— 界面显示「这一天还没有回忆」，看着像功能坏了。
2. **敲键盘前必须先把窗口顶到前台。** `SendKeys` 打到别的窗口上是没有任何报错的，
   表现是「密码框还是空的 + 提示密码至少 4 位」。`verify_vault_password.py` 因此红过一次，
   现在每次输入前都 `SetForegroundWindow`，没成功还会重试一轮。
3. **UIA 树的遍历深度要给够。** 日记附件是
   `ItemsControl → ContentPresenter → Border → StackPanel → ItemsControl → … → Image`，
   深度 14 走不到底，`ImageControl` 一个都数不到；改成 24 才找得到。
4. **读 PowerShell 的输出前先设 `[Console]::OutputEncoding=UTF8`。** 不然中文按 GBK 出来是乱码，
   `"版本：" in text` 这种断言会莫名其妙变红（`verify_diagnostic_info.py` 第一次跑就栽在这）。
5. **WPF 里 `Visibility=Collapsed` 的控件仍然在 UIA 树里**，只是 `IsOffscreen=True`，
   而 `BoundingRectangle` 还留着上一次布局的坐标 —— 看起来像"它明明在那儿"。
   `verify_first_run.py` 第一版没加 `IsOffscreen` 过滤，六项"侧栏已收起"全判成失败，
   可截图里侧栏明明只剩四项。判"藏没藏"必须看 `IsOffscreen`，不能看"找不找得到"。
6. **UIA 读 `Name` 会抛 COMError（-2147220991）。** 窗口正在重绘/销毁的瞬间元素句柄失效，
   这不是被测程序的问题，是 UIA 自己的时序。统一包一层 `name_of()` 吞掉，
   否则脚本会随机崩在"找窗口"这一步，看起来像程序根本没启动。
7. **脚本在模态对话框存活期做 UIA 根枚举，对话框约 2 秒就会自己消失**——
   `verify_plugin_breaker.py` 的「插件出错」框，只要脚本在等它的时候跑
   `GetRootControl/GetChildren` 轮询，`ShowDialog` 就提前返回（截屏也证明它真没了）；
   停止轮询后同一个框能一直开着。机制没查明，但解法有效且彻底：
   **等模态框用 Win32 `EnumWindows` 按标题找 HWND**，找到后 `SetForegroundWindow` +
   发 Enter（「确定」是 IsDefault 按钮），全程不碰 UIA。
8. **WPF ContextMenu 不生成独立的 `#32768` 菜单窗口**，菜单项就挂在所属窗口自己的
   UIA 树里（找「一定失败」要在主窗口里遍历，不是找顶层窗口）。另外
   `ControlFromHandle` 拿到对话框后，其 `Button` 的 Name 读不到（Content 是字符串
   也一样）—— 别依赖读对话框内部，直接发键盘。
9. **双击必须发原生鼠标事件，UIA 的 `Click` 连点两次不算双击。**
   `verify_memory.py` 第一版用 `item.Click(simulateMove=False)` 点两次想触发「双击打开」，
   查看器纹丝不动（连选中都不稳定）；改成 `SetCursorPos` + `mouse_event` 的
   down/up/down/up 一口气就开了。另外网格项的 UIA Name 以前是类名
   （`ThumbnailItemViewModel` 没写 `ToString`），定位只能靠坐标 —— 已补 `ToString`。

## 为什么用"平均亮度"当判据（深色 / 浅色）

深色/浅色渲染有没有生效，肉眼能看但机器难判。整块截图取平均亮度是个便宜又足够灵敏的代理指标：

- 深色底 + 白字 ≈ 40–60
- 浅色底 + 黑字 ≈ 240–250
- **白板故障 ≈ 240+**（配浅灰字，人眼几乎看不见）

阈值 150 落在两者中间，有足够余量。这个判据抓住过真实 bug：DatePicker 弹层第一版
改完"外面是深色、弹出来还是白的"，就是靠它被发现的。

## 为什么缩略图用"像素标准差"而不是平均亮度

"这一格到底画出了图，还是一块空灰框"没法用亮度判：深色主题下，空框是深灰（≈42），
一张深色界面截图也可能平均只有 60–80，两者混在一起分不开。
空框是一整块**纯色**（标准差 ≈ 0），真图总有明暗层次（实测 95）—— 所以判 `std ≥ 20`。

## 已知局限（别把 N/N 当全覆盖）

- 只覆盖**被写进脚本的路径**。脚本没写的交互等于没验。
- **视频播放器只覆盖 UI/键盘路径**（`verify_video_player.py`）：窗口、控制栏、暂停/步进
  这些 UIA 读得到的部分有守了，但 LibVLC 的**视频画面本身**（解码、渲染、黑屏类问题）
  截图截不到、UIA 读不到，仍然只靠人眼。改动 LibVLC 集成要自己放一遍视频。
- 内存有 `verify_memory.py` 记数字，但只在 27 条的开发库上测（合成数据没有真实缩略图，
  填不满缩略图缓存），**规模化后的内存没有数字**。SQL 层的规模化表现另看
  `build/bench_db.py`。
- 全是"点一下、看有没有"式断言，**没有性能断言**（不测耗时、不测帧率）。
- 依赖屏幕坐标和 UIA 树，**换个 DPI 缩放或系统语言可能失效**（本机按 125% DPI 写的）。
- 不属于 CI：没有无头环境能跑 WPF 真窗口，只能人工触发。
