# -*- coding: utf-8 -*-
"""v1.5.0「非核心类型外放」实机验证（一次性脚本）：

1) 主界面右键任意项，菜单里要有「用系统默认程序打开」
2) 搜索到库里的 .txt，双击 → 应该拉起系统默认程序，而不是内置窗口
3) 双击图片 → 仍然是内置查看器（不能被外放）

用法：python build/uia_verify_external_open.py
"""
import ctypes
import ctypes.wintypes as wt
import io
import os
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows", "MediaManager.App.exe")
LOG = os.path.join(HERE, "verify_external_log.txt")
logf = io.open(LOG, "w", encoding="utf-8")

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


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    log(("PASS " if ok else "FAIL ") + name + ((" | " + detail) if detail else ""))


def running_pids():
    out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True,
                         encoding="gbk", errors="ignore").stdout
    pids = {}
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2:
            try:
                pids[int(parts[1])] = parts[0]
            except ValueError:
                pass
    return pids


def collect(ctrl, depth=0, acc=None, maxdepth=16):
    acc = acc if acc is not None else []
    if depth > maxdepth:
        return acc
    try:
        for ch in ctrl.GetChildren():
            acc.append(ch)
            collect(ch, depth + 1, acc, maxdepth)
    except Exception:
        pass
    return acc


def find_grid(win):
    """网格 ListBox：在侧栏右边、面积最大的那个 ListControl（侧栏也有 ListBox，别拿错）。"""
    best, best_area = None, 0
    for ctrl in collect(win):
        if ctrl.ControlTypeName != "ListControl":
            continue
        r = ctrl.BoundingRectangle
        w, h = r.right - r.left, r.bottom - r.top
        if r.left > 180 and w > 400 and h > 200 and w * h > best_area:
            best, best_area = ctrl, w * h
    return best


def first_item(grid):
    for child in grid.GetChildren():
        if child.ControlTypeName == "ListItemControl":
            return child
    return None


def main():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.5)

    before = running_pids()
    proc = subprocess.Popen([APP], cwd=os.path.dirname(APP))
    log("启动", APP)

    win = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if not win.Exists(30, 1):
        check("主窗口出现", False)
        return 1
    check("主窗口出现", True)
    time.sleep(3)

    try:
        user32.SetForegroundWindow(win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.6)

    # ---- 1. 右键菜单里有「用系统默认程序打开」 ----
    grid = find_grid(win)
    found_menu = False
    if grid is not None:
        item = first_item(grid)
        if item is not None:
            log("网格首项:", item.Name)
            item.RightClick()
            time.sleep(1.2)
            menu = uia.MenuControl(searchDepth=6)
            if menu.Exists(4, 1):
                texts = [m.Name for m in menu.GetChildren()]
                log("菜单项:", texts)
                found_menu = any("用系统默认程序打开" in (t or "") for t in texts)
                uia.SendKeys("{ESC}")
            else:
                log("没找到右键菜单")
        else:
            log("网格里没找到项")
    else:
        log("没找到缩略图网格")
    check("右键菜单含「用系统默认程序打开」", found_menu)

    # ---- 2. 打开 txt 应外放 ----
    # 用命令行传文件（等价「右键 → 用媒体管理器打开」/ 拖拽进窗口），
    # 走的是和主界面双击同一条 MediaOpenStrategy + ShellHelper 分流
    txt = r"C:\测试\测试文档.txt"
    opened_external = False
    external_proc = ""
    if os.path.exists(txt):
        subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
        time.sleep(1.5)
        base = running_pids()
        subprocess.Popen([APP, txt], cwd=os.path.dirname(APP))
        log("命令行打开:", txt)
        time.sleep(6)

        after = running_pids()
        # 噪声：跑脚本的宿主进程（bash/dotnet/tail…）和系统后台进程。它们会因为
        # 管道和任务调度忽然冒出来，和「打开了 txt」毫无关系。不排掉的话，
        # 判据就退化成「有任意新进程」——那等于什么都没验（实测就抓到过 bash.exe 顶包）。
        NOISE = {
            "tasklist.exe", "conhost.exe", "mediamanager.app.exe",
            "bash.exe", "sh.exe", "git.exe", "dotnet.exe",
            "tail.exe", "head.exe", "grep.exe", "cat.exe", "sed.exe", "sort.exe",
            "python.exe", "pythonw.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
            "svchost.exe", "dllhost.exe", "runtimebroker.exe", "sihost.exe",
            "ctfmon.exe", "widgets.exe", "applicationframehost.exe",
            "searchprotocolhost.exe", "searchfilterhost.exe", "taskhostw.exe",
        }
        # 系统里真能「打开 .txt」的默认程序。看到其中一个才算证据；
        # 出现别的名字会判红并把名字打印出来 —— 由人确认后补进这张表。宁可红，不要假绿。
        DOC_HANDLERS = {
            "notepad.exe", "notepad++.exe", "code.exe", "vscodium.exe",
            "sublime_text.exe", "winword.exe", "wps.exe", "atom.exe",
            "gvim.exe", "emacs.exe", "typora.exe", "obsidian.exe", "everedit.exe",
        }

        newbies = [(pid, name) for pid, name in after.items()
                   if pid not in base and name.lower() not in NOISE]
        log("新增外部进程:", newbies)

        handlers = sorted({name for _pid, name in newbies if name.lower() in DOC_HANDLERS})
        opened_external = bool(handlers)
        if handlers:
            external_proc = "、".join(handlers)
        elif newbies:
            external_proc = "只看到非文档进程：" + "、".join(sorted({n for _, n in newbies}))
        else:
            external_proc = "没有任何新进程"

        for pid, _name in newbies:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)

        # 确认没有被内置窗口接管（TextPreviewWindow 已删除，这里再兜一层）
        builtin_txt = [w for w in uia.GetRootControl().GetChildren()
                       if w.ControlTypeName == "WindowControl" and "预览" in (w.Name or "")]
        if builtin_txt:
            opened_external = False
            log("出现了内置预览窗口:", [w.Name for w in builtin_txt])
            for w in builtin_txt:
                try:
                    w.GetPattern(uia.PatternId.WindowPattern).Close()
                except Exception:
                    pass
    else:
        log("测试文件不存在，跳过:", txt)

    check("打开 txt 交给系统默认程序", opened_external, external_proc)

    # ---- 3. 双击图片仍是内置查看器 ----
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1.5)
    subprocess.Popen([APP], cwd=os.path.dirname(APP))
    win = uia.WindowControl(searchDepth=1, Name="媒体管理器")
    if win.Exists(30, 1):
        time.sleep(4)
        try:
            user32.SetForegroundWindow(win.NativeWindowHandle)
        except Exception:
            pass
        time.sleep(0.6)

    builtin_ok = False
    grid3 = find_grid(win)
    if grid3 is not None:
        item3 = first_item(grid3)
        if item3 is not None:
            item3.DoubleClick()
            time.sleep(4)
            viewers = [w for w in uia.GetRootControl().GetChildren()
                       if w.ControlTypeName == "WindowControl" and "查看器" in (w.Name or "")]
            builtin_ok = len(viewers) > 0
            log("查看器窗口:", [w.Name for w in viewers])
            for w in viewers:
                try:
                    w.GetPattern(uia.PatternId.WindowPattern).Close()
                except Exception:
                    pass
    check("双击图片仍走内置查看器", builtin_ok)

    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)

    ok = all(r[1] for r in results)
    log("")
    log("=== 结果 %d/%d ===" % (sum(1 for r in results if r[1]), len(results)))
    for name, good, detail in results:
        log(("  ✓ " if good else "  ✗ ") + name + (("  " + detail) if detail else ""))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
