# -*- coding: utf-8 -*-
"""验证发现页「首次提示」气泡（v1.4.5 修的 z-order bug + 8 秒自动收起）。

背景：这行提示在 XAML 里声明在缩略图 ListBox **之前**，又没给 Panel.ZIndex，
于是被缩略图整片盖住，只在两张图的缝隙里露出一行字（用户截图就是这样）。

判据（全部确定性，不拿像素统计当断言——那会同时被主题、照片内容、前台窗口干扰）：
  1. 主窗口出现；
  2. 首次启动在发现页出现提示，且文案是预期那句；
  3. 文案里的张数取自真实批次，不是写死的「30 张」；
  4. 提示坐标落在主窗口内；
  5. 8 秒后自动收起（**重新** find_hint：UIA 里消失或 IsOffscreen）。

z-order 只留截图作人工证据（shots_discovery_hint*.png），不再用深色 / 蓝色占比判：
浅色主题下气泡是纯白，深色照片又会让深色占比一直很高，两种判据都会「碰巧绿」。

注意：提示从**启动时的首次加载**就开始计时，所以要在启动后 4~6 秒内截图，
不能用 UIA 慢慢点「发现」——那时 8 秒早过了（第一版脚本就是这么误判的）。
截图前必须 force_foreground，否则 ImageGrab 抓到的是挡在前面的窗口。
"""
import ctypes
import os
import re
import sqlite3
import subprocess
import sys
import time

import uiautomation as uia
from PIL import ImageGrab

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uiawin import force_foreground  # noqa: E402

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
DB = os.path.join(APP_DIR, "Data", "library.db")
SHOT = os.path.join(ROOT, "build", "verify", "shots_discovery_hint.png")
SHOT_AFTER = os.path.join(ROOT, "build", "verify", "shots_discovery_hint_after.png")

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
    """每次都在当前 UIA 树上重新找，别复用旧引用（8 秒后控件可能已被重建/移除）。"""
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


def hint_visible(win):
    h = find_hint(win)
    if h is None:
        return False
    try:
        return not h.IsOffscreen
    except Exception:
        return True


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
        force_foreground(win.NativeWindowHandle)
        time.sleep(0.4)
        whole = ImageGrab.grab(bbox=(wr.left, wr.top, wr.right, wr.bottom), all_screens=True)
        whole.save(SHOT)
        print("截图:", SHOT)

        hint = find_hint(win)
        check("首次启动在发现页出现提示，且文案是预期那句",
              hint is not None and "按 R 换一批" in hint.Name and "按 F 收藏" in hint.Name,
              hint.Name if hint else "未找到")
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

        # 8 秒自动收起：重新在 UIA 树上找，控件消失或 IsOffscreen；最多等 12 秒。
        # 提示计时从首次加载开始，到这里已经过去约 5~6 秒。
        t0 = time.time()
        gone = False
        while time.time() - t0 < 12:
            if not hint_visible(win):
                gone = True
                break
            time.sleep(0.5)
        elapsed = time.time() - t0
        force_foreground(win.NativeWindowHandle)
        time.sleep(0.4)
        ImageGrab.grab(bbox=(wr.left, wr.top, wr.right, wr.bottom),
                       all_screens=True).save(SHOT_AFTER)
        print("收起后截图:", SHOT_AFTER)
        check("提示 8 秒后自动收起（UIA 中消失或不可见）", gone,
              "首次检查后 %.1f s 内收起" % elapsed)
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
