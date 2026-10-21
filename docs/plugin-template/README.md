# 插件模板（HelloPlugin）

一个 40 行的最小插件，用来验证你的开发环境是不是通的。

## 1. 编译

```powershell
# 指向你本机安装目录里的 MediaManager.Core.dll
dotnet build -c Release -p:MediaManagerCorePath="C:\Program Files\MediaManager\MediaManager.Core.dll"
```

产物：`bin\Release\net8.0\HelloPlugin.dll`

## 2. 安装

1. 打开程序 → 设置 → **插件扩展** → 「📂 打开目录」（一般是 `%LocalAppData%\MediaManager\plugins`）。
2. 新建文件夹 `HelloPlugin`，把 `HelloPlugin.dll` 和 `plugin.json` 拷进去。
3. 点「🔄 重新扫描」（或重启程序）。
4. 主界面右上角出现 **🧩 插件** → 「统计选中项」。

## 3. 然后呢

- 想把功能做成什么样，看 [插件接口文档](../plugin-api.md)。
- 处理文件请用 `System.IO` 自己来 —— 插件不直接暴露主程序的数据库和缩略图管线，
  需要写文件时请遵守「不静默覆盖原文件」这条约定。
