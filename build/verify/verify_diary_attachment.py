# -*- coding: utf-8 -*-
"""v1.4.2 补丁验证：日记里的附件缩略图能不能点开。

背景：v1.4.2 上线后自查发现，「日记」窗口里那排附件缩略图**没绑任何事件** ——
看上去像功能，点上去毫无反应（比不显示还糟）。修法是让它们走 MediaOpenLauncher，
和主界面 / 私密空间共用同一份「什么类型开哪个窗」的实现。

这个脚本证明修好了，而且证明的不只是「有个窗口弹出来」：
  1. 点缩略图 → 出现标题带文件名的「查看器」窗口
  2. 按 → 翻下一张，翻到的仍然是**这一篇日记**里的图（范围没被放大成整个媒体库）
  3. 三张同篇附件里，从第二张开始点，打开的应该是第二张（不是永远第一张）

会动开发库：往 `%LOCALAPPDATA%\\MediaManager\\library.db` 插 1 条日记 + 3 条媒体，
跑完（含异常退出）都会删掉，图片放 %TEMP%\\mm_diary_verify 也会删。
跑之前请先关掉正在运行的 MediaManager.App.exe。

用法：python build/verify/verify_diary_attachment.py
前置：pip install uiautomation pillow
"""
import ctypes
import datetime
import io
import os
import shutil
import sqlite3
import subprocess
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # build/verify/ → 仓库根
APP_DIR = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
APP = os.path.join(APP_DIR, "MediaManager.App.exe")
SHOTS = os.path.join(HERE, "shots_diary_attach")
LOG = os.path.join(HERE, "verify_diary_attach_log.txt")
os.makedirs(SHOTS, exist_ok=True)
logf = io.open(LOG, "w", encoding="utf-8")

DB = os.path.join(os.environ["LOCALAPPDATA"], "MediaManager", "library.db")

# 便携模式：exe 旁边有 portable.marker 时，数据在 exe 目录下的 Data/ 里。
# 开发用的这个 Debug 构建就是便携模式 —— 之前按 %LOCALAPPDATA% 播种，数据插到了
# 一个程序根本不读的库里，界面上自然「这一天还没有回忆」。所以这里必须跟程序同一套判断。
if os.path.exists(os.path.join(APP_DIR, "portable.marker")):
    DB = os.path.join(APP_DIR, "Data", "library.db")

TMP_DIR = os.path.join(tempfile.gettempdir(), "mm_diary_verify")
ENTRY_TITLE = "自动化验证：点开附件"
FILE_PREFIX = "diary_shot_"

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


def viewer_windows():
    """标题形如「xxx.png - 查看器」的顶层窗口。"""
    out = []
    for w in top_windows():
        n = w.Name or ""
        if n.endswith("查看器"):
            out.append(w)
    return out


def wait_viewer(seconds=6):
    end = time.time() + seconds
    while time.time() < end:
        ws = viewer_windows()
        if ws:
            return ws[0]
        time.sleep(0.3)
    return None


# ---------------- 数据库播种 / 清理 ----------------

def db_connect():
    con = sqlite3.connect(DB, timeout=10)
    con.execute("pragma busy_timeout=8000")
    return con


def purge(con):
    """删掉本脚本可能留下的任何东西（按标题 / 文件名前缀识别，不碰真实数据）。"""
    con.execute(
        "delete from DiaryMedia where DiaryId in (select Id from DiaryEntry where Title = ?)",
        (ENTRY_TITLE,))
    con.execute("delete from DiaryEntry where Title = ?", (ENTRY_TITLE,))
    con.execute("delete from MediaItem where FileName like ?", (FILE_PREFIX + "%",))


def seed():
    os.makedirs(TMP_DIR, exist_ok=True)
    src = os.path.join(ROOT, "screenshot.png")
    if not os.path.exists(src):
        # 换一张仓库里肯定有的图
        for cand in ("docs/logo.png", "screenshot.png"):
            if os.path.exists(os.path.join(ROOT, cand)):
                src = os.path.join(ROOT, cand)
                break

    paths = []
    for i in (1, 2, 3):
        dst = os.path.join(TMP_DIR, "%s%d.png" % (FILE_PREFIX, i))
        shutil.copyfile(src, dst)
        paths.append(dst)

    con = db_connect()
    purge(con)
    root_id = con.execute("select Id from RootFolder order by Id limit 1").fetchone()[0]
    now = datetime.datetime.utcnow().isoformat() + "Z"
    today = datetime.date.today().isoformat()

    media_ids = []
    for p in paths:
        # 故意不写 ThumbnailPath：模拟「清过缩略图缓存 / 导入时生成失败 / 备份恢复」——
        # 修好之前这里就是几个永远空着的灰框，修好之后应当自己补生成
        cur = con.execute(
            """insert into MediaItem
                 (RootFolderId, Path, FileName, Extension, MediaType, SizeBytes,
                  DateAddedUtc, IsMissing, IsPrivate)
               values (?,?,?,?,?,?,?,0,0)""",
            (root_id, p, os.path.basename(p), ".png", "Image", os.path.getsize(p), now))
        media_ids.append(cur.lastrowid)

    cur = con.execute(
        """insert into DiaryEntry (EntryDate, Title, Content, CreatedAtUtc, UpdatedAtUtc)
           values (?,?,?,?,?)""",
        (today, ENTRY_TITLE, "点下面的缩略图，应该打开内置查看器。", now, now))
    entry_id = cur.lastrowid

    for order, mid in enumerate(media_ids):
        con.execute("insert into DiaryMedia (DiaryId, MediaId, SortOrder) values (?,?,?)",
                    (entry_id, mid, order))

    con.commit()
    con.close()
    log("已插入日记 %s 与 3 个附件（%s）" % (entry_id, today))
    return paths, media_ids, entry_id


def cleanup():
    try:
        con = db_connect()
        purge(con)
        con.commit()
        con.close()
        log("已清理开发库里的验证数据")
    except Exception as e:
        log("清理数据库失败（请手动删标题为「%s」的日记）: %s" % (ENTRY_TITLE, e))
    try:
        shutil.rmtree(TMP_DIR, ignore_errors=True)
    except Exception:
        pass


def main():
    if not os.path.exists(APP):
        log("找不到 %s，先 dotnet build" % APP)
        return 1

    kill_app()
    try:
        paths, media_ids, entry_id = seed()
    except Exception as e:
        log("播种失败:", e)
        return 1

    rc = 1
    try:
        rc = run(paths)
    finally:
        kill_app()
        cleanup()
        logf.close()
    return rc


def run(paths):
    main_win = launch()
    check("主窗口出现", main_win is not None)
    if main_win is None:
        return 1

    # ---- 打开日记 ----
    diary_item = None
    for c in collect(main_win):
        if c.ControlTypeName == "ListItemControl" and "日记" in (c.Name or ""):
            diary_item = c
            break
    check("侧栏找到「日记」入口", diary_item is not None)
    if diary_item is None:
        return 1

    diary = uia.WindowControl(searchDepth=2, Name="日记")
    for _ in range(3):
        try:
            diary_item.Click()
        except Exception as e:
            log("点击日记失败", e)
        time.sleep(2.2)
        if diary.Exists(1, 0.5):
            break

    check("日记窗口打开", diary.Exists(3, 0.5))
    if not diary.Exists(1, 0.5):
        # 打不开通常意味着有个模态框压在前面（例如「根文件夹不存在」），留张全屏图好查
        dump_tops("日记没打开")
        ImageGrab.grab().save(os.path.join(SHOTS, "02_diary_not_open_screen.png"))
        return 1
    time.sleep(1.5)
    shot(diary, "01_diary_entry")

    # ---- 找到附件缩略图 ----
    # 缩略图是个 Border（无 Name），ToolTip 是文件名 → UIA 里体现为 HelpText。
    # 深度要给够：Window → Grid×3 → ScrollViewer → StackPanel → ItemsControl →
    # ContentPresenter → Border → StackPanel → ItemsControl → ContentPresenter → Border → Image
    thumbs = []
    nodes = collect(diary, maxdepth=24)
    log("日记窗口 UIA 节点数:", len(nodes),
        "其中 ImageControl:", sum(1 for c in nodes if c.ControlTypeName == "ImageControl"))

    for c in nodes:
        blob = "%s %s" % (c.Name or "", getattr(c, "HelpText", "") or "")
        if FILE_PREFIX in blob:
            thumbs.append(c)
    log("按文件名匹配到 %d 个控件" % len(thumbs))

    if not thumbs:
        # 兜底：132x100 的图（高 DPI 下会放大）
        for c in nodes:
            if c.ControlTypeName != "ImageControl":
                continue
            r = c.BoundingRectangle
            w, h = r.right - r.left, r.bottom - r.top
            if 100 <= w <= 300 and 70 <= h <= 230:
                thumbs.append(c)
        log("按尺寸兜底匹配到 %d 个缩略图" % len(thumbs))

    check("日记里能看到附件缩略图", len(thumbs) > 0, "找到 %d 个" % len(thumbs))
    if not thumbs:
        shot(diary, "02_no_thumb")
        return 1

    # ---- 缩略图必须真的画出来，不是几个空灰框 ----
    # 判断用「像素标准差」而不是平均亮度：空框是一整块纯色（标准差≈0），
    # 真图再暗也有明暗层次（截图类内容标准差通常 40+）。平均亮度在深色主题下
    # 区分不开「深灰空框」和「深色界面截图」。
    def tile_std(ctrl):
        r = ctrl.BoundingRectangle
        if r.right - r.left < 20:
            return 0.0
        img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom)).convert("L")
        px = list(img.getdata())
        if not px:
            return 0.0
        mean = sum(px) / len(px)
        return (sum((v - mean) ** 2 for v in px) / len(px)) ** 0.5

    best = 0.0
    for _ in range(20):  # 最多等 10 秒（后台生成缩略图）
        best = max(tile_std(t) for t in thumbs)
        if best >= 20:
            break
        time.sleep(0.5)
    log("缩略图区域像素标准差最大值: %.1f" % best)
    shot(diary, "03_thumbs_rendered")
    check("附件缩略图真的画出来了（不是空灰框）", best >= 20, "std=%.1f" % best)

    # 选第二个（不是第一张）：修好之前点哪个都没反应，
    # 但如果修复实现里把「打开哪一项」写死成第一项，这里会露馅
    target = thumbs[1] if len(thumbs) > 1 else thumbs[0]
    tr = target.BoundingRectangle
    log("点击缩略图 rect=(%d,%d,%d,%d)" % (tr.left, tr.top, tr.right, tr.bottom))

    # ---- 断言 1：点开后有查看器 ----
    ok_click = click(target)
    check("缩略图可以点", ok_click)
    viewer = wait_viewer(8)
    check("点缩略图后弹出查看器", viewer is not None)
    if viewer is None:
        shot(diary, "03_after_click_no_viewer")
        return 1
    time.sleep(1.2)
    log("查看器标题:", viewer.Name)
    shot(viewer, "04_viewer_opened")

    # ---- 断言 2：打开的是点的那一张，不是永远第一张 ----
    # 点的是第 2 个缩略图，标题应该是 diary_shot_2.png
    check("打开的是被点的那一张（不是第一张）", "diary_shot_2" in (viewer.Name or ""),
          viewer.Name or "")

    # ---- 断言 3：翻页范围是这一篇日记，不是整个媒体库 ----
    try:
        user32.SetForegroundWindow(viewer.NativeWindowHandle)
    except Exception:
        pass
    time.sleep(0.4)
    uia.SendKeys("{RIGHT}", waitTime=0.2)
    time.sleep(1.6)
    shot(viewer, "05_viewer_next")
    after = viewer.Name or ""
    log("按 → 之后标题:", after)
    check("按 → 翻到的还是本篇日记里的图", FILE_PREFIX in after, after)

    # 关掉查看器
    for w in viewer_windows():
        try:
            w.GetPattern(uia.PatternId.WindowPattern).Close()
        except Exception:
            pass
    time.sleep(0.8)

    passed = sum(1 for _, ok, _ in results if ok)
    log("---- 结果 %d/%d ----" % (passed, len(results)))
    for name, ok, detail in results:
        if not ok:
            log("未通过:", name, detail)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
