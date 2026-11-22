#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""设置页「取消」必须把「点击即写」的开关退回去。

背景（用户 2026-11-22 指出）：
  · 右键集成点击就写注册表、扩展格式点击就写库，而「取消」原来只是 Close()；
  · 后来补了回滚，但 OnCancel 回滚一次、Close() 触发 OnClosing 又回滚一次，
    扩展格式那条等于连写两遍库，而且写库是 `_ = ...` 发射后不管，失败也没人知道；
  · CheckBox 也不复位，界面状态和「什么都没改」自相矛盾。

现在：OnCancel 只 Close，回滚统一在 OnClosing 做一次；写库走线程池 + 有界等待。

检查：
  A. 设置页能打开，插件扩展页的「启用扩展格式」默认勾选
  B. 取消勾选 → 立刻写库（EnableExtendedFormats = 0，这是设计如此）
  C. 点「取消」→ 库里的值退回 1
  D. 重开设置 → 复选框又是勾上的（界面和库一致）
  E. 改成取消勾选后直接点标题栏 × 关闭 → 同样退回 1（× 不是后门）

只碰便携开发库（exe 旁的 Data/library.db），跑完还原。
"""
import ctypes
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
BAK = DB + ".settings_cancel_bak"
EXE_NAME = os.path.basename(APP)

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


def db_setting(key):
    if not os.path.exists(DB):
        return None
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


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", EXE_NAME], capture_output=True)
    time.sleep(1.2)


def launch():
    env = dict(os.environ)
    env.pop("MM_SHORTCUT_USE_SELF", None)
    return subprocess.Popen([APP], cwd=os.path.dirname(APP), env=env)


def wait_window(regex=r"^媒体管理器", depth=1, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        w = uia.WindowControl(searchDepth=depth, RegexName=regex)
        if w.Exists(0, 0):
            return w
        time.sleep(0.5)
    return None


def walk_controls(node, d=0, acc=None, maxd=18):
    acc = acc if acc is not None else []
    if d > maxd:
        return acc
    try:
        kids = node.GetChildren()
    except Exception:
        return acc
    for ch in kids:
        acc.append(ch)
        walk_controls(ch, d + 1, acc, maxd)
    return acc


def wait_button(pred, timeout=20, root=None):
    end = time.time() + timeout
    while time.time() < end:
        for c in walk_controls(root):
            try:
                if c.ControlTypeName == "ButtonControl" and pred(c.Name or ""):
                    return c
            except Exception:
                pass
        time.sleep(0.5)
    return None


def open_settings(main):
    btn = wait_button(lambda s: s.strip().endswith("设置"), timeout=25, root=main)
    if btn is None:
        return None
    try:
        btn.GetInvokePattern().Invoke()
    except Exception:
        r = btn.BoundingRectangle
        uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)
    return wait_window(regex=r"^设置$", depth=2, timeout=20)


def goto_plugins(sw):
    """左侧导航 → 插件扩展（扩展格式开关在这一页）。"""
    end = time.time() + 15
    while time.time() < end:
        for c in walk_controls(sw):
            try:
                if c.ControlTypeName == "ListItemControl" and "插件扩展" in (c.Name or ""):
                    try:
                        c.GetSelectionItemPattern().Select()
                    except Exception:
                        c.Click()
                    time.sleep(1.2)
                    return True
            except Exception:
                pass
        time.sleep(0.4)
    return False


def ext_checkbox(sw):
    for c in walk_controls(sw, maxd=22):
        try:
            if c.ControlTypeName == "CheckBoxControl" and "扩展格式" in (c.Name or ""):
                return c
        except Exception:
            pass
    return None


def is_on(cb):
    try:
        return cb.GetTogglePattern().ToggleState == uia.ToggleState.On
    except Exception:
        return None


def set_ext(sw, cb, want_on):
    """把扩展格式开关切到 want_on，返回切换后的实际状态。

    ⚠ 必须用**真实鼠标点击**，不能用 TogglePattern：
    这个开关挂的是 WPF 的 Click 事件（不是 Checked/Unchecked），
    UIA 的 Toggle() 只改 IsChecked、不触发 Click —— 它自己也不写库，
    结果「状态变了但库里没变」，看起来像 bug，其实是驱动方式不对。
    （程序里故意用 Click 而不是 Checked：Checked 在打开设置页回填状态时也会触发，
      那会一进页面就写库。）
    """
    for _ in range(3):
        try:
            user32.SetForegroundWindow(sw.NativeWindowHandle)
            time.sleep(0.5)
        except Exception:
            pass
        if is_on(cb) is want_on:
            return want_on
        try:
            r = cb.BoundingRectangle
            user32.SetCursorPos((r.left + r.right) // 2, (r.top + r.bottom) // 2)
            time.sleep(0.25)
            user32.mouse_event(2, 0, 0, 0, 0)   # LEFTDOWN
            user32.mouse_event(4, 0, 0, 0, 0)   # LEFTUP
        except Exception:
            try:
                cb.GetTogglePattern().Toggle()
            except Exception:
                pass
        time.sleep(1.6)
        if is_on(cb) is want_on:
            return want_on
    return is_on(cb)


def main():
    had_db = backup_db()
    kill_app()
    try:
        launch()
        main_win = wait_window()
        check("主窗口启动", main_win is not None)
        if main_win is None:
            return 1

        sw = open_settings(main_win)
        check("设置窗口已打开", sw is not None)
        if sw is None:
            return 1

        check("导航里有「插件扩展」", goto_plugins(sw))
        cb = ext_checkbox(sw)
        check("找到「启用扩展格式」开关", cb is not None)
        if cb is None:
            return 1
        check("默认是勾上的", is_on(cb) is True, "ToggleState=%r" % (is_on(cb),))

        before = db_setting("EnableExtendedFormats")
        print("点击前库里的值：", before)

        # ---- B 点击即写（这是设计：改了马上生效） ----
        check("能把开关切到「关」", set_ext(sw, cb, False) is False,
              "ToggleState=%r" % (is_on(cb),))
        time.sleep(1.2)
        check("取消勾选后立刻写库（=0）", db_setting("EnableExtendedFormats") == "0",
              "实际 %r" % db_setting("EnableExtendedFormats"))

        # ---- C 点「取消」→ 库里的值退回 ----
        cancel = wait_button(lambda s: s.strip() == "取消", timeout=15, root=sw)
        check("找到「取消」按钮", cancel is not None)
        if cancel is not None:
            try:
                cancel.GetInvokePattern().Invoke()
            except Exception:
                r = cancel.BoundingRectangle
                uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)
        time.sleep(2.5)
        check("设置窗口已关闭", not uia.WindowControl(searchDepth=2, RegexName=r"^设置$").Exists(0, 0))
        check("取消后库里的值退回 1", db_setting("EnableExtendedFormats") == "1",
              "实际 %r" % db_setting("EnableExtendedFormats"))

        # ---- D 重开设置：界面状态和库一致 ----
        sw = open_settings(main_win)
        check("设置窗口能重新打开", sw is not None)
        if sw is not None:
            check("重新进入「插件扩展」", goto_plugins(sw))
            cb2 = ext_checkbox(sw)
            check("复选框回到勾上状态（不是用户刚点的那个）", is_on(cb2) is True,
                  "ToggleState=%r" % (is_on(cb2),))

            # ---- E 直接点标题栏 × 关闭，也要回滚 ----
            if cb2 is not None:
                check("能再次把开关切到「关」", set_ext(sw, cb2, False) is False,
                      "ToggleState=%r" % (is_on(cb2),))
                time.sleep(1.2)
                check("再次取消勾选 → 库里又是 0", db_setting("EnableExtendedFormats") == "0",
                      "实际 %r" % db_setting("EnableExtendedFormats"))
            try:
                sw.GetWindowPattern().Close()
            except Exception:
                user32.PostMessageW(sw.NativeWindowHandle, 0x0010, 0, 0)   # WM_CLOSE
            time.sleep(2.5)
            check("点 × 关闭也会回滚（× 不是后门）",
                  db_setting("EnableExtendedFormats") == "1",
                  "实际 %r" % db_setting("EnableExtendedFormats"))

    finally:
        kill_app()
        if had_db:
            restore_db()

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
