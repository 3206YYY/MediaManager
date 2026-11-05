# -*- coding: utf-8 -*-
"""
实机验证：V1.4.5 的「批量删除」按钮 + 内置帮助页（含深色模式配色）。

只碰**便携开发库**（exe 旁的 Data/library.db，portable.marker 保留），
不碰用户的真实库 %LOCALAPPDATA%\\MediaManager\\library.db。
删除操作只针对临时目录里现造的 5 个 PNG，用户文件一个都不动。

检查：
  A. 未选中 → 按钮**可点**（不再置灰）、悬浮条不出现
  A2. 点按钮 → 直接启用「删除模式」→ 单击文件就删（点谁删谁）→ 撤销还原 →
      勾「先勾选」变成只打勾 → Esc 退出，旧的多选行为恢复
  B. 选中 5 项 → 按钮高亮且显示数量、悬浮条出现「已选中 5 项 / 批量分类 / 批量删除」
  C. 取消选择 → 回到可点的「批量删除」
  D. 点按钮 → 与 Delete 键同一条链路：确认弹窗 → 回收站 → 撤销提示条 → 记录消失
  E. 状态栏「❓ 帮助与快捷键」直达帮助页，四个快捷键分组都在
  F. 深色模式配色：背景深、文字浅、没有白底大块
"""
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import zlib

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
# 发布版验证：给它临时放一个 portable.marker + 单独的 Data 目录，仍然不碰真实库
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".batch_help_bak"
SHOT = os.path.join(ROOT, "build", "verify", "shots_help_dark.png")
SHOT_MAIN = os.path.join(ROOT, "build", "verify", "shots_batch_delete.png")
SHOT_MODE = os.path.join(ROOT, "build", "verify", "shots_delete_mode.png")

BASE = os.path.join(tempfile.gettempdir(), "MM_批量删除验证")
N = 5

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


def collect(ctrl, acc=None, d=0, maxd=24):
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


def is_offscreen(c):
    try:
        return bool(c.IsOffscreen)
    except Exception:
        return True


def is_enabled(c):
    try:
        return bool(c.IsEnabled)
    except Exception:
        return False


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def png_bytes(w=8, h=8):
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    raw = b"".join(b"\x00" + b"\x66\x99\xcc" * w for _ in range(h))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def build_files():
    if os.path.exists(BASE):
        shutil.rmtree(BASE, ignore_errors=True)
    os.makedirs(BASE)
    made = []
    for i in range(1, N + 1):
        p = os.path.join(BASE, "批量测试%d.png" % i)
        with open(p, "wb") as f:
            f.write(png_bytes())
        made.append(p)
    return made


def seed_db(files):
    con = sqlite3.connect(DB)
    con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    now = time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())
    cur = con.execute(
        "INSERT INTO RootFolder (Path, VolumeSerial, RelativePath, IsEnabled, AddedAtUtc) "
        "VALUES (?, NULL, NULL, 1, ?)", (BASE, now))
    root_id = cur.lastrowid
    for p in files:
        st = os.stat(p)
        con.execute(
            "INSERT OR IGNORE INTO MediaItem "
            "(RootFolderId, Path, FileName, Extension, MediaType, SizeBytes, "
            " Favorite, Rating, IsMissing, DateAddedUtc) "
            "VALUES (?, ?, ?, ?, ?, ?, 0, 0, 0, ?)",
            (root_id, p, os.path.basename(p),
             os.path.splitext(p)[1].lstrip(".").lower(), "Image", st.st_size, now))
    # 明确锁深色：帮助页配色就是要在深色下验
    con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES ('Theme', 'Dark')")
    con.commit()
    con.close()
    return root_id


def find_ctrl(win, kind, predicate):
    for c in collect(win, maxd=18):
        if c.ControlTypeName != kind:
            continue
        try:
            if predicate(name_of(c)):
                return c
        except Exception:
            continue
    return None


def click(hwnd, ctrl):
    """UIA Invoke 优先；不行就原生鼠标点击（先确保窗口在前台）。"""
    try:
        ctrl.GetInvokePattern().Invoke()
        return True
    except Exception:
        pass
    try:
        r = ctrl.BoundingRectangle
        cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        for _ in range(3):
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.35)
            if user32.GetForegroundWindow() == hwnd:
                break
        user32.SetCursorPos(cx, cy)
        time.sleep(0.2)
        user32.mouse_event(2, 0, 0, 0, 0)
        user32.mouse_event(4, 0, 0, 0, 0)
        return True
    except Exception:
        return False


def native_click(hwnd, ctrl):
    """强制走真实鼠标事件。

    删除模式是在 PreviewMouseLeftButtonDown 里处理的；UIA 的 Invoke / Select 是
    程序化调用，根本不经过鼠标消息，用它「点」文件测不出点谁删谁。
    """
    try:
        r = ctrl.BoundingRectangle
        cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        for _ in range(3):
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.35)
            if user32.GetForegroundWindow() == hwnd:
                break
        user32.SetCursorPos(cx, cy)
        time.sleep(0.2)
        user32.mouse_event(2, 0, 0, 0, 0)   # LEFTDOWN
        user32.mouse_event(4, 0, 0, 0, 0)   # LEFTUP
        return True
    except Exception:
        return False


def main():
    if not os.path.exists(DB):
        print("找不到便携开发库：", DB)
        sys.exit(1)

    kill_app()
    shutil.copy2(DB, BAK)
    files = build_files()
    root_id = None
    t0 = time.time()

    try:
        root_id = seed_db(files)
        subprocess.Popen([APP], cwd=os.path.dirname(APP))

        hwnd = None
        for _ in range(30):
            time.sleep(0.6)
            w = find_window("媒体管理器")
            if w:
                hwnd = w[0]
                break
        boot = time.time() - t0
        check("主窗口启动（%.1fs）" % boot, hwnd is not None and boot < 20,
              "帮助页在设置窗口里按需解析，不参与主窗口启动")
        if hwnd is None:
            return
        time.sleep(6)
        win = uia.ControlFromHandle(hwnd)

        # 侧栏点开临时文件夹
        folder = find_ctrl(win, "ListItemControl", lambda s: s == os.path.basename(BASE))
        check("侧栏出现临时文件夹", folder is not None)
        if folder is not None:
            try:
                folder.GetSelectionItemPattern().Select()
            except Exception:
                click(hwnd, folder)
            time.sleep(3)

        def batch_btn():
            return find_ctrl(win, "ButtonControl",
                             lambda s: s == "批量删除" or s.startswith("删除选中的")
                             or s == "退出删除模式")

        def grid_items():
            """网格里可见的列表项；优先按文件名认，避开右侧详情面板的列表项。"""
            named = [c for c in collect(win, maxd=12)
                     if c.ControlTypeName == "ListItemControl"
                     and name_of(c).startswith("批量测试")
                     and not is_offscreen(c)]
            if len(named) >= N:
                return named[:N]
            return [c for c in collect(win, maxd=12)
                    if c.ControlTypeName == "ListItemControl"
                    and not is_offscreen(c)
                    and c.BoundingRectangle.left > 400][:N]

        def mode_controls_visible():
            """横幅真的展开了吗？看它里面的「退出模式」按钮 / 「先勾选」开关是否可见。

            Collapsed 的控件仍在 UIA 树里（IsOffscreen=True），只按名字存在判断会假绿；
            而且勾上「先勾选」后横幅文案会变，按文案找也会漏。
            """
            return any(c.ControlTypeName in ("ButtonControl", "CheckBoxControl")
                       and (("退出模式" in name_of(c)) or ("先勾选" in name_of(c)))
                       and not is_offscreen(c)
                       for c in collect(win))

        def db_count(rid):
            con = sqlite3.connect(DB)
            n = con.execute("SELECT COUNT(*) FROM MediaItem WHERE RootFolderId=?",
                            (rid,)).fetchone()[0]
            con.close()
            return n

        def visible_texts():
            return [name_of(c) for c in collect(win)
                    if c.ControlTypeName == "TextControl" and not is_offscreen(c)]

        def count_shown():
            for t in visible_texts():
                if t.startswith("共 ") and t.endswith(" 项"):
                    try:
                        return int(t[2:-2])
                    except ValueError:
                        pass
            return None

        check("点开临时文件夹显示 5 项", count_shown() == 5, "实际 %r" % count_shown())

        # ---------- A 未选中 ----------
        b = batch_btn()
        check("顶部有「批量删除」按钮", b is not None)
        if b is None:
            return
        check("未选中时按钮可点（不再置灰）", is_enabled(b),
              "IsEnabled=%s" % is_enabled(b))
        check("未选中时文案是「批量删除」", name_of(b) == "批量删除", name_of(b))

        bar_text = find_ctrl(win, "TextControl", lambda s: s.startswith("已选中 "))
        check("未选中时悬浮操作条不显示", bar_text is None or is_offscreen(bar_text))

        # ---------- A2 删除模式：没选中也能启用，启用后点谁删谁 ----------
        # 这是用户反馈的核心：按钮灰着点不动，本该「一启用这个功能就点谁删谁」。
        click(hwnd, b)
        time.sleep(1.2)
        banner = find_ctrl(win, "TextControl",
                           lambda s: "删除模式" in s and "点谁删谁" in s)
        check("没选中也能点「批量删除」→ 直接启用删除模式",
              banner is not None and not is_offscreen(banner) and mode_controls_visible())
        b = batch_btn()
        check("模式开启后按钮变「退出删除模式」",
              b is not None and name_of(b) == "退出删除模式",
              name_of(b) if b is not None else "None")
        mark = find_ctrl(win, "CheckBoxControl", lambda s: "先勾选" in s)
        check("横幅提供「先勾选，再统一删除」安全开关", mark is not None)

        # 留一张「删除模式已开启」的主界面图，方便人工核对红色横幅 / 按钮警示色
        try:
            from PIL import ImageGrab
            r = win.BoundingRectangle
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.6)
            ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom),
                           all_screens=True).save(SHOT_MODE)
            print("删除模式截图：", SHOT_MODE)
        except Exception as e:
            print("删除模式截图失败：", repr(e))

        one = grid_items()
        check("删除模式下能定位到待删网格项", len(one) > 0)
        if one:
            # 必须走真实鼠标事件：UIA Invoke / Select 不经过 PreviewMouseDown
            native_click(hwnd, one[0])
            time.sleep(2.5)
            check("单击一个文件立即删掉它（点谁删谁）", db_count(root_id) == 4,
                  "库里剩 %d 条" % db_count(root_id))
            gone_now = sum(1 for p in files if not os.path.exists(p))
            check("该文件真的进了回收站", gone_now == 1, "移走 %d/5" % gone_now)
            tap_toast = visible_texts()
            # 删完文件监视器可能紧接着触发一次刷新，把 StatusText 改写成「共 4 项 —— …」；
            # 两种反馈都算数，真正的判据是上面那条库记录 / 磁盘文件的变化。
            check("点删后界面同步反馈",
                  any(("共 4 项" in t) or ("已删除 1 个" in t) for t in tap_toast),
                  str([t for t in tap_toast if "共 " in t or "删除" in t][:4]))
            undo = find_ctrl(win, "ButtonControl", lambda s: s == "撤销")
            check("点删沿用同一套 5 秒撤销条", undo is not None and not is_offscreen(undo))
            if undo is not None:
                click(hwnd, undo)
                time.sleep(3.5)
            back = sum(1 for p in files if os.path.exists(p))
            check("撤销后文件完整还原（不抹掉用户数据）", back == 5, "回来 %d/5" % back)
            check("撤销后库记录也还原", db_count(root_id) == 5,
                  "剩 %d 条" % db_count(root_id))
            # 撤销会 LoadMediaAsync 重建列表，重新点一次侧栏文件夹把视图拉回来
            folder2 = find_ctrl(win, "ListItemControl",
                                lambda s: s == os.path.basename(BASE))
            if folder2 is not None:
                try:
                    folder2.GetSelectionItemPattern().Select()
                except Exception:
                    click(hwnd, folder2)
                time.sleep(2.5)

        # 「先勾选」开关：单击只打勾，不删文件
        mark = find_ctrl(win, "CheckBoxControl", lambda s: "先勾选" in s)
        if mark is not None:
            try:
                mark.GetTogglePattern().Toggle()
            except Exception:
                native_click(hwnd, mark)
            time.sleep(1)
        two = grid_items()
        for it in two[:2]:
            native_click(hwnd, it)
            time.sleep(0.5)
        time.sleep(1)
        check("勾上「先勾选」后单击只打勾、不删文件",
              db_count(root_id) == 5 and sum(1 for p in files if os.path.exists(p)) == 5,
              "库里 %d 条" % db_count(root_id))
        sel2 = find_ctrl(win, "TextControl", lambda s: s == "已选中 2 项")
        check("勾选模式复用原来的悬浮条", sel2 is not None and not is_offscreen(sel2))

        # 退出：点横幅按钮 + Esc 各走一次，两种都要能关掉
        exit_btn = find_ctrl(win, "ButtonControl", lambda s: "退出模式" in s)
        check("横幅上有「退出模式」按钮", exit_btn is not None)
        if exit_btn is not None:
            click(hwnd, exit_btn)
            time.sleep(1)
        check("点退出按钮后横幅收起", not mode_controls_visible())
        b = batch_btn()
        check("退出后按钮恢复成「批量删除」且可点",
              b is not None and name_of(b) == "批量删除" and is_enabled(b),
              name_of(b) if b is not None else "None")
        # Esc 也能退出
        b = batch_btn()
        if b is not None:
            click(hwnd, b)
            time.sleep(1)
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.4)
            user32.keybd_event(0x1B, 0, 0, 0)   # VK_ESCAPE down
            user32.keybd_event(0x1B, 0, 2, 0)   # up
            time.sleep(1)
            check("按 Esc 也能退出删除模式", not mode_controls_visible())
        # 退出后单击恢复成「选中」，绝不能还偷偷删文件
        rest = grid_items()
        if rest:
            native_click(hwnd, rest[0])
            time.sleep(1.5)
        check("退出模式后单击不再删文件",
              db_count(root_id) == 5 and sum(1 for p in files if os.path.exists(p)) == 5)
        clear2 = find_ctrl(win, "ButtonControl", lambda s: s == "取消选择")
        if clear2 is not None:
            click(hwnd, clear2)
            time.sleep(1)

        # ---------- B 全选 ----------
        items = [c for c in collect(win, maxd=12)
                 if c.ControlTypeName == "ListItemControl"
                 and name_of(c).startswith("批量测试")
                 and not is_offscreen(c)]
        # 网格项的 UIA Name 可能不是文件名（模板是 Grid），退回按「可见的网格项」取前 N 个
        if len(items) < N:
            items = [c for c in collect(win, maxd=12)
                     if c.ControlTypeName == "ListItemControl"
                     and not is_offscreen(c)
                     and c.BoundingRectangle.left > 400][:N]
        check("网格里能看到 5 个待选项", len(items) >= N, "找到 %d 个" % len(items))
        for idx, it in enumerate(items[:N]):
            try:
                if idx == 0:
                    it.GetSelectionItemPattern().Select()
                else:
                    it.GetSelectionItemPattern().AddToSelection()
            except Exception:
                click(hwnd, it)
            time.sleep(0.15)
        time.sleep(1.2)

        b = batch_btn()
        check("选中后按钮可点", b is not None and is_enabled(b))
        check("按钮动态显示数量", b is not None and name_of(b) == "删除选中的 5 项",
              b.Name if b is not None else "None")
        sel_text = find_ctrl(win, "TextControl", lambda s: s == "已选中 5 项")
        check("悬浮条出现「已选中 5 项」", sel_text is not None and not is_offscreen(sel_text))
        btns = [name_of(c) for c in collect(win, maxd=18)
                if c.ControlTypeName == "ButtonControl" and not is_offscreen(c)]
        check("悬浮条含「批量分类」", any("批量分类" in s for s in btns), str(btns[-6:]))
        check("悬浮条含「取消选择」", any("取消选择" in s for s in btns))
        check("悬浮条含「删除选中的 5 项」", "删除选中的 5 项" in btns)

        # 留一张主界面图：目视检查工具栏按钮 / 悬浮条的图标字形与高亮效果
        try:
            from PIL import ImageGrab
            r = win.BoundingRectangle
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.6)
            ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom),
                           all_screens=True).save(SHOT_MAIN)
            print("主界面截图：", SHOT_MAIN)
        except Exception as e:
            print("主界面截图失败：", repr(e))

        # ---------- C 取消选择 ----------
        clear_btn = find_ctrl(win, "ButtonControl", lambda s: s == "取消选择")
        if clear_btn is not None:
            click(hwnd, clear_btn)
            time.sleep(1)
        b = batch_btn()
        check("取消选择后按钮回到可点的「批量删除」",
              b is not None and name_of(b) == "批量删除" and is_enabled(b),
              name_of(b) if b is not None else "None")
        sel_text = find_ctrl(win, "TextControl", lambda s: s.startswith("已选中 "))
        check("取消选择后悬浮条收起", sel_text is None or is_offscreen(sel_text))

        # ---------- D 再全选 → 点顶部按钮 → 完整删除链路 ----------
        for idx, it in enumerate(items[:N]):
            try:
                if idx == 0:
                    it.GetSelectionItemPattern().Select()
                else:
                    it.GetSelectionItemPattern().AddToSelection()
            except Exception:
                click(hwnd, it)
            time.sleep(0.12)
        time.sleep(1)
        b = batch_btn()
        check("有选中时按钮仍是「删除选中的 5 项」",
              b is not None and name_of(b) == "删除选中的 5 项",
              name_of(b) if b is not None else "None")
        if b is not None and is_enabled(b):
            click(hwnd, b)
        time.sleep(1.5)
        dlg = find_window("确认删除")
        check("旧批量删除链路仍在：点按钮弹出「确认删除」弹窗", len(dlg) > 0)
        if dlg:
            dwin = uia.ControlFromHandle(dlg[0])
            ok_btn = find_ctrl(dwin, "ButtonControl", lambda s: s == "删除")
            check("弹窗有「删除」确认按钮", ok_btn is not None)
            if ok_btn is not None:
                click(dlg[0], ok_btn)
        time.sleep(2.5)

        toast = [t for t in visible_texts() if "已删除" in t]
        check("删除后弹出撤销提示条", any("5" in t for t in toast), str(toast[:3]))
        undo = find_ctrl(win, "ButtonControl", lambda s: s == "撤销")
        check("撤销提示条带「撤销」按钮", undo is not None and not is_offscreen(undo))
        b = batch_btn()
        check("删除后按钮自动回到可点的「批量删除」",
              b is not None and name_of(b) == "批量删除" and is_enabled(b),
              name_of(b) if b is not None else "None")
        # 删除完成后 StatusText 会被写成「已删除 5 个文件」，覆盖掉「共 N 项」，
        # 这是既有行为，别拿「共 0 项」当判据。
        after = visible_texts()
        check("删除后状态栏给出完成反馈",
              any(("已删除 5 个文件" in t) or ("共 0 项" in t) for t in after),
              str([t for t in after if "删除" in t or "共 " in t][:4]))

        con = sqlite3.connect(DB)
        left = con.execute("SELECT COUNT(*) FROM MediaItem WHERE RootFolderId=?", (root_id,)).fetchone()[0]
        con.close()
        check("库里 5 条临时记录已删除", left == 0, "剩 %d 条" % left)
        gone = sum(1 for p in files if not os.path.exists(p))
        check("5 个临时文件已移进回收站", gone == 5, "移走 %d/5" % gone)

        # ---------- E 帮助页 ----------
        help_btn = find_ctrl(win, "ButtonControl", lambda s: "帮助与快捷键" in s)
        check("状态栏有「❓ 帮助与快捷键」入口", help_btn is not None)
        if help_btn is not None:
            click(hwnd, help_btn)
        time.sleep(2.5)
        shwnd = None
        for _ in range(12):
            w = find_window("设置")
            if w:
                shwnd = w[0]
                break
            time.sleep(0.5)
        check("设置窗口打开", shwnd is not None)
        if shwnd is None:
            return
        user32.SetForegroundWindow(shwnd)
        time.sleep(1)
        swin = uia.ControlFromHandle(shwnd)
        htexts = [name_of(c) for c in collect(swin)
                  if c.ControlTypeName == "TextControl" and not is_offscreen(c)]
        all_texts = [name_of(c) for c in collect(swin)
                     if c.ControlTypeName == "TextControl"]
        # 帮助页很长，滚动区外的 TextBlock 在 UIA 里 IsOffscreen=True；
        # 内容校验取 PageHelp 子树（找不到再退回整窗），可见性校验仍只看当前可见文字。
        page_help = next((c for c in collect(swin) if c.AutomationId == "PageHelp"), None)
        scope = collect(page_help) if page_help is not None else collect(swin)
        blob = "\n".join(name_of(c) for c in scope if c.ControlTypeName == "TextControl")
        print("PageHelp 子树定位：", "命中" if page_help is not None else "退回整窗")
        check("直达「快速上手与快捷键」页", "快速上手与快捷键" in "\n".join(htexts))
        check("左侧新增「帮助与快捷键」分类", any("帮助与快捷键" in t for t in htexts))
        for group in ("全局热键", "主界面 / 批量操作", "图片查看器", "图片编辑器", "视频播放器"):
            check("帮助页分组「%s」" % group, group in blob)
        for key in ("Ctrl + A", "Delete", "Ctrl + Z", "Shift + 点击", "拖框", "Space"):
            check("帮助页列出「%s」" % key, key in blob)
        check("帮助页写明「怎么一次选中多张」", "怎么一次选中多张" in blob)
        for word in ("删除模式", "点谁删谁", "先勾选，再统一删除", "Esc"):
            check("帮助页写明「%s」" % word, word in blob)

        # ---------- F 深色配色（像素统计 + 留图人工复核） ----------
        try:
            from PIL import ImageGrab
            r = swin.BoundingRectangle
            box = (r.left, r.top, r.right, r.bottom)
            img = ImageGrab.grab(bbox=box, all_screens=True).convert("RGB")
            img.save(SHOT)
            px = list(img.getdata())
            lum = [0.299 * a + 0.587 * b + 0.114 * c for a, b, c in px]
            dark_ratio = sum(1 for v in lum if v < 90) / len(lum)
            light_ratio = sum(1 for v in lum if v > 170) / len(lum)
            near_white = sum(1 for a, b, c in px if a > 225 and b > 225 and c > 225) / len(px)
            check("深色模式：窗口以深色为底", dark_ratio > 0.5, "深像素 %.1f%%" % (dark_ratio * 100))
            check("深色模式：有清晰的浅色文字", light_ratio > 0.005,
                  "亮像素 %.2f%%" % (light_ratio * 100))
            check("深色模式：没有白底大块", near_white < 0.08,
                  "近白像素 %.2f%%" % (near_white * 100))
            print("截图：%s  尺寸 %s" % (SHOT, img.size))
        except Exception as e:
            check("深色配色像素统计", False, repr(e))

    finally:
        kill_app()
        shutil.copy2(BAK, DB)
        os.remove(BAK)
        # 必须连 -wal/-shm 一起删，否则 WAL 重放会把测试数据又写回刚还原的库
        for suffix in ("-wal", "-shm"):
            side = DB + suffix
            if os.path.exists(side):
                os.remove(side)
        if os.path.exists(BASE):
            shutil.rmtree(BASE, ignore_errors=True)

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
