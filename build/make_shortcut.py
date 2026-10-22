# -*- coding: utf-8 -*-
"""创建桌面快捷方式。

路径全部相对仓库根目录推导，桌面用 ~/Desktop —— 不写死本机用户名
（仓库是公开的，`C:\\Users\\<名字>` 属于隐私泄漏）。
"""
import os

import win32com.client

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
exe = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "MediaManager.App.exe")
icon = os.path.join(ROOT, "src", "MediaManager.App", "MediaManager.ico")
lnk = os.path.join(os.path.expanduser("~"), "Desktop", "媒体管理器.lnk")

ws = win32com.client.Dispatch("WScript.Shell")
s = ws.CreateShortcut(lnk)
s.TargetPath = exe
s.WorkingDirectory = os.path.dirname(exe)
s.IconLocation = icon
s.Description = "MediaManager 本地图片视频管理工具"
s.Save()
print("shortcut created:", lnk)
