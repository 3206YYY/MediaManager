# -*- coding: utf-8 -*-
"""设置页「遇到问题」的实机验证：日志路径有没有显示、复制出来的诊断信息对不对。

为什么值得单独验一次：
诊断信息是给「用户报错了但说不清」准备的，它自己要是错了（路径不对、剪贴板没写进去、
或者把用户名一起带出去），等于把人往错的方向推。这三件事都只有真跑起来才看得见。

判据：
  1. 「关于与更新」页有两个按钮
  2. 页面上显示的是**真实存在的**日志目录（目录能在磁盘上找到）
  3. 点「复制诊断信息」后剪贴板里有内容，含版本号和日志目录
  4. 剪贴板内容**不含当前用户名**（会被贴到公开论坛）

用法：python build/verify/verify_diagnostic_info.py
前置：pip install uiautomation pillow；先 dotnet build，并关掉正在运行的 MediaManager.App.exe
"""
import ctypes
import io
import os
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
SHOTS = os.path.join(HERE, "shots_diag")
LOG = os.path.join(HERE, "verify_diag_log.txt")
os.makedirs(SHOTS, exist_ok=True)
logf = io.open(LOG, "w", encoding="utf-8")

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

import uiautomation as uia  # noqa: E402
from PIL import ImageGrab  # noqa: E402

results = []


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    logf.write(line + "\n")
    logf.flush()


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    log(("PASS " if ok else "FAIL ") + name + ((" | " + detail) if detail else ""))


def collect(ctrl, acc=None, depth=0, maxdepth=14):
    acc = acc if acc is not None else []
    if depth > maxdepth:
        return acc
    try:
        for ch in ctrl.GetChildren():
            acc.append(ch)
            collect(ch, acc, depth + 1, maxdepth)
    except Exception:
        pass
    return acc


def find_by_name(root, text, types=None):
    for c in collect(root, maxdepth=20):
        if types and c.ControlTypeName not in types:
            continue
        if text in (c.Name or ""):
            return c
    return None


def click(ctrl):
    try:
        ctrl.GetPattern(uia.PatternId.InvokePattern).Invoke()
        return True
    except Exception:
        try:
            ctrl.Click()
            return True
        except Exception:
            return False


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.5)


def launch():
    subprocess.Popen([APP], cwd=APP_DIR)
    win = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if not win.Exists(30, 1):
        return None
    time.sleep(3.5)
    try:
        user32.SetForegroundWindow(win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.6)
    return win


def clipboard_text():
    """读剪贴板文本。

    ⚠ 必须先把 PowerShell 的输出编码设成 UTF-8，否则中文按 GBK 出来是一堆问号，
    断言「含版本行」会莫名其妙变红（第一次跑就栽在这）。
    """
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw"],
            capture_output=True, timeout=15)
        return out.stdout.decode("utf-8", "replace")
    except Exception as e:
        log("读剪贴板失败:", e)
        return ""


def main():
    if not os.path.exists(APP):
        log("找不到 %s，先 dotnet build" % APP)
        return 1

    kill_app()
    # 先清剪贴板：不然上一次的内容会让「复制成功」变成假阳性
    subprocess.run(["powershell", "-NoProfile", "-Command", "Set-Clipboard -Value ''"],
                   capture_output=True)

    main_win = launch()
    check("主窗口出现", main_win is not None)
    if main_win is None:
        return 1

    settings_btn = None
    for c in collect(main_win, maxdepth=20):
        if c.ControlTypeName == "ButtonControl" and "设置" in (c.Name or ""):
            settings_btn = c
            break
    check("主界面能找到「设置」按钮", settings_btn is not None,
          (settings_btn.Name if settings_btn else ""))
    if settings_btn is None:
        ImageGrab.grab().save(os.path.join(SHOTS, "00_no_settings_btn.png"))
        kill_app()
        return 1

    click(settings_btn)
    time.sleep(2.0)
    st = uia.WindowControl(searchDepth=2, Name="设置")
    check("设置窗口打开", st.Exists(3, 0.5))
    if not st.Exists(1, 0.5):
        kill_app()
        return 1

    about = find_by_name(st, "关于与更新")
    check("设置导航里有「关于与更新」", about is not None)
    if about is not None:
        click(about)
        time.sleep(1.2)
    ImageGrab.grab().save(os.path.join(SHOTS, "01_about_page.png"))

    open_btn = find_by_name(st, "打开日志文件夹")
    copy_btn = find_by_name(st, "复制诊断信息")
    check("「遇到问题」卡片有两个按钮", open_btn is not None and copy_btn is not None)

    # 页面上的日志目录得是真的
    logline = None
    for c in collect(st, maxdepth=20):
        if c.ControlTypeName == "TextControl" and "日志目录：" in (c.Name or ""):
            logline = c.Name
            break
    check("页面上显示了日志目录", logline is not None, logline or "")
    if logline:
        path = logline.split("日志目录：", 1)[1].strip()
        check("日志目录在磁盘上真的存在", os.path.isdir(path), path)

    if copy_btn is None:
        kill_app()
        return 1

    click(copy_btn)
    time.sleep(1.5)
    ImageGrab.grab().save(os.path.join(SHOTS, "02_after_copy.png"))

    # 复制成功会弹一个提示框，关掉它再读剪贴板
    tip = uia.WindowControl(searchDepth=2, Name="遇到问题")
    if tip.Exists(1, 0.5):
        uia.SendKeys("{ENTER}", waitTime=0.2)
        time.sleep(0.6)

    text = clipboard_text()
    log("剪贴板内容 ----")
    for line in (text or "").splitlines():
        log("  " + line)
    log("---- 结束 ----")

    check("剪贴板里有内容", bool(text.strip()))
    check("含版本行", "版本：" in text)
    check("含日志目录行", "日志目录：" in text)

    user = os.environ.get("USERNAME", "")
    leaked = bool(user) and user.lower() in (text or "").lower()
    check("不含当前用户名（会被贴到公开论坛）", not leaked, "用户名=%r" % user)

    kill_app()

    passed = sum(1 for _, ok, _ in results if ok)
    log("---- 结果 %d/%d ----" % (passed, len(results)))
    for name, ok, detail in results:
        if not ok:
            log("未通过:", name, detail)
    logf.close()
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
