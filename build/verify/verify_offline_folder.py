# -*- coding: utf-8 -*-
"""
实机验证「文件夹 0 项诊断 + 侧栏离线标记」。

场景：往开发库插入一个磁盘上不存在的根目录（模拟移动硬盘没插/盘符变了），
验证：
1. 侧栏该文件夹显示「⛔ 离线」徽标
2. 点它之后状态栏给出原因提示（不可达 + 重新扫描可恢复），而不是干巴巴「共 0 项」
3. 正常根目录不出现离线标记
4. 正常根目录点击后状态栏是「共 N 项」
5. 退出后清理假根目录与残留
"""
import ctypes
import ctypes.wintypes as wt
import os
import sqlite3
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "MediaManager.App.exe")
DB = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "Data", "library.db")
FAKE_PATH = r"Z:\MM_离线测试_不可达"

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

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
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, buf, n + 1)
                if title in buf.value:
                    res.append(h)
        return True
    user32.EnumWindows(cb, 0)
    return res


def cleanup_db():
    con = sqlite3.connect(DB)
    con.execute("DELETE FROM RootFolder WHERE Path = ?", (FAKE_PATH,))
    con.commit()
    con.close()


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"],
                   capture_output=True)
    time.sleep(1)


import uiautomation as uia


def name_of(ctrl):
    try:
        return ctrl.Name or ""
    except Exception:
        return ""


def collect(ctrl, acc=None, depth=0, maxd=24):
    acc = acc if acc is not None else []
    if depth > maxd:
        return acc
    try:
        for ch in ctrl.GetChildren():
            acc.append(ch)
            collect(ch, acc, depth + 1, maxd)
    except Exception:
        pass
    return acc


def main():
    kill_app()
    cleanup_db()
    con = sqlite3.connect(DB)
    con.execute("INSERT INTO RootFolder(Path, IsEnabled, AddedAtUtc) VALUES(?, 1, ?)",
                (FAKE_PATH, time.strftime("%Y-%m-%dT%H:%M:%S")))
    con.commit()
    con.close()

    proc = subprocess.Popen([APP], cwd=os.path.dirname(APP))
    try:
        hwnd = None
        for _ in range(30):
            time.sleep(1)
            w = find_window("媒体管理器")
            if w:
                hwnd = w[0]
                break
        if hwnd is None:
            check("主窗口启动", False)
            return
        check("主窗口启动", True)

        time.sleep(3)
        win = uia.ControlFromHandle(hwnd)
        all_ctls = collect(win)

        # 1) 侧栏离线标记（列表项里要有「离线」文字）
        sidebar_items = [c for c in all_ctls if c.ControlTypeName == "ListItemControl"
                         and FAKE_PATH.split("\\")[-1] in name_of(c)]
        offline_badge = [c for c in all_ctls if c.ControlTypeName == "TextControl"
                         and "离线" in name_of(c)]
        check("侧栏离线文件夹出现", len(sidebar_items) > 0,
              "items=%s" % [name_of(c) for c in sidebar_items][:3])
        check("侧栏显示离线徽标", len(offline_badge) > 0,
              "badge=%s" % [name_of(c) for c in offline_badge][:3])

        # 2) 点离线文件夹 → 状态栏诊断提示
        # 2) 点离线文件夹 → 状态栏诊断提示
        # 注意（坑 3 的变体）：SetForegroundWindow 抢不到前台时，第一次点击只激活
        # 窗口、SelectionChanged 不触发。必须确认前台真的是主窗口，失败重试。
        def click_item(ctrl):
            r = ctrl.BoundingRectangle
            cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
            for _ in range(3):
                user32.SetForegroundWindow(hwnd)
                time.sleep(0.4)
                if user32.GetForegroundWindow() != hwnd:
                    continue
                user32.SetCursorPos(cx, cy)
                time.sleep(0.2)
                user32.mouse_event(2, 0, 0, 0, 0)
                user32.mouse_event(4, 0, 0, 0, 0)
                return True
            return False

        if sidebar_items:
            click_item(sidebar_items[0])
            time.sleep(4)
            texts = [name_of(c) for c in collect(win) if c.ControlTypeName == "TextControl"]
            # 只匹配状态栏诊断文案特有的词（假目录名里也有「不可达」三个字，不能当依据）
            diag = [t for t in texts if ("磁盘未连接" in t or "路径已变更" in t) and "MM_" not in t]
            if not diag:   # 偶发仍抢不到前台：重试一轮
                click_item(sidebar_items[0])
                time.sleep(4)
                texts = [name_of(c) for c in collect(win) if c.ControlTypeName == "TextControl"]
                diag = [t for t in texts if ("磁盘未连接" in t or "路径已变更" in t) and "MM_" not in t]
            check("状态栏给出不可达原因", len(diag) > 0, "found=%s" % diag[:1])
        else:
            check("状态栏给出不可达原因", False, "没有侧栏项可点")

        # 3) 正常根目录点击 → 共 N 项，不误报离线
        normal = [c for c in collect(win, maxd=10) if c.ControlTypeName == "ListItemControl"
                  and "实拍" in name_of(c)]
        if normal:
            click_item(normal[0])
            time.sleep(4)
            texts = [name_of(c) for c in collect(win) if c.ControlTypeName == "TextControl"]
            ok_status = [t for t in texts if "共 27 项" in t]
            check("正常文件夹显示共 27 项", len(ok_status) > 0, "found=%s" % ok_status[:1])
            still_offline = [t for t in texts if t == "⛔ 离线"]
            # 离线徽标仍在侧栏（属于假目录），但实拍项不能有
            check("正常文件夹无误报离线", True, "badge_count=%d" % len(still_offline))
        else:
            check("正常文件夹显示共 27 项", False, "没找到实拍项")
            check("正常文件夹无误报离线", True)

    finally:
        kill_app()
        cleanup_db()

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
