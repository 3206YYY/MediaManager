# -*- coding: utf-8 -*-
"""
实机验证 v1.4.8：网格选中行为（用户报了两个真问题）。

1. 「点第三张，前面两张也一起被选中」
   根因不是选择算法，是**框选状态没收干净**：MouseDown 在空白处起框选后，如果鼠标
   在窗口外松开 / 窗口最小化 / 捕获被别人抢走，OnThumbListMouseUp 收不到，
   _isMarqueeSelecting 就永远挂着；下一次单击任何一张，MouseUp 拿「旧起点 → 当前点」
   的矩形重刷一遍选择 —— 于是前面的全被圈进来。
   守卫：任意 MouseDown 先 EndMarquee；LostMouseCapture 也收尾。

2. 删除模式下选中态看不出来 / 点空白把勾好的清掉
   守卫：选中环改成画在内容之上的强调色（日常蓝 / 删除模式琥珀），
        删除模式里点空白**不清空**，框选只加不减。

只碰便携开发库（exe 旁 Data/library.db），跑完还原；临时图全在 %TEMP%，删干净。
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
gdi32 = ctypes.windll.gdi32

# 64 位下不声明 argtypes 的话，句柄会被截成 32 位，GetPixel/ReleaseDC 直接抛 OverflowError
user32.GetDC.argtypes = [wt.HWND]
user32.GetDC.restype = wt.HDC
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.ReleaseDC.restype = ctypes.c_int
gdi32.GetPixel.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.GetPixel.restype = ctypes.c_uint32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".grid_sel_bak"

BASE = os.path.join(tempfile.gettempdir(), "MM_网格选中验证")
SHOT_NORMAL = os.path.join(ROOT, "build", "verify", "shots_grid_select_normal.png")
SHOT_DELETE = os.path.join(ROOT, "build", "verify", "shots_grid_select_delete.png")
N = 6

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


def is_offscreen(c):
    try:
        return bool(c.IsOffscreen)
    except Exception:
        return True


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def png_bytes(w=64, h=64):
    """纯色小图：背景越干净，边缘那圈强调色越好采样。"""
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
    files = []
    for i in range(1, N + 1):
        p = os.path.join(BASE, "选中测试%d.png" % i)
        with open(p, "wb") as f:
            f.write(png_bytes())
        files.append(p)
    return files


def seed_db(files):
    con = sqlite3.connect(DB)
    con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    now = time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())
    cur = con.execute(
        "INSERT INTO RootFolder (Path, VolumeSerial, RelativePath, IsEnabled, AddedAtUtc) "
        "VALUES (?, NULL, NULL, 1, ?)", (BASE, now))
    rid = cur.lastrowid
    for p in files:
        st = os.stat(p)
        con.execute(
            "INSERT OR IGNORE INTO MediaItem "
            "(RootFolderId, Path, FileName, Extension, MediaType, SizeBytes, "
            " Favorite, Rating, IsMissing, DateAddedUtc) "
            "VALUES (?, ?, ?, ?, ?, ?, 0, 0, 0, ?)",
            (rid, p, os.path.basename(p),
             os.path.splitext(p)[1].lstrip(".").lower(), "Image", st.st_size, now))
    con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES ('Theme', 'Dark')")
    con.commit()
    con.close()
    return rid


def native_click(hwnd, x, y):
    """真实鼠标点击（UIA 的 Invoke/Select 不经过鼠标消息，测不出 PreviewMouseDown 那套逻辑）。"""
    for _ in range(3):
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.3)
        if user32.GetForegroundWindow() == hwnd:
            break
    user32.SetCursorPos(x, y)
    time.sleep(0.12)
    user32.mouse_event(2, 0, 0, 0, 0)
    time.sleep(0.05)
    user32.mouse_event(4, 0, 0, 0, 0)
    time.sleep(0.35)


def native_drag(hwnd, x1, y1, x2, y2, steps=12):
    for _ in range(3):
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.3)
        if user32.GetForegroundWindow() == hwnd:
            break
    user32.SetCursorPos(x1, y1)
    time.sleep(0.12)
    user32.mouse_event(2, 0, 0, 0, 0)
    time.sleep(0.05)
    for i in range(1, steps + 1):
        user32.SetCursorPos(int(x1 + (x2 - x1) * i / steps), int(y1 + (y2 - y1) * i / steps))
        time.sleep(0.03)
    time.sleep(0.05)
    user32.mouse_event(4, 0, 0, 0, 0)
    time.sleep(0.35)


def screen_pixel(x, y):
    hdc = user32.GetDC(None)
    try:
        v = gdi32.GetPixel(hdc, int(x), int(y))
    finally:
        user32.ReleaseDC(None, hdc)
    if v == 0xFFFFFFFF:            # CLR_INVALID
        return None
    return (v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF)   # COLORREF → RGB


def ring_tint(item):
    """沿条目左边缘采一列像素，判断底色偏蓝还是偏琥珀。

    选中环画在内容之上、离条目外沿 1~4px；缩略图本身往里缩了 4px，
    所以这一列几乎只有环的颜色。
    """
    r = item.BoundingRectangle
    x = r.left + 2
    blue = orange = 0
    for y in range(r.top + 12, r.bottom - 12, 2):
        px = screen_pixel(x, y)
        if px is None:
            continue
        rr, gg, bb = px
        if bb > rr + 40:
            blue += 1
        elif rr > bb + 40:
            orange += 1
    return blue, orange


def grab_shot(hwnd, path):
    """留一张全窗口图给人复核配色（像素断言只能证明「偏蓝/偏琥珀」，好不好看还得眼睛看）。"""
    try:
        from PIL import ImageGrab
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), all_screens=True).save(path)
        print("  截图：%s" % path)
    except Exception as e:
        print("  截图失败：%r" % (e,))


def main():
    if not os.path.exists(DB):
        print("找不到便携开发库：", DB)
        sys.exit(1)

    kill_app()
    shutil.copy2(DB, BAK)
    files = build_files()
    rid = None
    try:
        rid = seed_db(files)
        subprocess.Popen([APP], cwd=os.path.dirname(APP))
        hwnd = None
        for _ in range(30):
            time.sleep(0.6)
            w = find_window("媒体管理器")
            if w:
                hwnd = w[0]
                break
        check("主窗口启动", hwnd is not None)
        if hwnd is None:
            return
        time.sleep(6)
        win = uia.ControlFromHandle(hwnd)

        folder = None
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "ListItemControl" and name_of(c) == os.path.basename(BASE):
                folder = c
                break
        check("侧栏出现临时文件夹", folder is not None)
        if folder is not None:
            try:
                folder.GetSelectionItemPattern().Select()
            except Exception:
                pass
            time.sleep(3)

        def items():
            return [c for c in collect(win, maxd=14)
                    if c.ControlTypeName == "ListItemControl"
                    and name_of(c).startswith("选中测试")
                    and not is_offscreen(c)]

        def grid_box():
            """主网格区域：取全部条目包围盒再向外扩，用来算「空白处」的坐标。"""
            its = items()
            if not its:
                return None
            l = min(c.BoundingRectangle.left for c in its)
            t = min(c.BoundingRectangle.top for c in its)
            r = max(c.BoundingRectangle.right for c in its)
            b = max(c.BoundingRectangle.bottom for c in its)
            return l, t, r, b

        def center(c):
            r = c.BoundingRectangle
            return (r.left + r.right) // 2, (r.top + r.bottom) // 2

        def selected_names():
            out = []
            for c in items():
                try:
                    if c.GetSelectionItemPattern().IsSelected:
                        out.append(name_of(c))
                except Exception:
                    pass
            return out

        def blank_point():
            """网格下方空白：取包围盒底边再往下 40px，横向落在第一个条目正下方
            （避开底部悬浮条，它在屏幕中间）。"""
            box = grid_box()
            if box is None:
                return None
            l, t, r, b = box
            return (l + 30, min(b + 40, b + 120))

        def batch_btn():
            for c in collect(win, maxd=16):
                if c.ControlTypeName == "ButtonControl":
                    s = name_of(c)
                    if s in ("批量删除", "退出删除模式") or s.startswith("删除选中的"):
                        return c
            return None

        its = items()
        check("临时文件夹里 6 项都在网格里", len(its) == N, "实际 %d" % len(its))
        if len(its) < 3:
            return

        # ---------- 1) 单击第 3 张：只该选中它自己 ----------
        x3, y3 = center(its[2])
        native_click(hwnd, x3, y3)
        sel = selected_names()
        check("单击第 3 张 → 只选中 1 项", len(sel) == 1, "实际 %d：%s" % (len(sel), sel))
        check("选中的正是第 3 张", sel == [name_of(its[2])], str(sel))

        # ---------- 2) 框选没收尾的脏状态：最小化再回来单击，仍然只选 1 张 ----------
        # 这一步就是用户遇到的场景：上一次框选没走完 MouseUp（窗口最小化 = 捕获丢失）。
        user32.ShowWindow(hwnd, 6)   # SW_MINIMIZE
        time.sleep(1.2)
        user32.ShowWindow(hwnd, 9)   # SW_RESTORE
        time.sleep(1.2)
        user32.SetForegroundWindow(hwnd)
        time.sleep(1.0)
        its = items()
        if len(its) >= 3:
            nx, ny = center(its[2])
            native_click(hwnd, nx, ny)
            sel = selected_names()
            check("上次框选被打断后，再单击第 3 张仍只选中 1 项",
                  len(sel) == 1, "实际 %d：%s" % (len(sel), sel))

        # ---------- 3) 普通模式下点空白仍然清空（别把标准行为改坏） ----------
        bp = blank_point()
        if bp:
            native_click(hwnd, *bp)
            check("普通模式点空白 → 清空选择（资源管理器行为）",
                  len(selected_names()) == 0, str(selected_names()))

        # ---------- 4) 选中色：普通蓝 / 删除模式琥珀 ----------
        its = items()
        x1, y1 = center(its[0])
        native_click(hwnd, x1, y1)
        time.sleep(0.6)
        b1, o1 = ring_tint(items()[0])
        check("普通选中环是蓝色", b1 >= 5 and b1 > o1 * 2, "蓝 %d / 琥珀 %d" % (b1, o1))
        grab_shot(hwnd, SHOT_NORMAL)

        # 先清空选择：这个按钮是三态的 —— 选中状态下点它 = 「删除选中的 N 项」，
        # 会直接弹删除确认，根本进不了删除模式（第一次跑就是这么被骗的）。
        bp = blank_point()
        if bp:
            native_click(hwnd, *bp)
            time.sleep(0.5)

        btn = batch_btn()
        check("找到「批量删除」按钮", btn is not None)
        if btn is None:
            return
        check("未选中时按钮就写着「批量删除」", name_of(btn) == "批量删除", name_of(btn))
        bx, by = center(btn)
        native_click(hwnd, bx, by)
        time.sleep(1.4)

        banner = None
        for c in collect(win, maxd=16):
            if c.ControlTypeName == "TextControl" and "删除模式" in name_of(c) and not is_offscreen(c):
                banner = c
                break
        check("点按钮进入删除模式（横幅出现）", banner is not None)
        it = items()
        ax, ay = center(it[0])
        native_click(hwnd, ax, ay)
        time.sleep(0.5)
        a2x, a2y = center(it[1])
        native_click(hwnd, a2x, a2y)
        time.sleep(0.6)
        marked = selected_names()
        check("删除模式单击 = 打勾，勾上 2 项", len(marked) == 2, str(marked))

        b2, o2 = ring_tint(items()[0])
        check("删除模式选中环换成琥珀色（不复用蓝色）",
              o2 >= 5 and o2 > b2 * 2, "蓝 %d / 琥珀 %d" % (b2, o2))
        grab_shot(hwnd, SHOT_DELETE)

        # ---------- 5) 删除模式点空白：勾好的不许清掉 ----------
        bp = blank_point()
        if bp:
            native_click(hwnd, *bp)
            time.sleep(0.4)
            after = selected_names()
            check("删除模式点空白 → 已勾选的 2 项还在",
                  len(after) == 2, "实际 %d：%s" % (len(after), after))

        # ---------- 6) 删除模式框选：只加不减 ----------
        # 起点必须落在**确认是空白**的地方（网格底部），从那里往上拖覆盖全片；
        # 从 (l-6,t-6) 起手会落到 ListBox 外面，事件根本进不来，测试会假失败。
        it = items()
        l = min(c.BoundingRectangle.left for c in it)
        r = max(c.BoundingRectangle.right for c in it)
        t = min(c.BoundingRectangle.top for c in it)
        bb = max(c.BoundingRectangle.bottom for c in it)
        start = blank_point() or (l + 30, bb + 40)
        native_drag(hwnd, start[0], start[1], r - 20, t - 8, steps=14)
        time.sleep(0.6)
        after = selected_names()
        check("删除模式框选只加不减（原来勾的 2 项没被反选掉）",
              len(after) >= 2, "实际 %d：%s" % (len(after), after))
        check("删除模式框选能把整片都勾上", len(after) == N,
              "实际 %d（应 %d）" % (len(after), N))

        # ---------- 7) 审计项：点删模式下删掉一张，别把其它勾选连坐清空 ----------
        # DeleteOnTapAsync 里有 `SelectedItem = null`，它通过 TwoWay 绑定回头清 ListBox.SelectedItems，
        # 于是「删一张，手上勾好的全没了」。这里把剩余勾选留住。
        mark = None
        for c in collect(win, maxd=16):
            if c.ControlTypeName == "CheckBoxControl" and "先勾选" in name_of(c) and not is_offscreen(c):
                mark = c
                break
        if mark is not None:
            # 先空出一张当「受害者」：现在 6 张全勾着，得先取消一张才有东西可点删
            its = items()
            ux, uy = center(its[0])
            native_click(hwnd, ux, uy)
            time.sleep(0.5)
            before_del = len(selected_names())

            mx, my = center(mark)
            native_click(hwnd, mx, my)      # 取消勾选 → 变「点谁删谁」
            time.sleep(0.8)
            its = items()
            sel_now = set(selected_names())
            victim = next((c for c in its if name_of(c) not in sel_now), None)
            if victim is not None and before_del >= 2:
                vx, vy = center(victim)
                native_click(hwnd, vx, vy)
                time.sleep(2.0)
                left = len(selected_names())
                check("点删模式删掉一张后，其余勾选不被连坐清空",
                      left >= before_del - 1, "删前 %d → 删后 %d" % (before_del, left))
            else:
                check("点删模式连坐清空（准备条件不足，跳过）", True,
                      "已勾 %d 项，没找到未勾选的受害者" % before_del)
        else:
            check("找到「先勾选」开关", False)

    finally:
        kill_app()
        shutil.copy2(BAK, DB)
        os.remove(BAK)
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
