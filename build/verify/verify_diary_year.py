# -*- coding: utf-8 -*-
"""
实机验证 v1.4.8：日记年视图。

用户提了两件事，验收就按这两条来：
  1. 「年视图」按钮挪到左上角日历标题旁边，不再飞在窗口右上角跟「写一篇」作伴；
  2. 年视图不能只写「3 月 / 1 篇」——每个月都要把具体日期画出来，点某一天直接跳过去。

为了让「有回忆的日期会不会亮」能被验证，脚本会先往库里塞两条临时日记
（3 月 15 日、今天），跑完连库一起还原。只碰便携开发库。
"""
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import sqlite3
import subprocess
import sys
import time
from datetime import date, datetime, timezone

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".diary_year_bak"
SHOT_YEAR = os.path.join(ROOT, "build", "verify", "shots_diary_year.png")
SHOT_MONTH = os.path.join(ROOT, "build", "verify", "shots_diary_month.png")

import uiautomation as uia

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uiawin import force_foreground      # noqa: E402

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
                if title == b.value:
                    res.append(h)
        return True

    user32.EnumWindows(cb, 0)
    return res


def collect(ctrl, acc=None, d=0, maxd=22):
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


def is_offscreen(c):
    try:
        return bool(c.IsOffscreen)
    except Exception:
        return True


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


def find_ctrl(root, ctype, pred):
    for c in collect(root):
        if c.ControlTypeName == ctype and pred(name_of(c)):
            return c
    return None


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def grab_shot(hwnd, path):
    try:
        from PIL import ImageGrab
        r = uia.ControlFromHandle(hwnd).BoundingRectangle
        force_foreground(hwnd)
        time.sleep(0.7)
        ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom),
                       all_screens=True).save(path)
        print("截图：", path)
    except Exception as e:
        print("截图失败：", repr(e))


def seed_entries():
    """塞两条临时日记：3 月 15 日 + 今天。返回插入的 Id，方便精确删除。"""
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    today = date.today().strftime("%Y-%m-%d")
    rows = [("2026-03-15", "年视图验证 A", "临时数据，跑完就删", "🙂", now, now),
            (today, "年视图验证 B", "临时数据，跑完就删", "🙂", now, now)]
    con = sqlite3.connect(DB, timeout=8)
    ids = []
    for r in rows:
        cur = con.execute(
            "INSERT INTO DiaryEntry (EntryDate,Title,Content,Mood,CreatedAtUtc,UpdatedAtUtc) "
            "VALUES (?,?,?,?,?,?)", r)
        ids.append(cur.lastrowid)
    con.commit()
    con.close()
    return ids


def drop_entries(ids):
    con = sqlite3.connect(DB, timeout=8)
    for i in ids:
        con.execute("DELETE FROM DiaryEntry WHERE Id=?", (i,))
    con.commit()
    con.close()


def main():
    global DB
    if not os.path.exists(DB):
        print("找不到开发库：", DB)
        return 2

    kill_app()
    shutil.copy2(DB, BAK)

    ids = []
    try:
        ids = seed_entries()
        print("临时日记已插入：", ids)

        subprocess.Popen([APP], cwd=APP_DIR)
        time.sleep(10)

        wins = find_window("媒体管理器")
        check("主窗口启动", len(wins) > 0)
        if not wins:
            return 1
        hwnd = wins[0]
        force_foreground(hwnd)
        time.sleep(1)
        win = uia.ControlFromHandle(hwnd)

        # ---------- 打开日记 ----------
        nav = find_ctrl(win, "ListItemControl", lambda s: "日记" in s)
        check("侧栏有「日记」入口", nav is not None)
        if nav is None:
            return 1
        # 侧栏导航挂的是 PreviewMouseLeftButtonDown，UIA 的 Select() 不产生鼠标事件，
        # 必须真点一下才会开窗（Select 只会让那一行变蓝，看着像成功了）。
        native_click(hwnd, *center(nav))
        time.sleep(3.5)

        dwins = find_window("日记")
        check("日记窗口打开", len(dwins) > 0)
        if not dwins:
            return 1
        dh = dwins[0]
        force_foreground(dh)
        time.sleep(1.2)
        dwin = uia.ControlFromHandle(dh)
        dr = dwin.BoundingRectangle

        # ---------- 1) 切换按钮在左上角 ----------
        toggle = find_ctrl(dwin, "ButtonControl", lambda s: "年视图" in s)
        check("日记窗口有「年视图」按钮", toggle is not None)
        if toggle is None:
            return 1
        tr = toggle.BoundingRectangle
        width = dr.right - dr.left
        check("切换按钮在左上角（不再飞在右上角）",
              (tr.left - dr.left) < width * 0.45 and (tr.top - dr.top) < 80,
              "左边距 %d / 窗口宽 %d，上边距 %d"
              % (tr.left - dr.left, width, tr.top - dr.top))

        grab_shot(dh, SHOT_MONTH)   # 月视图留一张，对照按钮位置

        # ---------- 2) 进年视图 ----------
        try:
            toggle.GetInvokePattern().Invoke()
        except Exception:
            native_click(dh, *center(toggle))
        time.sleep(2.5)

        toggle = find_ctrl(dwin, "ButtonControl", lambda s: "月视图" in s)
        check("点后按钮变成「月视图」", toggle is not None)

        texts = [name_of(c) for c in collect(dwin)
                 if c.ControlTypeName == "TextControl" and not is_offscreen(c)]

        months = [t for t in texts if t.endswith(" 月") and t[:-2].strip().isdigit()]
        check("年视图画出 12 个月份", len(months) >= 12, "找到 %d 个：%s" % (len(months), sorted(months)[:14]))

        title = [t for t in texts if "2026 年" in t]
        check("年视图标题显示年份与总篇数", any("共" in t for t in title), str(title[:2]))

        # ---------- 3) 每个月里要有具体日期 ----------
        days = [t for t in texts if t.isdigit() and 1 <= int(t) <= 31]
        check("年视图里出现具体日期数字（不是只有篇数）", len(days) >= 300,
              "可见日期格 %d 个（12 个月 × 约 30 天）" % len(days))

        check("3 月的卡片在年视图里可见", "3 月" in months, str([m for m in months if m.startswith("3")]))
        check("11 月的卡片在年视图里可见", "11 月" in months, str([m for m in months if m.startswith("11")]))

        grab_shot(dh, SHOT_YEAR)

        # ---------- 4) 点某一天 → 回到月视图那一天 ----------
        day_cells = [c for c in collect(dwin)
                     if c.ControlTypeName == "TextControl" and not is_offscreen(c)
                     and name_of(c).isdigit() and name_of(c) == "15"]
        check("能找到 15 号这一格", len(day_cells) > 0, "找到 %d 个" % len(day_cells))
        if day_cells:
            native_click(dh, *center(day_cells[0]))
            time.sleep(2.5)

            toggle = find_ctrl(dwin, "ButtonControl", lambda s: "年视图" in s)
            check("点日期后回到月视图", toggle is not None)

            texts2 = [name_of(c) for c in collect(dwin)
                      if c.ControlTypeName == "TextControl" and not is_offscreen(c)]
            head = [t for t in texts2 if "15 日" in t and "星期" in t]
            check("右侧标题落到点的那个日期", len(head) > 0, str([t for t in texts2 if "星期" in t][:3]))

            # 月视图的日期格回来了（42 格）
            days2 = [t for t in texts2 if t.isdigit() and 1 <= int(t) <= 31]
            check("月视图日历正常渲染", len(days2) >= 25, "日期格 %d" % len(days2))

    finally:
        kill_app()
        if ids:
            try:
                drop_entries(ids)
            except Exception as e:
                print("清理临时日记失败：", repr(e))
        shutil.move(BAK, DB) if not os.path.exists(DB) else os.replace(BAK, DB)

    ok = sum(1 for _, o, _ in results if o)
    print()
    print("%d/%d" % (ok, len(results)))
    for n, o, d in results:
        if not o:
            print("  FAIL:", n, "|", d)
    return 0 if ok == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
