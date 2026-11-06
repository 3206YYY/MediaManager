# -*- coding: utf-8 -*-
"""验证发现页「首次提示」气泡浮在缩略图之上（v1.4.5 修的 z-order bug）。

背景：这行提示在 XAML 里声明在缩略图 ListBox **之前**，又没给 Panel.ZIndex，
于是被缩略图整片盖住，只在两张图的缝隙里露出一行字（用户截图就是这样）。

判据：
  1. 首次启动能在发现页找到提示文字；
  2. 提示所在矩形内部是深色面板（不是照片）→ 说明它真的盖在缩略图上面；
  3. 文案里的张数取自真实批次，不是写死的「30 张」；
  4. 8 秒后自动收起，不长期挡照片。

注意：提示从**启动时的首次加载**就开始计时，所以要在启动后 4~6 秒内截图，
不能用 UIA 慢慢点「发现」——那时 8 秒早过了（第一版脚本就是这么误判的）。
"""
import ctypes
import os
import re
import sqlite3
import subprocess
import time

import uiautomation as uia
from PIL import ImageGrab

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
DB = os.path.join(APP_DIR, "Data", "library.db")
SHOT = os.path.join(ROOT, "build", "verify", "shots_discovery_hint.png")

ok = 0
bad = 0


def check(name, cond, detail=""):
    global ok, bad
    print(("PASS " if cond else "FAIL ") + name + ("  | " + detail if detail else ""), flush=True)
    if cond:
        ok += 1
    else:
        bad += 1


def find_hint(win):
    stack = [win]
    while stack:
        node = stack.pop()
        try:
            kids = node.GetChildren()
        except Exception:
            continue
        for ch in kids:
            try:
                if ch.ControlTypeName == "TextControl" and "按 R 换一批" in (ch.Name or ""):
                    return ch
            except Exception:
                pass
            stack.append(ch)
    return None


def main():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(2)

    con = sqlite3.connect(DB)
    con.execute("delete from AppSetting where Key = 'DiscoveryHintShown'")
    total = con.execute("select count(*) from MediaItem").fetchone()[0]
    con.commit()
    con.close()

    p = subprocess.Popen([APP], cwd=APP_DIR)
    try:
        win = uia.WindowControl(searchDepth=2, Name="媒体管理器")
        check("主窗口出现", win.Exists(30, 0.5))
        time.sleep(5.0)   # 列表绑定完，提示还在 8 秒窗口内

        wr = win.BoundingRectangle
        whole = ImageGrab.grab(bbox=(wr.left, wr.top, wr.right, wr.bottom), all_screens=True)
        whole.save(SHOT)
        print("截图:", SHOT)

        hint = find_hint(win)
        check("首次启动在发现页出现提示气泡", hint is not None)
        if hint is None:
            return
        text = hint.Name
        print("提示文案:", text)

        m = re.search(r"(\d+)\s*张", text)
        n_hint = int(m.group(1)) if m else -1
        check("文案张数取自真实批次（不是写死的 30）",
              0 < n_hint <= total, "文案 %d 张 / 库里 %d 条" % (n_hint, total))

        r = hint.BoundingRectangle
        inside = (wr.left - 2 <= r.left and r.right <= wr.right + 2 and
                  wr.top - 2 <= r.top and r.bottom <= wr.bottom + 2)
        check("气泡坐标落在主窗口内", inside,
              "rect=(%d,%d,%d,%d)" % (r.left, r.top, r.right, r.bottom))
        if not inside:
            return

        rgb = whole.convert("RGB")
        box = (r.left - wr.left + 3, r.top - wr.top + 3,
               r.right - wr.left - 3, r.bottom - wr.top - 3)

        def stats(img):
            px = list(img.crop(box).getdata())
            blue = sum(1 for (rr, gg, bb) in px if bb > 120 and bb - rr > 30 and gg > 90)
            dark = sum(1 for (rr, gg, bb) in px if rr < 80 and gg < 80 and bb < 95)
            n = max(1, len(px))
            return dark / n, blue / n

        dark_before, blue_before = stats(rgb)

        # 8 秒自动收起：同一块区域重新截一次，应该变回照片（深色骤降、彩色回升）。
        # 用「前后对照」判定比写死一个绝对阈值可靠：照片明暗差别很大，面板底色不会。
        time.sleep(6)
        after = ImageGrab.grab(bbox=(wr.left, wr.top, wr.right, wr.bottom), all_screens=True).convert("RGB")
        dark_after, blue_after = stats(after)

        print("同一区域：显示时 深色 %.1f%% / 蓝 %.1f%%  →  收起后 深色 %.1f%% / 蓝 %.1f%%"
              % (dark_before * 100, blue_before * 100, dark_after * 100, blue_after * 100))

        check("气泡完整盖在缩略图之上（内部以深色面板为主）",
              dark_before > 0.50, "深色 %.1f%%" % (dark_before * 100))
        check("提示 8 秒后自动收起（同一区域明显变回照片）",
              dark_before - dark_after > 0.25,
              "深色 %.1f%% → %.1f%%" % (dark_before * 100, dark_after * 100))
    finally:
        subprocess.run(["taskkill", "/F", "/PID", str(p.pid)], capture_output=True)
        try:
            con = sqlite3.connect(DB)
            con.execute("insert or replace into AppSetting(Key,Value) values('DiscoveryHintShown','1')")
            con.commit()
            con.close()
        except Exception:
            pass
        print("\n结果 %d/%d" % (ok, ok + bad))


main()
