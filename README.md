# MediaManager — 本地图片/视频媒体管理工具

> 一款免费、开源、纯本地的 Windows 媒体管理工具：帮你集中浏览、整理、编辑电脑与移动硬盘里的照片和视频，数据始终掌握在自己手里。

基于 **C# .NET 8 + WPF + MVVM** 的 Windows 10/11 本地媒体管理工具，用于管理图片和视频。

## 下载安装

- 前往 **[Releases](../../releases)** 页面，下载最新版的 `MediaManager-Setup-x.x.x.exe` 安装包（用户级安装，无需管理员权限）。
- 双击安装后启动，在「设置 → 数据备份」或主界面添加你的媒体根文件夹，程序会在后台自动扫描并建立缩略图。
- 想从源码构建？见下方「构建」一节（需自行准备 FFmpeg，见文末说明）。

## 功能特性

- ✅ 添加/移除媒体根文件夹，后台递归扫描（500 条/批事务，进度显示，可取消）
- ✅ 缩略图网格（虚拟化 VirtualizingWrapPanel，可调大小，多选）
- ✅ 图片查看器（缩放、拖拽、适应窗口、全屏、翻页、EXIF 展示）
- ✅ 视频播放（LibVLCSharp，播放/暂停/进度/音量/全屏/倍速）
- ✅ 视频缩略图（FFmpeg 抽帧，ffprobe 读元数据）
- ✅ 删除到回收站（Shift+Delete 永久删除）
- ✅ 收藏、标签（多对多，颜色）、分类（树形，多对多）
- ✅ 未分类（收件箱）视图
- ✅ 搜索（文件名 + 路径 + 标签 + 分类，SQLite FTS5）
- ✅ 筛选、排序、缩略图缓存清理
- ✅ 便携模式（portable.marker）与安装模式
- ✅ 卷序列号恢复（移动硬盘盘符变化后自动重建路径）
- ✅ FileSystemWatcher 监控（防抖 800ms，增量刷新）
- ✅ 图片基础编辑（旋转、裁剪、缩放、格式转换、亮度对比度）

## 技术栈

| 组件 | 用途 |
|------|------|
| .NET 8 + WPF | 应用框架 |
| CommunityToolkit.Mvvm | MVVM |
| Microsoft.Extensions.Hosting / DI | 依赖注入 |
| Microsoft.Data.Sqlite + Dapper | 数据库（不用 EF Core） |
| SixLabors.ImageSharp | 图片缩略图与编辑 |
| MetadataExtractor | EXIF 读取 |
| LibVLCSharp.WPF | 视频播放 |
| FFmpeg | 视频缩略图 |
| Serilog | 文件日志 |
| VirtualizingWrapPanel | 缩略图虚拟化 |
| Inno Setup | 打包 |

## 项目结构

```
MediaManager.sln
├─ src/MediaManager.App/              WPF UI
├─ src/MediaManager.Core/             领域模型、接口
├─ src/MediaManager.Infrastructure/   SQLite、文件系统、缩略图、媒体、便携模式
├─ tests/MediaManager.Tests/          单元测试
├─ tools/ffmpeg/                      ffmpeg.exe（需自行下载放入，见文末；不进仓库）
└─ build/installer.iss                Inno Setup 脚本
```

## 构建

```bash
# 还原 + 编译
dotnet build MediaManager.sln

# 运行测试
dotnet test MediaManager.sln

# 发布（安装包用）
dotnet publish src/MediaManager.App -c Release -r win-x64 --self-contained false
```

> 注意：需要 .NET 8 SDK。若 `dotnet` 不在 PATH，请用完整路径。

## 便携模式

1. 发布后，在程序根目录放置一个空文件 `portable.marker`
2. 数据库、缩略图、日志都会放在 `程序目录\Data\` 下
3. 整个目录可拷贝到移动硬盘，插到任意电脑直接运行，数据跟随

安装模式（无 portable.marker）数据默认存 `%LocalAppData%\MediaManager\`。

## 数据库位置

- 便携模式：`<程序目录>\Data\library.db`
- 安装模式：`%LocalAppData%\MediaManager\library.db`
- 设置里可自定义数据目录（如移动硬盘）

## 快捷键

| 按键 | 功能 |
|------|------|
| Ctrl+A | 全选 |
| Ctrl+Click | 多选 |
| Shift+Click | 范围选择 |
| Delete | 删除到回收站 |
| Shift+Delete | 永久删除 |
| Space | 快速预览 |
| Enter | 打开查看器 |
| F | 收藏 |
| Ctrl+F | 搜索 |
| F2 | 重命名 |
| 1~5 | 打星 |
| Esc | 退出查看器 |

## FFmpeg 说明

视频缩略图需要 FFmpeg。`tools/ffmpeg/` 目录**不纳入 Git 仓库**（文件约 100MB），
请自行下载后把 `ffmpeg.exe` 放入该目录，或在软件「设置」里手动指定 FFmpeg 路径。

- 下载地址：https://www.gyan.dev/ffmpeg/builds/ （取 `ffmpeg-release-essentials` 里的 `bin/ffmpeg.exe` 即可）
- 普通用户无需此步骤——官方安装包已自带 FFmpeg。仅从源码构建/发布时才需要。
