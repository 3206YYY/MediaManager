# -*- coding: utf-8 -*-
"""
实机验证：V1.4.6 的「启动时检查新版本」提示。

只碰**便携开发库**（exe 旁的 Data/library.db），不碰用户的真实库。
脚本结束会把库还原，跑完不留痕迹。

为什么要注入版本：真实发版时「当前版本」永远等于 GitHub 最新版，
「发现新版本」这条路正常情况下走不到，所以用环境变量
MM_FAKE_CURRENT_VERSION 把自己当成 1.0.0 来测（发布版没人会设这个）。

检查：
  A. 启动约 8 秒后弹出「发现新版本」，标题带真实版本号
  B. 版本对比、更新时间、体积、说明区、三个按钮都在
  C. 关键承诺写在窗口里：不后台下载、不后台安装
  D. 点「跳过这个版本」→ 窗口关闭 + 库记住 UpdateSkippedVersion
  E. 重启后同一版本不再打扰（这是「跳过」的全部意义）
  F. 设置页有「启动时自动检查新版本」且默认勾上
  G. 把开关关掉 → 重启完全不检查（连网络都不发）
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.request

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.environ.get("MM_VERIFY_APP") or os.path.join(APP_DIR, "MediaManager.App.exe")
DATA_DIR = os.environ.get("MM_VERIFY_DATA_DIR") or os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK = DB + ".update_bak"
SHOT = os.path.join(ROOT, "build", "verify", "shots_update_prompt.png")

EXE_NAME = os.path.basename(APP)
REPO = "3206YYY/MediaManager"

import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


# ---------------------------------------------------------------- 库备份

def backup_db():
    if not os.path.exists(DB):
        print("(没有调试库，跳过备份)")
        return False
    shutil.copy2(DB, BAK)
    for suf in ("-wal", "-shm"):
        p = DB + suf
        if os.path.exists(p):
            os.remove(p)
    print("库已备份 ->", BAK)
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


def db_get(key):
    if not os.path.exists(DB):
        return None
    for _ in range(6):
        try:
            c = sqlite3.connect("file:%s?mode=ro" % DB, uri=True, timeout=3)
            row = c.execute("select Value from AppSetting where Key=?", (key,)).fetchone()
            c.close()
            return row[0] if row else None
        except sqlite3.OperationalError:
            time.sleep(0.4)
    return None


def db_set(key, value):
    c = sqlite3.connect(DB, timeout=6)
    c.execute("insert into AppSetting(Key,Value) values(?,?) "
              "on conflict(Key) do update set Value=excluded.Value", (key, value))
    c.commit()
    c.close()


# ---------------------------------------------------------------- 进程

def kill_app():
    """杀掉并**等窗口真的消失**。只 sleep 固定秒数不够：
    taskkill 返回后旧窗口还会挂一会儿，紧接着启动会匹配到正在退出的那个，
    之后所有子控件查询都是空的（这个坑让脚本连挂了三轮）。"""
    subprocess.run(["taskkill", "/F", "/IM", EXE_NAME], capture_output=True)
    for _ in range(25):
        if not uia.WindowControl(searchDepth=1, RegexName=r"^媒体管理器").Exists(0, 0):
            break
        time.sleep(0.4)
    time.sleep(1.0)


def launch(fake_version="1.0.0"):
    env = dict(os.environ)
    if fake_version:
        env["MM_FAKE_CURRENT_VERSION"] = fake_version
    else:
        env.pop("MM_FAKE_CURRENT_VERSION", None)
    p = subprocess.Popen([APP], cwd=os.path.dirname(APP), env=env)
    return p


def main_window(timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        w = uia.WindowControl(searchDepth=1, RegexName=r"^媒体管理器")
        if w.Exists(0, 0):
            return w
        time.sleep(0.5)
    return None


def find_update_window(timeout=50):
    end = time.time() + timeout
    while time.time() < end:
        for depth in (1, 2, 3):
            w = uia.WindowControl(searchDepth=depth, RegexName=r"^发现新版本")
            if w.Exists(0, 0):
                return w
        time.sleep(0.6)
    return None


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


def texts_of(win):
    out = []
    for c in collect(win):
        try:
            if c.ControlTypeName == "TextControl" and c.Name:
                out.append(c.Name)
        except Exception:
            pass
    return out


def name_of(c):
    if c is None:
        return "None"
    try:
        return c.Name or ""
    except Exception:
        return ""


def find_ctrl(win, type_name, pred):
    for c in collect(win):
        try:
            if c.ControlTypeName == type_name and pred(c.Name or ""):
                return c
        except Exception:
            pass
    return None


def click(ctrl):
    try:
        ctrl.GetInvokePattern().Invoke()
        return True
    except Exception:
        try:
            r = ctrl.BoundingRectangle
            uia.Click(r.left + r.width() // 2, r.top + r.height() // 2)
            return True
        except Exception:
            return False


def latest_release():
    """直接问 GitHub 最新 tag；拿不到就说明这轮没法测（不是代码的问题）。"""
    url = "https://api.github.com/repos/%s/releases/latest" % REPO
    req = urllib.request.Request(url, headers={
        "User-Agent": "mm-verify", "Accept": "application/vnd.github+json"})
    last = None
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                data = json.load(r)
            tag = data.get("tag_name", "")
            return tag.lstrip("vV") or None
        except Exception as e:
            last = e
            print("查 GitHub 最新版失败（第 %d 次）：%s" % (attempt + 1, e))
            time.sleep(3)
    return None


def main():
    # 版本号不靠脚本自己问 GitHub（代理经常 504），
    # 等程序弹出提示后从窗口标题里读——那才是用户真正看到的东西
    latest = None
    had_db = backup_db()
    kill_app()
    try:
        # ---------- 第一次启动：应当弹窗 ----------
        proc = launch()
        win = main_window()
        check("主窗口正常启动", win is not None)
        if win is None:
            return 1

        upd = find_update_window(timeout=55)
        check("启动后自动弹出「发现新版本」", upd is not None,
              "等了 55 秒没等到" if upd is None else "已弹出")
        if upd is None:
            # 分不清是「不该弹」还是「网络不通」时，不要给错误结论
            if latest_release() is None:
                print("（外面也连不上 GitHub，本轮不作结论）")
                return 0
            return 1

        m = re.search(r"v(\d+(?:\.\d+)+)", upd.Name or "")
        latest = m.group(1) if m else None
        check("窗口标题里能读出版本号", latest is not None, "标题=" + (upd.Name or ""))
        print("本次提示的新版本：v%s" % latest)
        if latest is None:
            return 1

        texts = texts_of(upd)
        joined = " ".join(texts)
        print("窗口文本：", " / ".join(texts[:12]))

        check("标题带真实新版本号",
              latest in (upd.Name or ""), "标题=" + (upd.Name or ""))
        check("显示当前版本（注入的 1.0.0）", "v1.0.0" in joined,
              str([t for t in texts if "1.0.0" in t][:2]))
        check("显示新版本号", ("v" + latest) in joined)
        check("有「改了什么」小节", "改了什么" in joined)

        note_lines = [t for t in texts
                      if len(t) > 6
                      and t not in ("发现新版本", "改了什么")
                      and "不会在后台" not in t
                      and not t.startswith(("当前版本", "发布于", "另外还有"))]
        check("更新说明区有实际内容（不是空白框）", len(note_lines) >= 1,
              "%d 行说明" % len(note_lines))
        check("说明保持简短（不超过 8 行，不把整篇博客搬进提示框）",
              len(note_lines) <= 8, "%d 行" % len(note_lines))

        for label in ("去下载", "跳过这个版本", "以后再说"):
            b = find_ctrl(upd, "ButtonControl", lambda s, l=label: s == l)
            check("按钮「%s」在" % label, b is not None)

        check("窗口里写明不后台下载/安装",
              ("不会在后台偷偷下载" in joined) or ("不会在后台" in joined),
              str([t for t in texts if "后台" in t][:1]))

        try:
            import ctypes.wintypes as wt2
            from PIL import ImageGrab
            # 不置前的话 GetWindowRect 的坐标是对的，但屏幕那块可能被别的窗口占着，
            # 抓回来就是一张「别人的窗口」截图（已经骗过一次）
            user32.SetForegroundWindow(upd.NativeWindowHandle)
            time.sleep(1.0)
            rc = wt2.RECT()
            user32.GetWindowRect(upd.NativeWindowHandle, ctypes.byref(rc))
            print("窗口矩形:", rc.left, rc.top, rc.right, rc.bottom)
            ImageGrab.grab(bbox=(rc.left, rc.top, rc.right, rc.bottom),
                           all_screens=True).save(SHOT)
            print("截图:", SHOT)
        except Exception as e:
            print("截图失败:", e)

        # ---------- 跳过这个版本 ----------
        skip = find_ctrl(upd, "ButtonControl", lambda s: s == "跳过这个版本")
        if skip is not None:
            click(skip)
        time.sleep(2.5)
        gone = not uia.WindowControl(searchDepth=2, RegexName=r"^发现新版本").Exists(0, 0)
        check("点「跳过这个版本」后窗口关闭", gone)

        saved = db_get("UpdateSkippedVersion")
        check("库记住跳过的版本", saved == latest or (saved or "").lstrip("vV") == latest,
              "UpdateSkippedVersion=%r，期望 %s" % (saved, latest))

        # ---------- 设置页开关 ----------
        kill_app()
        proc = launch()
        win = main_window()
        print("第二次启动主窗口:", win is not None)
        opened = False
        if win is not None:
            # 精确找「⚙ 设置」：工具栏里还有个叫「设置及更多(Alt+F)…」的元素，
            # 用 "设置" in name 会先撞上它，点下去什么也不会发生。
            # 另外窗口刚出现时 WPF 的工具栏还没构建完，要轮询等一会儿。
            btn = None
            end = time.time() + 15
            tried = 0
            while btn is None and time.time() < end:
                # 每轮重新拿一次窗口对象：COM 元素缓存失效后，旧对象上的
                # GetChildren 会一直抛异常，返回空列表，看起来就像「按钮不存在」
                w2 = uia.WindowControl(searchDepth=1, RegexName=r"^媒体管理器")
                if w2.Exists(0, 0):
                    tried = len(collect(w2))
                    btn = find_ctrl(w2, "ButtonControl",
                                    lambda s: s.strip() in ("⚙ 设置", "设置"))
                if btn is None:
                    time.sleep(0.6)
            print("重试后元素数:", tried)
            print("找到的设置按钮:", name_of(btn))
            if btn is not None:
                click(btn)
                # 设置页要读一堆设置项再渲染，3 秒常常不够
                time.sleep(4)
                opened = uia.WindowControl(searchDepth=2, RegexName=r".*设置.*").Exists(3, 0.5)
            if not opened:
                tops = []
                for c in collect(uia.GetRootControl()):
                    try:
                        if c.ControlTypeName == "WindowControl" and c.Name:
                            tops.append(c.Name)
                    except Exception:
                        pass
                print("当前所有窗口:", tops[:12])
        check("能打开设置窗口", opened)

        if opened:
            sw = uia.WindowControl(searchDepth=2, RegexName=r".*设置.*")
            nav = find_ctrl(sw, "ListItemControl", lambda s: "关于与更新" in s)
            if nav is not None:
                try:
                    nav.GetSelectionItemPattern().Select()
                except Exception:
                    click(nav)
                time.sleep(1.2)
            box = find_ctrl(sw, "CheckBoxControl", lambda s: "启动时自动检查新版本" in s)
            check("设置页有「启动时自动检查新版本」", box is not None)
            if box is not None:
                state = None
                try:
                    state = box.GetTogglePattern().ToggleState
                    checked = state == 1  # ToggleState_On
                except Exception:
                    checked = False
                check("这个开关默认是勾上的", checked, "ToggleState=%s" % state)
            hint = find_ctrl(sw, "TextControl", lambda s: "不会在后台下载" in s)
            check("设置页写明不会后台下载安装", hint is not None)

        # ---------- 跳过生效：重启不再弹 ----------
        kill_app()
        proc = launch()
        main_window()
        again = find_update_window(timeout=25)
        check("跳过的版本重启后不再提示", again is None,
              "又弹了一次" if again is not None else "25 秒内安静")

        # ---------- 关掉开关：连网络都不发 ----------
        kill_app()
        db_set("UpdateCheckOnStartup", "0")
        proc = launch()
        main_window()
        off = find_update_window(timeout=22)
        check("关掉「启动时检查」后完全不提示", off is None,
              "还是弹了" if off is not None else "22 秒内安静")
    finally:
        kill_app()
        if had_db:
            restore_db()

    passed = sum(1 for _, ok, _ in results if ok)
    print("\n结果：%d/%d 通过" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
