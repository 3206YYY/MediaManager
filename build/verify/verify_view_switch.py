# -*- coding: utf-8 -*-
"""网格 / 列表切换守卫（静态段 + UIA 实机段）。

背景：这一版只换三处模板 —— ItemsPanel / ItemTemplate / ItemContainerStyle，
其余交互（点击、Ctrl/Shift 多选、框选、点删、拖拽、双击、右键、分页）**必须共用**。
抽 MediaGrid 控件会碰到 MainWindow.xaml.cs 里绑在同一个 ThumbList 上的 Preview 事件，
所以这一版明确不抽控件；守卫就是用来防止「换着换着又长出一套列表逻辑」。

静态段（13 项）：
  1 View.Mode 设置键：定义 + 读取 + 回写都在 VM 里（重启保留偏好）
  2 ViewMode 枚举只有 Grid / List 两个成员
  3 VM 暴露 IsGridView / IsListView + SetViewMode 命令
  4 抬头右侧有网格 / 列表两个 RadioButton，都带 AutomationProperties.Name 与 GroupName
  5 XAML 里定义了三件套：MediaListPanel / MediaListRowTemplate / ListItemStyle
  6 行模板把八个字段都绑上了（FileName / TypeLabel / SizeDisplay / ResolutionDisplay /
    DurationDisplay / CategoryNames / ModifiedAtDisplay / IsFavorite）
  7 行右端不再绑评分星标（RatingStars / HasRating），只留可点的收藏 ★
  8 列表行与网格卡片的 ToolTip 都带完整路径（同名文件两个视图都能分辨）
  9 列表容器样式的选中环用 ThumbSelectionBrush，且没照抄网格的角标偏移（0,6,28,0）
 10 列表和网格共用同一套框选/单击逻辑：该段代码里没有 IsListView 分支
 11 「缩略图大小」滑块在列表模式下整行隐藏（IsGridView 绑定）
 12 界面条数文案解析（「共 N 项」/「已加载 X / 共 N 项」）用样本串静态钉住
 13 仍然只有一个 ThumbList、一个 SelectionChanged —— 没有复制出第二套列表逻辑

UIA 实机段（九组 29 项检查）：
  U1 主窗口出现，「网格视图 / 列表视图」能从主窗口 UIA 树直接按名字找到（不在 Popup）
  U2 默认网格：条数取界面「共 N 项 / 已加载 X」（不假设扩展开关全开、不假设库 ≤ PageSize）；
     滚到底数卡片，界面项都来自库；溢出菜单里「缩略图大小」可见，关掉后确实消失
  U3 点列表：IsChecked 切换，滚到底行数与网格同量；列表行 UIA Name 就是文件名
  U4 列表模式下溢出菜单里找不到「缩略图大小」
  U5 列表里清空选择后 Ctrl 从空白框选 → 能选中多行（和网格共用二维框选）
  U6 选 2 项 → 切列表仍 2 项 → 切回网格仍 2 项（验 RestoreViewAnchor）
  U7 列表模式点「批量删除」能进删除模式、横幅出现（不删文件）
  U8 关程序重开 → 列表视图仍选中（验 View.Mode 持久化）

前置约定（踩过的坑，别改）：
  · 先 SetProcessDpiAwareness(2)；置前用 uiawin.force_foreground，不直接 SetForegroundWindow
  · **别给进程级共享的 ctypes.windll.user32.keybd_event / mouse_event 设 argtypes**：
    同一进程里 uiawin.force_foreground 的 ALT 退路传整数 0，而 POINTER(c_ulong) 会把它
    打成 ctypes.ArgumentError（锁屏 / 浏览器占前台时才走这条退路，所以偶发）
  · 库文件（db / -wal / -shm）整份备份、try/finally 还原；**不直接改 DB 写 View.Mode**，
    要切就点 UI，才验得到持久化链路
  · 进程必须 try/finally 关干净（残留实例会把后续脚本打红）
  · collect 深度 ≥18：切换按钮在抬头里层级深
  · 默认运行就真跑实机（run_all_regress 会收录本脚本），不是 --uia 才生效

用法：
    python build/verify/verify_view_switch.py
"""

import glob
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
APP = os.path.join(ROOT, "src", "MediaManager.App")
VM = os.path.join(APP, "ViewModels", "MainViewModel.cs")
XAML = os.path.join(APP, "Views", "MainWindow.xaml")
CS = os.path.join(APP, "Views", "MainWindow.xaml.cs")
ENUM = os.path.join(ROOT, "src", "MediaManager.Core", "Enums", "ViewMode.cs")

_failed = 0
_total = 0


def check(name, ok, detail=""):
    global _failed, _total
    _total += 1
    if not ok:
        _failed += 1
    print("[%s]  %s%s" % ("PASS" if ok else "FAIL", name, ("  —— " + detail) if detail else ""))


def read(path):
    return io.open(path, encoding="utf-8").read()


def no_comments(text):
    return re.sub(r"<!--.*?-->", "", text, flags=re.S)


def parse_result_counts(texts):
    """从界面文案里解析 (共 N 项, 已加载 X)。纯函数，方便用样本串静态钉住。

    抬头固定是「共 N 项」；库超过 PageSize 时分页加载，状态栏写「已加载 X / 共 N 项」，
    此时 X 才是界面实际加载的条数。两条同时在以「已加载」为准，顺序无关。
    """
    total = loaded = None
    for t in texts:
        m = re.search(r"已加载\s*(\d+)\s*/\s*共\s*(\d+)\s*项", t)
        if m:
            loaded, total = int(m.group(1)), int(m.group(2))
            continue
        m = re.fullmatch(r"共\s*(\d+)\s*项", t)
        if m and total is None:
            total = int(m.group(1))
    return total, loaded


def block_after(text, start, length=900):
    """从 start 出现的位置往后取一段，用于「这个控件附近的绑定」这类局部断言。"""
    i = text.find(start)
    return text[i:i + length] if i >= 0 else ""


# ===================================================================
#  静态段
# ===================================================================

def run_static():
    for path in (VM, XAML, CS, ENUM):
        if not os.path.exists(path):
            check("文件存在：" + os.path.basename(path), False, path)
            return

    # 视图切换的状态与命令在 MainViewModel.Browsing.cs（partial），
    # 数据层断言按「所有 MainViewModel*.cs」一起查，别被文件拆分骗过去。
    vm_all = "\n".join(read(p) for p in sorted(
        glob.glob(os.path.join(os.path.dirname(VM), "MainViewModel*.cs"))))
    xaml = no_comments(read(XAML))
    cs = read(CS)
    enum = read(ENUM)

    # 1 View.Mode 设置键
    key_ok = 'ViewModeSettingKey = "View.Mode"' in vm_all
    read_ok = "GetSettingAsync(ViewModeSettingKey)" in vm_all
    save_ok = "SaveSettingAsync(ViewModeSettingKey" in vm_all
    check("View.Mode 设置键：定义 / 初始化读回 / 变更回写 三处齐全",
          key_ok and read_ok and save_ok,
          "定义=%s 读回=%s 回写=%s" % (key_ok, read_ok, save_ok))

    # 2 枚举成员就两个
    members = re.findall(r"^\s*(Grid|List)\s*,?\s*$", enum, re.M)
    check("ViewMode 枚举只有 Grid / List", sorted(set(members)) == ["Grid", "List"],
          "实际：" + ("、".join(members) if members else "没解析到成员"))

    # 3 VM 属性与命令
    has_props = "public bool IsGridView" in vm_all and "public bool IsListView" in vm_all
    has_cmd = "SetViewMode(string? mode)" in vm_all and "ViewMode = parsed" in vm_all
    check("VM 暴露 IsGridView / IsListView 与 SetViewMode 命令", has_props and has_cmd,
          "属性=%s 命令=%s" % (has_props, has_cmd))

    # 4 抬头切换入口
    radios = re.findall(r"<RadioButton\b[^>]*>", xaml)
    named = [r for r in radios if 'GroupName="MediaViewMode"' in r
             and "AutomationProperties.Name=" in r]
    names = re.findall(r'AutomationProperties.Name="([^"]+)"', "\n".join(named))
    check("抬头有网格 / 列表两个 RadioButton（同组、都有 AutomationProperties.Name）",
          len(named) == 2 and "网格视图" in names and "列表视图" in names,
          "找到 %d 个，名称：%s" % (len(named), "、".join(names) or "无"))

    # 5 三件套资源
    resources = [
        ('<ItemsPanelTemplate x:Key="MediaListPanel">', "MediaListPanel"),
        ('<DataTemplate x:Key="MediaListRowTemplate">', "MediaListRowTemplate"),
        ('<Style x:Key="ListItemStyle"', "ListItemStyle"),
    ]
    missing = [label for tag, label in resources if tag not in xaml]
    check("列表三件套都有定义（ItemsPanel / ItemTemplate / ItemContainerStyle）",
          not missing, "缺：" + "、".join(missing) if missing else "3/3")

    # 6 行模板字段
    row = block_after(xaml, '<DataTemplate x:Key="MediaListRowTemplate">', 6000)
    fields = ["FileName", "TypeLabel", "SizeDisplay", "ResolutionDisplay",
              "DurationDisplay", "CategoryNames", "ModifiedAtDisplay", "IsFavorite"]
    # 绑定后面可能跟 Converter / StringFormat，用前缀匹配，别死抠右花括号
    missing_fields = [f for f in fields
                      if not re.search(r"\{Binding %s\b" % f, row)]
    check("行模板绑全八个字段", not missing_fields,
          "缺：" + "、".join(missing_fields) if missing_fields else "8/8")
    check("行右端只剩收藏星标（评分星标已移除，避免两排星语义混）",
          "{Binding RatingStars}" not in row and "{Binding HasRating}" not in row,
          "还在绑 RatingStars/HasRating" if "{Binding RatingStars}" in row or
          "{Binding HasRating}" in row else "已移除")

    # 8 两个视图的 ToolTip 都要带完整路径
    #   真实库里不同文件夹的 IMG_001.jpg 很常见，只给文件名分不清；
    #   以前只查 8 个字段绑定，把这行删了照样全绿。
    #   取样窗口按整块 <Grid.ToolTip>…</Grid.ToolTip> 切，不写死长度 ——
    #   注释多两行就会把 Path 挤到窗口外，断言变假绿。
    tip_blocks = re.findall(r"<Grid\.ToolTip>.*?</Grid\.ToolTip>", xaml, flags=re.S)
    tip_missing = [b for b in tip_blocks if "{Binding Path}" not in b]
    check("列表行与网格卡片的 ToolTip 都带完整路径（同名文件两个视图都能分辨）",
          len(tip_blocks) >= 2 and not tip_missing,
          "%d 块 ToolTip，缺路径 %d 块" % (len(tip_blocks), len(tip_missing)))
    # 8b 两边限宽一致，超长路径的换行观感才一样
    tip_narrow = [b for b in tip_blocks if 'MaxWidth="320"' not in b]
    check("列表与网格的 ToolTip 统一限宽 320（超长路径换行观感一致）",
          len(tip_blocks) >= 2 and not tip_narrow,
          "%d 块 ToolTip，没限宽 %d 块" % (len(tip_blocks), len(tip_narrow)))

    # 9 行容器选中环
    item_style = block_after(xaml, '<Style x:Key="ListItemStyle"', 2500)
    has_ring = "ThumbSelectionBrush" in item_style
    no_badge_offset = 'Margin="0,6,28,0"' not in item_style
    check("列表选中环有自己的样式（用 ThumbSelectionBrush、没照抄网格角标偏移）",
          has_ring and no_badge_offset,
          "选中环=%s 角标偏移=%s" % (has_ring, "照抄了" if not no_badge_offset else "没照抄"))

    # 10 列表和网格共用同一套框选 / 单击
    #   列表视图也是二维换行面板（2~4 列紧凑行），没有「按网格算会选错行」的问题，
    #   所以鼠标按下 → 框选 → 选中判定这一段代码里不该再出现 IsListView 分支。
    m_start = cs.find("private void OnThumbListMouseDown")
    m_end = cs.find("private bool IsOverItem", m_start)
    marquee_src = cs[m_start:m_end] if m_start >= 0 and m_end > m_start else ""
    check("列表模式复用网格同一套框选/单击逻辑（该段没有 IsListView 分支）",
          bool(marquee_src) and "IsListView" not in marquee_src,
          "找到代码段=%s IsListView=%s" % (bool(marquee_src), "IsListView" in marquee_src))

    # 11 缩略图滑块整行隐藏
    slider_pos = xaml.find('AutomationProperties.Name="缩略图大小"')
    slider_ctx = xaml[max(0, slider_pos - 700):slider_pos] if slider_pos >= 0 else ""
    check("「缩略图大小」滑块在列表模式下整行隐藏（IsGridView 控制可见性）",
          slider_pos >= 0 and 'Visibility="{Binding IsGridView' in slider_ctx,
          "滑块=%s 隐藏绑定=%s" % (slider_pos >= 0, 'Visibility="{Binding IsGridView' in slider_ctx))

    # 12 界面条数文案解析（大库分支没有真机库可跑，用样本串钉住）
    count_cases = [
        (["共 29 项"], (29, None)),
        (["已加载 2000 / 共 2500 项"], (2500, 2000)),
        (["共 29 项", "已加载 2000 / 共 2500 项"], (2500, 2000)),
        (["已加载 2000 / 共 2500 项", "共 29 项"], (2500, 2000)),
        (["已加载 2000 / 共 2000 项"], (2000, 2000)),
        (["排序：时间", "共 1 项"], (1, None)),
        (["没有匹配项"], (None, None)),
    ]
    count_bad = [(texts, want, parse_result_counts(texts))
                 for texts, want in count_cases if parse_result_counts(texts) != want]
    check("界面条数文案解析（含「已加载 X / 共 N 项」分页分支）", not count_bad,
          "；".join("%s 期望%s 实际%s" % (t, w, g) for t, w, g in count_bad) or
          "%d 条样本全对" % len(count_cases))

    # 12b 文案来源本身也要钉住：上面测的是解析函数，VM 把格式改了就白测，
    #     只有等实机 U2 读不到文案才会红。这里直接认 VM 的输出格式。
    fmt_total = re.search(r'\$"共 \{\w+(?:\.\w+)*\} 项"', vm_all)
    fmt_loaded = re.search(r'\$"已加载 \{\w+(?:\.\w+)*\} / 共 \{\w+(?:\.\w+)*\} 项"', vm_all)
    check("条数文案来自 VM 的固定格式（共 N 项 / 已加载 X / 共 N 项）",
          bool(fmt_total) and bool(fmt_loaded),
          "共N项=%s 已加载X/共N项=%s" % (bool(fmt_total), bool(fmt_loaded)))

    # 13 没有第二套列表
    one_list = len(re.findall(r'x:Name="ThumbList"', xaml)) == 1
    one_sel = len(re.findall(r"OnThumbListSelectionChanged", xaml)) == 1
    only_switch = all(s in cs for s in ('FindResource("MediaListPanel")',
                                        'FindResource("MediaListRowTemplate")',
                                        'FindResource("ListItemStyle")'))
    check("没有复制第二套列表逻辑（单 ThumbList / 单选择事件 / 只换三处模板）",
          one_list and one_sel and only_switch,
          "ThumbList=%d 选择事件=%d 三处切换=%s" % (
              len(re.findall(r'x:Name="ThumbList"', xaml)),
              len(re.findall(r"OnThumbListSelectionChanged", xaml)), only_switch))


# ===================================================================
#  UIA 实机段
# ===================================================================

def run_uia():
    try:
        import ctypes
        import ctypes.wintypes as wt
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except Exception:
            ctypes.windll.user32.SetProcessDPIAware()
        import uiautomation as uia
        from uiawin import force_foreground
    except Exception as exc:
        check("UIA 依赖可用（pip install uiautomation pillow）", False,
              "%s：%s" % (type(exc).__name__, exc))
        return

    import shutil
    import sqlite3
    import subprocess
    import tempfile
    import time

    user32 = ctypes.windll.user32
    user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
    user32.MoveWindow.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_int, ctypes.c_int, wt.BOOL]
    # 不给 mouse_event / keybd_event 设 argtypes：ctypes.windll.user32 是进程级共享，
    # uiawin.force_foreground 的 ALT 退路传整数 0（锁屏/浏览器占前台时才走）会撞上
    # POINTER(c_ulong) 而报 ctypes.ArgumentError —— 等于脚本把自己的兜底打断了。

    app_dir = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug", "net8.0-windows")
    app = os.environ.get("MM_VERIFY_APP") or os.path.join(app_dir, "MediaManager.App.exe")
    data_dir = os.path.join(app_dir, "Data")
    if not os.path.isdir(data_dir):
        data_dir = os.path.join(os.environ.get("LOCALAPPDATA", ""), "MediaManager")
    db = os.path.join(data_dir, "library.db")
    bak_dir = os.path.join(tempfile.gettempdir(), "MM_视图切换_备份")
    shot_path = os.path.join(HERE, "shots_view_switch_list.png")

    if not os.path.exists(app) or not os.path.exists(db):
        check("开发库与 Debug 版可执行文件存在", False, "app=%s db=%s" % (app, db))
        return

    # ---------- 小工具 ----------
    def name_of(c):
        try:
            return c.Name or ""
        except Exception:
            return ""

    def is_offscreen(c):
        try:
            return bool(c.IsOffscreen)
        except Exception:
            return True

    def collect(ctrl, acc=None, d=0, maxd=18):
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

    def top_windows():
        out = []
        for w in uia.GetRootControl().GetChildren():
            try:
                out.append(w)
            except Exception:
                pass
        return out

    def main_window():
        for w in top_windows():
            try:
                if w.Name and "媒体管理器" in w.Name and collect(w, maxd=18):
                    return w
            except Exception:
                continue
        return None

    def wait_for(fn, seconds=25, interval=0.4):
        end = time.time() + seconds
        while time.time() < end:
            v = fn()
            if v is not None:
                return v
            time.sleep(interval)
        return None

    def kill_app():
        subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
        time.sleep(1.2)

    def launch():
        subprocess.Popen([app], cwd=app_dir)
        win = wait_for(main_window, 30)
        if win is not None:
            time.sleep(3.0)  # InitializeAsync + 首启判定
        return win

    def backup_db():
        # 上次被强杀可能留下备份目录：先还原再备份，否则备的是「已被改过的库」，
        # finally 又把脏库固化回去，越跑越偏。
        if os.path.isdir(bak_dir):
            print("发现残留备份，先还原再重新备份：", bak_dir)
            restore_db()
        os.makedirs(bak_dir, exist_ok=True)
        n = 0
        for suffix in ("", "-wal", "-shm"):
            src = db + suffix
            if os.path.exists(src):
                shutil.copy2(src, os.path.join(bak_dir, "library.db" + suffix))
                n += 1
        return n

    def restore_db():
        for suffix in ("", "-wal", "-shm"):
            p = db + suffix
            if os.path.exists(p):
                os.remove(p)
        for suffix in ("", "-wal", "-shm"):
            src = os.path.join(bak_dir, "library.db" + suffix)
            if os.path.exists(src):
                shutil.copy2(src, db + suffix)
        shutil.rmtree(bak_dir, ignore_errors=True)

    def db_files():
        """库里全部可见项的 (Id, FileName) —— 全库口径，没有 limit。
        界面会再叠扩展名过滤和分页（只加载 PageSize），所以库是界面的超集，
        条数以界面为准（ui_counts），这里只用来过滤侧栏项和做「界面 ⊆ 库」。
        只查 Name 会让不同目录的同名文件被去重，而 media_items 又靠文件名认项 ——
        所以带上 Id，调用方再断言「文件名无重名」。
        IsMissing=0 必须带：MediaRepository 每条查询都带这条，漏掉会让
        verify_offline_folder 跑完后留下的离线记录混进来，断言必挂。"""
        con = sqlite3.connect(db, timeout=5)
        try:
            return [(r[0], r[1]) for r in con.execute(
                "select Id, FileName from MediaItem "
                "where IsPrivate=0 and IsMissing=0")]
        finally:
            con.close()

    def radio(win, label):
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "RadioButtonControl" and name_of(c) == label:
                return c
        return None

    def radio_checked(ctrl):
        try:
            return bool(ctrl.GetSelectionItemPattern().IsSelected)
        except Exception:
            return False

    def switch_to(win, label):
        r = radio(win, label)
        if r is None:
            return False
        try:
            r.GetSelectionItemPattern().Select()
        except Exception:
            try:
                r.Click()
            except Exception:
                return False
        time.sleep(1.8)
        return True

    def media_items(win, names):
        """本页媒体项（按文件名去重）。
        虚拟化滚动之后 UIA 树里偶尔会同时返回新容器和未回收的旧容器：
        同一个文件出现两次时，「先 Select 再 AddToSelection」会落到同一项上，
        选中数只剩 1（U2/U6 假红）。同名时优先取更靠上的可见元素。"""
        best = {}
        for c in collect(win, maxd=18):
            try:
                if c.ControlTypeName != "ListItemControl":
                    continue
                n = name_of(c)
                if n not in names:
                    continue
                top = c.BoundingRectangle.top
            except Exception:
                continue
            if n not in best:
                best[n] = (top, c)
            elif top >= 0 and (best[n][0] < 0 or top < best[n][0]):
                best[n] = (top, c)
        return [c for _, c in best.values()]

    def list_control_of(item):
        """顺着父链找到媒体 ListBox，用来拿 ScrollPattern。"""
        node = item
        for _ in range(10):
            try:
                node = node.GetParentControl()
            except Exception:
                return None
            if node is None:
                return None
            if getattr(node, "ControlTypeName", "") == "ListControl":
                return node
        return None

    def all_names(win, names):
        """网格和列表都是虚拟化的：UIA 只实例化视口内的项（实测网格 24/29、列表 25/29）。
        从头滚到底把项凑齐（去重），再滚回顶部 —— 用来验证「一条不少」。
        别用 len(media_items(...)) 直接比，那只是当前视口里的项。"""
        items = media_items(win, names)
        if not items:
            return set()
        lst = list_control_of(items[0])
        seen = set()
        last = -1
        for _ in range(40):
            seen |= {name_of(c) for c in media_items(win, names)}
            if len(seen) >= len(names) or len(seen) == last:
                break
            last = len(seen)
            try:
                sp = lst.GetScrollPattern()
                if not sp.VerticallyScrollable:
                    break
                sp.Scroll(uia.ScrollAmount.NoAmount, uia.ScrollAmount.LargeIncrement)
            except Exception:
                break
            time.sleep(0.35)
        seen |= {name_of(c) for c in media_items(win, names)}
        try:
            lst.GetScrollPattern().SetScrollPercent(-1, 0)
        except Exception:
            pass
        time.sleep(0.5)
        return seen

    def selected_names(items):
        out = []
        for c in items:
            try:
                if c.GetSelectionItemPattern().IsSelected:
                    out.append(name_of(c))
            except Exception:
                pass
        return out

    def ui_counts(win):
        """本页条数以界面为准，别从库里推。
        抬头是「共 N 项」；库超过 PageSize 时分页加载，状态栏会写「已加载 X / 共 N 项」。
        解析逻辑在模块级 parse_result_counts（纯函数，静态段用样本串钉住），
        这样「关掉扩展格式开关」和「库 > 2000 条」都不会让全量比对莫名变红。"""
        texts = []
        for c in collect(win, maxd=18):
            try:
                if c.ControlTypeName != "TextControl":
                    continue
                texts.append(name_of(c))
            except Exception:
                continue
        return parse_result_counts(texts)

    def pick_first(items, n=2):
        ordered = sorted(items, key=lambda c: (c.BoundingRectangle.top,
                                              c.BoundingRectangle.left))
        if len(ordered) < n:
            return False
        for i, c in enumerate(ordered[:n]):
            try:
                if i == 0:
                    c.GetSelectionItemPattern().Select()
                else:
                    c.GetSelectionItemPattern().AddToSelection()
            except Exception:
                return False
            time.sleep(0.3)
        time.sleep(0.6)
        return True

    def select_two(win, names):
        """列表行会随搜索重建，UIA 选中偶尔只落一项；多试几次，最后用真实 Ctrl+单击兜底。"""
        for attempt in range(3):
            items = media_items(win, names)
            if len(items) >= 2 and pick_first(items, 2):
                if len(selected_names(media_items(win, names))) == 2:
                    return True
            time.sleep(0.6)
        ordered = sorted(media_items(win, names),
                         key=lambda c: (c.BoundingRectangle.top, c.BoundingRectangle.left))
        if len(ordered) < 2:
            return False
        force_foreground(hwnd_holder[0])
        user32.keybd_event(0x11, 0, 0, 0)  # Ctrl down
        try:
            time.sleep(0.15)
            for c in ordered[:2]:
                r = c.BoundingRectangle
                user32.SetCursorPos((r.left + r.right) // 2, (r.top + r.bottom) // 2)
                time.sleep(0.2)
                user32.mouse_event(2, 0, 0, 0, 0)
                user32.mouse_event(4, 0, 0, 0, 0)
                time.sleep(0.3)
        finally:
            # 异常也必须放开 Ctrl，否则会把按键卡在按下状态，后续框选/点击全乱
            user32.keybd_event(0x11, 0, 2, 0)  # Ctrl up
        time.sleep(0.8)
        return len(selected_names(media_items(win, names))) == 2

    # select_two 的兜底分支需要窗口句柄；开跑后填
    hwnd_holder = [None]

    def native_click(hwnd, ctrl):
        """删除模式走 PreviewMouseLeftButtonDown，UIA Invoke 不经过鼠标消息。"""
        try:
            r = ctrl.BoundingRectangle
            cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        except Exception:
            return False
        force_foreground(hwnd)
        user32.SetCursorPos(cx, cy)
        time.sleep(0.2)
        user32.mouse_event(2, 0, 0, 0, 0)  # LEFTDOWN
        user32.mouse_event(4, 0, 0, 0, 0)  # LEFTUP
        time.sleep(0.6)
        return True

    def click_ctrl(hwnd, ctrl):
        """普通按钮优先走 Invoke（不依赖窗口位置/鼠标），失败再退回真实鼠标。"""
        try:
            ctrl.GetInvokePattern().Invoke()
            time.sleep(0.9)
            return True
        except Exception:
            return native_click(hwnd, ctrl)

    def find_button(win, labels):
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "ButtonControl" and name_of(c) in labels:
                return c
        return None

    def visible_text(win, text):
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "TextControl" and text in name_of(c) and not is_offscreen(c):
                return c
        return None

    def popup_has_slider(text="缩略图大小"):
        """溢出菜单是独立 Popup 窗口，主窗口树里扫不到，要扫所有顶层窗口。
        只认 SliderControl：⋯ 按钮的 ToolTip 里也有「缩略图大小」字样，
        按文本判会在鼠标停在按钮上时假红。"""
        for w in top_windows():
            for c in collect(w, maxd=18):
                try:
                    if (c.ControlTypeName == "SliderControl"
                            and name_of(c) == text and not is_offscreen(c)):
                        return True
                except Exception:
                    continue
        return False

    def open_overflow(win, hwnd):
        btn = find_button(win, ("更多操作",))
        if btn is None:
            return False
        native_click(hwnd, btn)
        time.sleep(0.9)
        return True

    def overflow_state(win):
        """溢出菜单的 IsOpen 与 ⋯ 的 IsChecked 是 TwoWay 绑定：ToggleState 就是真实开关状态。
        别在 UIA 树里找 Popup 内容判断 —— WPF Popup 关掉后旧元素仍会残留并报「可见」，
        会同时造成假红（U2 关不掉）和假绿（U4 看着没有其实菜单还开着）。"""
        btn = find_button(win, ("更多操作",))
        if btn is None:
            return None
        try:
            return btn.GetTogglePattern().ToggleState == uia.ToggleState.On
        except Exception:
            return None

    def close_overflow(win):
        """关掉溢出菜单并确认真关了。
        以前只发 ESC —— 前台不在应用上时 ESC 根本不生效，菜单会一直盖在网格/列表上，
        后面「选中 2 项」「找不到缩略图大小」全被它搅乱（U2/U4/U5/U6 假红或假绿）。
        这里走 TogglePattern：和用户点 ⋯ 是同一条 IsOpen ↔ IsChecked 链路。"""
        for _ in range(4):
            if overflow_state(win) is not True:
                return True
            btn = find_button(win, ("更多操作",))
            try:
                btn.GetTogglePattern().Toggle()
            except Exception:
                try:
                    uia.SendKeys("{ESC}")
                except Exception:
                    pass
            time.sleep(0.5)
        return overflow_state(win) is False

    def drag_with_ctrl(x1, y1, x2, y2):
        user32.keybd_event(0x11, 0, 0, 0)  # Ctrl down
        try:
            time.sleep(0.15)
            user32.SetCursorPos(x1, y1)
            time.sleep(0.15)
            user32.mouse_event(2, 0, 0, 0, 0)
            for i in range(1, 6):
                user32.SetCursorPos(x1 + (x2 - x1) * i // 5, y1 + (y2 - y1) * i // 5)
                time.sleep(0.06)
            user32.mouse_event(4, 0, 0, 0, 0)
        finally:
            user32.keybd_event(0x11, 0, 2, 0)  # Ctrl up
        time.sleep(0.6)

    # ---------- 开跑 ----------
    kill_app()
    check("库文件备份（db / -wal / -shm）", backup_db() >= 1, bak_dir)
    win2 = None
    try:
        win = launch()
        check("主窗口出现", win is not None)
        if win is None:
            return
        hwnd = None
        for w in top_windows():
            try:
                if w.Name and "媒体管理器" in w.Name and radio(w, "网格视图") is not None:
                    hwnd = w.NativeWindowHandle
                    win = w
                    break
            except Exception:
                continue
        force_foreground(hwnd)
        hwnd_holder[0] = hwnd
        user32.MoveWindow(hwnd, 20, 20, 1900, 1000, True)
        time.sleep(1.5)

        # U1 切换入口在主窗口树里，不在 Popup
        g = radio(win, "网格视图")
        l = radio(win, "列表视图")
        check("U1 主窗口里能按名字找到「网格视图 / 列表视图」（不在 Popup）",
              g is not None and l is not None
              and not is_offscreen(g) and not is_offscreen(l))

        # 进「全部媒体」，拿稳定的本页集合
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "ListItemControl" and name_of(c) == "🏠 全部媒体":
                try:
                    c.GetSelectionItemPattern().Select()
                except Exception:
                    c.Click()
                break
        time.sleep(3.0)

        files = db_files()
        names = {n for _, n in files}
        check("库里读到文件名集合（全库口径，只用来过滤侧栏项，条数以界面为准）",
              len(names) > 0, "%d 个" % len(names))
        # UIA 只能按 Name（文件名）认项；同名会让集合比对把两项折成一项。
        # 这是「当前库」的前提，不是产品的保证 —— 真实库同名时这里会明确红，不会假绿。
        check("库里文件名无重名（全库口径，UIA 按名认项的前提）",
              len(files) == len(names), "%d 条 / %d 个文件名" % (len(files), len(names)))

        # 期望条数以界面为准：不假设「扩展格式开关全开」，也不假设「库 ≤ PageSize」。
        # 抬头是「共 N 项」，分页加载时状态栏是「已加载 X / 共 N 项」——有 X 就用 X。
        total, loaded = ui_counts(win)
        expected = loaded if loaded is not None else total
        check("U2 能从界面读到本页条数（共 N 项 / 已加载 X）",
              expected is not None and expected > 0,
              "共 %s / 已加载 %s" % (total, loaded))

        # 上一次可能停在列表：先经 UI 切回网格（顺便验一遍回写链路）
        if radio_checked(radio(win, "列表视图")):
            switch_to(win, "网格视图")
            time.sleep(0.5)
        check("U2 默认停在网格视图", radio_checked(radio(win, "网格视图")))

        # 网格同样是虚拟化的（一屏 24 个），必须滚到底数 —— 不能数当前树里的卡片
        grid_seen = all_names(win, names)
        check("U2 网格里的项都来自库（扩展名/筛选只做减法，界面是库的子集）",
              grid_seen <= names,
              "多出：%s" % ("、".join(sorted(grid_seen - names)[:5]) or "无"))
        if expected:
            check("U2 网格滚到底 == 界面条数（虚拟化只实例化视口）",
                  len(grid_seen) == expected,
                  "见到 %d / 界面 %d" % (len(grid_seen), expected))
        grid_items = media_items(win, names)

        # 缩略图滑块：网格里可见
        if open_overflow(win, hwnd):
            check("U2 网格模式溢出菜单里「缩略图大小」可见",
                  popup_has_slider())
            check("U2 关溢出菜单后 Popup 真的消失", close_overflow(win))
            check("U2 关菜单后「缩略图大小」不再可见",
                  not popup_has_slider())
        else:
            check("U2 找到「更多操作」按钮", False)

        # 溢出菜单开关会让 UIA 元素句柄失效，所以选中前重新取一次，别用开菜单之前那批
        grid_items = media_items(win, names)
        if len(grid_items) >= 2:
            check("U2 网格里能选中 2 项", pick_first(grid_items, 2))
        grid_selected = selected_names(media_items(win, names))
        check("U2 网格选中数 == 2", len(grid_selected) == 2,
              "实际 %d：%s" % (len(grid_selected), grid_selected))

        # U3 切列表
        check("U3 点「列表视图」后 IsChecked 切换",
              switch_to(win, "列表视图") and radio_checked(radio(win, "列表视图"))
              and not radio_checked(radio(win, "网格视图")))
        # 选中项要在滚动之前查：滚到底会把上面的行回收掉
        list_items = media_items(win, names)
        check("U3 列表行 UIA Name 就是文件名",
              bool(list_items) and all(name_of(c) in names for c in list_items))
        check("U3 切到列表后选中项没丢",
              sorted(selected_names(list_items)) == sorted(grid_selected),
              "前=%s 后=%s" % (grid_selected, selected_names(list_items)))
        # 列表同样虚拟化：UIA 只实例化视口内的行，滚到底凑齐再比
        list_seen = all_names(win, names)
        check("U3 列表里的项都来自库", list_seen <= names,
              "多出：%s" % ("、".join(sorted(list_seen - names)[:5]) or "无"))
        if expected:
            check("U3 列表滚到底 == 界面条数（和网格同量）",
                  len(list_seen) == expected,
                  "见到 %d / 界面 %d" % (len(list_seen), expected))

        # 截图留作人工证据（不进断言）
        try:
            from PIL import ImageGrab
            r = win.BoundingRectangle
            ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom)).save(shot_path)
            print("列表模式截图：", shot_path)
        except Exception as exc:
            print("截图跳过：", exc)

        # U4 列表里没有缩略图滑块
        if open_overflow(win, hwnd):
            check("U4 列表模式溢出菜单里找不到「缩略图大小」",
                  not popup_has_slider())
            close_overflow(win)

        # U6 切回网格 / 再切列表，选择都在
        check("U6 切回网格后仍选中 2 项",
              switch_to(win, "网格视图")
              and len(selected_names(media_items(win, names))) == 2)
        check("U6 再切回列表后仍选中 2 项",
              switch_to(win, "列表视图")
              and len(selected_names(media_items(win, names))) == 2)

        # U5 列表模式也支持二维框选（面板是 2~4 列紧凑行，和网格共用同一套逻辑）
        #     先用搜索缩到少数行，给框选留出下方空白
        search = None
        for c in collect(win, maxd=18):
            if c.ControlTypeName == "EditControl" and getattr(c, "AutomationId", "") == "SearchTextBox":
                search = c
                break
        if search is not None:
            try:
                search.GetValuePattern().SetValue("IMG_001")
            except Exception:
                pass
            time.sleep(1.8)
        narrowed = [c for c in collect(win, maxd=18)
                    if c.ControlTypeName == "ListItemControl" and name_of(c).startswith("IMG_001")
                    and not is_offscreen(c)]
        check("U5 搜索后列表只剩少数行（留出空白）", 2 <= len(narrowed) < 20,
              "%d 行" % len(narrowed))
        if len(narrowed) >= 2:
            narrowed_names = {name_of(c) for c in narrowed}
            check("U5 列表里 Ctrl 多选仍可用（与网格共用）", select_two(win, narrowed_names))

            # 清空选择：框选从 0 开始，选中数增加才能证明框选生效
            # （以前列表模式直接 return，Ctrl 拖空白会一直是 0）
            for c in media_items(win, narrowed_names):
                try:
                    if c.GetSelectionItemPattern().IsSelected:
                        c.GetSelectionItemPattern().RemoveFromSelection()
                except Exception:
                    pass
            time.sleep(0.6)
            before = selected_names(media_items(win, narrowed_names))
            box = [c.BoundingRectangle for c in narrowed]
            x1 = min(b.left for b in box) + 20
            x2 = max(b.right for b in box) - 20
            y1 = min(max(b.bottom for b in box) + 40, 920)
            y2 = min(b.top for b in box) + 4
            drag_with_ctrl(x1, y1, x2, y2)
            after = selected_names(media_items(win, narrowed_names))
            check("U5 列表模式 Ctrl 从空白框选能选中多行（和网格共用一套框选）",
                  before == [] and len(after) >= 2,
                  "框选前=%s 框选后=%s" % (before, after))
        # 清空搜索
        if search is not None:
            try:
                search.GetValuePattern().SetValue("")
            except Exception:
                pass
            time.sleep(2.0)

        # U7 列表模式进删除模式（不真删）
        batch = find_button(win, ("批量删除",))
        check("U7 找到「批量删除」按钮", batch is not None)
        if batch is not None:
            click_ctrl(hwnd, batch)
            banner = visible_text(win, "删除模式")
            # 顶部按钮进模式后改名「退出删除模式」；横幅里另有一个「退出模式 (Esc)」
            exit_btn = find_button(win, ("退出删除模式", "退出模式 (Esc)"))
            check("U7 列表模式能进删除模式、横幅出现",
                  banner is not None and exit_btn is not None,
                  "横幅=%s 退出按钮=%s" % (banner is not None, exit_btn is not None))
            if exit_btn is not None:
                click_ctrl(hwnd, exit_btn)
                time.sleep(0.6)

        # U8 重启后仍是列表
        kill_app()
        win2 = launch()
        check("U8 重启后主窗口出现", win2 is not None)
        if win2 is not None:
            check("U8 重启后「列表视图」仍选中",
                  radio_checked(radio(win2, "列表视图")))
    finally:
        kill_app()
        restore_db()
        print("库文件已还原")


def main():
    print("===== 静态段 =====")
    run_static()
    print("\n===== UIA 实机段 =====")
    run_uia()
    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    sys.exit(main())
