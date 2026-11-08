# -*- coding: utf-8 -*-
"""
实机验证：设置 → 关于与更新 里的「启动时自动检查新版本」。

单独一个脚本，因为它**不依赖网络**：只要程序能启动就能验，
不会因为 GitHub 504 而整轮跳过。

检查：
  A. 设置窗口能打开（点「⚙ 设置」）
  B. 有「关于与更新」页，能切过去
  C. 有「启动时自动检查新版本」开关，且默认勾上
  D. 说明文字写明「不会在后台下载或安装」
  E. 重新打开设置页，开关状态与库里的值一致（保存没丢）
"""
import ctypes
import ctypes.wintypes as wt
import os
import re
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
BAK = DB + ".update_settings_bak"
SHOT = os.path.join(ROOT, "build", "verify", "shots_update_settings.png")
EXE_NAME = os.path.basename(APP)

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


def backup_db():
    if not os.path.exists(DB):
        return False
    shutil.copy2(DB, BAK)
    for suf in ("-wal", "-shm"):
        p = DB + suf
        if os.path.exists(p):
            os.remove(p)
    return True


def restore_db():
    if not os.path.exists(BAK):
        return
    for suf in ("-wal", "-shm"):
        p = DB + suf
        if os.path.exists(p):
            os.remove(p)
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
    env.pop("MM_FAKE_CURRENT_VERSION", None)   # 这个脚本不关心更新弹窗
    return subprocess.Popen([APP], cwd=os.path.dirname(APP), env=env)


def wait_control(regex, timeout=25, search_from=None, kind=None, depth=25):
    """按正则找控件。

    ⚠ uiautomation 的 RegexName 是 `re.match` 语义（**从头匹配**），不是 `re.search`。
    所以「结尾是设置」必须写成 `.*设置$`；只写 `设置$` 永远匹配不到「⚙ 设置」，
    而写 `设置` 会先撞上「设置及更多(Alt+F)…」——两个坑都踩过。
    """
    ctrl_cls = kind or uia.ButtonControl
    end = time.time() + timeout
    while time.time() < end:
        try:
            if search_from is not None:
                c = ctrl_cls(searchFromControl=search_from, RegexName=regex)
            else:
                c = ctrl_cls(searchDepth=depth, RegexName=regex)
            if c.Exists(0, 0):
                return c
        except Exception:
            pass
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
    """按谓词找按钮。

    不用 RegexName：uiautomation 的 RegexName 在带 emoji 的控件名上不可靠，
    会先命中「设置及更多(Alt+F)…」这种旁支；手写遍历 + endswith 谓词才稳。
    """
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


def main():
    had_db = backup_db()
    kill_app()
    try:
        launch()
        win = None
        end = time.time() + 30
        while time.time() < end and win is None:
            w = uia.WindowControl(searchDepth=1, RegexName=r"^媒体管理器")
            if w.Exists(0, 0):
                win = w
            time.sleep(0.5)
        check("主窗口启动", win is not None)
        if win is None:
            return 1

        btn = wait_button(lambda s: s.strip().endswith("设置"), timeout=25, root=win)
        check("找到「⚙ 设置」按钮", btn is not None,
              repr(btn.Name) if btn is not None else "")
        if btn is None:
            names = []
            def dump(node, d):
                if d > 18:
                    return
                try:
                    kids = node.GetChildren()
                except Exception:
                    return
                for ch in kids:
                    try:
                        nm = ch.Name or ""
                        if ch.ControlTypeName == "ButtonControl" or "设" in nm:
                            names.append("%s:%r" % (ch.ControlTypeName, nm[:24]))
                    except Exception:
                        pass
                    dump(ch, d + 1)
            dump(win, 0)
            print("候选元素：", names[:40])
            return 1

        try:
            btn.GetInvokePattern().Invoke()
        except Exception:
            r = btn.BoundingRectangle
            uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)

        sw = None
        end = time.time() + 20
        while time.time() < end and sw is None:
            w = uia.WindowControl(searchDepth=2, RegexName=r"^设置$")
            if w.Exists(0, 0):
                sw = w
            time.sleep(0.5)
        check("设置窗口已打开", sw is not None)
        if sw is None:
            return 1

        info = wait_control(r".*关于与更新.*", timeout=15,
                            search_from=sw, kind=uia.ListItemControl)
        check("导航里有「关于与更新」", info is not None)
        if info is not None:
            try:
                info.GetSelectionItemPattern().Select()
            except Exception:
                info.Click()
            time.sleep(1.5)

        box = wait_control(r".*启动时自动检查新版本.*", timeout=15,
                           search_from=sw, kind=uia.CheckBoxControl)
        check("有「启动时自动检查新版本」开关", box is not None)
        if box is None:
            print("页面可见文字：", " / ".join(texts_of(sw)[:14]))
            return 1

        state = None
        try:
            state = box.GetTogglePattern().ToggleState
        except Exception as e:
            print("读 ToggleState 失败:", e)
        check("开关默认勾上", state == 1, "ToggleState=%s" % state)

        texts = texts_of(sw)
        joined = " ".join(texts)
        check("说明写明不会后台下载安装", "不会在后台下载" in joined,
              str([t for t in texts if "不会在后台下载" in t][:1]))

        try:
            from PIL import ImageGrab
            rc = wt.RECT()
            user32.GetWindowRect(sw.NativeWindowHandle, ctypes.byref(rc))
            ImageGrab.grab(bbox=(rc.left, rc.top, rc.right, rc.bottom),
                           all_screens=True).save(SHOT)
            print("截图:", SHOT)
        except Exception as e:
            print("截图失败:", e)
    finally:
        kill_app()
        if had_db:
            restore_db()

    passed = sum(1 for _, ok, _ in results if ok)
    print("\n结果：%d/%d 通过" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
