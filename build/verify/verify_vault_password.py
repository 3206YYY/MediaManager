# -*- coding: utf-8 -*-
"""v1.5.0「私密空间密码」实机验证（一次性脚本）。

盯的是一条链路：
  1) 默认没设密码 → 点开私密空间直接就进（不许拿弹窗催人设密码）
  2) 顶栏「🔑 设置密码」→ 输两遍 → 按钮变「密码已开启」
  3) 关窗再进 → 必须先解锁；输错原地报错、输对进去
  4) 「取消密码」→ 之后又是一点就进，库里凭据清干净

用完会把 Data/library.db 里的凭据删掉，不留痕迹。

用法：python build/uia_verify_vault_password.py
"""
import ctypes
import io
import os
import sqlite3
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
DB = os.path.join(APP_DIR, "Data", "library.db")
SHOTS = os.path.join(HERE, "shots_vault")
LOG = os.path.join(HERE, "verify_vault_log.txt")
os.makedirs(SHOTS, exist_ok=True)
logf = io.open(LOG, "w", encoding="utf-8")

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

import uiautomation as uia  # noqa: E402
from PIL import ImageGrab  # noqa: E402

results = []


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    logf.write(line + "\n")
    logf.flush()


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    log(("PASS " if ok else "FAIL ") + name + ((" | " + detail) if detail else ""))


def shot(win, tag):
    try:
        r = win.BoundingRectangle
        img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
        img.save(os.path.join(SHOTS, tag + ".png"))
    except Exception as e:
        log("截图失败", tag, e)


def collect(ctrl, acc=None, depth=0, maxdepth=14):
    acc = acc if acc is not None else []
    if depth > maxdepth:
        return acc
    try:
        for ch in ctrl.GetChildren():
            acc.append(ch)
            collect(ch, acc, depth + 1, maxdepth)
    except Exception:
        pass
    return acc


def find_btn(root, text):
    """按名字找按钮（WPF Button 的 Name 就是它的内容文字）。"""
    for c in collect(root):
        if c.ControlTypeName == "ButtonControl" and text in (c.Name or ""):
            return c
    return None


def wait_btn(root, text, seconds=6):
    end = time.time() + seconds
    while time.time() < end:
        b = find_btn(root, text)
        if b is not None:
            return b
        time.sleep(0.3)
    return None


def find_password_box(root):
    for c in collect(root):
        if c.ControlTypeName == "EditControl":
            try:
                if c.IsPassword:
                    return c
            except Exception:
                pass
    return None


def click(ctrl):
    try:
        ctrl.GetPattern(uia.PatternId.InvokePattern).Invoke()
        return True
    except Exception:
        try:
            ctrl.Click()
            return True
        except Exception:
            return False


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.5)


def launch():
    subprocess.Popen([APP], cwd=APP_DIR)
    win = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if not win.Exists(30, 1):
        return None
    time.sleep(3.5)
    try:
        user32.SetForegroundWindow(win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.6)
    return win


def open_vault_via_hotkey(main):
    try:
        user32.SetForegroundWindow(main.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.5)
    uia.SendKeys("{Ctrl}l", waitTime=0.2)
    time.sleep(1.8)
    return uia.WindowControl(searchDepth=2, Name="🔒 私密空间")


def kill_vault_windows():
    for w in uia.GetRootControl().GetChildren():
        if w.ControlTypeName != "WindowControl":
            continue
        n = w.Name or ""
        if "私密空间" in n:
            try:
                w.GetPattern(uia.PatternId.WindowPattern).Close()
            except Exception:
                pass


def main():
    kill_app()

    main_win = launch()
    if main_win is None:
        check("主窗口出现", False)
        return 1
    check("主窗口出现", True)

    # ---- 1. 没设密码时，私密空间应该直接就进，不许弹任何密码窗 ----
    vault = open_vault_via_hotkey(main_win)
    no_prompt = True
    for w in uia.GetRootControl().GetChildren():
        if w.ControlTypeName == "WindowControl" and "密码" in (w.Name or ""):
            no_prompt = False
            log("意外弹窗:", w.Name)
            try:
                w.GetPattern(uia.PatternId.WindowPattern).Close()
            except Exception:
                pass
    check("未设密码时进入私密空间不弹密码窗", vault.Exists(1, 0.5) and no_prompt)
    if not vault.Exists(1, 0.5):
        kill_app()
        return 1
    shot(vault, "01_vault_no_password")

    # ---- 2. 设密码 ----
    setup_btn = wait_btn(vault, "设置密码")
    check("顶栏有「🔑 设置密码」入口", setup_btn is not None)
    if setup_btn is None:
        kill_app()
        return 1

    click(setup_btn)
    time.sleep(1.5)
    pwd_win = uia.WindowControl(searchDepth=2, Name="设置私密空间密码")
    check("设置密码窗弹出", pwd_win.Exists(1, 0.5))
    if not pwd_win.Exists(1, 0.5):
        shot(vault, "02_vault_after_setup_click")
        kill_app()
        return 1

    shot(pwd_win, "03_setup_dialog")
    box = find_password_box(pwd_win)
    if box is not None:
        box.Click()
        uia.SendKeys("1234", waitTime=0.1)
        uia.SendKeys("{TAB}", waitTime=0.1)
        uia.SendKeys("1234", waitTime=0.1)
    ok_btn = find_btn(pwd_win, "创建")
    check("设置窗有「创建」按钮", ok_btn is not None)
    if ok_btn is not None:
        click(ok_btn)
    time.sleep(1.8)
    check("设置后密码窗关闭", not pwd_win.Exists(1, 0.5))

    # 按钮文字应该变成「密码已开启」
    opened_btn = wait_btn(vault, "密码已开启", seconds=5)
    check("顶栏按钮变为「🔑 密码已开启」", opened_btn is not None)
    shot(vault, "04_vault_password_on")

    # ---- 3. 关窗再进：必须先解锁 ----
    close_btn = find_btn(vault, "关闭")
    if close_btn is not None:
        click(close_btn)
    time.sleep(1.5)
    check("私密窗口已关闭", not vault.Exists(1, 0.5))

    main_win2 = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if main_win2.Exists(3, 0.5):
        main_win = main_win2

    vault2 = open_vault_via_hotkey(main_win)
    time.sleep(1.0)
    unlock_win = uia.WindowControl(searchDepth=2, Name="私密空间已上锁")
    check("再次进入时弹解锁窗", unlock_win.Exists(3, 0.5))
    if not unlock_win.Exists(1, 0.5):
        kill_app()
        return 1
    shot(unlock_win, "05_unlock_dialog")

    # 输错 → 原地报错，不关窗
    box = find_password_box(unlock_win)
    if box is not None:
        box.Click()
        uia.SendKeys("9999", waitTime=0.1)
    ok_btn = find_btn(unlock_win, "解锁")
    if ok_btn is not None:
        click(ok_btn)
    time.sleep(1.5)
    err = [c for c in collect(unlock_win)
           if c.ControlTypeName == "TextControl" and "密码不对" in (c.Name or "")]
    check("输错密码原地提示「密码不对」", unlock_win.Exists(1, 0.5) and len(err) > 0)
    shot(unlock_win, "06_unlock_wrong")

    # 输对 → 进去
    box = find_password_box(unlock_win)
    if box is not None:
        box.Click()
        uia.SendKeys("1234", waitTime=0.1)
    ok_btn = find_btn(unlock_win, "解锁")
    if ok_btn is not None:
        click(ok_btn)
    time.sleep(2.0)
    vault3 = uia.WindowControl(searchDepth=2, Name="🔒 私密空间")
    check("输对密码进入私密空间", vault3.Exists(3, 0.5))
    if vault3.Exists(1, 0.5):
        shot(vault3, "07_vault_unlocked")

        # ---- 4. 修改密码：1234 → 5678，然后关窗重开验新密码 ----
        on_btn = wait_btn(vault3, "密码已开启")
        check("已设密码时顶栏按钮为「密码已开启」", on_btn is not None)

        if on_btn is not None:
            click(on_btn)
            time.sleep(1.2)
            menu = uia.MenuControl(searchDepth=6)
            items = [m.Name for m in menu.GetChildren()] if menu.Exists(3, 1) else []
            log("密码菜单:", items)
            check("已设密码时菜单含「修改 / 取消」",
                  any("修改密码" in (t or "") for t in items) and any("取消密码" in (t or "") for t in items))

            change_item = None
            if menu.Exists(1, 0.3):
                for m in menu.GetChildren():
                    if "修改密码" in (m.Name or ""):
                        change_item = m
            if change_item is not None:
                try:
                    change_item.GetPattern(uia.PatternId.InvokePattern).Invoke()
                except Exception:
                    change_item.Click()
            time.sleep(1.6)

            chg_win = uia.WindowControl(searchDepth=2, Name="修改私密空间密码")
            check("修改密码窗弹出", chg_win.Exists(2, 0.5))
            if chg_win.Exists(1, 0.5):
                shot(chg_win, "08_change_dialog")
                # 焦点默认在原密码框上：原密码 → 新密码 → 再输一次
                uia.SendKeys("1234", waitTime=0.1)
                uia.SendKeys("{TAB}", waitTime=0.1)
                uia.SendKeys("5678", waitTime=0.1)
                uia.SendKeys("{TAB}", waitTime=0.1)
                uia.SendKeys("5678", waitTime=0.1)
                b = find_btn(chg_win, "保存")
                if b is not None:
                    click(b)
                time.sleep(1.8)
                check("修改密码窗关闭（原密码正确）", not chg_win.Exists(1, 0.5))

                # 关窗重开：旧密码必须失效，新密码必须有效
                b = find_btn(vault3, "关闭")
                if b is not None:
                    click(b)
                time.sleep(1.5)

                m3 = uia.WindowControl(searchDepth=1, Name="媒体管理器")
                vault4 = open_vault_via_hotkey(m3 if m3.Exists(2, 0.5) else main_win)
                uw = uia.WindowControl(searchDepth=2, Name="私密空间已上锁")
                if uw.Exists(3, 0.5):
                    uia.SendKeys("1234", waitTime=0.1)
                    b = find_btn(uw, "解锁")
                    if b is not None:
                        click(b)
                    time.sleep(1.5)
                    old_rejected = uw.Exists(1, 0.5) and not \
                        uia.WindowControl(searchDepth=2, Name="🔒 私密空间").Exists(1, 0.3)
                    check("改完密码后旧密码 1234 打不开", old_rejected)

                    uia.SendKeys("5678", waitTime=0.1)
                    b = find_btn(uw, "解锁")
                    if b is not None:
                        click(b)
                    time.sleep(2.0)
                check("改完密码后新密码 5678 能进",
                      uia.WindowControl(searchDepth=2, Name="🔒 私密空间").Exists(3, 0.5))
                vault3 = uia.WindowControl(searchDepth=2, Name="🔒 私密空间")

            # ---- 5. 取消密码 → 又回到一点就进 ----
            on_btn = wait_btn(vault3, "密码已开启") if vault3.Exists(1, 0.3) else None
            if on_btn is not None:
                click(on_btn)
                time.sleep(1.2)
                menu = uia.MenuControl(searchDepth=6)
                remove_item = None
                if menu.Exists(2, 0.5):
                    for m in menu.GetChildren():
                        if "取消密码" in (m.Name or ""):
                            remove_item = m
                if remove_item is not None:
                    try:
                        remove_item.GetPattern(uia.PatternId.InvokePattern).Invoke()
                    except Exception:
                        remove_item.Click()
                time.sleep(1.6)

                rm_win = uia.WindowControl(searchDepth=2, Name="取消私密空间密码")
                check("取消密码窗弹出", rm_win.Exists(2, 0.5))
                if rm_win.Exists(1, 0.5):
                    shot(rm_win, "09_remove_dialog")
                    uia.SendKeys("5678", waitTime=0.1)
                    b = find_btn(rm_win, "取消密码")
                    if b is not None:
                        click(b)
                    time.sleep(1.8)

                again = wait_btn(vault3, "设置密码", seconds=5)
                check("取消后顶栏回到「🔑 设置密码」", again is not None)

    kill_app()

    # ---- 5. 库里不该残留凭据 ----
    try:
        conn = sqlite3.connect(DB)
        rows = conn.execute("SELECT value FROM AppSetting WHERE key='Vault.Credential'").fetchall()
        conn.close()
        left = [r[0] for r in rows if r[0]]
        check("数据库里没有残留密码凭据", len(left) == 0, str(left)[:60])
    except Exception as e:
        check("数据库里没有残留密码凭据", False, "查库失败 " + str(e))

    ok = all(r[1] for r in results)
    log("")
    log("=== 结果 %d/%d ===" % (sum(1 for r in results if r[1]), len(results)))
    for name, good, detail in results:
        log(("  ✓ " if good else "  ✗ ") + name + (("  " + detail) if detail else ""))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
