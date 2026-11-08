# -*- coding: utf-8 -*-
"""实机验证「设置 → 关于与更新 → 开源许可」这张卡片。

要证明的是：许可证信息在界面上真能看到、不是埋在仓库里没人知道。
1. 设置页侧栏「关于与更新」里有一张「开源许可」卡片
2. 卡片写明本项目是 MIT、版权人 3206YYY
3. 三个第三方组件（libVLC / FFmpeg / ImageSharp）各自的协议都在界面上
4. FFmpeg 那条明确写了「独立程序调用、不链接进本程序」——这句是合规的关键
5. 两个按钮存在且可点（MIT 许可全文 / 第三方组件声明）
6. 安装目录里确实躺着 LICENSE.txt 与 THIRD-PARTY-NOTICES.md（断网也能查）

用法：
    python build/verify/verify_license_card.py
    MM_VERIFY_APP=... MM_VERIFY_DATA_DIR=... python build/verify/verify_license_card.py
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import time

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
SHOT = os.path.join(ROOT, "build", "verify", "shots_license_card.png")

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


def find_window(title):
    res = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if title in b.value:
                    res.append(h)
        return True

    user32.EnumWindows(cb, 0)
    return res


def name_of(c):
    try:
        return c.Name or ""
    except Exception:
        return ""


def walk(node, depth=0, limit=9):
    """递归遍历控件树（UIA 的 FindAll 不认 TextBlock，只能自己走）。"""
    if depth > limit:
        return
    for ch in node.GetChildren():
        yield ch
        yield from walk(ch, depth + 1, limit)


def is_offscreen(c):
    try:
        return bool(c.IsOffscreen)
    except Exception:
        return True


def click(hwnd, c):
    """真实鼠标点击：有些控件 Invoke 不生效，落到坐标最稳。"""
    r = c.BoundingRectangle
    x, y = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.3)
    user32.SetCursorPos(x, y)
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.8)


def main():
    if not os.path.exists(APP):
        print("找不到 exe：", APP)
        return 2

    for h in find_window("媒体管理器"):
        user32.PostMessageW(h, 0x0010, 0, 0)
    time.sleep(1.5)

    proc = subprocess.Popen([APP], cwd=os.path.dirname(APP))
    win = None
    for _ in range(20):
        time.sleep(1)
        hs = find_window("媒体管理器")
        if hs:
            win = uia.ControlFromHandle(hs[0])
            break
    check("主窗口启动", win is not None)
    if win is None:
        return 1

    try:
        hwnd = win.NativeWindowHandle
        time.sleep(3)

        # 1. 打开设置
        settings_btn = None
        for c in walk(win, 0, 6):
            try:
                if c.ControlTypeName == "ButtonControl" and "设置" in name_of(c):
                    settings_btn = c
                    break
            except Exception:
                pass
        check("主界面找得到「设置」按钮", settings_btn is not None)
        if settings_btn is None:
            return 1
        try:
            settings_btn.GetInvokePattern().Invoke()
        except Exception:
            click(hwnd, settings_btn)
        time.sleep(3)

        hs = find_window("设置")
        check("设置窗口打开了", len(hs) > 0)
        if not hs:
            return 1
        sw = uia.ControlFromHandle(hs[0])
        time.sleep(1.5)

        # 2. 切到「关于与更新」
        about = None
        for c in walk(sw, 0, 8):
            try:
                if c.ControlTypeName == "ListItemControl" and "关于与更新" in name_of(c):
                    about = c
                    break
            except Exception:
                pass
        check("侧栏有「关于与更新」", about is not None)
        if about is not None:
            try:
                about.GetSelectionItemPattern().Select()
            except Exception:
                click(sw.NativeWindowHandle, about)
            time.sleep(2.5)

        # 3. 卡片文字
        texts = [name_of(c) for c in walk(sw, 0, 10)
                 if c.ControlTypeName in ("TextControl", "ButtonControl")]

        def matched(*keys):
            for t in texts:
                if all(k in t for k in keys):
                    return t
            return ""

        def has(*keys):
            return bool(matched(*keys))

        check("出现「开源许可」小节", has("开源许可"))
        check("写明 MIT 协议", has("MIT"), matched("MIT"))
        check("写明版权人 3206YYY", has("3206YYY"), matched("3206YYY"))
        check("界面列出 libVLC 的 LGPL-2.1", has("libVLC", "LGPL"), matched("libVLC", "LGPL"))
        check("界面列出 FFmpeg 的 GPLv3", has("FFmpeg", "GPLv3"), matched("FFmpeg", "GPLv3"))
        check("FFmpeg 写明「独立程序、不链接进本程序」",
              has("FFmpeg", "不链接"), matched("FFmpeg", "不链接"))
        check("界面列出 ImageSharp 的分割许可",
              has("ImageSharp", "Labors"), matched("ImageSharp", "Labors"))

        # 4. 两个按钮
        b1 = b2 = None
        for c in walk(sw, 0, 10):
            try:
                if c.ControlTypeName != "ButtonControl":
                    continue
                n = name_of(c)
                if "MIT" in n and "许可" in n:
                    b1 = c
                elif "第三方" in n and "声明" in n:
                    b2 = c
            except Exception:
                pass
        check("有「MIT 许可全文」按钮", b1 is not None and not is_offscreen(b1))
        check("有「第三方组件声明」按钮", b2 is not None and not is_offscreen(b2))

        # 5. 截图留证据（看排版有没有被裁切/重叠）
        try:
            from PIL import ImageGrab
            r = sw.BoundingRectangle
            ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom),
                           all_screens=True).save(SHOT)
            print("截图:", SHOT)
        except Exception as ex:
            print("截图失败:", ex)

        # 6. 关掉设置窗口
        user32.PostMessageW(sw.NativeWindowHandle, 0x0010, 0, 0)
        time.sleep(1)
    finally:
        try:
            proc.terminate()
            time.sleep(1)
        except Exception:
            pass
        subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)],
                       capture_output=True)

    # 7. 安装目录里真的有这两份文件
    d = os.path.dirname(APP)
    lic = os.path.join(d, "LICENSE.txt")
    noti = os.path.join(d, "THIRD-PARTY-NOTICES.md")
    check("输出目录有 LICENSE.txt", os.path.exists(lic))
    check("输出目录有 THIRD-PARTY-NOTICES.md", os.path.exists(noti))
    if os.path.exists(noti):
        body = open(noti, encoding="utf-8").read()
        check("声明里写了 libVLC 的许可与源码地址",
              "LGPL-2.1" in body and "videolan" in body)
        check("声明里写了 FFmpeg 是 GPL 版并给出源码获取方式",
              "--enable-gpl" in body and "gyan.dev" in body)
        check("声明里提到 ImageSharp 开源项目免费", "不需要购买任何许可" in body)

    print("\n%d/%d 通过" % (sum(1 for _, ok, _ in results if ok), len(results)))
    return 0 if all(ok for _, ok, _ in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
