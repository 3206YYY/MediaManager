# -*- coding: utf-8 -*-
"""
实机验证：插件动作熔断（累计 3 次失败 → 整组菜单撤下，不再弹错误框）。

流程：
  1. 造一个「每次都炸」的插件（build/TestPlugins/FaultyPlugin 编译产物），
     丢进便携库 Data/plugins/faulty/，启动主程序。
  2. 点「🧩 插件」→「一定失败」×3：
     - 第 1、2 次：弹「插件出错」错误框 → Enter 关掉（「确定」是默认按钮）；
     - 第 3 次：不再弹框，状态栏出现「已停用」。
  3. 断言「🧩 插件」入口按钮整个消失（动作清空后自动隐藏）。

⚠ 脚本自身的坑（都真踩过）：
  - WPF 的 ContextMenu 不生成独立 #32768 窗口，菜单项就挂在主窗口的 UIA 树里；
  - 错误对话框**不能用 UIA 根枚举去找**——实验证明脚本在对话框存活期做根级
    GetRootControl/GetChildren 轮询时，对话框约 2 秒就会消失（机制未查明，
    疑与 UIA 客户端的焦点/事件订阅有关）。改用 Win32 EnumWindows 按标题找
    HWND + 发 Enter，完全绕开 UIA，对话框全程稳定；
  - 对话框里「确定」按钮的 UIA Name 走 ControlFromHandle 也读不到（Content
    是直接字符串），Enter 是唯一稳的路径。

结束时会恢复原状（插件目录是脚本自建的）。
"""
import ctypes
import os
import shutil
import subprocess
import sys
import time

import uiautomation as uia

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_EXE = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "MediaManager.App.exe")
DATA_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "Data")
PLUGIN_DLL = os.path.join(ROOT, "build", "TestPlugins", "FaultyPlugin", "bin", "Debug", "net8.0", "FaultyPlugin.dll")
PLUGIN_DIR = os.path.join(DATA_DIR, "plugins", "faulty")

LOG = os.path.join("build", "verify", "verify_plugin_breaker_log.txt")
SHOT_DIR = os.path.join("build", "verify", "shots_plugin_breaker")

user32 = ctypes.windll.user32

_logf = None
results = []


def log(*a):
    line = " ".join(str(x) for x in a)
    print(line)
    if _logf:
        _logf.write(line + "\n")
        _logf.flush()


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    log(("PASS" if ok else "FAIL"), name, ("| " + str(detail)) if detail else "")


def shot(ctrl, name):
    try:
        from PIL import ImageGrab
        r = ctrl.BoundingRectangle
        img = ImageGrab.grab((max(0, r.left), max(0, r.top), r.right, r.bottom))
        os.makedirs(SHOT_DIR, exist_ok=True)
        img.save(os.path.join(SHOT_DIR, name + ".png"))
        log("已截图:", name)
    except Exception as e:
        log("截图失败:", name, e)


def name_of(c):
    """UIA 读 Name 在窗口重绘/销毁瞬间会抛 COMError —— 统一吞掉。"""
    try:
        return c.Name or ""
    except Exception:
        return ""


def kill_app():
    r = subprocess.run(["tasklist"], capture_output=True)
    for line in r.stdout.decode("gbk", errors="ignore").splitlines():
        if "MediaManager.App" in line:
            pid = line.split()[1]
            subprocess.run(["taskkill", "/F", "/PID", pid], capture_output=True)
            log("已杀进程", pid)
    time.sleep(1.5)


def launch():
    for attempt in range(3):
        proc = subprocess.Popen([APP_EXE])
        time.sleep(13)
        for _ in range(3):
            for w in uia.GetRootControl().GetChildren():
                try:
                    if w.ControlTypeName == "WindowControl" and "媒体管理器" in name_of(w):
                        log("主窗口已出现")
                        return proc, w
                except Exception:
                    pass
            time.sleep(2)
        log("第 %d 次尝试没找到主窗口，重试" % (attempt + 1))
        kill_app()
    return None, None


def find_in(ctrl, text, types=None, visible_only=False):
    """深度遍历找包含 text 的后代控件。"""
    def walk(c, d):
        if d > 24:
            return None
        try:
            children = c.GetChildren()
        except Exception:
            return None
        for ch in children:
            tn = ch.ControlTypeName
            if not types or tn in types:
                if text in name_of(ch):
                    if visible_only:
                        try:
                            if ch.IsOffscreen:
                                continue
                        except Exception:
                            pass
                    return ch
            found = walk(ch, d + 1)
            if found is not None:
                return found
        return None
    return walk(ctrl, 0)


def click(c):
    try:
        c.GetPattern(uia.PatternId.InvokePattern).Invoke()
        return "invoke"
    except Exception:
        pass
    try:
        c.Click()
        return "click"
    except Exception as e:
        log("点击失败:", e)
        return "fail"


def open_plugin_menu(main_win):
    """点「🧩 插件」按钮。WPF 的 ContextMenu 不生成独立 #32768 窗口，
    菜单项就挂在主窗口自己的 UIA 树里。返回是否找到了「一定失败」。"""
    btn = find_in(main_win, "插件", types=("ButtonControl",), visible_only=True)
    if btn is None:
        return False
    click(btn)
    time.sleep(1.2)
    return find_in(main_win, "一定失败", types=("MenuItemControl", "ListItemControl")) is not None


def click_menu_item(main_win, text):
    item = find_in(main_win, text, types=("MenuItemControl", "ListItemControl"))
    if item is None:
        return False
    click(item)
    return True


def find_dialog_hwnd():
    """Win32 按标题找可见的「插件出错」对话框。不用 UIA 根枚举（见文件头说明）。"""
    out = []

    def cb(hwnd, _):
        length = user32.GetWindowTextLengthW(hwnd)
        if length:
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            if "插件出错" in buf.value and user32.IsWindowVisible(hwnd):
                out.append(hwnd)
        return True

    CMPFUNC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    user32.EnumWindows(CMPFUNC(cb), None)
    return out[0] if out else None


def wait_and_close_dialog(seconds=6):
    """等错误框出现并用 Enter 关掉（「确定」是 IsDefault 按钮）。返回是否出现过。"""
    t0 = time.time()
    while time.time() - t0 < seconds:
        hwnd = find_dialog_hwnd()
        if hwnd:
            time.sleep(0.4)  # 等它完成激活
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.3)
            uia.SendKeys("{Enter}", waitTime=0.3)
            time.sleep(0.8)
            return True
        time.sleep(0.2)
    return False


def main():
    global _logf
    os.makedirs(SHOT_DIR, exist_ok=True)
    _logf = open(LOG, "w", encoding="utf-8")
    kill_app()

    # 0) 预置：编译产物 + 插件目录
    check("测试插件 dll 存在", os.path.isfile(PLUGIN_DLL), PLUGIN_DLL)
    if os.path.isdir(PLUGIN_DIR):
        shutil.rmtree(PLUGIN_DIR, ignore_errors=True)
    os.makedirs(PLUGIN_DIR, exist_ok=True)
    shutil.copy2(PLUGIN_DLL, os.path.join(PLUGIN_DIR, "FaultyPlugin.dll"))

    try:
        proc, win = launch()
        check("主窗口启动", win is not None)
        if win is None:
            return 1

        # 第 1、2 次：弹错误框，Enter 关掉
        for i in (1, 2):
            check("第 %d 次打开插件菜单并看到「一定失败」" % i, open_plugin_menu(win))
            check("第 %d 次：点了菜单项" % i, click_menu_item(win, "一定失败"))
            dlg = wait_and_close_dialog()
            check("第 %d 次：弹了「插件出错」并关掉" % i, dlg)
            if not dlg:
                shot(win, "%d_no_dialog" % i)
                return 1

        # 第 3 次：熔断 —— 不弹框，状态栏出现「已停用」
        check("第 3 次打开插件菜单", open_plugin_menu(win))
        check("第 3 次：点了菜单项", click_menu_item(win, "一定失败"))
        dlg = wait_and_close_dialog(seconds=4)
        check("第 3 次：不再弹错误框（熔断生效）", not dlg)
        if dlg:
            shot(win, "3_dialog_still_pops")
        close_menu()

        # 状态栏提示 + 入口按钮消失
        time.sleep(1.5)
        status = find_in(win, "已停用")
        check("状态栏出现「已停用」提示", status is not None)
        if status is None:
            shot(win, "3_no_status")

        # 重开菜单前：按钮应整个消失（动作清空 → RefreshPluginEntry 藏掉入口）
        time.sleep(1)
        btn_gone = find_in(win, "插件", types=("ButtonControl",), visible_only=True) is None
        check("「🧩 插件」入口按钮已自动隐藏", btn_gone)
        if not btn_gone:
            shot(win, "3_button_still_there")

        check("主程序仍然活着", proc.poll() is None)

        kill_app()
        return 0 if all(ok for _, ok, _ in results) else 1
    finally:
        kill_app()
        shutil.rmtree(PLUGIN_DIR, ignore_errors=True)
        try:
            plugins_root = os.path.dirname(PLUGIN_DIR)
            if os.path.isdir(plugins_root) and not os.listdir(plugins_root):
                os.rmdir(plugins_root)
        except Exception:
            pass
        _logf.close()


def close_menu():
    uia.SendKeys("{Esc}", waitTime=0.3)
    time.sleep(0.5)


if __name__ == "__main__":
    sys.exit(main())
