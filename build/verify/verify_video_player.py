# -*- coding: utf-8 -*-
"""视频播放器实机验证（一次性脚本）：

之前审查清单里「视频播放器分支没有实机脚本」的欠账。LibVLC 的视频画面是
HwndHost（独立 HWND），截图截不到也读不到，所以这个脚本只断言
UIA 读得到的 + 键盘能驱动的，不碰视频区截图：

1) 命令行带视频路径启动 → 「视频播放器」窗口出现
2) 没有出现「无法播放此视频」错误面板
3) 顶栏标题是文件名
4) 总时长不是 00:00（元数据已加载）
5) 时间在走（真的在播）
6) 空格暂停 → 时间停住
7) 暂停时按 → 步进 5 秒
8) 再按空格 → 时间继续走
9) 关闭播放器 → 主窗口还活着

用法：python build/verify/verify_video_player.py
"""
import ctypes
import ctypes.wintypes as wt
import io
import os
import re
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug",
                   "net8.0-windows", "MediaManager.App.exe")
VIDEO = os.path.join(ROOT, "build", "testmedia", "mm_clip.mp4")  # 25 秒
SHOTS = os.path.join(HERE, "shots_video_player")
LOG = os.path.join(HERE, "verify_video_player_log.txt")
os.makedirs(SHOTS, exist_ok=True)
logf = io.open(LOG, "w", encoding="utf-8")

user32 = ctypes.windll.user32
user32.SetForegroundWindow.argtypes = [wt.HWND]
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

import uiautomation as uia  # noqa: E402
from PIL import ImageGrab  # noqa: E402

results = []
TIME_RE = re.compile(r"^\d{1,2}:\d{2}$")


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    logf.write(line + "\n")
    logf.flush()


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    log(("PASS " if ok else "FAIL ") + name + ((" | " + detail) if detail else ""))


def shot(ctrl, tag, pad=0):
    try:
        r = ctrl.BoundingRectangle
        if r.right - r.left <= 0:
            return
        img = ImageGrab.grab(bbox=(r.left - pad, r.top - pad, r.right + pad, r.bottom + pad))
        img.save(os.path.join(SHOTS, tag + ".png"))
    except Exception as e:
        log("截图失败", tag, e)


# ---------- Win32 窗口枚举（别用 UIA 根枚举找窗口，太慢而且会碰前台窗口） ----------

def enum_windows():
    out = []

    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                out.append((hwnd, buf.value))
        return True

    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


def find_window(title, contains=True):
    for hwnd, text in enum_windows():
        if (title in text) if contains else (text == title):
            return hwnd
    return None


# ---------- UIA 辅助 ----------

def collect(ctrl, acc=None, depth=0, maxdepth=20):
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


def visible(ctrl):
    """WPF Collapsed 的控件仍然躺在 UIA 树里，必须用 IsOffscreen 判可见。"""
    try:
        return not ctrl.IsOffscreen
    except Exception:
        return False


def read_times(player):
    """按屏幕横向顺序返回 UIA 上所有 mm:ss 文本（0=当前时间，1=总时长）。"""
    hits = []
    for c in collect(player, maxdepth=24):
        if c.ControlTypeName != "TextControl":
            continue
        name = (c.Name or "").strip()
        if TIME_RE.match(name):
            try:
                r = c.BoundingRectangle
                hits.append((name, r.left))
            except Exception:
                pass
    hits.sort(key=lambda t: t[1])
    return [h[0] for h in hits]


def send_key(vk):
    """先抢前台再发键。播放器快捷键走 PreProcessInput，必须让它真的拿到键盘焦点。
    SetForegroundWindow 偶尔抢不到（README 坑 2），必须确认前台窗口真的是播放器，
    没抢到就重试 —— 否则按键打到别的窗口上，无声无息。"""
    hwnd = find_window("视频播放器")
    if hwnd is None:
        return False
    for _ in range(4):
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)
        if user32.GetForegroundWindow() == hwnd:
            break
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(vk, 0, 2, 0)  # KEYEVENTF_KEYUP
    return True


VK_SPACE = 0x20
VK_RIGHT = 0x27


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.0)


def main():
    if not os.path.exists(APP):
        log("找不到 %s，先 dotnet build" % APP)
        return 1
    if not os.path.exists(VIDEO):
        log("找不到测试视频 %s" % VIDEO)
        return 1

    kill_app()
    subprocess.Popen([APP, VIDEO], cwd=os.path.dirname(APP))

    # ---- 1) 播放器窗口出现 ----
    hwnd = None
    for _ in range(20):
        time.sleep(1.0)
        hwnd = find_window("视频播放器")
        if hwnd:
            break
    check("播放器窗口出现", hwnd is not None)
    if hwnd is None:
        kill_app()
        return 1

    player = uia.ControlFromHandle(hwnd)
    time.sleep(2.5)
    shot(player, "01_player_open")

    # ---- 2) 没有错误面板 ----
    err = None
    for c in collect(player, maxdepth=24):
        if c.ControlTypeName == "TextControl" and "无法播放此视频" in (c.Name or ""):
            if visible(c):
                err = c
                break
    check("没有「无法播放此视频」错误面板", err is None,
          "LibVLC 解码失败时会弹出这个面板" if err else "")

    # ---- 3) 顶栏标题 ----
    title_ok = False
    for c in collect(player, maxdepth=24):
        if c.ControlTypeName == "TextControl" and "mm_clip" in (c.Name or ""):
            title_ok = True
            break
    check("顶栏标题显示文件名 mm_clip.mp4", title_ok)

    # ---- 4) 总时长已加载（mm_clip 是 25 秒） ----
    times = read_times(player)
    check("读到时间文本（当前+总时长）", len(times) >= 2, str(times))
    total = times[1] if len(times) >= 2 else "00:00"
    check("总时长不是 00:00", total != "00:00", "总时长=" + total)

    # ---- 5) 时间在走 ----
    t1 = read_times(player)
    time.sleep(2.5)
    t2 = read_times(player)
    playing = bool(t1) and bool(t2) and t1[0] != t2[0]
    check("播放中时间在走", playing, "%s -> %s" % (t1[:1], t2[:1]))

    # ---- 6) 空格暂停 → 时间停住 ----
    # 抢前台偶发失败会让第一下空格打空（时间还在走 = 没暂停），此时再发一次空格；
    # 反过来如果第一次空格其实生效了，时间停住，判断直接通过 —— 两种情况都正确。
    paused = False
    detail = ""
    for attempt in range(2):
        send_key(VK_SPACE)
        time.sleep(0.6)
        p1 = read_times(player)
        time.sleep(2.2)
        p2 = read_times(player)
        if p1 and p2 and p1[0] == p2[0]:
            paused = True
            detail = "%s vs %s%s" % (p1[0], p2[0], "" if attempt == 0 else "（第 2 次尝试才停住）")
            break
        detail = "%s vs %s" % (p1[:1], p2[:1])
    check("空格暂停后时间停住", paused, detail)

    # ---- 7) 暂停时按 → 步进 5 秒 ----
    send_key(VK_RIGHT)
    time.sleep(1.0)
    s1 = read_times(player)
    stepped = False
    if paused and s1:
        def sec(t):
            m, s = t.split(":")
            return int(m) * 60 + int(s)
        delta = sec(s1[0]) - sec(p2[0])
        stepped = 3 <= delta <= 9
        check("暂停时按→步进约5秒", stepped, "%s -> %s (Δ%ds)" % (p2[0], s1[0], delta))
    else:
        check("暂停时按→步进约5秒", False, "前置失败: paused=%s times=%s" % (paused, s1))

    # ---- 8) 再按空格恢复 ----
    resumed = False
    detail = ""
    for attempt in range(2):
        send_key(VK_SPACE)
        time.sleep(0.5)
        r1 = read_times(player)
        time.sleep(2.2)
        r2 = read_times(player)
        if r1 and r2 and r1[0] != r2[0]:
            resumed = True
            detail = "%s -> %s%s" % (r1[0], r2[0], "" if attempt == 0 else "（第 2 次尝试才恢复）")
            break
        detail = "%s vs %s" % (r1[:1], r2[:1])
    check("再按空格恢复播放", resumed, detail)

    shot(player, "02_after_controls")

    # ---- 9) 关闭播放器，主窗口还活着 ----
    user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
    time.sleep(2.0)
    main_hwnd = find_window("媒体管理器")
    check("关闭播放器后主窗口仍在", main_hwnd is not None)

    kill_app()

    failed = [n for n, ok, _ in results if not ok]
    log("----")
    log("共 %d 项，失败 %d 项" % (len(results), len(failed)))
    return 0 if not failed else 1


if __name__ == "__main__":
    try:
        code = main()
    finally:
        logf.close()
    raise SystemExit(code)
