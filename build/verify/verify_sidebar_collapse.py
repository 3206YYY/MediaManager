# -*- coding: utf-8 -*-
"""
实机验证 v1.4.8：两侧栏收起 / 展开。

用户原话：「我想给两边侧边栏加上折叠隐藏功能，这样看略缩图的空间又大了点」。
这个功能的验收标准只有两条，别跑偏成「按钮点得动」：
  1. 收起后中间网格真的变宽（220 / 280 全给缩略图），不是只是面板视觉上消失；
  2. 下次启动还是收着的（记在 AppSetting，纯布局偏好，不进 MediaItem）。

只碰便携开发库（exe 旁 Data/library.db），跑完还原；不改库里的媒体内容。
"""
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import sqlite3
import subprocess
import sys
import time

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".sidebar_bak"
SHOT_COLLAPSED = os.path.join(ROOT, "build", "verify", "shots_sidebar_collapsed.png")
SHOT_EXPANDED = os.path.join(ROOT, "build", "verify", "shots_sidebar_expanded.png")

# 侧栏展开宽度，和 XAML 里 ColumnDefinition 的初值一致
LEFT_W = 220
RIGHT_W = 280
# 收起判定给 40px 余量：列宽 0 之后中间栏会多出垂直滚动条那一小块
TOL = 40

import uiautomation as uia

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uiawin import force_foreground      # noqa: E402  抢前台统一入口，别直接 SetForegroundWindow

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


def collect(ctrl, acc=None, d=0, maxd=20):
    acc = acc if acc is not None else []
    if d > maxd:
        return acc
    try:
        for ch in ctrl.GetChildren():
            acc.append(ch)
            collect(ch, acc, d + 1, maxd)
    except Exception:
        pass
    return acc


def name_of(c):
    try:
        return c.Name or ""
    except Exception:
        return ""


def auto_id(c):
    try:
        return c.AutomationId or ""
    except Exception:
        return ""


def center(c):
    r = c.BoundingRectangle
    return (r.left + r.right) // 2, (r.top + r.bottom) // 2


def native_click(hwnd, x, y):
    force_foreground(hwnd)
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.12)
    user32.mouse_event(2, 0, 0, 0, 0)
    time.sleep(0.05)
    user32.mouse_event(4, 0, 0, 0, 0)
    time.sleep(0.6)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def grab_shot(hwnd, path):
    """留一张全窗口图给人复核：把手好不好点、有没有压住滚动条，像素断言看不出来。"""
    try:
        from PIL import ImageGrab
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), all_screens=True).save(path)
        print("  截图：%s" % path)
    except Exception as e:
        print("  截图失败：%r" % (e,))


def read_setting(key):
    try:
        con = sqlite3.connect(DB, timeout=6)
        row = con.execute("SELECT Value FROM AppSetting WHERE Key = ?", (key,)).fetchone()
        con.close()
        return row[0] if row else None
    except Exception:
        return None


def main():
    if not os.path.exists(DB):
        print("找不到便携开发库：", DB)
        sys.exit(1)

    kill_app()
    shutil.copy2(DB, BAK)
    try:
        subprocess.Popen([APP], cwd=os.path.dirname(APP))
        hwnd = None
        for _ in range(30):
            time.sleep(0.6)
            w = find_window("媒体管理器")
            if w:
                hwnd = w[0]
                break
        check("主窗口出现", hwnd is not None)
        if hwnd is None:
            return

        win = uia.ControlFromHandle(hwnd)
        time.sleep(2.5)     # 等首次布局 / 列表加载

        def handles():
            """展开态和收起态各有一个同 id 的把手（一个在侧栏里，一个浮在网格上），
            取当前真正可见的那个。"""
            out = {}
            for c in collect(win, maxd=16):
                aid = auto_id(c)
                if aid not in ("LeftSidebarToggle", "RightSidebarToggle"):
                    continue
                try:
                    if c.IsOffscreen:
                        continue
                except Exception:
                    continue
                out[aid] = c
            return out

        def thumb_list():
            """主网格 = 页面里最宽的那个 ListControl（侧栏里的导航/标签都窄得多）。"""
            best = None
            for c in collect(win, maxd=12):
                if c.ControlTypeName != "ListControl":
                    continue
                try:
                    r = c.BoundingRectangle
                    w = r.right - r.left
                except Exception:
                    continue
                if best is None or w > best[0]:
                    best = (w, c)
            return best[1] if best else None

        def grid_width():
            lst = thumb_list()
            if lst is None:
                return 0
            r = lst.BoundingRectangle
            return r.right - r.left

        def nav_visible():
            """左侧栏里的某个固定文案在不在。收起后 Border 是 Collapsed，UIA 里应当找不到。"""
            for c in collect(win, maxd=14):
                if c.ControlTypeName == "ListItemControl" and "全部媒体" in name_of(c):
                    try:
                        return not c.IsOffscreen
                    except Exception:
                        return False
            return False

        hs = handles()
        check("找到左把手", "LeftSidebarToggle" in hs)
        check("找到右把手", "RightSidebarToggle" in hs)
        if len(hs) < 2:
            return

        w0 = grid_width()
        check("初始网格宽度合理（两侧栏都展开）", w0 > 300, "宽 %d px" % w0)
        check("初始左侧栏可见", nav_visible())
        grab_shot(hwnd, SHOT_EXPANDED)

        # ---------- 收起左侧 ----------
        x, y = center(hs["LeftSidebarToggle"])
        native_click(hwnd, x, y)
        time.sleep(1.2)
        w1 = grid_width()
        check("收起左侧栏后网格变宽", w1 - w0 >= LEFT_W - TOL,
              "%d → %d（+%d；这里含系统 DPI 缩放，150%% 下 220 显示为 330）"
              % (w0, w1, w1 - w0))
        check("收起左侧栏后导航不可见", not nav_visible())
        hs = handles()
        check("左把手箭头翻向内侧", name_of(hs["LeftSidebarToggle"]).strip() == "›",
              "Name=%r" % name_of(hs["LeftSidebarToggle"]))

        # ---------- 收起右侧 ----------
        hs = handles()
        x, y = center(hs["RightSidebarToggle"])
        native_click(hwnd, x, y)
        time.sleep(1.2)
        w2 = grid_width()
        check("收起右侧栏后网格再变宽", w2 - w1 >= RIGHT_W - TOL,
              "%d → %d（+%d；280 在 150%% 缩放下显示为 420）" % (w1, w2, w2 - w1))

        grab_shot(hwnd, SHOT_COLLAPSED)

        check("收起的两个状态都写进了库",
              read_setting("Ui.LeftSidebarCollapsed") == "1"
              and read_setting("Ui.RightSidebarCollapsed") == "1",
              "L=%s R=%s" % (read_setting("Ui.LeftSidebarCollapsed"),
                             read_setting("Ui.RightSidebarCollapsed")))

        # ---------- 都展开回来 ----------
        hs = handles()
        x, y = center(hs["LeftSidebarToggle"])
        native_click(hwnd, x, y)
        time.sleep(1.0)
        hs = handles()
        x, y = center(hs["RightSidebarToggle"])
        native_click(hwnd, x, y)
        time.sleep(1.2)

        w3 = grid_width()
        check("两个都展开后宽度回到初始", abs(w3 - w0) <= TOL, "%d vs %d" % (w3, w0))
        check("展开后左侧栏又可见", nav_visible())
        check("展开状态也写进了库",
              read_setting("Ui.LeftSidebarCollapsed") == "0"
              and read_setting("Ui.RightSidebarCollapsed") == "0",
              "L=%s R=%s" % (read_setting("Ui.LeftSidebarCollapsed"),
                             read_setting("Ui.RightSidebarCollapsed")))

        # ---------- 重启后记住「收着」 ----------
        hs_now = handles()
        x, y = center(hs_now["LeftSidebarToggle"])
        native_click(hwnd, x, y)
        time.sleep(1.5)

        kill_app()
        subprocess.Popen([APP], cwd=os.path.dirname(APP))
        hwnd2 = None
        for _ in range(30):
            time.sleep(0.6)
            w = find_window("媒体管理器")
            if w:
                hwnd2 = w[0]
                break
        check("重启后窗口出现", hwnd2 is not None)
        if hwnd2:
            win2 = uia.ControlFromHandle(hwnd2)
            time.sleep(3.0)
            best = None
            for c in collect(win2, maxd=12):
                if c.ControlTypeName != "ListControl":
                    continue
                try:
                    br = c.BoundingRectangle
                    bw = br.right - br.left
                except Exception:
                    continue
                if best is None or bw > best[0]:
                    best = (bw, c)
            w4 = best[0] if best else 0
            check("重启后左侧栏仍是收起的", w4 - w0 >= LEFT_W - TOL,
                  "宽 %d（展开时 %d）" % (w4, w0))

    finally:
        kill_app()
        shutil.copy2(BAK, DB)
        os.remove(BAK)
        for suffix in ("-wal", "-shm"):
            side = DB + suffix
            if os.path.exists(side):
                os.remove(side)

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
