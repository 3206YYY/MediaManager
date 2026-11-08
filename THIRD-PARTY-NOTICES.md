# 第三方组件与许可声明

MediaManager 本体以 **MIT** 协议发布，版权归 **3206YYY** 所有，全文见 [LICENSE](LICENSE)。

程序运行时依赖下列第三方组件。它们的许可与本项目不同，按各自条款分发；
下面写清每一处的**用途**、**分发形式**和**源码位置**，不靠一句话糊过去。

---

## 一、随安装包分发的二进制

### 1. VLC / libVLC 3.0.24 — LGPL-2.1-or-later

| 项 | 说明 |
|---|---|
| 用途 | 视频播放（音频/视频解码、字幕、硬件加速） |
| 引入方式 | NuGet `VideoLAN.LibVLC.Windows` 3.0.24 + `LibVLCSharp.WPF` 3.10.1（同为 LGPL-2.1-or-later） |
| 分发形式 | 动态链接。`libvlc.dll` / `libvlccore.dll` 与 `libvlc/win-x64/plugins` 原样分发，**未修改任何源码** |
| 许可全文 | https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html |
| 源码 | https://code.videolan.org/videolan/vlc |

**替换自由**：因为没有静态链接、也没有改动库本身，你可以用自己编译的同版本
`libvlc.dll` / `libvlccore.dll` 直接替换安装目录下的文件，程序不会因此失效。
这是 LGPL-2.1 第 6 条要求我们保证的。

### 2. FFmpeg 9.0.2 — GPL-3.0-or-later

| 项 | 说明 |
|---|---|
| 用途 | 视频与部分特殊格式的缩略图抽帧、时长探测 |
| 具体构建 | `ffmpeg version 9.0.2-essentials_build-www.gyan.dev`，编译参数含 `--enable-gpl --enable-version3`，静态链接 libx264 / libx265 等 GPL 组件 |
| 分发形式 | **独立可执行程序**：`tools/ffmpeg/ffmpeg.exe`，由主程序以子进程方式调用（传参、读输出），**不链接进 MediaManager** |
| 许可全文 | https://www.gnu.org/licenses/gpl-3.0.html |
| 源码 | https://www.gyan.dev/ffmpeg/builds/ （对应 9.0.2 的构建源码）<br>上游：https://ffmpeg.org/download.html |

**两点必须说清**：

1. 因为是通过命令行调用独立程序，而不是链接库，按自由软件基金会对 GPL 的解释，
   这属于「聚合」（mere aggregation）——**它不会让 MediaManager 本体变成 GPL**，
   本项目的 MIT 协议不受影响。
2. 但「把这个 GPL 二进制装进安装包发给别人」这个动作本身受 GPLv3 约束，需要提供
   对应源码。**如需该二进制对应的完整源代码，可在本仓库 Issues 中提出，我们会提供
   下载地址或直接发送**（此承诺自 v1.4.5 起的每个分发版本均有效）。

如果你不想要 GPL 版：本项目支持自行替换——把 `tools/ffmpeg/ffmpeg.exe` 换成任意
LGPL 构建的 ffmpeg，或在「设置 → 媒体与缩略图」里指定其它 ffmpeg 路径即可，
程序功能不受影响。

---

## 二、NuGet 依赖

以下版本取自各项目的 `.nuspec` 元数据，与 `*.csproj` 中的引用一致。

### MIT

| 组件 | 版本 | 用途 |
|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 源生成器（ObservableProperty / RelayCommand） |
| Microsoft.Data.Sqlite | 8.0.11 | SQLite 访问 |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | 依赖注入容器 |
| Microsoft.Extensions.Logging / .Abstractions | 8.0.1 | 日志抽象 |
| VirtualizingWrapPanel | 2.5.4 | 网格虚拟化容器 |

### Apache-2.0

| 组件 | 版本 | 用途 |
|---|---|---|
| Dapper | 2.1.89 | 轻量 ORM |
| MetadataExtractor | 2.9.3 | EXIF / 拍摄时间 / 相机信息 |
| Serilog / Serilog.Extensions.Logging / Serilog.Sinks.File | 4.0.2 / 8.0.0 / 6.0.0 | 本地文件日志 |

### LGPL-2.1-or-later

| 组件 | 版本 | 用途 |
|---|---|---|
| LibVLCSharp.WPF | 3.10.1 | libVLC 的 .NET 绑定与 WPF 视频控件 |
| VideoLAN.LibVLC.Windows | 3.0.24 | libVLC 运行时二进制 |

### Six Labors Split License（非 OSI 标准协议，请注意）

| 组件 | 版本 | 用途 |
|---|---|---|
| SixLabors.ImageSharp | 3.1.7 | 图片解码 / 缩略图 / 编辑器绘制 |
| SixLabors.ImageSharp.Drawing | 2.1.4 | 文字与图形绘制（打码、水印、标注） |

该协议（2023 年 3 月起替代 Apache-2.0）的规则是：

- **开源或源码可见的软件**可以继续按 Apache-2.0 条款免费使用 —— 本项目是 MIT 开源，
  属于这一档，**不需要购买任何许可**；
- 作为**传递依赖**引入、非营利组织、以及**年总营收低于 100 万美元**的营利主体，同样免费；
- 只有「闭源 + 年营收 ≥ 100 万美元」且直接引用该包时才需要商业许可。

也就是说：**只要你拿到的是一份针对本项目的源代码副本，无论个人还是公司使用、
修改、二次分发，都不会触发 ImageSharp 的付费条款**。真正会触发的是「把本项目代码
拿去做了闭源的商业产品且公司营收过线」——那种情况下付费义务在那一方，不在本项目。

协议全文：https://sixlabors.com/policies/licenses/six-labors-split-license/

---

## 三、本项目不使用的内容

为避免误解，明确列出**没有**用到的东西：

- 不含任何 GPL/AGPL 授权的**源代码**。同类开源项目（digiKam、ImageGlass、Immich、
  PhotoPrism 等）仅作为功能与交互的参考，未复制其代码。
- 不含 Adobe、Microsoft、Google 等公司的商标素材；界面图标为自绘矢量或系统字体。
- 不含任何需要联网的第三方 SDK、统计埋点或广告组件。

---

## 四、发现问题怎么办

如果你认为本项目的某个文件侵犯了你的权利，或上述声明有遗漏，
请在本仓库提交 Issue，或直接联系维护者（GitHub: [@3206YYY](https://github.com/3206YYY)），
我们会在核实后尽快处理。

*最后更新：2026-11-08*
