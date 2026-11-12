# -*- coding: utf-8 -*-
"""实机验证脚本共用的「抢前台」工具。

为什么需要它：
  Windows 有前台锁 —— 后台进程直接调 SetForegroundWindow 会被拒绝（返回 0），
  接下来的 SendKeys 于是打到当时真正在前台的那个窗口上（浏览器、终端……）。
  脚本里的表现是「窗口打不开 / 按钮点不动 / 按键没反应」，而功能其实好好的。

  v1.4.7 回归时连撞三次：用户开着浏览器，verify_vault_password / verify_external_open /
  verify_plugin_breaker 全红，但程序日志里连一次按键都没收到。

  之前靠 taskkill 清残留进程只能解决「自己的窗口互抢」，解决不了「别的程序占着前台」。

用法：
    from uiawin import force_foreground
    force_foreground(win.NativeWindowHandle)
"""
import ctypes
from ctypes import wintypes

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

user32.GetForegroundWindow.restype = wintypes.HWND
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
user32.BringWindowToTop.argtypes = [wintypes.HWND]
user32.BringWindowToTop.restype = wintypes.BOOL
user32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
user32.AttachThreadInput.restype = wintypes.BOOL
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int


def _is_foreground(hwnd):
    return user32.GetForegroundWindow() == hwnd


def force_foreground(hwnd, retries=3):
    """把窗口真正顶到前台。返回是否成功（调用方可以据此判定「本轮不作数」）。"""
    if not hwnd:
        return False
    if _is_foreground(hwnd):
        return True

    for _ in range(retries):
        fg = user32.GetForegroundWindow()
        tid_fg = user32.GetWindowThreadProcessId(fg, None) if fg else 0
        tid_me = kernel32.GetCurrentThreadId()
        attached = False
        if tid_fg and tid_fg != tid_me:
            attached = bool(user32.AttachThreadInput(tid_me, tid_fg, True))
        try:
            user32.BringWindowToTop(hwnd)
            user32.SetForegroundWindow(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(tid_me, tid_fg, False)

        if _is_foreground(hwnd):
            return True

        # 退路：按一下 ALT。系统会认为「用户刚有输入」，前台锁随之松开。
        # 单独发 ALT 不会给任何程序留下可见影响（不会触发菜单，因为没配其它键）。
        user32.keybd_event(0x12, 0, 0, 0)
        user32.keybd_event(0x12, 0, 2, 0)
        user32.SetForegroundWindow(hwnd)
        if _is_foreground(hwnd):
            return True

    return False


def foreground_title():
    """当前前台窗口标题，调试用（判断按键到底会打到哪去）。"""
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return ""
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value
