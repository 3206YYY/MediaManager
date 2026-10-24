# -*- coding: utf-8 -*-
"""一次性：启动 1.3.2 主窗口，设为 16:10 窗口后 PrintWindow 抓图，输出宣传用截图。

用法： python build/_shot_new_ui.py <输出 jpg 路径>
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import Image

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "dist", "MediaManager-win-x64", "MediaManager.App.exe")
TARGET = (1750, 1000)


def exe_of(hwnd):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    h = kernel32.OpenProcess(0x1000, False, pid.value)
    if not h:
        return ""
    buf = ctypes.create_unicode_buffer(1024)
    n = wt.DWORD(1024)
    ok = kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n))
    kernel32.CloseHandle(h)
    return os.path.basename(buf.value).lower() if ok else ""


def find_main():
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        if "mediamanager" not in exe_of(hwnd):
            return True
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        if r.right - r.left > 600 and r.bottom - r.top > 400:
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found


def grab(hwnd, out):
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE
    try:
        user32.SetForegroundWindow(hwnd)
    except Exception:
        pass
    time.sleep(1.5)
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    ok = user32.PrintWindow(hwnd, mem, 2)   # PW_RENDERFULLCONTENT

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    bi = BIH()
    bi.biSize = ctypes.sizeof(bi)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount, bi.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    img = Image.frombuffer("RGB", (w, h), buf.raw, "raw", "BGRX", 0, 1)
    gdi32.DeleteDC(mem)
    gdi32.DeleteObject(bmp)
    user32.ReleaseDC(hwnd, hdc)
    return ok, img


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "shot.jpg")
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE))
    hwnd = None
    for _ in range(60):
        time.sleep(1)
        wins = find_main()
        if wins:
            hwnd = wins[0]
            break
    if not hwnd:
        print("NO_WINDOW")
        return 1
    # 固定窗口尺寸（含边框），保证 16:10
    user32.MoveWindow(hwnd, 40, 20, TARGET[0], TARGET[1], True)
    time.sleep(6)          # 等缩略图加载完
    ok, img = grab(hwnd, out)

    # 归一化：等比铺满裁切到 1750x1000
    tw, th = TARGET
    sw, sh = img.size
    scale = max(tw / sw, th / sh)
    img = img.resize((round(sw * scale), round(sh * scale)), Image.LANCZOS)
    w, h = img.size
    img = img.crop(((w - tw) // 2, (h - th) // 2, (w - tw) // 2 + tw, (h - th) // 2 + th))
    img.save(out, "JPEG", quality=94)
    print(f"PrintWindow ok={ok}  raw={sw}x{sh}  -> {out}  {img.size}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
