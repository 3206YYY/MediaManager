# -*- coding: utf-8 -*-
"""
实机验证：嵌套根目录（父目录 + 子目录都被添加过）点进去必须能显示内容。

复刻测试员的真实数据：库里同时有 `C:\\测试`（86 条）和 `C:\\测试\\实拍`（0 条记录，
文件在扫父目录时已入库并归属父级）。修复前点「实拍」= 共 0 项；修复后按路径前缀
查询 = 27 条。

用**真实库的副本**跑：先备份，验证后还原，绝不留下副作用。

UIA 两个老坑（本脚本按坑设计）：
1. 后台进程调 SetForegroundWindow 常常抢不到前台，鼠标点击只会「激活窗口」而不生效
   → 能用 UIA 模式的都用模式（Button=Invoke、ListItem=Select），不依赖前台。
2. WPF 里 Collapsed 的元素**仍出现在 UIA 树里**（IsOffscreen=True），
   「文字存在」不等于「用户看得见」→ 一律用 IsOffscreen 判可见。
"""
import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import sys
import time

ctypes.windll.shcore.SetProcessDpiAwareness(2)
user32 = ctypes.windll.user32

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")

# 允许指向任意构建（例如 dist 里的 Release 预览版）：验证「用户实际会装的这个 exe」
if os.environ.get("MM_VERIFY_APP"):
    APP = os.environ["MM_VERIFY_APP"]
    APP_DIR = os.path.dirname(APP)
    print("被测 exe：", APP)
PORTABLE_MARKER = os.path.join(APP_DIR, "portable.marker")
PORTABLE_OFF = PORTABLE_MARKER + ".verify_off"
DB = os.path.join(os.environ["LOCALAPPDATA"], "MediaManager", "library.db")
BAK = DB + ".verify_bak"

CHILD, PARENT = "实拍", "测试"
PARENT_PATH = "C:\\测试"
CHILD_PATH = PARENT_PATH + "\\" + CHILD


def expected_count(path):
    """从库本身算期望值：普通媒体 + 未缺失 + 路径前缀匹配。

    不写死数字：库里少一条被标缺失的文件就会假红（第一版写 86，实际 85）。
    """
    import sqlite3
    tmp = DB + ".query_copy"
    shutil.copy2(DB, tmp)
    for suffix in ("-wal", "-shm"):
        p = DB + suffix
        if os.path.exists(p):
            shutil.copy2(p, tmp + suffix)
    try:
        con = sqlite3.connect(tmp)
        con.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        esc = path.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
        pattern = esc + "\\\\%"
        n = con.execute(
            "SELECT COUNT(*) FROM MediaItem WHERE IsMissing=0 AND IsPrivate=0 "
            "AND Path LIKE ? ESCAPE '\\'", (pattern,)).fetchone()[0]
        con.close()
        return n
    finally:
        for f in (tmp, tmp + "-wal", tmp + "-shm"):
            if os.path.exists(f):
                os.remove(f)


import uiautomation as uia

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + ("  | " + detail if detail else ""))


def find_window(title):
    res = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if title in b.value:
                    res.append(h)
        return True

    user32.EnumWindows(cb, 0)
    return res


def name_of(c):
    try:
        return c.Name or ""
    except Exception:
        return ""


def collect(ctrl, acc=None, d=0, maxd=24):
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


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    time.sleep(1)


def main():
    if not os.path.exists(DB):
        print("找不到真实库：", DB)
        sys.exit(1)

    kill_app()
    shutil.copy2(DB, BAK)

    # 开发 exe 旁边有 portable.marker，会改读 bin/Data 下的开发库 —— 那不是用户的库。
    # 临时把 marker 挪走，让它走默认的 %LOCALAPPDATA% 库；结束时必须挪回来。
    marker_moved = False
    if os.path.exists(PORTABLE_MARKER):
        os.rename(PORTABLE_MARKER, PORTABLE_OFF)
        marker_moved = True

    proc = subprocess.Popen([APP], cwd=os.path.dirname(APP))
    try:
        hwnd = None
        for _ in range(30):
            time.sleep(1)
            w = find_window("媒体管理器")
            if w:
                hwnd = w[0]
                break
        if hwnd is None:
            check("主窗口启动", False)
            return
        check("主窗口启动", True)

        expect_child = expected_count(CHILD_PATH)
        expect_parent = expected_count(PARENT_PATH)
        print("库里的期望值：子 %d 条 / 父 %d 条" % (expect_child, expect_parent))

        # 等初始化完成（导航 + 首屏）
        time.sleep(6)
        win = uia.ControlFromHandle(hwnd)

        # ---- UIA 原语：只用模式，不碰前台 ----
        def sidebar_item(title):
            """侧栏（左 400px 内）标题恰好等于 title 的列表项；优先可见的。"""
            hits = []
            for c in collect(win, maxd=10):
                if c.ControlTypeName != "ListItemControl" or name_of(c) != title:
                    continue
                try:
                    if c.BoundingRectangle.left >= 400:
                        continue
                    hits.append((1 if c.IsOffscreen else 0, c))
                except Exception:
                    continue
            hits.sort(key=lambda p: p[0])
            return hits[0][1] if hits else None

        def select(ctrl):
            """选中列表项：优先 SelectionItemPattern（不需要窗口在前台）。

            WPF 列表虚拟化会回收 Item 容器：列表一重建，旧的 UIA 元素就失效
            （读 BoundingRectangle 直接抛 COMError）。所以这里只接受「刚刚查到」的元素，
            调用方每次重新 sidebar_item() 取。
            """
            try:
                ctrl.GetSelectionItemPattern().Select()
                return True
            except Exception:
                pass
            try:
                r = ctrl.BoundingRectangle
            except Exception:
                return False
            cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
            for _ in range(3):
                user32.SetForegroundWindow(hwnd)
                time.sleep(0.4)
                if user32.GetForegroundWindow() != hwnd:
                    continue
                user32.SetCursorPos(cx, cy)
                time.sleep(0.2)
                user32.mouse_event(2, 0, 0, 0, 0)
                user32.mouse_event(4, 0, 0, 0, 0)
                return True
            return False

        def expander_of(item):
            """列表项里的展开三角（Button），只要可见的。"""
            for c in collect(item, maxd=4):
                if c.ControlTypeName != "ButtonControl":
                    continue
                try:
                    if not c.IsOffscreen:
                        return c
                except Exception:
                    continue
            return None

        def hit_expander(ctrl):
            try:
                ctrl.GetInvokePattern().Invoke()
                return True
            except Exception as e:
                print("  Invoke 失败（%s），退回鼠标点击" % e)
            r = ctrl.BoundingRectangle
            cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.3)
            user32.SetCursorPos(cx, cy)
            time.sleep(0.2)
            user32.mouse_event(2, 0, 0, 0, 0)
            user32.mouse_event(4, 0, 0, 0, 0)
            return True

        def texts(visible_only=True):
            out = []
            for c in collect(win):
                if c.ControlTypeName != "TextControl":
                    continue
                if visible_only:
                    try:
                        if c.IsOffscreen:
                            continue
                    except Exception:
                        continue
                out.append(name_of(c))
            return out

        def subtitles():
            return [t for t in texts() if t.startswith("在 ")]

        def select_and_wait(item, seconds=4):
            select(item)
            time.sleep(seconds)

        def count_shown(expect):
            """状态栏出现「共 N 项」或「已加载 x / 共 N 项」都算。"""
            for t in texts():
                if ("共 %d 项" % expect) in t or ("/ 共 %d 项" % expect) in t:
                    return t
            return None

        # ① 父目录在侧栏（顶层根目录）
        parent = sidebar_item(PARENT)
        check("侧栏存在父文件夹节点", parent is not None, PARENT)
        if parent is None:
            return

        # ② 顶层根目录的副标题是盘符（「在 C:」），不是整条路径
        subs = subtitles()
        check("顶层根目录副标题是盘符", any(t.strip() in ("在 C:", "在 D:", "在 E:") for t in subs),
              "subs=%s" % subs[:3])

        # ③ 默认收起：子节点不该出现在侧栏
        check("默认收起：看不到子文件夹", sidebar_item(CHILD) is None)

        # ④ 展开三角 → 子节点出现
        exp = expander_of(parent)
        check("父文件夹有展开三角", exp is not None)
        if exp is None:
            return

        hit_expander(exp)
        time.sleep(2)
        child = sidebar_item(CHILD)
        check("展开后出现子文件夹节点", child is not None, CHILD)
        if child is None:
            return

        # ⑤ 子节点有缩进 + 副标题写明「在 父目录」（不是整条路径，也不能是本节点路径）
        # 缩进加在**行内**（模板里 Grid 的 Margin），ListBoxItem 自己仍从同一条左边开始，
        # 所以要比的是标题文字的左边界，不是整行的左边界。
        def title_left(title):
            best = None
            for c in collect(win, maxd=10):
                if c.ControlTypeName != "TextControl" or name_of(c) != title:
                    continue
                try:
                    r = c.BoundingRectangle
                except Exception:
                    continue
                if r.left >= 400 or r.right <= r.left:
                    continue
                best = r.left if best is None else min(best, r.left)
            return best

        cl, pl = title_left(CHILD), title_left(PARENT)
        check("子文件夹有缩进", cl is not None and pl is not None and cl > pl,
              "child_text_left=%s parent_text_left=%s" % (cl, pl))

        child_subs = subtitles()
        check("子节点副标题标明「在 父目录」",
              any(t.startswith("在 ") and PARENT in t for t in child_subs),
              "subs=%s" % child_subs[:4])

        # ⑥ 点「实拍」→ 必须显示条目（修复前是共 0 项）
        select_and_wait(child)
        shown = count_shown(expect_child)
        if not shown:
            again = sidebar_item(CHILD)
            if again is not None:
                select_and_wait(again)
            shown = count_shown(expect_child)
        zero = [t for t in texts() if "共 0 项" in t]
        check("点「实拍」显示 %d 项" % expect_child, shown is not None,
              "shown=%r zero=%s" % (shown, zero[:1]))

        # ⑦ 点「测试」→ 父目录含全部
        p3 = sidebar_item(PARENT)
        if p3 is not None:
            select_and_wait(p3)
        shown_p = count_shown(expect_parent)
        if not shown_p:
            p4 = sidebar_item(PARENT)
            if p4 is not None:
                select_and_wait(p4)
            shown_p = count_shown(expect_parent)
        check("点「测试」显示 %d 项" % expect_parent, shown_p is not None, "shown=%r" % (shown_p,))

        # ⑧ 再点一次三角 → 收起，子节点消失
        p5 = sidebar_item(PARENT)
        exp2 = expander_of(p5) if p5 is not None else None
        if exp2 is not None:
            hit_expander(exp2)
            time.sleep(2)
            check("再点三角可收起", sidebar_item(CHILD) is None)
        else:
            check("再点三角可收起", False, "找不到展开三角")

    finally:
        kill_app()
        # 还原真实库（验证过程只读，但保险起见覆盖回去）
        if os.path.exists(BAK):
            shutil.copy2(BAK, DB)
            os.remove(BAK)
        # 还原备份时必须连 -wal/-shm 一起删：SQLite 下次打开会把 WAL 里的页重放到
        # 刚还原的库上 —— 等于「备份白还了」（实测：验证跑完 root 8 又没了）。
        for suffix in ("-wal", "-shm"):
            side = DB + suffix
            if os.path.exists(side):
                os.remove(side)
        # 便携标记归位
        if marker_moved and os.path.exists(PORTABLE_OFF):
            os.replace(PORTABLE_OFF, PORTABLE_MARKER)

    failed = [r for r in results if not r[1]]
    print("\n%d/%d" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
