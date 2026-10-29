# -*- coding: utf-8 -*-
"""v1.4.3 验证：首次启动的「只看图和视频 / 我要整理」二选一。

为什么要有这个脚本：这个功能最大的风险不是「窗口弹不出来」（一眼能看见），
而是**弹错人** —— 老用户升级到新版本，一开程序冒出个选择框，
第一反应是「我的库是不是被清空了」。这种惊吓比「侧栏有点多」严重得多。

所以脚本验三件事：
  A. 空库（新装）→ 弹窗；选「只看图」→ 侧栏真的收窄了，设置真的写进去了
  B. 重启 → 不再弹（同一台机器只该被问一次）
  C. 恢复真实库（有 27 条媒体）+ 删掉「问过了」标记（模拟老用户升级）
     → 不弹窗，但标记被静默补上

⚠ 会临时替换开发库，跑完（含异常）恢复。跑之前请关掉 MediaManager.App.exe。

用法：python build/verify/verify_first_run.py
前置：pip install uiautomation pillow
"""
import ctypes
import io
import os
import shutil
import sqlite3
import subprocess
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
SHOTS = os.path.join(HERE, "shots_first_run")
LOG = os.path.join(HERE, "verify_first_run_log.txt")
os.makedirs(SHOTS, exist_ok=True)
logf = io.open(LOG, "w", encoding="utf-8")

# 数据目录：跟程序同一套判断（exe 旁有 portable.marker → 数据在 exe/Data）
DATA_DIR = os.path.join(os.environ["LOCALAPPDATA"], "MediaManager")
if os.path.exists(os.path.join(APP_DIR, "portable.marker")):
    DATA_DIR = os.path.join(APP_DIR, "Data")
DB = os.path.join(DATA_DIR, "library.db")
BAK_DIR = os.path.join(DATA_DIR, "_verify_first_run_bak")

# 侧栏文本：名字独特，不会和设置页/状态栏撞
SIDEBAR_MUST_GO = ["📔 日记", "🔒 私密空间", "🗑 最近删除"]
SIDEBAR_MUST_KEEP = ["✨ 发现", "🏠 全部媒体", "⭐ 收藏"]

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


def collect(ctrl, acc=None, depth=0, maxdepth=24):
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


def top_windows():
    out = []
    for w in uia.GetRootControl().GetChildren():
        try:
            out.append(w)
        except Exception:
            pass
    return out


def name_of(ctrl):
    """
    UIA 读 Name 会抛 COMError（-2147220991 事件无法调用任何订户）——
    窗口正在重绘/销毁的瞬间，元素句柄会失效。这不是被测程序的问题，
    是 UIA 自己的时序，所以统一吞掉当成「没名字」。
    """
    try:
        return ctrl.Name or ""
    except Exception:
        return ""


def find_in_window(win, text, types=None, visible_only=False):
    """
    注意：WPF 里 Visibility=Collapsed 的控件**仍在 UIA 树里**，只是 IsOffscreen=True
    （BoundingRectangle 还留着上一次布局的位置，看起来像"它在那儿"）。
    判断「侧栏有没有藏掉某项」必须加 visible_only，否则永远判成"没藏"。
    """
    for c in collect(win):
        try:
            if types and c.ControlTypeName not in types:
                continue
        except Exception:
            continue
        if text in name_of(c):
            if visible_only:
                try:
                    if c.IsOffscreen:
                        continue
                except Exception:
                    pass
            return c
    return None


def main_window():
    """主窗口和首启窗口标题都叫「媒体管理器」，靠侧栏文本来认主窗口。"""
    for w in top_windows():
        for t in SIDEBAR_MUST_KEEP:
            if find_in_window(w, t, visible_only=True):
                return w
    return None


def first_run_window():
    """首启窗靠卡片按钮的 AutomationProperties.Name 认。"""
    for w in top_windows():
        if find_in_window(w, "只看图和视频", ("ButtonControl",)):
            return w
    return None


def wait_for(fn, seconds=20, interval=0.4):
    end = time.time() + seconds
    while time.time() < end:
        v = fn()
        if v is not None:
            return v
        time.sleep(interval)
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


def launch(wait=30):
    subprocess.Popen([APP], cwd=APP_DIR)
    win = wait_for(main_window, wait)
    if win is None:
        return None
    time.sleep(3.0)  # 等 InitializeAsync + 首启判定走完
    return win


def read_settings():
    con = sqlite3.connect(DB)
    try:
        rows = con.execute(
            "select Key, Value from AppSetting where Key in ('Ui.SimpleMode','Ui.FirstRunDone')").fetchall()
        return dict(rows)
    finally:
        con.close()


def backup_db():
    """把真实库挪走，让程序自己建一个空库（= 全新安装）。"""
    if os.path.isdir(BAK_DIR):
        shutil.rmtree(BAK_DIR, ignore_errors=True)
    os.makedirs(BAK_DIR, exist_ok=True)
    moved = 0
    for suffix in ("", "-wal", "-shm"):
        src = DB + suffix
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(BAK_DIR, "library.db" + suffix))
            moved += 1
    log("备份库文件 %d 个 →" % moved, BAK_DIR)
    for suffix in ("", "-wal", "-shm"):
        p = DB + suffix
        if os.path.exists(p):
            os.remove(p)


def restore_db():
    if not os.path.isdir(BAK_DIR):
        return False
    for suffix in ("", "-wal", "-shm"):
        p = DB + suffix
        if os.path.exists(p):
            os.remove(p)
    restored = 0
    for suffix in ("", "-wal", "-shm"):
        src = os.path.join(BAK_DIR, "library.db" + suffix)
        if os.path.exists(src):
            shutil.copy2(src, DB + suffix)
            restored += 1
    shutil.rmtree(BAK_DIR, ignore_errors=True)
    log("恢复库文件 %d 个" % restored)
    return restored > 0


def assert_sidebar(win, tag):
    """侧栏断言：极简模式下该藏的藏了、该留的留着。"""
    for t in SIDEBAR_MUST_GO:
        found = find_in_window(win, t, visible_only=True) is not None
        check("%s：侧栏已收起「%s」" % (tag, t), not found)
    for t in SIDEBAR_MUST_KEEP:
        found = find_in_window(win, t, visible_only=True) is not None
        check("%s：侧栏仍保留「%s」" % (tag, t), found)
    # 标签分组也该一起收（以前「极简」里还留着新建标签按钮，和分类被藏掉自相矛盾）
    check("%s：侧栏已收起「新建标签」" % tag,
          find_in_window(win, "新建标签", visible_only=True) is None)


def run_phase_a():
    """A：空库 → 弹窗 → 选「只看图」→ 侧栏收窄 + 设置落库"""
    log("\n===== A. 全新安装 =====")
    win = launch()
    check("主窗口起来了", win is not None)
    if win is None:
        return None

    fw = wait_for(first_run_window, 8)
    check("首启选择窗出现", fw is not None)
    if fw is None:
        shot(win, "A_0_no_first_run")
        return win

    shot(fw, "A_1_first_run")
    # 非模态：主窗口必须同时在，不能是 ShowDialog 把整个启动流程卡住
    check("首启窗是非模态（主窗口同时在）", main_window() is not None)
    check("标题文案在", find_in_window(fw, "先问一句") is not None)
    check("两张卡片都在",
          find_in_window(fw, "只看图和视频", ("ButtonControl",)) is not None
          and find_in_window(fw, "整理和记录", ("ButtonControl",)) is not None)

    btn = find_in_window(fw, "只看图和视频", ("ButtonControl",))
    log("要点的按钮：", repr(name_of(btn)))
    log("点「只看图和视频」:", click(btn))
    time.sleep(1.6)
    check("选完窗口自己关掉", first_run_window() is None)
    shot(win, "A_2_sidebar_simple")

    try:
        user32.SetForegroundWindow(win.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.5)
    assert_sidebar(win, "A")

    s = read_settings()
    log("设置：", s)
    check("Ui.SimpleMode 写成 1", s.get("Ui.SimpleMode") == "1", str(s))
    check("Ui.FirstRunDone 写成 1", s.get("Ui.FirstRunDone") == "1", str(s))

    kill_app()
    return win


def run_phase_b():
    """B：同一台机器再开 → 不该再问"""
    log("\n===== B. 重启（已问过）=====")
    win = launch()
    check("主窗口起来了", win is not None)
    if win is None:
        return
    fw = wait_for(first_run_window, 5)
    check("重启后不再弹首启窗", fw is None)
    if fw is not None:
        shot(fw, "B_1_asked_again")
    assert_sidebar(win, "B")
    shot(win, "B_2_still_simple")
    kill_app()


def run_phase_c(original_simple):
    """C：恢复真实库 + 删掉标记（= 老用户升级）→ 不打扰，只静默补标记"""
    log("\n===== C. 老库升级 =====")
    ok = restore_db()
    check("真实库已恢复", ok)
    if not ok:
        return

    con = sqlite3.connect(DB)
    total = con.execute("select count(*) from MediaItem").fetchone()[0]
    con.execute("delete from AppSetting where Key = 'Ui.FirstRunDone'")
    con.commit()
    con.close()
    log("库内媒体条目：", total)
    check("恢复的库确实有内容（否则这个场景没意义）", total > 0, "共 %d 条" % total)

    win = launch()
    check("主窗口起来了", win is not None)
    if win is None:
        return
    fw = wait_for(first_run_window, 5)
    check("老用户升级不弹首启窗", fw is None)
    if fw is not None:
        shot(fw, "C_1_asked_old_user")

    s = read_settings()
    log("设置：", s)
    check("静默补上了 Ui.FirstRunDone", s.get("Ui.FirstRunDone") == "1", str(s))
    check("没动老用户原来的 Ui.SimpleMode",
          s.get("Ui.SimpleMode") == original_simple,
          "原来是 %r，现在是 %r" % (original_simple, s.get("Ui.SimpleMode")))
    shot(win, "C_2_old_library")
    kill_app()


def peek_backup_setting(key):
    """读备份库里的原值（用来证明首启流程没偷偷改老用户的设置）。"""
    bak = os.path.join(BAK_DIR, "library.db")
    if not os.path.exists(bak):
        return None
    con = sqlite3.connect("file:%s?mode=ro" % bak.replace("\\", "/"), uri=True)
    try:
        row = con.execute("select Value from AppSetting where Key = ?", (key,)).fetchone()
        return row[0] if row else None
    except Exception:
        return None
    finally:
        con.close()


def main():
    kill_app()
    original_simple = None
    try:
        backup_db()
        original_simple = peek_backup_setting("Ui.SimpleMode")
        log("备份库里的 Ui.SimpleMode =", original_simple)

        run_phase_a()
        run_phase_b()
        kill_app()
        # 恢复交给 run_phase_c 自己做（它要先生效再断言），这里不重复调用：
        # restore_db 成功一次就会删掉备份目录，调第二次会误判成"恢复失败"
        run_phase_c(original_simple)
    finally:
        kill_app()
        if os.path.isdir(BAK_DIR):
            restore_db()

    passed = sum(1 for _, ok, _ in results if ok)
    log("\n---- 结果 %d/%d ----" % (passed, len(results)))
    for name, ok, detail in results:
        if not ok:
            log("未通过:", name, detail)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
