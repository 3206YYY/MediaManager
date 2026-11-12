# -*- coding: utf-8 -*-
"""
实机验证：设置 → 系统集成 → 「桌面图标」（创建 / 修复 / 启动自愈）。

为什么不写在 verify_update_settings.py 里：那个脚本一启动就往用户真实桌面上写 .lnk，
跑完留在那儿说不清是谁的。这里全程用环境变量 `MM_SHORTCUT_DIR` 把「桌面」指到临时目录，
程序内部走的是同一套代码，但一个字节都不碰真实桌面。

检查：
  A. 主窗口能启动
  B. 「⚙ 设置」→「系统集成」页能打开
  C. 桌面上没有图标时，按钮写「创建桌面图标」
  D. 点按钮 → 临时桌面出现 媒体管理器.lnk，目标就是当前 exe
  E. 状态刷新成「已有图标，指向当前这个程序」
  F. 把图标改成指向一个不存在的程序 → 重启 → 启动时自动改回来（不用进设置页）
  G. 同一个桌面里「别人的」快捷方式不被动
"""
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import sys
import tempfile
import time

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".shortcut_bak"
SHOT = os.path.join(ROOT, "build", "verify", "shots_desktop_shortcut.png")
EXE_NAME = os.path.basename(APP)

FAKE_DESKTOP = tempfile.mkdtemp(prefix="mm_desktop_")
LNK = os.path.join(FAKE_DESKTOP, "媒体管理器.lnk")

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


# ----------------------------------------------------------------- 桌面 .lnk 读写
# 用系统自带的 WScript.Shell，不额外依赖 pywin32（这台机器的 python 没装）。

def _ps(script):
    # ⚠ 必须先把控制台输出改成 UTF-8：默认代码页下中文路径回来是乱码，
    #   比字符串就会假失败（第一次跑 3 个 FAIL 全是这个，功能其实是对的）。
    prelude = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;"
    return subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
         "-Command", prelude + script],
        capture_output=True, text=True, encoding="utf-8", errors="replace")


def make_lnk(path, target):
    p = path.replace("'", "''")
    t = target.replace("'", "''")
    r = _ps("$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');"
            "$s.TargetPath='%s';$s.Save();'ok'" % (p, t))
    return "ok" in (r.stdout or "")


def read_lnk(path):
    p = path.replace("'", "''")
    r = _ps("$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');$s.TargetPath" % p)
    out = (r.stdout or "").strip()
    return out or None


# ----------------------------------------------------------------- 进程控制

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
    for _ in range(25):
        if not uia.WindowControl(searchDepth=1, RegexName=r"^媒体管理器").Exists(0, 0):
            break
        time.sleep(0.4)
    time.sleep(1.0)


def launch():
    env = dict(os.environ)
    env["MM_SHORTCUT_DIR"] = FAKE_DESKTOP        # 「桌面」指到临时目录
    env.pop("MM_FAKE_CURRENT_VERSION", None)     # 不关心更新弹窗
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


def wait_button(pred, timeout=25, root=None):
    """手写遍历 + 谓词：uiautomation 的 RegexName 对带 emoji 的名字不可靠（踩过）。"""
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


def texts_of(win, maxd=22):
    out = []

    def walk(node, d):
        if d > maxd:
            return
        try:
            kids = node.GetChildren()
        except Exception:
            return
        for ch in kids:
            try:
                if ch.ControlTypeName == "TextControl" and ch.Name:
                    out.append(ch.Name)
            except Exception:
                pass
            walk(ch, d + 1)

    walk(win, 0)
    return out


def close_dialog(title_regex, timeout=12):
    dlg = wait_window(regex=title_regex, depth=3, timeout=timeout)
    if dlg is None:
        return False
    try:
        dlg.SendKeys("{Enter}")
    except Exception:
        btn = wait_button(lambda s: s.strip() in ("确定", "好的", "OK"), timeout=5, root=dlg)
        if btn is not None:
            try:
                btn.GetInvokePattern().Invoke()
            except Exception:
                r = btn.BoundingRectangle
                uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)
    time.sleep(0.8)
    return True


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


# ----------------------------------------------------------------- 主流程

def main():
    had_db = backup_db()
    kill_app()
    try:
        # ---- 阶段 1：干净桌面 → 建图标 ----
        launch()
        main_win = wait_window()
        check("主窗口启动", main_win is not None)
        if main_win is None:
            return 1

        sw = open_settings(main_win)
        check("设置窗口已打开", sw is not None)
        if sw is None:
            return 1

        nav = uia.ListItemControl(searchFromControl=sw, RegexName=r".*系统集成.*")
        found_nav = False
        end = time.time() + 15
        while time.time() < end and not found_nav:
            for c in walk_controls(sw):
                try:
                    if c.ControlTypeName == "ListItemControl" and "系统集成" in (c.Name or ""):
                        nav = c
                        found_nav = True
                        break
                except Exception:
                    pass
            time.sleep(0.4)
        check("导航里有「系统集成」", found_nav)
        if not found_nav:
            return 1
        try:
            nav.GetSelectionItemPattern().Select()
        except Exception:
            nav.Click()
        time.sleep(1.5)

        btn = wait_button(lambda s: "桌面图标" in s, timeout=15, root=sw)
        check("找到「创建桌面图标」按钮", btn is not None,
              repr(btn.Name) if btn is not None else "")
        if btn is None:
            print("页面可见文字：", " / ".join(texts_of(sw)[:16]))
            return 1

        check("没有图标时按钮写「创建」", "创建" in (btn.Name or ""), repr(btn.Name))
        check("点击前桌面上没有 .lnk", not os.path.exists(LNK))

        try:
            btn.GetInvokePattern().Invoke()
        except Exception:
            r = btn.BoundingRectangle
            uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)

        closed = close_dialog(r"^已创建$")
        check("弹出「已创建」提示", closed)

        target = read_lnk(LNK) if os.path.exists(LNK) else None
        check("桌面出现了 媒体管理器.lnk", os.path.exists(LNK), LNK)
        check("图标指向当前程序", target and os.path.normcase(target) == os.path.normcase(APP),
              "%r" % target)

        # 状态要跟着变（按钮文案变成「更新」）
        time.sleep(1.0)
        btn2 = wait_button(lambda s: "桌面图标" in s, timeout=10, root=sw)
        check("状态刷新成已有图标", btn2 is not None and "更新" in (btn2.Name or ""),
              repr(btn2.Name) if btn2 is not None else "找不到按钮")

        try:
            from PIL import ImageGrab
            rc = wt.RECT()
            user32.GetWindowRect(sw.NativeWindowHandle, ctypes.byref(rc))
            ImageGrab.grab(bbox=(rc.left, rc.top, rc.right, rc.bottom),
                           all_screens=True).save(SHOT)
            print("截图:", SHOT)
        except Exception as e:
            print("截图失败:", e)

        # ---- 阶段 2：图标失效 → 重启自愈 ----
        kill_app()
        stale = os.path.join(FAKE_DESKTOP, "已经不存在了", "MediaManager.App.exe")
        make_lnk(LNK, stale)
        check("已把图标改成指向不存在的程序", read_lnk(LNK) == stale, "%r" % read_lnk(LNK))

        # 放一个「别人的」快捷方式，自愈时不许被动
        someone = os.path.join(FAKE_DESKTOP, "记事本.lnk")
        make_lnk(someone, r"C:\Windows\notepad.exe")

        launch()
        main2 = wait_window()
        check("第二次启动", main2 is not None)
        time.sleep(3.0)          # 启动自愈在 5) 步，给一点余量

        healed = read_lnk(LNK)
        check("启动时自动改回当前程序", healed and os.path.normcase(healed) == os.path.normcase(APP),
              "%r" % healed)
        check("别人的快捷方式没被动", read_lnk(someone) == r"C:\Windows\notepad.exe",
              "%r" % read_lnk(someone))
    finally:
        kill_app()
        if had_db:
            restore_db()
        shutil.rmtree(FAKE_DESKTOP, ignore_errors=True)

    passed = sum(1 for _, ok, _ in results if ok)
    print("\n结果：%d/%d 通过" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
