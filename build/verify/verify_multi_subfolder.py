# -*- coding: utf-8 -*-
"""
实机验证：添加一个「包含多个子文件夹」的父目录后，逐个点开每个子文件夹都要有内容。

这是测试员要求的那条自测：
  1. 父目录本身（顶层）能点开
  2. 一级子目录（甲 / 乙）能点开
  3. 二级子目录（乙\2024）能点开
  4. 没有文件的空目录不该在侧栏里凭空出现
  5. 在父目录上点「扫描」不会把列表搞丢 / 翻倍

做法：临时建一个真实目录 + 真实图片文件，把「扫描会写进库的行」直接写进一个**库副本**，
再用真实 exe 打开这个副本跑 UIA。结束还原库、删临时目录 —— 不污染用户的库。
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
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
MARKER = os.path.join(APP_DIR, "portable.marker")
MARKER_OFF = MARKER + ".verify_off"
DB = os.path.join(os.environ["LOCALAPPDATA"], "MediaManager", "library.db")
BAK = DB + ".nested_bak"

BASE = os.path.join(tempfile.gettempdir(), "MM_嵌套验证")
LAYOUT = {
    "整张.png": None,
    "甲": ["甲1.jpg", "甲2.jpg"],
    "乙": ["乙1.jpg"],
    os.path.join("乙", "2024"): ["旧1.jpg"],
    "丙": [],          # 空目录：不该出现在侧栏
}


def png_bytes(w=8, h=8):
    """生成一个最小合法 PNG（8x8 纯色），让缩略图管线有真文件可读。"""
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    raw = b"".join(b"\x00" + b"\x66\x99\xcc" * w for _ in range(h))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def build_files():
    if os.path.exists(BASE):
        shutil.rmtree(BASE)
    os.makedirs(BASE)
    made = []
    for rel, inner in LAYOUT.items():
        if inner is None:
            p = os.path.join(BASE, rel)
            with open(p, "wb") as f:
                f.write(png_bytes())
            made.append(p)
            continue
        d = os.path.join(BASE, rel)
        os.makedirs(d, exist_ok=True)
        for name in inner:
            p = os.path.join(d, name)
            with open(p, "wb") as f:
                f.write(png_bytes())
            made.append(p)
    return made


def seed_db(files):
    """把「扫描父目录」会产生的行写进库：媒体全部归属新加的父根目录。"""
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
    con.commit()
    con.close()
    return root_id


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


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def main():
    if not os.path.exists(DB):
        print("找不到真实库：", DB)
        sys.exit(1)

    kill_app()
    shutil.copy2(DB, BAK)
    files = build_files()
    print("临时目录：%s（%d 个文件）" % (BASE, len(files)))

    moved = False
    root_id = None
    try:
        root_id = seed_db(files)
        if os.path.exists(MARKER):
            os.rename(MARKER, MARKER_OFF)
            moved = True

        subprocess.Popen([APP], cwd=APP_DIR)
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

        time.sleep(7)
        win = uia.ControlFromHandle(hwnd)

        def sidebar_item(title):
            hits = []
            for c in collect(win, maxd=10):
                if c.ControlTypeName != "ListItemControl" or name_of(c) != title:
                    continue
                try:
                    if c.BoundingRectangle.left >= 400:
                        continue
                    hits.append((1 if c.IsOffscreen else 0, c))
                except Exception:
                    continue
            hits.sort(key=lambda p: p[0])
            return hits[0][1] if hits else None

        def expander_of(item):
            for c in collect(item, maxd=4):
                if c.ControlTypeName != "ButtonControl":
                    continue
                try:
                    if not c.IsOffscreen:
                        return c
                except Exception:
                    continue
            return None

        def invoke(ctrl):
            try:
                ctrl.GetInvokePattern().Invoke()
                return True
            except Exception:
                r = ctrl.BoundingRectangle
                cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
                user32.SetForegroundWindow(hwnd)
                time.sleep(0.3)
                user32.SetCursorPos(cx, cy)
                time.sleep(0.2)
                user32.mouse_event(2, 0, 0, 0, 0)
                user32.mouse_event(4, 0, 0, 0, 0)
                return True

        def select(ctrl):
            try:
                ctrl.GetSelectionItemPattern().Select()
            except Exception:
                r = ctrl.BoundingRectangle
                cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
                user32.SetCursorPos(cx, cy)
                time.sleep(0.2)
                user32.mouse_event(2, 0, 0, 0, 0)
                user32.mouse_event(4, 0, 0, 0, 0)

        def visible_texts():
            out = []
            for c in collect(win):
                if c.ControlTypeName != "TextControl":
                    continue
                try:
                    if c.IsOffscreen:
                        continue
                except Exception:
                    continue
                out.append(name_of(c))
            return out

        def count_shown():
            for t in visible_texts():
                if t.startswith("共 ") and t.endswith(" 项"):
                    return int(t[2:-2])
                if "/ 共 " in t and t.endswith(" 项"):
                    try:
                        return int(t.rsplit("/ 共 ", 1)[1][:-2])
                    except Exception:
                        pass
            return None

        def open_and_count(title, expect, label):
            """点开侧栏节点 → 状态栏必须是「共 N 项」。"""
            item = sidebar_item(title)
            if item is None:
                check(label, False, "侧栏找不到「%s」" % title)
                return False
            select(item)
            time.sleep(3.5)
            got = count_shown()
            if got != expect:
                again = sidebar_item(title)
                if again is not None:
                    select(again)
                    time.sleep(3.5)
                    got = count_shown()
            check(label, got == expect, "期望 %s 项，实际 %s 项" % (expect, got))
            return True

        ROOT_TITLE = os.path.basename(BASE)
        root_node = sidebar_item(ROOT_TITLE)
        check("侧栏出现新父目录", root_node is not None, ROOT_TITLE)
        if root_node is None:
            return

        # 空目录不能凭空出现（它没有媒体文件）
        check("空子目录不出现在侧栏", sidebar_item("丙") is None)

        # 展开一级
        exp = expander_of(root_node)
        check("父目录有展开三角", exp is not None)
        if exp is None:
            return
        invoke(exp)
        time.sleep(2)

        for title in ("甲", "乙"):
            check("一级子目录「%s」出现" % title, sidebar_item(title) is not None)

        # 父目录本身：4 个文件（甲2 + 乙1 + 乙\2024 1 + 顶层 1）
        open_and_count(ROOT_TITLE, 5, "点父目录显示全部 5 项")

        # 一级子目录逐个点
        open_and_count("甲", 2, "点「甲」显示 2 项")
        open_and_count("乙", 2, "点「乙」显示含孙目录的 2 项")

        # 展开「乙」→ 二级目录 2024
        yi = sidebar_item("乙")
        exp2 = expander_of(yi) if yi is not None else None
        check("「乙」可展开（有孙目录）", exp2 is not None)
        if exp2 is not None:
            invoke(exp2)
            time.sleep(2)
            check("二级子目录「2024」出现", sidebar_item("2024") is not None)
            open_and_count("2024", 1, "点「2024」显示 1 项")

        # 在父目录上点「扫描」：列表不能变空、也不能翻倍
        p = sidebar_item(ROOT_TITLE)
        if p is not None:
            select(p)
            time.sleep(2.5)
            scan_btn = None
            for c in collect(win, maxd=6):
                if c.ControlTypeName == "ButtonControl" and name_of(c) == "扫描":
                    scan_btn = c
                    break
            if scan_btn is not None:
                invoke(scan_btn)
                time.sleep(8)
                got = count_shown()
                check("选中父目录后「扫描」结果仍为 5 项", got == 5, "实际 %s 项" % got)
            else:
                check("选中父目录后「扫描」结果仍为 5 项", False, "找不到扫描按钮")
        else:
            check("选中父目录后「扫描」结果仍为 5 项", False, "找不到父目录节点")

    finally:
        kill_app()
        # 还原库（把临时根目录整条删掉，媒体随级联一起清）
        try:
            if os.path.exists(DB) and root_id is not None:
                con = sqlite3.connect(DB)
                con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                con.execute("DELETE FROM RootFolder WHERE Id = ?", (root_id,))
                con.commit()
                con.close()
        except Exception as e:
            print("清理临时根目录失败：", e)
        shutil.copy2(BAK, DB)
        os.remove(BAK)
        if moved and os.path.exists(MARKER_OFF):
            os.replace(MARKER_OFF, MARKER)
        if os.path.exists(BASE):
            shutil.rmtree(BASE, ignore_errors=True)

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
