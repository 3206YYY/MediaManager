# 插件接口（Plugin API）

> 适用版本：MediaManager v1.2.5 起。
> 一句话：**你能做的**是「在主界面加一个菜单动作，对用户当前选中的文件做事」；
> **你不能做的**是改主程序内部状态 —— 这是故意的，为了插件崩了不拖垮主程序、主程序升级不把插件全废掉。

---

## 1. 插件放在哪

```
{数据目录}\plugins\
├─ 我的插件\                 ← 推荐：一个插件一个文件夹
│   ├─ 我的插件.dll          ← 入口命名与文件夹同名最省事
│   ├─ plugin.json          ← 可选：显式指定入口、名字
│   └─ 其它依赖.dll
└─ 随手写的小插件.dll         ← 也可以直接把 dll 丢在 plugins 根目录
```

设置页 → **插件扩展** → 「打开目录」，就是这个位置。

`plugin.json`（可选，全部字段都可省）：

```json
{
  "name": "批量加水印",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "给选中的图片右下角统一加文字水印",
  "entry": "WatermarkPlugin.dll"
}
```

> 改完插件后请**重启程序**或在设置页点「重新扫描」；已加载过的 dll 不会被重复加载（不支持热卸载）。

---

## 2. 最小可用插件

```csharp
using MediaManager.Core.Plugins;

public sealed class HelloPlugin : IMediaManagerPlugin
{
    private IPluginContext? _context;

    public string Name => "示例插件";
    public string Version => "1.0.0";
    public string? Author => "你";
    public string? Description => "把选中文件的路径记到日志里";

    public void Initialize(IPluginContext context)
    {
        _context = context;

        context.AddMenuAction("打个招呼", selection =>
        {
            if (selection.IsEmpty)
            {
                context.Notify("先选中一张图片或一个视频");
                return;
            }

            context.Log($"选中了 {selection.Paths.Count} 个文件，主文件 = {selection.PrimaryPath}");
            context.Notify($"你好：{selection.PrimaryFileName}");
        }, "演示用，不修改任何文件");
    }

    public void Shutdown() { }
}
```

编译后把 dll 丢进插件目录，重启程序，主界面右上角会出现 **🧩 插件** 菜单。

---

## 3. 接口说明

### `IMediaManagerPlugin`

| 成员 | 说明 |
|---|---|
| `Name` | 显示名，设置页会列出来 |
| `Version` | 版本号字符串 |
| `Author` | 作者（可空） |
| `Description` | 一句话说明（可空） |
| `Initialize(IPluginContext)` | 加载后调用一次，在这里注册菜单 |
| `Shutdown()` | 程序退出前调用，可留空 |

需要一个**公开的无参构造函数**。

### `IPluginContext`

| 成员 | 说明 |
|---|---|
| `DataDirectory` | 程序数据目录，可读写，适合存插件自己的配置 |
| `PluginDirectory` | 本插件所在目录 |
| `AppVersion` | 主程序版本，例如 `1.2.5` |
| `AddMenuAction(title, handler, description?)` | 在主界面「🧩 插件」菜单里加一项 |
| `Log(message)` | 写进主程序日志（自动带插件名前缀） |
| `Notify(message)` | 主窗口状态栏显示一句话 |

### `PluginSelection`

| 成员 | 说明 |
|---|---|
| `Paths` | 当前选中的所有文件完整路径（已去重） |
| `PrimaryPath` | 主文件路径，通常是刚点开的那一个 |
| `PrimaryFileName` | 主文件名 |
| `IsEmpty` | 有没有选中东西 |

`handler` 在主线程调用；**抛异常会被捕获**并弹一个「插件出错」提示，主程序不会崩。
但请自己 try/catch 处理「文件被占用 / 磁盘满」这类可预期错误，给用户更清楚的提示。

---

## 4. 编译要点

- 目标框架必须是 **net8.0**（和主程序一致）。
- 引用 `MediaManager.Core.dll`（主程序安装目录里有），**不要**把它一起拷进插件目录。
- `MediaManager.*` 程序集由主程序统一提供；你自己的其它依赖 dll 可以放在插件目录里，会从那里解析。
- 不要引用 `MediaManager.App.dll` / `MediaManager.Infrastructure.dll`：那些是内部实现，随时会变。

---

## 5. 示例工程

见 [`docs/plugin-template`](plugin-template)。改一行 `MediaManagerCorePath` 就能编译：

```bash
cd docs/plugin-template/HelloPlugin
dotnet build -c Release -p:MediaManagerCorePath="C:\Program Files\MediaManager\MediaManager.Core.dll"
```

---

## 6. 边界（请遵守）

- 不要静默修改用户的原始文件；需要写文件时优先写新文件，并在菜单项说明里讲清楚。
- 不要在主线程做长时间同步 IO（菜单回调是 UI 线程）。
- 不要在插件里弹自己的窗口以外的全局钩子。
- 插件目录里的东西会随「卸载保留数据」保留下来，别往里面塞几十 MB 的缓存。
