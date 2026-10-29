# -*- coding: utf-8 -*-
"""v1.5.0 三个视觉/功能任务的实机验证（一次性脚本）。

1) 日记编辑器里的 DatePicker：深色模式下弹出来的日历必须是深底浅字，
   点某一天要能真的写回日期框（模板写错的话日历会空着或点了没反应）
2) 私密空间顶部「⭐ 收藏」开关：套上胶囊样式，深色下不再是一块白板
3) 时光轴年视图：12 个方块、点某月能跳回该月日历

用法：python build/uia_verify_diary_calendar.py
"""
import ctypes
import io
import os
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
SHOTS = os.path.join(HERE, "shots_cal")
LOG = os.path.join(HERE, "verify_cal_log.txt")
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


def shot(ctrl, tag, pad=0):
    try:
        r = ctrl.BoundingRectangle
        if r.right - r.left <= 0:
            return None
        img = ImageGrab.grab(bbox=(r.left - pad, r.top - pad, r.right + pad, r.bottom + pad))
        path = os.path.join(SHOTS, tag + ".png")
        img.save(path)
        return path
    except Exception as e:
        log("截图失败", tag, e)
        return None


def shot_screen(tag):
    img = ImageGrab.grab()
    path = os.path.join(SHOTS, tag + ".png")
    img.save(path)
    return path


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


def find_by_name(root, text, types=None):
    for c in collect(root):
        if types and c.ControlTypeName not in types:
            continue
        if text in (c.Name or ""):
            return c
    return None


def wait_by_name(root, text, types=None, seconds=6):
    end = time.time() + seconds
    while time.time() < end:
        c = find_by_name(root, text, types)
        if c is not None:
            return c
        time.sleep(0.25)
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


def top_windows():
    out = []
    for w in uia.GetRootControl().GetChildren():
        try:
            out.append(w)
        except Exception:
            pass
    return out


def dump_tops(tag):
    log("---- 顶层窗口（%s）----" % tag)
    for w in top_windows():
        try:
            r = w.BoundingRectangle
            log("  type=%s class=%s name=%r rect=(%d,%d,%d,%d)" % (
                w.ControlTypeName, w.ClassName, w.Name, r.left, r.top, r.right, r.bottom))
        except Exception as e:
            log("  读取失败", e)


def find_popup(pid, anchor):
    """找出日历弹窗那个 HWND。

    不能用类名「Popup」判断：这个 WPF 版本给弹层用的是别的类名，
    也不能靠 UIA 树（弹层有时挂在 owner 下面、有时不在）。
    改成「本进程所有可见 HWND 里，紧贴在日期框下沿、尺寸像日历的那一个」。
    """
    al, at, ar, ab = anchor[0], anchor[1], anchor[2], anchor[3]
    found = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(hwnd, _lparam):
        p = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid or not user32.IsWindowVisible(hwnd):
            return True

        r = ctypes.wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if not (140 <= w <= 460 and 140 <= h <= 460):
            return True
        # 弹在日期框正下方（允许稍微偏一点）
        if abs(r.top - ab) > 60:
            return True
        if r.left < al - 80 or r.left > ar + 80:
            return True

        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, buf, 256)
        found.append((hwnd, r.left, r.top, r.right, r.bottom, buf.value))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def avg_brightness(ctrl):
    """取控件区域的平均亮度（0-255），用来判断弹出来的日历是不是还是一块白板。"""
    try:
        r = ctrl.BoundingRectangle
        if r.right - r.left < 20:
            return None
        img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom)).convert("L")
        px = list(img.getdata())
        return sum(px) / len(px)
    except Exception:
        return None


def datebox_text(dp):
    """DatePicker 的 UIA Name 是空的，实际日期在内层编辑框的 Value 里。"""
    for c in collect(dp):
        if c.ControlTypeName == "EditControl":
            try:
                return c.GetValuePattern().Value or ""
            except Exception:
                pass
    return ""


def main():
    kill_app()
    main_win = launch()
    if main_win is None:
        check("主窗口出现", False)
        return 1
    check("主窗口出现", True)

    # ---- 打开日记窗口（侧栏「日记」） ----
    diary_item = None
    for c in collect(main_win):
        if c.ControlTypeName == "ListItemControl" and "日记" in (c.Name or ""):
            diary_item = c
            break
    check("侧栏找到「日记」入口", diary_item is not None)
    if diary_item is None:
        kill_app()
        return 1

    # 侧栏点击有时会落在窗口还没完全就绪的那一瞬间，点两次兜底
    diary = uia.WindowControl(searchDepth=2, Name="日记")
    for attempt in range(3):
        try:
            diary_item.Click()
        except Exception as e:
            log("点击日记失败", attempt, e)
        time.sleep(2.2)
        if diary.Exists(1, 0.5):
            break

    check("日记窗口打开", diary.Exists(3, 0.5))
    if not diary.Exists(1, 0.5):
        kill_app()
        return 1
    time.sleep(1.2)
    shot(diary, "01_diary_month")

    # ---- 任务 3：年视图 ----
    year_btn = wait_by_name(diary, "年视图", types=("ButtonControl",))
    check("顶部有「📅 年视图」按钮", year_btn is not None)
    if year_btn is not None:
        click(year_btn)
        time.sleep(1.5)
        shot(diary, "02_diary_year")

        # 12 个方块：找「N 月」文本
        month_texts = [c for c in collect(diary)
                       if c.ControlTypeName == "TextControl"
                       and (c.Name or "").replace(" ", "").endswith("月")
                       and (c.Name or "").replace(" ", "")[:-1].isdigit()]
        check("年视图有 12 个月份方块", len(month_texts) == 12, "找到 %d 个" % len(month_texts))

        # 点 1 月 → 应跳回月视图并停在 1 月
        jan = next((c for c in month_texts if (c.Name or "").replace(" ", "") == "1月"), None)
        if jan is not None:
            try:
                jan.Click()
            except Exception as e:
                log("点 1 月失败", e)
            time.sleep(1.6)
            title = find_by_name(diary, "年 1 月", types=("TextControl",))
            shot(diary, "03_diary_after_jump")
            check("点「1 月」跳回该月日历", title is not None)

        # 回到年视图，供后面截图留档
        backup = wait_by_name(diary, "年视图", types=("ButtonControl",), seconds=2)
        if backup is not None:
            click(backup)
            time.sleep(1.2)

    # ---- 任务 1：DatePicker 深色日历 ----
    new_btn = wait_by_name(diary, "写一篇", types=("ButtonControl",))
    check("找到「＋ 写一篇」", new_btn is not None)
    editor = None
    if new_btn is not None:
        click(new_btn)
        time.sleep(2.0)
        editor = uia.WindowControl(searchDepth=2, Name="写一篇")
        check("编辑器窗口打开", editor.Exists(3, 0.5))

    if editor is not None and editor.Exists(1, 0.5):
        shot(editor, "04_editor")

        dp = None
        for c in collect(editor):
            if (c.AutomationId or "") == "DateBox":
                dp = c
                break
        check("找到日期选择框 DateBox", dp is not None)

        if dp is not None:
            # DatePicker 里那个日历小按钮
            cal_btn = None
            for c in collect(dp):
                if c.ControlTypeName == "ButtonControl":
                    cal_btn = c
                    break
            check("日期框右侧有打开日历的按钮", cal_btn is not None)

            if cal_btn is not None:
                click(cal_btn)
                time.sleep(1.5)

            # 弹出层是个独立顶层 HWND，贴在日期框正下方
            dr = dp.BoundingRectangle
            anchor = (dr.left, dr.top, dr.right, dr.bottom)
            handles = find_popup(editor.ProcessId, anchor)
            log("候选弹层 HWND:", handles)
            popup = None
            if handles:
                hwnd, pl, pt, pr, pb, cls = max(handles, key=lambda h: (h[3] - h[1]) * (h[4] - h[2]))
                log("选中弹层:", cls, (pl, pt, pr, pb))
                popup = uia.ControlFromHandle(hwnd)
            check("日历弹窗出现", popup is not None)
            if popup is None:
                dump_tops("找日历弹窗")
                shot_screen("05_no_popup_screen")
            else:
                img = ImageGrab.grab(bbox=(pl, pt, pr, pb))
                img.save(os.path.join(SHOTS, "05_calendar_popup.png"))
                gray = img.convert("L")
                px = list(gray.getdata())
                bright = sum(px) / len(px)
                log("日历弹窗平均亮度:", bright)
                # 深色主题下整块白底平均亮度接近 240+，深底应该是 40-110
                check("日历弹窗是深色底（不是白板）", bright < 150, "亮度 %.0f" % bright)

                # 点一个日期，必须真的写回日期框
                kids = collect(popup)
                btn_names = [c.Name for c in kids if c.ControlTypeName == "ButtonControl"]
                log("弹层里的按钮:", btn_names[:40])

                day15 = None
                for c in kids:
                    if c.ControlTypeName != "ButtonControl":
                        continue
                    n = (c.Name or "").strip()
                    if n == "15" or n.endswith("15日") or n.endswith("-15"):
                        day15 = c
                        break
                check("日历里能找到「15」这一格", day15 is not None,
                      "按钮名样例 %s" % (btn_names[:6],))

                before = datebox_text(dp)
                if day15 is not None:
                    click(day15)
                    time.sleep(1.4)
                    after = datebox_text(dp)
                    log("日期框：", before, "->", after)
                    check("点 15 号后日期真的变了", before != after and "15" in after,
                          "%s -> %s" % (before, after))
                    shot(editor, "06_editor_after_pick")

                # 关掉日历弹窗（可能还开着）
                if popup.Exists(0.5, 0.2):
                    uia.SendKeys("{ESC}")
                    time.sleep(0.5)

    # ---- 任务 2：私密空间「⭐ 收藏」开关 ----
    for w in top_windows():
        if "写一篇" in (w.Name or ""):
            try:
                w.GetPattern(uia.PatternId.WindowPattern).Close()
            except Exception:
                pass
    time.sleep(1.0)

    try:
        user32.SetForegroundWindow(main_win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.5)
    uia.SendKeys("{Ctrl}l", waitTime=0.2)
    time.sleep(2.2)
    vault = uia.WindowControl(searchDepth=2, Name="🔒 私密空间")
    check("私密空间打开", vault.Exists(3, 0.5))
    if vault.Exists(1, 0.5):
        fav = wait_by_name(vault, "收藏", types=("ButtonControl",))
        check("顶栏「⭐ 收藏」是按钮/开关控件", fav is not None)
        if fav is not None:
            r = fav.BoundingRectangle
            shot(fav, "07_fav_toggle", pad=6)
            try:
                img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom)).convert("RGB")
                w, h = img.size
                # 取边缘 2px 内的像素估底色：白板的话是 240+ 的纯白
                edge = [img.getpixel((x, 1)) for x in range(0, w, 3)] + \
                       [img.getpixel((x, h - 2)) for x in range(0, w, 3)]
                avg = sum((p[0] + p[1] + p[2]) / 3 for p in edge) / max(1, len(edge))
                log("收藏开关边缘平均亮度:", avg)
                check("收藏开关底色不再是纯白", avg < 200, "亮度 %.0f" % avg)
            except Exception as e:
                log("取样失败", e)
                check("收藏开关底色不再是纯白", False, str(e))

            # 点一下确认能切到选中态（选中态是主色，更亮）
            click(fav)
            time.sleep(1.0)
            shot(fav, "08_fav_checked", pad=6)
            log("收藏开关点击后 Name:", fav.Name)

    kill_app()

    ok = all(r[1] for r in results)
    log("")
    log("=== 结果 %d/%d ===" % (sum(1 for r in results if r[1]), len(results)))
    for name, good, detail in results:
        log(("  ✓ " if good else "  ✗ ") + name + (("  " + detail) if detail else ""))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
