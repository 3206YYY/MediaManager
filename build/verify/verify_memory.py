# -*- coding: utf-8 -*-
"""内存占用实测（可复跑）。

之前 README 写过「内存占用硬顶 200MB」，后来因为没有任何测量能支撑而删掉。
这个脚本就是补上那个测量的：不猜、只记采样数字。

## 测什么

对正在运行的主进程每秒采样一次（工作集 / 私有内存 / 峰值工作集），
按场景记三张快照：

  A 空载     —— 主窗口加载完、静置 20 秒
  B 查看器开 —— 双击网格第一项打开内置图片查看器（解码大图）
  C 查看器关 —— 关掉查看器静置 10 秒（看内存是否回落）

## 不测什么

- 十万条规模：合成数据只有元数据没有真实文件，缩略图缓存填不满，
  测出来的数字没有代表性。要看规模化表现去跑 build/bench_db.py（SQL 层）。
- 缩略图缓存预算是设计上限 200MB（ThumbnailMemoryCache.DefaultBudgetBytes），
  实际占用取决于网格里解了多少张图。

## 用法

    python build/verify/verify_memory.py
    结果写 build/verify/mem_result.json
"""
import ctypes
import ctypes.wintypes as wt
import io
import json
import os
import subprocess
import time
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug",
                   "net8.0-windows", "MediaManager.App.exe")
RESULT = os.path.join(HERE, "mem_result.json")
logf = io.open(os.path.join(HERE, "verify_memory_log.txt"), "w", encoding="utf-8")

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

import uiautomation as uia  # noqa: E402

results = []


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    logf.write(line + "\n")
    logf.flush()


# ---------- 进程内存采样（psapi，无需第三方库） ----------

class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD),
        ("PageFaultCount", wt.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),  # PROCESS_MEMORY_COUNTERS_EX 才有
    ]


psapi = ctypes.windll.psapi
kernel32 = ctypes.windll.kernel32


def sample(pid):
    """返回 (工作集MB, 私有内存MB, 峰值工作集MB)。进程没了返回 None。"""
    h = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    try:
        pmc = PROCESS_MEMORY_COUNTERS_EX()
        pmc.cb = ctypes.sizeof(pmc)
        if not psapi.GetProcessMemoryInfo(h, ctypes.byref(pmc), pmc.cb):
            return None
        mb = 1024 * 1024
        return (pmc.WorkingSetSize / mb, pmc.PrivateUsage / mb, pmc.PeakWorkingSetSize / mb)
    finally:
        kernel32.CloseHandle(h)


def sample_seconds(pid, seconds, label):
    """连续采样，返回摘要 dict。进程消失立即停（也算有效结果）。"""
    ws = priv = peak = 0.0
    n = 0
    end = time.time() + seconds
    while time.time() < end:
        s = sample(pid)
        if s is None:
            log("  [%s] 进程已退出，提前结束采样" % label)
            break
        ws = max(ws, s[0])
        priv = max(priv, s[1])
        peak = max(peak, s[2])
        n += 1
        time.sleep(1.0)
    log("  [%s] 采样 %d 次：工作集峰值 %.0fMB | 私有内存峰值 %.0fMB | 进程峰值工作集 %.0fMB"
        % (label, n, ws, priv, peak))
    return {"label": label, "samples": n,
            "working_set_mb": round(ws), "private_mb": round(priv), "peak_ws_mb": round(peak)}


# ---------- UIA 辅助 ----------

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


def find_window(title):
    for hwnd, text in enum_windows():
        if title in text:
            return hwnd
    return None


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


def find_grid(win):
    """面积最大的 ListControl 就是缩略图网格（侧栏也有 ListBox，别拿错）。"""
    best, best_area = None, 0
    for ctrl in collect(win):
        if ctrl.ControlTypeName != "ListControl":
            continue
        r = ctrl.BoundingRectangle
        w, h = r.right - r.left, r.bottom - r.top
        if w * h > best_area:
            best, best_area = ctrl, w * h
    return best


def native_double_click(x, y):
    """原生鼠标双击。uia 的 Click(simulateMove=False) 连点两次不算双击
    （实测过：查看器不开），必须是 down/up/down/up 一口气。"""
    user32.SetCursorPos(x, y)
    time.sleep(0.3)
    user32.mouse_event(2, 0, 0, 0, 0); user32.mouse_event(4, 0, 0, 0, 0)
    time.sleep(0.08)
    user32.mouse_event(2, 0, 0, 0, 0); user32.mouse_event(4, 0, 0, 0, 0)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.0)


def main():
    if not os.path.exists(APP):
        log("找不到 %s，先 dotnet build" % APP)
        return 1

    kill_app()
    proc = subprocess.Popen([APP], cwd=os.path.dirname(APP))
    pid = proc.pid

    # ---- A: 空载 ----
    hwnd = None
    for _ in range(20):
        time.sleep(1.0)
        hwnd = find_window("媒体管理器")
        if hwnd:
            break
    if hwnd is None:
        log("主窗口没出现")
        kill_app()
        return 1
    time.sleep(20)  # 静置：库加载、缩略图、布局都稳定下来
    a = sample_seconds(pid, 10, "A 空载")

    # ---- B: 查看器打开（双击网格第一项） ----
    win = uia.ControlFromHandle(hwnd)
    grid = find_grid(win)
    b = None
    c = None
    if grid is None:
        log("没找到网格，跳过 B/C 场景")
    else:
        items = [c for c in collect(grid, maxdepth=6)
                 if c.ControlTypeName == "ListItemControl"]
        log("网格 %d 项" % len(items))
        if items:
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.5)
            r = items[0].BoundingRectangle
            native_double_click((r.left + r.right) // 2, (r.top + r.bottom) // 2)
            vhwnd = None
            for _ in range(10):
                time.sleep(1.0)
                vhwnd = find_window("查看器")
                if vhwnd:
                    break
            if vhwnd is None:
                log("查看器没打开（库可能是空的）")
            else:
                time.sleep(8)  # 大图解码
                b = sample_seconds(pid, 6, "B 查看器开")

                # ---- C: 查看器关 ----
                user32.PostMessageW(vhwnd, 0x0010, 0, 0)
                time.sleep(10)
                c = sample_seconds(pid, 6, "C 查看器关")

    kill_app()

    out = {
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "build": "Debug",
        "scenarios": [x for x in (a, b, c) if x],
    }
    with io.open(RESULT, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    log("结果已写入", RESULT)

    failed = bool(a is None)
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        code = main()
    finally:
        logf.close()
    raise SystemExit(code)
