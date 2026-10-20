# -*- coding: utf-8 -*-
"""创建桌面快捷方式"""
import win32com.client

exe = r"C:\Users\upmen\WorkBuddy\图片视频管理工具\src\MediaManager.App\bin\Debug\net8.0-windows\MediaManager.App.exe"
icon = r"C:\Users\upmen\WorkBuddy\图片视频管理工具\src\MediaManager.App\MediaManager.ico"
lnk = r"C:\Users\upmen\OneDrive\Desktop\媒体管理器.lnk"

ws = win32com.client.Dispatch("WScript.Shell")
s = ws.CreateShortcut(lnk)
s.TargetPath = exe
s.WorkingDirectory = exe.rsplit("\\", 1)[0]
s.IconLocation = icon
s.Description = "MediaManager 本地图片视频管理工具"
s.Save()
print("shortcut created:", lnk)
