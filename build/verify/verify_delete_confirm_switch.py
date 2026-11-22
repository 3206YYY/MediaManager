#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""设置页「永久删除提醒确认」开关的实机验证。

背景（用户 2026-11-22 要求）：
  删除行为选「永久删除」后，每删一张都要弹一次确认框，批量清图时很烦。
  于是加了这一项：默认**开启**（保留确认），用户主动关掉后永久删除直接执行。
  危险就在这儿 —— 开关关掉之后，界面上任何「每次都会确认」的话都成了假话，
  而且删了是真的没了。所以这个脚本盯两头：库里的值、删的时候到底问不问。

检查：
  A. 从没写过这个键的库 → 设置页开关存在、勾着、可点（默认开启）
  B. 取消勾选 → 保存 → 库里 DeleteConfirmPermanent = 0；重开设置仍是关的
  C. 删除行为切到「回收站 / 每次询问」→ 开关灰掉（不生效的档位不许假装能关）；
     切回「永久删除」→ 恢复可点；点取消 → 库里的删除行为没被改
  D. 主窗口：删除模式 + 取消「先勾选」→ 点一个文件**不弹确认框**，
     文件直接从磁盘消失（证明 0 真的生效，不是界面自己在演）
  E. 重新勾上并保存 → 点文件**先弹确认框**，点取消文件一个不少

只碰便携开发库（exe 旁的 Data/library.db），文件都是临时目录里现造的 5 个 PNG，
跑完还原本库。
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
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".delete_confirm_bak"
EXE_NAME = os.path.basename(APP)

BASE = os.path.join(tempfile.gettempdir(), "MM_永久删除确认验证")
N = 5
KEY = "DeleteConfirmPermanent"

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


# ---------------- 基础工具 ----------------

def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", EXE_NAME], capture_output=True)
    time.sleep(1.2)


def launch():
    env = dict(os.environ)
    env.pop("MM_SHORTCUT_USE_SELF", None)
    return subprocess.Popen([APP], cwd=os.path.dirname(APP), env=env)


def wait_window(regex=r"^媒体管理器", depth=1, timeout=35):
    end = time.time() + timeout
    while time.time() < end:
        w = uia.WindowControl(searchDepth=depth, RegexName=regex)
        if w.Exists(0, 0):
            return w
        time.sleep(0.5)
    return None


def walk(node, d=0, acc=None, maxd=18):
    acc = acc if acc is not None else []
    if d > maxd:
        return acc
    try:
        kids = node.GetChildren()
    except Exception:
        return acc
    for ch in kids:
        acc.append(ch)
        walk(ch, d + 1, acc, maxd)
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


def is_enabled(c):
    try:
        return bool(c.IsEnabled)
    except Exception:
        return False


def wait_button(pred, timeout=20, root=None):
    end = time.time() + timeout
    while time.time() < end:
        for c in walk(root):
            try:
                if c.ControlTypeName == "ButtonControl" and pred(c.Name or ""):
                    return c
            except Exception:
                pass
        time.sleep(0.5)
    return None


def find_ctrl(root, kind, pred, maxd=18):
    for c in walk(root, maxd=maxd):
        if c.ControlTypeName != kind:
            continue
        try:
            if pred(name_of(c)):
                return c
        except Exception:
            continue
    return None


def bring_front(hwnd):
    for _ in range(3):
        try:
            user32.SetForegroundWindow(hwnd)
        except Exception:
            pass
        time.sleep(0.35)
        try:
            if user32.GetForegroundWindow() == hwnd:
                return True
        except Exception:
            pass
    return False


def native_click(hwnd, ctrl):
    """真实鼠标点击：删除模式挂在 PreviewMouseLeftButtonDown 上，程序化 Invoke 测不出来。"""
    try:
        r = ctrl.BoundingRectangle
        cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        bring_front(hwnd)
        user32.SetCursorPos(cx, cy)
        time.sleep(0.25)
        user32.mouse_event(2, 0, 0, 0, 0)
        user32.mouse_event(4, 0, 0, 0, 0)
        return True
    except Exception:
        return False


def invoke(hwnd, ctrl):
    try:
        ctrl.GetInvokePattern().Invoke()
        return True
    except Exception:
        return native_click(hwnd, ctrl)


def find_popup_window(title):
    """按标题找可见顶层窗口（自定义消息框都是独立 Window，没有 Owner）。"""
    res = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if b.value.strip() == title.strip():
                    res.append(h)
        return True

    user32.EnumWindows(cb, 0)
    return res


def windows_starting(prefix):
    res = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if b.value.strip().startswith(prefix):
                    res.append(h)
        return True

    user32.EnumWindows(cb, 0)
    return res


def db_setting(key):
    if not os.path.exists(DB):
        return "<无库>"
    con = sqlite3.connect(DB)
    try:
        row = con.execute("SELECT Value FROM AppSetting WHERE Key=?", (key,)).fetchone()
        return row[0] if row else None
    finally:
        con.close()


def backup_db():
    if not os.path.exists(DB):
        return False
    shutil.copy2(DB, BAK)
    for suf in ("-wal", "-shm"):
        q = DB + suf
        if os.path.exists(q):
            os.remove(q)
    return True


def restore_db():
    if not os.path.exists(BAK):
        return
    for suf in ("-wal", "-shm"):
        q = DB + suf
        if os.path.exists(q):
            os.remove(q)
    shutil.copy2(BAK, DB)
    os.remove(BAK)
    print("库已还原")


# ---------------- 造数据 ----------------

def png_bytes(w=8, h=8):
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    raw = b"".join(b"\x00" + b"\x77\xaa\x55" * w for _ in range(h))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def build_files():
    if os.path.exists(BASE):
        shutil.rmtree(BASE, ignore_errors=True)
    os.makedirs(BASE)
    made = []
    for i in range(1, N + 1):
        p = os.path.join(BASE, "确认测试%d.png" % i)
        with open(p, "wb") as f:
            f.write(png_bytes())
        made.append(p)
    return made


def add_media_rows(con, root_id, files):
    now = time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())
    for p in files:
        st = os.stat(p)
        con.execute(
            "INSERT OR IGNORE INTO MediaItem "
            "(RootFolderId, Path, FileName, Extension, MediaType, SizeBytes, "
            " Favorite, Rating, IsMissing, DateAddedUtc) "
            "VALUES (?, ?, ?, ?, ?, ?, 0, 0, 0, ?)",
            (root_id, p, os.path.basename(p),
             os.path.splitext(p)[1].lstrip(".").lower(), "Image", st.st_size, now))


def seed_db(files, behavior="Permanent", confirm=None):
    """confirm=None → 故意**不写**这个键，模拟全新库（验默认开启）。"""
    con = sqlite3.connect(DB)
    con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    now = time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())
    cur = con.execute(
        "INSERT INTO RootFolder (Path, VolumeSerial, RelativePath, IsEnabled, AddedAtUtc) "
        "VALUES (?, NULL, NULL, 1, ?)", (BASE, now))
    root_id = cur.lastrowid
    add_media_rows(con, root_id, files)
    con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES ('Theme', 'Light')")
    con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES ('DeleteBehavior', ?)",
                (behavior,))
    con.execute("DELETE FROM AppSetting WHERE Key=?", (KEY,))
    if confirm is not None:
        con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES (?, ?)",
                    (KEY, confirm))
    con.commit()
    con.close()
    return root_id


def reset_files(root_id, files, behavior=None):
    """重建 5 个文件并重新登记（D 段删掉一个后再验 E 段用）。"""
    files2 = build_files()
    con = sqlite3.connect(DB)
    con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    add_media_rows(con, root_id, files2)
    if behavior:
        con.execute("INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES ('DeleteBehavior', ?)",
                    (behavior,))
    con.commit()
    con.close()
    return files2


# ---------------- 设置页操作 ----------------

def open_settings(main):
    btn = wait_button(lambda s: s.strip().endswith("设置"), timeout=25, root=main)
    if btn is None:
        return None
    invoke(main.NativeWindowHandle, btn)
    return wait_window(regex=r"^设置$", depth=2, timeout=20)


def confirm_checkbox(sw):
    return find_ctrl(sw, "CheckBoxControl", lambda s: "永久删除前先确认" in s, maxd=22)


def is_on(cb):
    try:
        return cb.GetTogglePattern().ToggleState == uia.ToggleState.On
    except Exception:
        return None


def set_checkbox(hwnd, cb, want_on):
    """真实鼠标点击，点完回读状态（TogglePattern 在某些绑定下只改视觉）。"""
    for _ in range(3):
        if is_on(cb) is want_on:
            return want_on
        bring_front(hwnd)
        try:
            r = cb.BoundingRectangle
            user32.SetCursorPos((r.left + r.right) // 2, (r.top + r.bottom) // 2)
            time.sleep(0.25)
            user32.mouse_event(2, 0, 0, 0, 0)
            user32.mouse_event(4, 0, 0, 0, 0)
        except Exception:
            try:
                cb.GetTogglePattern().Toggle()
            except Exception:
                pass
        time.sleep(1.4)
    return is_on(cb)


def delete_combo(sw):
    """按「选中项文本」认下拉，不按位置。

    WPF ComboBox 的 UIA Name 是空的（探查过），只能读 Selection——
    常规页第一个下拉是主题，按顺序取会一脚踩空。
    """
    for c in walk(sw, maxd=24):
        if c.ControlTypeName != "ComboBoxControl":
            continue
        cur = combo_current(c)
        if any(k in cur for k in ("回收站", "永久删除", "每次询问")):
            return c
    return None


def combo_current(cb):
    try:
        return " ".join(x.Name or "" for x in cb.GetSelectionPattern().GetSelection())
    except Exception:
        return ""


def combo_shows(cb, text):
    return text in combo_current(cb)


def combo_pick(hwnd, cb, target_text):
    """展开 → 点中那一项（ListItemControl 展开后挂在 ComboBox 自己下面）。"""
    if combo_shows(cb, target_text):
        return True
    bring_front(hwnd)
    try:
        cb.GetExpandCollapsePattern().Expand()
    except Exception:
        return False
    time.sleep(1.1)

    for it in walk(cb, maxd=6):
        if it.ControlTypeName == "ListItemControl" and target_text in name_of(it):
            try:
                it.GetSelectionItemPattern().Select()
                time.sleep(1.1)
                try:
                    cb.GetExpandCollapsePattern().Collapse()
                except Exception:
                    pass
                return combo_shows(cb, target_text)
            except Exception:
                break

    try:
        cb.GetExpandCollapsePattern().Collapse()
    except Exception:
        pass
    return False


def press_save(hwnd, sw):
    btn = wait_button(lambda s: s.strip() == "保存", timeout=15, root=sw)
    if btn is None:
        return False
    invoke(hwnd, btn)
    time.sleep(2.5)
    # 保存成功会弹一个「保存成功」消息框，点掉它
    for _ in range(10):
        pop = find_popup_window("保存成功")
        if not pop:
            time.sleep(0.5)
            continue
        pwin = uia.ControlFromHandle(pop[0])
        ok = wait_button(lambda s: s.strip() in ("知道了", "确定", "好"), timeout=8, root=pwin)
        if ok is not None:
            invoke(pop[0], ok)
        time.sleep(1.5)
        break
    return True


def press_cancel(hwnd, sw):
    btn = wait_button(lambda s: s.strip() == "取消", timeout=15, root=sw)
    if btn is None:
        return False
    invoke(hwnd, btn)
    time.sleep(2.2)
    return True


def settings_closed():
    return not uia.WindowControl(searchDepth=2, RegexName=r"^设置$").Exists(0, 0)


# ---------------- 主流程 ----------------

def main():
    if not os.path.exists(DB):
        print("找不到便携开发库：", DB)
        return 1

    kill_app()
    had_db = backup_db()
    root_id = None
    try:
        files = build_files()
        root_id = seed_db(files)          # 故意不写 KEY，验默认值
        launch()
        main_win = wait_window()
        check("主窗口启动", main_win is not None)
        if main_win is None:
            return 1
        time.sleep(5)
        mhwnd = main_win.NativeWindowHandle

        # 点开临时文件夹
        folder = find_ctrl(main_win, "ListItemControl",
                           lambda s: s == os.path.basename(BASE), maxd=14)
        if folder is not None:
            try:
                folder.GetSelectionItemPattern().Select()
            except Exception:
                native_click(mhwnd, folder)
            time.sleep(3)

        # ---------- A 默认开启 ----------
        sw = open_settings(main_win)
        check("设置窗口已打开", sw is not None)
        if sw is None:
            return 1
        shwnd = sw.NativeWindowHandle

        cb = confirm_checkbox(sw)
        check("设置页有「永久删除前先确认」开关", cb is not None)
        if cb is None:
            return 1
        check("库里从没写过这个键（老库/新库）", db_setting(KEY) is None,
              "实际 %r" % db_setting(KEY))
        check("默认是勾上的（不会一上来就静默删除）", is_on(cb) is True,
              "ToggleState=%r" % (is_on(cb),))

        combo = delete_combo(sw)
        check("找到删除行为下拉", combo is not None)
        check("下拉当前是「永久删除」", combo is not None and combo_shows(combo, "永久删除"),
              combo_current(combo) if combo else "")
        check("永久删除档位下开关可点", is_enabled(cb),
              "IsEnabled=%s" % is_enabled(cb))

        # ---------- B 关掉 → 保存 → 写库 ----------
        check("能把开关关掉", set_checkbox(shwnd, cb, False) is False,
              "ToggleState=%r" % (is_on(cb),))
        hint_text = " / ".join(name_of(c) for c in walk(sw)
                               if c.ControlTypeName == "TextControl" and not is_offscreen(c))
        check("关掉后提示改成「直接执行、不再问你」",
              "直接执行" in hint_text or "不再问你" in hint_text, hint_text[-60:])

        check("点「保存」", press_save(shwnd, sw))
        check("保存后库里的值 = 0", db_setting(KEY) == "0", "实际 %r" % db_setting(KEY))
        check("设置窗口已关闭", settings_closed())

        # ---------- C 重开：状态一致 + 不生效档位灰掉 ----------
        sw = open_settings(main_win)
        check("设置窗口能重新打开", sw is not None)
        if sw is None:
            return 1
        shwnd = sw.NativeWindowHandle
        cb = confirm_checkbox(sw)
        check("重开后开关仍是关的（保存真的生效）", is_on(cb) is False,
              "ToggleState=%r" % (is_on(cb),))

        combo = delete_combo(sw)
        if combo is not None:
            ok = combo_pick(shwnd, combo, "回收站")
            check("能把删除行为切到「回收站」", ok, combo_current(combo))
            time.sleep(1.2)
            check("回收站档位下开关灰掉（那档本来就会问，不许假装能关）",
                  not is_enabled(cb), "IsEnabled=%s" % is_enabled(cb))

            ok = combo_pick(shwnd, combo, "每次询问")
            check("能切到「每次询问」", ok, combo_current(combo))
            time.sleep(1.2)
            check("每次询问档位下开关也灰掉", not is_enabled(cb),
                  "IsEnabled=%s" % is_enabled(cb))

            combo_pick(shwnd, combo, "永久删除")
            time.sleep(1.2)
            check("切回「永久删除」后开关恢复可点", is_enabled(cb),
                  "IsEnabled=%s" % is_enabled(cb))

        check("点「取消」关掉设置", press_cancel(shwnd, sw))
        check("取消后删除行为没被改（仍是 Permanent）",
              db_setting("DeleteBehavior") == "Permanent", "实际 %r" % db_setting("DeleteBehavior"))
        check("取消后开关的值也没被改（仍是 0）", db_setting(KEY) == "0",
              "实际 %r" % db_setting(KEY))

        # ---------- D 关掉提醒后：点删不弹框、文件真的没了 ----------
        def grid_items():
            named = [c for c in walk(main_win, maxd=12)
                     if c.ControlTypeName == "ListItemControl"
                     and name_of(c).startswith("确认测试")
                     and not is_offscreen(c)]
            return named[:N]

        b = find_ctrl(main_win, "ButtonControl",
                      lambda s: s in ("批量删除", "退出删除模式"), maxd=14)
        check("主窗口有「批量删除」按钮", b is not None)
        if b is not None:
            invoke(mhwnd, b)
            time.sleep(1.3)

        mark = find_ctrl(main_win, "CheckBoxControl", lambda s: "先勾选" in s, maxd=16)
        if mark is not None:
            try:
                if mark.GetTogglePattern().ToggleState == uia.ToggleState.On:
                    mark.GetTogglePattern().Toggle()
            except Exception:
                native_click(mhwnd, mark)
            time.sleep(1)

        items = grid_items()
        check("网格里有可点的项", len(items) > 0, "%d 项" % len(items))
        before = sum(1 for p in files if os.path.exists(p))
        if items:
            native_click(mhwnd, items[0])
            time.sleep(2.5)

        dialogs = windows_starting("确认删除")
        check("关掉提醒后点删**不再弹确认框**", len(dialogs) == 0,
              "居然弹了 %d 个窗口" % len(dialogs))
        after = sum(1 for p in files if os.path.exists(p))
        check("文件真的被永久删除（不是界面在演）", after == before - 1,
              "%d → %d" % (before, after))

        # ---------- E 勾回去：又要确认 ----------
        sw = open_settings(main_win)
        check("设置窗口再打开", sw is not None)
        if sw is None:
            return 1
        shwnd = sw.NativeWindowHandle
        cb = confirm_checkbox(sw)
        check("能把开关重新勾上", set_checkbox(shwnd, cb, True) is True,
              "ToggleState=%r" % (is_on(cb),))
        check("点「保存」", press_save(shwnd, sw))
        check("库里的值回到 1", db_setting(KEY) == "1", "实际 %r" % db_setting(KEY))

        files = reset_files(root_id, files)
        time.sleep(1.5)
        # 刷新文件夹视图（重新点一下侧栏目录）
        folder = find_ctrl(main_win, "ListItemControl",
                           lambda s: s == os.path.basename(BASE), maxd=14)
        if folder is not None:
            try:
                folder.GetSelectionItemPattern().Select()
            except Exception:
                native_click(mhwnd, folder)
            time.sleep(3)

        items = grid_items()
        check("重建后网格里又有项", len(items) > 0, "%d 项" % len(items))
        before = sum(1 for p in files if os.path.exists(p))
        if items:
            native_click(mhwnd, items[0])
            time.sleep(2.5)

        dialogs = windows_starting("确认删除")
        check("勾回去之后点删**又先弹确认框**", len(dialogs) > 0)
        if dialogs:
            dwin = uia.ControlFromHandle(dialogs[0])
            dtext = "\n".join(name_of(c) for c in walk(dwin)
                              if c.ControlTypeName == "TextControl")
            check("确认框写明「永久删除 / 无法恢复」",
                  "永久删除" in dtext and "无法恢复" in dtext, dtext[:60])
            cancel = find_ctrl(dwin, "ButtonControl", lambda s: s == "取消")
            if cancel is not None:
                invoke(dialogs[0], cancel)
                time.sleep(1.5)
        after = sum(1 for p in files if os.path.exists(p))
        check("点取消后文件一个不少", after == before, "%d → %d" % (before, after))

    finally:
        kill_app()
        if had_db:
            restore_db()

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
