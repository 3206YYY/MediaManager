# -*- coding: utf-8 -*-
"""切到浅色主题拍一张日历 / 年视图，确认这次换模板没把浅色弄坏，拍完切回深色。

用法：python build/uia_light_check.py
"""
import ctypes
import io
import os
import sqlite3
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
DB = os.path.join(APP_DIR, "Data", "library.db")
SHOTS = os.path.join(HERE, "shots_cal")

logf = io.open(os.path.join(HERE, "verify_light_log.txt"), "w", encoding="utf-8")


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    logf.write(line + "\n")
    logf.flush()


user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

import uiautomation as uia  # noqa: E402
from PIL import ImageGrab  # noqa: E402


def set_theme(value):
    conn = sqlite3.connect(DB)
    conn.execute("UPDATE AppSetting SET value=? WHERE key='Theme'", (value,))
    conn.commit()
    conn.close()
    log("主题 ->", value)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.5)


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
    for c in collect(root):
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


def shot(ctrl, tag):
    r = ctrl.BoundingRectangle
    ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom)).save(os.path.join(SHOTS, tag + ".png"))


def find_popup(pid, anchor):
    al, _at, ar, ab = anchor
    found = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(hwnd, _lparam):
        p = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid or not user32.IsWindowVisible(hwnd):
            return True
        r = ctypes.wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if 140 <= w <= 460 and 140 <= h <= 460 and abs(r.top - ab) <= 60 and al - 80 <= r.left <= ar + 80:
            found.append((r.left, r.top, r.right, r.bottom))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def main():
    kill_app()
    set_theme("Light")

    subprocess.Popen([APP], cwd=APP_DIR)
    win = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if not win.Exists(30, 1):
        log("主窗口没出现")
        kill_app()
        set_theme("Dark")
        return 1
    time.sleep(3.5)
    try:
        user32.SetForegroundWindow(win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.6)

    item = None
    for c in collect(win):
        if c.ControlTypeName == "ListItemControl" and "日记" in (c.Name or ""):
            item = c
            break

    diary = uia.WindowControl(searchDepth=2, Name="日记")
    for _ in range(3):
        if item is not None:
            click(item)
        time.sleep(2.2)
        if diary.Exists(1, 0.5):
            break
    if diary.Exists(1, 0.5):
        time.sleep(1.2)
        shot(diary, "20_light_month")

        ybtn = find_by_name(diary, "年视图", types=("ButtonControl",))
        if ybtn is not None:
            click(ybtn)
            time.sleep(1.6)
            shot(diary, "21_light_year")
            back = find_by_name(diary, "月视图", types=("ButtonControl",))
            if back is not None:
                click(back)
                time.sleep(1.2)

        new_btn = find_by_name(diary, "写一篇", types=("ButtonControl",))
        if new_btn is not None:
            click(new_btn)
            time.sleep(2.0)
            editor = uia.WindowControl(searchDepth=2, Name="写一篇")
            if editor.Exists(3, 0.5):
                dp = None
                for c in collect(editor):
                    if (c.AutomationId or "") == "DateBox":
                        dp = c
                        break
                if dp is not None:
                    dr = dp.BoundingRectangle
                    btn = None
                    for c in collect(dp):
                        if c.ControlTypeName == "ButtonControl":
                            btn = c
                            break
                    if btn is not None:
                        click(btn)
                        time.sleep(1.5)
                        cands = find_popup(editor.ProcessId, (dr.left, dr.top, dr.right, dr.bottom))
                        log("浅色弹层:", cands)
                        if cands:
                            l, t, r, b = max(cands, key=lambda h: (h[2] - h[0]) * (h[3] - h[1]))
                            img = ImageGrab.grab(bbox=(l, t, r, b))
                            img.save(os.path.join(SHOTS, "22_light_calendar.png"))
                            gray = img.convert("L")
                            px = list(gray.getdata())
                            avg = sum(px) / len(px)
                            log("浅色日历亮度: %.0f" % avg)
                for w in collect(editor):
                    pass
                try:
                    editor.GetPattern(uia.PatternId.WindowPattern).Close()
                except Exception:
                    pass

    kill_app()
    set_theme("Dark")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
