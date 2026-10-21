# 📸 MediaManager — 一个窗口，管完你所有的图片和视频

> 免费、开源、纯本地无广告的 Windows 媒体管理器。把电脑和移动硬盘里散落各处的照片、视频收进同一个库，数据始终在自己手里。

**[📥 下载最新版安装包](https://github.com/3206YYY/MediaManager/releases)** ｜ **[☕ 赞助开发者](#sponsor)** ｜ **[🐛 提交 Bug](https://github.com/3206YYY/MediaManager/issues)**

---

## 它最想解决的两件事

### 🗂 一个窗口，管完所有盘

C 盘、D 盘、移动硬盘里的图片视频，可以一起加进同一个库：
**一个搜索框、一次筛选、一次批量改名/导出 —— 再也不用开十几个资源管理器窗口来回对照。**

### ✨ 让照片主动来找你

文件太多，谁都没动力一个个文件夹翻。
「发现」页会从你的库里**随机推送你还没细看的照片和视频** ——
它们不再只是躺在硬盘深处的一串文件名，而是会重新跳到你眼前。

| ☀️ 浅色模式 | 🌙 深色模式 |
|:---:|:---:|
| ![主界面 · 浅色模式](docs/screenshot_light.jpg) | ![主界面 · 深色模式](docs/screenshot_dark.jpg) |

---

## 🤖 开发者自白 (About the Developer)

本项目是由本人虔诚地向圣诞老人（AI）许愿得到。本人只参与了产品创意、UI 交互设计和测试，一周做完，79 项测试全过，产品仍在快速迭代，欢迎提 Issue 指出问题。

*This project is co-developed with AI. Ideas, UI/UX design, and rigorous testing are done by a human; coding is done by AI.*

---

## ✨ 还有什么

- 🚀 **极速不卡顿**：虚拟化列表 + 后台多线程解码，实测十万张图滚动丝滑，内存占用硬顶 200MB。
- 💾 **纯本地无广告**：数据全在本地 SQLite，支持深色 / 浅色主题。拔出移动硬盘不崩溃，甚至能自动恢复盘符路径。
- ✂️ **内置轻量编辑器**：拖拽裁剪、涂鸦、加文字、马赛克、滤镜。**编辑原图自动生成新副本，编辑副本则原地更新**，原图绝不会被偷偷改掉。
- 🎬 **视频播放像样**：内置 LibVLC，倍速、全屏、快捷键；播完可选**播完暂停 / 列表循环 / 单曲循环 / 随机播放**，选一次就记住。
- 🗑️ **后悔药**：误删直接进回收站并可一键撤销；收藏、分类、标签自动备份。
- 🧩 **可扩展**：官方没做的小功能，可以用插件接口自己加（见 [插件接口](docs/plugin-api.md)）。
- 📦 **极致轻量**：安装包仅 99MB，免装 .NET 运行库，傻瓜式一键安装。

---

## 📥 下载与安装

1. 前往 **[Releases](https://github.com/3206YYY/MediaManager/releases)** 页面，下载最新版的 `MediaManager-Setup-x.x.x.exe` 安装包（用户级安装，无需管理员权限）。
2. 双击安装后启动，点击主界面的「添加文件夹」选择你的媒体目录。
3. 程序会在后台自动扫描并生成缩略图，立马就能使用！

> ✅ **安装包已内置全部依赖**：.NET 运行时、**FFmpeg**（视频缩略图）、LibVLC（视频播放）。
> 双击即用，**不需要额外安装任何东西，也不需要联网**。装完就能看视频封面、播视频、编辑图片。

> 🔄 **更新**：设置 → 关于与更新 → 「检查更新」，会直接告诉你 GitHub 上有没有新版本，并可一键跳去下载。

---

## 🧩 插件

主程序只做「大多数人都要」的功能。剩下那些小众玩法（导出特定格式、接自己的脚本、批量处理某个奇怪需求），交给插件：

- 插件放在 **`{数据目录}\plugins`**，设置页「插件扩展」里可直接打开目录、重新扫描。
- 插件只拿到「当前选中的文件」+ 一个注册菜单的接口，**碰不到主程序内部** —— 插件崩了不会拖垮软件，主程序升级也不会把插件全废掉。
- 模板：**[docs/plugin-template](docs/plugin-template)**（40 行就能跑起来）；接口说明：**[docs/plugin-api.md](docs/plugin-api.md)**。

---

## 🛠️ 技术栈与架构

本项目基于 **C# .NET 8 + WPF + MVVM** 构建。

| 组件 | 用途 |
|------|------|
| .NET 8 + WPF | 应用框架 |
| CommunityToolkit.Mvvm | MVVM |
| Microsoft.Extensions.Hosting / DI | 依赖注入 |
| Microsoft.Data.Sqlite + Dapper | 数据库（**不用 EF Core**） |
| SixLabors.ImageSharp | 图片缩略图与编辑 |
| MetadataExtractor | EXIF 读取 |
| LibVLCSharp.WPF | 视频播放 |
| FFmpeg | 视频抽帧 |
| Serilog | 文件日志 |
| VirtualizingWrapPanel | 缩略图虚拟化 |
| Inno Setup | 打包 |

**项目结构：**

```
MediaManager.sln
├─ src/MediaManager.App/               WPF UI（主窗口 / 查看器 / 视频播放器 / 设置 / 插件宿主）
├─ src/MediaManager.Core/              领域模型、接口、纯逻辑（编辑/媒体/插件契约）
├─ src/MediaManager.Infrastructure/    SQLite、文件系统、缩略图、便携模式
├─ tests/MediaManager.Tests/           单元测试（103 项）
├─ docs/plugin-template/               插件模板（可编译的最小示例）
├─ tools/ffmpeg/                       ffmpeg.exe（不进仓库；Release 安装包已内置）
└─ build/installer.iss                 Inno Setup 安装脚本
```

---

## 💻 从源码构建 (Build from Source)

> 需要 **.NET 8 SDK**。若 `dotnet` 不在 PATH，请用完整路径（例如 `C:\Users\<你>\.dotnet\dotnet.exe`）。

```bash
# 还原 + 编译
dotnet build MediaManager.sln

# 运行测试
dotnet test MediaManager.sln

# 发布（本地运行）
dotnet publish src/MediaManager.App -c Release -r win-x64 --self-contained false
```

> 🎬 **FFmpeg 说明（只与源码构建者有关）**
>
> 官方 Release 安装包**已经内置** `tools/ffmpeg/ffmpeg.exe`，普通用户不用管。
>
> 自己从源码运行的话：视频缩略图需要 FFmpeg，而 `tools/ffmpeg/` 目录**不进 Git 仓库**（单个 exe 约 100MB）。
> 请从 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 下载 `ffmpeg-release-essentials`，解压出 `bin/ffmpeg.exe`
> 放到 `tools/ffmpeg/` 下；也可以启动后在软件「设置 → 媒体与性能 → FFmpeg 路径」里手动指定。
> 没有它程序照样能跑，只是视频格子不生成封面（显示占位图标），图片功能完全不受影响。

---

## ⌨️ 常用快捷键

| 按键 | 功能 |
|------|------|
| Space / Enter | 快速预览 / 打开查看器 |
| ← / → | 查看器内切换上一张 / 下一张；视频内快退 / 快进 5 秒 |
| Delete / Shift+Delete | 删除到回收站 / 永久删除 |
| F | 收藏 |
| 1 ~ 5 | 打星（1~5 星） |
| Ctrl+F | 搜索 |
| Esc | 退出查看器 / 退出全屏 |

> 视频播放器内还支持：空格播放 / 暂停、↑ / ↓ 音量 ±5%、M 静音、F 或双击画面切全屏。
> 查看器内：鼠标移到画面左右边缘呼出翻页箭头，滚轮可缩到 15%，双击画面回到「适应窗口」。

---

<a name="sponsor"></a>

## ☕ 赞助与支持 (Support the Developer)

本软件完全免费、无广告且遵循 MIT 开源协议。如果你觉得这个软件帮你解决了痛点，或者单纯想支持一下大学生的开源梦想，欢迎请我喝杯咖啡！

👉 **[点击这里前往爱发电支持我](https://afdian.com/a/3206YYY)**

您的赞助将用于：升级维护该软件，以及作者其他软件的开发。

---

## 📄 License

本项目采用 **MIT 协议**开源。你可以自由使用、修改甚至商用，但请保留原作者信息。

Copyright © 2026 MediaManager
