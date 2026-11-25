# -*- coding: utf-8 -*-
"""图片编辑器「工具即点即用」守卫（v1.4.10）：

  1) 切分类时退出裁剪 —— 裁剪框不再残留；点「裁剪」分类直接出框
  2) 点「文字」分类直接弹输入框 —— 没敲字就切走，文档与撤销栈都不留痕
  3) 退出裁剪恢复缩放 / 平移 —— 点进去看一眼不会把视图重置
  4) 打码面板里的「淡色描边只是预览辅助」说明
  5) 打码画笔提示圈：只在界面层，跟随鼠标 / 笔刷大小 / 缩放；名字固定不刷屏
  6) 按住反斜杠看原图时，裁剪框 / 画笔圈这些辅助线一起隐藏
  7) 马赛克笔迹切走再切回不丢；切矩形工具时画笔圈消失
  8) 导出路径不带预览描边：工具段真跑 overlaycheck，逐像素比对真实渲染

用法：
    python build/verify/verify_editor_tool_modes.py
"""

import io
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
APP = os.path.join(ROOT, "src", "MediaManager.App")
XAML = os.path.join(APP, "Views", "ViewerWindow.xaml")
CS = os.path.join(APP, "Views", "ViewerWindow.xaml.cs")
RENDERER = os.path.join(APP, "Editing", "EditorRenderer.cs")
DOC = os.path.join(APP, "Editing", "EditorDocument.cs")
OVERLAY = os.path.join(APP, "Editing", "EditorOverlayFactory.cs")
OVERLAYCHECK = os.path.join(ROOT, "build", "overlaycheck", "Program.cs")
OVERLAYCHECK_PROJ = os.path.join(ROOT, "build", "overlaycheck", "OverlayCheck.csproj")

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


def block_after(text, start, length=2400):
    i = text.find(start)
    return text[i:i + length] if i >= 0 else ""


# ===================================================================
#  静态段
# ===================================================================

def run_static():
    for path in (XAML, CS, RENDERER, DOC):
        if not os.path.exists(path):
            check("文件存在：" + os.path.basename(path), False, path)
            return

    xaml = no_comments(read(XAML))
    cs = read(CS)
    renderer = read(RENDERER)

    # 1 七个分类 Tag 齐全，且都有 UIA 名字（实机段按名字点）
    tags = re.findall(r'Tag="(basic|crop|color|text|draw|privacy|mark)"', xaml)
    named = re.findall(r'AutomationProperties\.Name="(基础|裁剪|调色|文字|标注|打码|水印)"', xaml)
    check("七个分类 Tag 与 UIA 名字齐全",
          len(set(tags)) == 7 and len(set(named)) == 7,
          "Tag=%d 个 / 名字=%d 个" % (len(set(tags)), len(set(named))))

    # 2 切分类时退出裁剪（裁剪框残留守卫）
    show = block_after(cs, "private void ShowCategory(string tag)", 2600)
    default_part = show[show.find("default:"):] if "default:" in show else ""
    check("ShowCategory 的 default 分支会退出裁剪（切走不再残留裁剪框）",
          "ExitCropMode()" in default_part,
          "default 段含 ExitCropMode=%s" % ("ExitCropMode()" in default_part))

    # 2b 分类 rail 即点即用：点裁剪直接出框，点文字直接弹输入框
    crop_case = re.search(r'case "crop":.{0,200}?EnterCropMode\(\);', show, re.S)
    text_case = re.search(r'case "text":.{0,200}?AddTextItem\(isWatermark: false\);', show, re.S)
    check("点「裁剪 / 文字」分类就进入对应工具（不用再点面板按钮）",
          bool(crop_case) and bool(text_case),
          "裁剪=%s 文字=%s" % (bool(crop_case), bool(text_case)))

    # 2c 裁剪框默认就是整张图，不能悄悄按 80% 内缩
    check("裁剪框默认框住整张图（不再按 80% 内缩）",
          bool(re.search(r"_cropBox = new CropBox\(0, 0, w, h\);", cs)),
          "找到 80% 内缩" if "w * 0.1" in cs else "整图初始化")

    # 3 进入裁剪保存视图、退出恢复；确认裁剪时不恢复
    enter = block_after(cs, "private void EnterCropMode()", 900)
    exit_block = block_after(cs, "private void ExitCropMode(bool restoreView = true)", 900)
    confirm = block_after(cs, "private void OnConfirmCrop", 1200)
    saved = "_cropZoomBefore = _viewModel.Zoom" in enter and "_cropPanBefore" in enter
    restored = ("ApplyZoom(_cropZoomBefore)" in exit_block
                and "_cropPanBefore.X" in exit_block)
    confirm_skips = "ExitCropMode(restoreView: false)" in confirm
    check("进入裁剪保存缩放/平移，退出恢复（确认裁剪除外）",
          saved and restored and confirm_skips,
          "保存=%s 恢复=%s 确认不恢复=%s" % (saved, restored, confirm_skips))

    # 4 自动文本框不再预写占位串；挂起到编辑结束一次性落库（一次撤销回干净）
    add_start = cs.find("private void AddTextItem")
    add_end = cs.find("private void SelectText", add_start)
    add = cs[add_start:add_end] if add_start >= 0 and add_end > add_start else ""
    end = block_after(cs, "private void EndTextEditing", 2200)
    ti = cs.find("box.TextChanged")
    typing = cs[ti:ti + 420] if ti >= 0 else ""
    pending_added = "_textPending = true" in add
    add_defers = "Texts.Add" not in add
    end_writes = ('Mutate("添加文字"' in end and "_doc.Texts.Add(added)" in end)
    discard = "wasPending" in end
    typing_quiet = ("Texts.Add" not in typing and "Mutate(" not in typing
                    and "CommitPendingText" not in cs)
    # 提示语「直接输入文字」里也含这四个字，这里只认带引号的占位串字面量
    check("文字框先挂起，编辑结束一次性落库；没敲就切走直接丢（一次撤销回干净）",
          pending_added and add_defers and end_writes and discard and typing_quiet
          and '"输入文字"' not in cs,
          "挂起=%s 添加时不写库=%s 结束时落库=%s 切走丢弃=%s 敲字阶段不写库=%s 占位串=%s"
          % (pending_added, add_defers, end_writes, discard, typing_quiet,
             '"输入文字"' in cs))

    # 5 打码说明只在 PrivacyPanel 里
    privacy = block_after(xaml, '<StackPanel x:Name="PrivacyPanel"', 5000)
    explain = ("成品里不会有这圈描边" in privacy
               and 'AutomationProperties.Name="打码描边说明"' in privacy)
    tool_hint = block_after(xaml, 'x:Name="ToolHint"', 300)
    check("「淡色描边只是预览辅助」说明在打码面板内（不在顶部单行提示里）",
          explain and "成品里不会有这圈描边" not in tool_hint,
          "面板内=%s 顶部提示内=%s" % (explain, "成品里不会有这圈描边" in tool_hint))

    # 6 导出侧永远不传 showOutline:true（成品不带辅助描边）
    check("EditorRenderer 调打码绘制时不传 showOutline:true（成品不带描边）",
          "showOutline: true" not in renderer,
          "发现 showOutline: true" if "showOutline: true" in renderer else "0 处")

    # 7 画笔提示圈只在界面层；文档 / 渲染器不许出现，界面钩子要齐
    doc = read(DOC)
    brush_leak = any(k in doc or k in renderer for k in ("BrushCursor", "画笔大小提示圈"))
    brush_ui = ('x:Name="BrushCursorLayer"' in xaml
                and 'AutomationProperties.Name="画笔大小提示圈"' in xaml)
    hooks = ("UpdateBrushCursor(e.GetPosition(ImageBox))" in cs
             and "HideBrushCursor();" in cs
             and "RefreshBrushCursor();" in cs)
    check("画笔提示圈只在界面层，且跟随鼠标 / 滑块 / 缩放刷新",
          brush_ui and not brush_leak and hooks,
          "界面=%s 泄漏进文档/渲染器=%s 刷新钩子=%s"
          % (brush_ui, brush_leak, hooks))

    # 8 画笔圈尺寸不许塞进 UIA 名字（否则读屏软件每次鼠标移动都播报一遍）
    fixed_name = ('AutomationProperties.SetName(BrushCursorProbe' not in cs
                  and "_brushCursorDiameter" in cs)
    check("画笔提示圈的 UIA 名字固定（直径只放代码变量，读屏不刷屏）",
          fixed_name,
          "动态改名字=%s 直径变量=%s"
          % ('AutomationProperties.SetName(BrushCursorProbe' in cs,
             "_brushCursorDiameter" in cs))

    # 9 按住 \ 看原图：画笔圈、裁剪框这类辅助线也得一起藏，且鼠标再动不能画出来
    begin_cmp = block_after(cs, "private void BeginCompare()", 1200)
    end_cmp = block_after(cs, "private void EndCompare()", 1200)
    brush_fn = block_after(cs, "private void UpdateBrushCursor", 500)
    cmp_ok = ("HideBrushCursor();" in begin_cmp
              and "CropLayer.Visibility = Visibility.Collapsed" in begin_cmp
              and "_comparing" in brush_fn
              and "_mode == ToolMode.Crop" in end_cmp)
    check("按 \\ 看原图时画笔圈/裁剪框一起隐藏（松开后裁剪框恢复）",
          cmp_ok,
          "开始藏画笔=%s 开始藏裁剪=%s 鼠标移动守卫=%s 结束恢复=%s"
          % ("HideBrushCursor();" in begin_cmp,
             "CropLayer.Visibility = Visibility.Collapsed" in begin_cmp,
             "_comparing" in brush_fn,
             "_mode == ToolMode.Crop" in end_cmp))

    # 10 预览侧打码覆盖层带 UIA 名字（只加在 RebuildMosaics，导出工厂不许碰）
    rebuild = block_after(cs, "private void RebuildMosaics", 1800)
    overlay_src = read(OVERLAY)
    check("预览侧打码覆盖层带 UIA 名字，导出工厂不受影响",
          "打码覆盖层" in rebuild and "AutomationProperties.SetName" in rebuild
          and "打码覆盖层" not in overlay_src,
          "重建里命名=%s 工厂未污染=%s"
          % ("打码覆盖层" in rebuild, "打码覆盖层" not in overlay_src))

    # 11 overlaycheck 工具的静态口径：断言写在源码里、且工具本身已入库（真跑在工具段）
    overlaycheck = read(OVERLAYCHECK) if os.path.exists(OVERLAYCHECK) else ""
    check("overlaycheck 源码里写有「导出 vs 无描边」的逐像素断言（静态，执行见工具段）",
          "EXPORT_NO_OUTLINE" in overlaycheck
          and "EditorRenderer.Render(doc)" in overlaycheck
          and "showOutline: true" in overlaycheck,
          "断言=%s 真渲染=%s"
          % ("EXPORT_NO_OUTLINE" in overlaycheck,
             "EditorRenderer.Render(doc)" in overlaycheck))


# ===================================================================
#  工具段：真跑 overlaycheck，用真实渲染出来的像素说话
# ===================================================================

def run_tool():
    import re as _re
    import shutil
    import subprocess

    check("overlaycheck 已入库（在 build/ 下，不在被忽略的 artifacts/）",
          os.path.exists(OVERLAYCHECK) and os.path.exists(OVERLAYCHECK_PROJ),
          "Program.cs=%s 项目=%s"
          % (os.path.exists(OVERLAYCHECK), os.path.exists(OVERLAYCHECK_PROJ)))

    dotnet = shutil.which("dotnet")
    if dotnet:
        cmd = [dotnet, "run", "--project", OVERLAYCHECK_PROJ, "-c", "Debug"]
    else:
        exe = os.path.join(ROOT, "build", "overlaycheck", "bin", "Debug",
                           "net8.0-windows", "OverlayCheck.exe")
        if not os.path.exists(exe):
            check("本机能跑 overlaycheck（dotnet 或已编好的 exe）", False,
                  "找不到 dotnet，也没有 %s" % exe)
            return
        cmd = [exe]

    try:
        proc = subprocess.run(cmd, cwd=ROOT, capture_output=True, timeout=300)
        out = (proc.stdout or b"").decode("utf-8", "replace") \
            + (proc.stderr or b"").decode("utf-8", "replace")
    except Exception as exc:
        check("overlaycheck 实跑成功", False, "%s：%s" % (type(exc).__name__, exc))
        return

    outline = _re.search(r"OUTLINE_DIFF=([\d.]+)", out)
    export = _re.search(r"EXPORT_DIFF=([\d.]+)", out)
    check("overlaycheck 实跑成功并输出两项像素差",
          proc.returncode == 0 and outline is not None and export is not None
          and "EXPORT_NO_OUTLINE=PASS" in out,
          "退出码=%s 描边=%s 导出=%s"
          % (proc.returncode,
             outline.group(1) if outline else None,
             export.group(1) if export else None))
    if outline is None or export is None:
        return

    x, y = float(outline.group(1)), float(export.group(1))
    check("实跑像素比对：开描边的预览确实改像素（证明这圈只在界面层）",
          x > 0.5, "描边差=%.3f" % x)
    check("实跑像素比对：导出与「无描边」逐像素一致（成品不带描边）",
          y < 1.0, "导出差=%.3f" % y)


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
    import subprocess
    import tempfile
    import time

    user32 = ctypes.windll.user32
    user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
    # 不给 mouse_event 设 argtypes：详情见 verify_view_switch.py 的同类注释

    app_dir = os.path.join(APP, "bin", "Debug", "net8.0-windows")
    app = os.environ.get("MM_VERIFY_APP") or os.path.join(app_dir, "MediaManager.App.exe")
    data_dir = os.path.join(app_dir, "Data")
    if not os.path.isdir(data_dir):
        data_dir = os.path.join(os.environ.get("LOCALAPPDATA", ""), "MediaManager")
    db = os.path.join(data_dir, "library.db")
    bak_dir = os.path.join(tempfile.gettempdir(), "MM_编辑器工具_备份")

    if not os.path.exists(app) or not os.path.exists(db):
        check("开发库与 Debug 版可执行文件存在", False, "app=%s db=%s" % (app, db))
        return

    def name_of(c):
        try:
            return c.Name or ""
        except Exception:
            return ""

    def offscreen(c):
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
        return uia.GetRootControl().GetChildren()

    def main_window():
        for w in top_windows():
            try:
                if w.Name and "媒体管理器" in w.Name:
                    return w
            except Exception:
                continue
        return None

    def viewer_window():
        for w in top_windows():
            try:
                if (w.Name or "").endswith("查看器"):
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
        time.sleep(1.0)

    def backup_db():
        if os.path.isdir(bak_dir):
            restore_db()
        os.makedirs(bak_dir, exist_ok=True)
        for suffix in ("", "-wal", "-shm"):
            src = db + suffix
            if os.path.exists(src):
                shutil.copy2(src, os.path.join(bak_dir, "library.db" + suffix))

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

    def radio(win, label):
        for c in collect(win):
            if c.ControlTypeName == "RadioButtonControl" and name_of(c) == label:
                return c
        return None

    def button_exact(win, name):
        for c in collect(win):
            if c.ControlTypeName == "ButtonControl" and name_of(c) == name:
                return c
        return None

    def text_named(win, name):
        for c in collect(win):
            if c.ControlTypeName == "TextControl" and name_of(c) == name:
                return c
        return None

    def edit_named(win, name):
        for c in collect(win):
            if c.ControlTypeName == "EditControl" and name_of(c) == name:
                return c
        return None

    def undo_button(win):
        """撤销按钮：按钮自身的 UIA 名字可能是空的，靠里面的「撤销」文字反找祖先。"""
        for c in collect(win):
            if c.ControlTypeName == "ButtonControl" and "撤销" in name_of(c):
                return c
        target = None
        for c in collect(win):
            if c.ControlTypeName == "TextControl" and name_of(c) == "撤销":
                target = c
                break
        p = target
        for _ in range(4):
            if p is None:
                break
            try:
                p = p.GetParentControl()
            except Exception:
                break
            if p is not None and p.ControlTypeName == "ButtonControl":
                return p
        return None

    def zoom_text(win):
        """缩放文本紧贴在「＋」左侧同一行：按位置取，别让按钮文字和面板里的 100% 混进来。"""
        plus = button_exact(win, "＋")
        if plus is None:
            return ""
        pr = plus.BoundingRectangle
        best = ""
        for c in collect(win):
            if c.ControlTypeName != "TextControl":
                continue
            n = name_of(c)
            if not (n.startswith("适应窗口") or re.fullmatch(r"\d{1,3}%", n)):
                continue
            r = c.BoundingRectangle
            if r.bottom <= pr.top or r.top >= pr.bottom:
                continue
            if r.right > pr.left + 5 or pr.left - r.right > 160:
                continue
            best = n
        return best

    def select(c):
        try:
            c.GetSelectionItemPattern().Select()
            return True
        except Exception:
            return False

    def press(c):
        try:
            c.GetInvokePattern().Invoke()
            return True
        except Exception:
            pass
        try:
            r = c.BoundingRectangle
            user32.SetCursorPos((r.left + r.right) // 2, (r.top + r.bottom) // 2)
            time.sleep(0.15)
            user32.mouse_event(2, 0, 0, 0, 0); user32.mouse_event(4, 0, 0, 0, 0)
            return True
        except Exception:
            return False

    def native_double_click(x, y):
        user32.SetCursorPos(x, y)
        time.sleep(0.3)
        user32.mouse_event(2, 0, 0, 0, 0); user32.mouse_event(4, 0, 0, 0, 0)
        time.sleep(0.08)
        user32.mouse_event(2, 0, 0, 0, 0); user32.mouse_event(4, 0, 0, 0, 0)

    def biggest_image(win):
        best, area = None, 0
        for c in collect(win):
            if c.ControlTypeName != "ImageControl" or offscreen(c):
                continue
            r = c.BoundingRectangle
            a = (r.right - r.left) * (r.bottom - r.top)
            if a > area:
                best, area = c, a
        return best

    def hover(ctrl):
        """把鼠标移到画面上再抖一下，确保 WPF 收到 MouseMove。"""
        r = ctrl.BoundingRectangle
        cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        user32.SetCursorPos(cx, cy)
        time.sleep(0.15)
        user32.SetCursorPos(cx + 6, cy + 4)
        time.sleep(0.35)

    def probe_named(win, prefix):
        for c in collect(win):
            if c.ControlTypeName == "TextControl" and name_of(c).startswith(prefix):
                return c
        return None

    def probe_size(probe):
        """探针名字是固定的，直径从它的宽高（UIA 外接矩形）量。"""
        if probe is None:
            return None
        r = probe.BoundingRectangle
        return int(round(max(r.right - r.left, r.bottom - r.top)))

    def slider_named(win, name):
        for c in collect(win):
            if c.ControlTypeName == "SliderControl" and name_of(c) == name:
                return c
        return None

    def combo_named(win, name):
        for c in collect(win):
            if c.ControlTypeName == "ComboBoxControl" and name_of(c) == name:
                return c
        return None

    def choose_combo(win, combo_name, item_label):
        """展开下拉框选一项；下拉项可能在弹窗里，控件子树和顶层窗口都找一遍。"""
        combo = combo_named(win, combo_name)
        if combo is None:
            return False
        try:
            combo.GetExpandCollapsePattern().Expand()
        except Exception:
            pass
        time.sleep(0.4)
        target = None
        scopes = [combo] + list(top_windows())
        for scope in scopes:
            for c in collect(scope):
                if c.ControlTypeName == "ListItemControl" and name_of(c) == item_label:
                    target = c
                    break
            if target is not None:
                break
        ok = False
        if target is not None:
            ok = select(target)
        try:
            combo.GetExpandCollapsePattern().Collapse()
        except Exception:
            pass
        time.sleep(0.4)
        return ok

    def overlay_count(win):
        """数预览侧打码覆盖层：RebuildMosaics 给每个覆盖层贴了「打码覆盖层 N」。"""
        return len([c for c in collect(win)
                    if name_of(c).startswith("打码覆盖层") and not offscreen(c)])

    def brush_drag(ctrl, steps=7):
        """在画面左上区域横着刷一笔，避开默认落在中间的文字（落库后才会进 RebuildMosaics）。"""
        r = ctrl.BoundingRectangle
        x0 = r.left + (r.right - r.left) // 4
        y0 = r.top + (r.bottom - r.top) // 4
        user32.SetCursorPos(x0, y0)
        time.sleep(0.2)
        user32.mouse_event(2, 0, 0, 0, 0)
        time.sleep(0.1)
        for i in range(1, steps + 1):
            user32.SetCursorPos(x0 + i * 18, y0 + (i % 2) * 6)
            time.sleep(0.05)
        user32.mouse_event(4, 0, 0, 0, 0)
        time.sleep(0.9)

    def key_down(vk):
        user32.keybd_event(vk, 0, 0, 0)

    def key_up(vk):
        user32.keybd_event(vk, 0, 2, 0)

    def crop_pixels(win):
        for c in collect(win):
            if c.ControlTypeName != "TextControl":
                continue
            m = re.match(r"^(\d+)\s*×\s*(\d+)\s*像素$", name_of(c))
            if m:
                return int(m.group(1)), int(m.group(2))
        return None

    def image_pixels(win):
        """底部信息里的原始分辨率，形如 1280×800（不带空格和「像素」后缀）。"""
        for c in collect(win):
            if c.ControlTypeName != "TextControl":
                continue
            m = re.match(r"^(\d{2,5})×(\d{2,5})", name_of(c))
            if m:
                return int(m.group(1)), int(m.group(2))
        return None

    def open_viewer(win):
        """双击网格里第一个可见项，返回查看器窗口（首启/虚拟化慢时重试）。"""
        for _ in range(30):
            if find_item(win) is not None:
                break
            time.sleep(0.5)
        for _ in range(3):
            item = find_item(win)
            if item is None:
                time.sleep(1.0)
                continue
            force_foreground(win.NativeWindowHandle)
            r = item.BoundingRectangle
            native_double_click((r.left + r.right) // 2, (r.top + r.bottom) // 2)
            v = wait_for(viewer_window, 8)
            if v is not None:
                return v
        return None

    def find_item(win):
        best, area = None, 0
        for c in collect(win):
            if c.ControlTypeName != "ListControl":
                continue
            r = c.BoundingRectangle
            a = (r.right - r.left) * (r.bottom - r.top)
            if a > area:
                best, area = c, a
        if best is None:
            return None
        for c in best.GetChildren():
            if c.ControlTypeName == "ListItemControl" and not offscreen(c):
                return c
        return None

    kill_app()
    backup_db()
    subprocess.Popen([app], cwd=app_dir)
    try:
        main = wait_for(main_window, 30)
        if main is None:
            check("主窗口出现", False)
            return
        time.sleep(3.0)
        force_foreground(main.NativeWindowHandle)
        viewer = open_viewer(main)
        check("双击网格第一项能打开查看器", viewer is not None)
        if viewer is None:
            return
        time.sleep(1.5)
        force_foreground(viewer.NativeWindowHandle)

        # ---- U1 打码说明只在打码面板 ----
        priv = radio(viewer, "打码")
        basic = radio(viewer, "基础")
        check("查看器里能找到「打码 / 基础」分类", priv is not None and basic is not None)
        if priv is not None and basic is not None:
            select(priv)
            time.sleep(0.6)
            explain = text_named(viewer, "打码描边说明")
            check("打码面板里能读到描边说明", explain is not None and not offscreen(explain))
            select(basic)
            time.sleep(0.6)
            explain2 = text_named(viewer, "打码描边说明")
            gone = explain2 is None or offscreen(explain2)
            check("切到「基础」后描边说明不可见", gone)

        # ---- U2 点「裁剪」分类立刻出框 + 缩放恢复 ----
        crop = radio(viewer, "裁剪")
        basic = radio(viewer, "基础")
        check("查看器里能找到「裁剪 / 基础」分类", crop is not None and basic is not None)
        if crop is not None:
            force_foreground(viewer.NativeWindowHandle)
            uia.SendKeys("{Ctrl}0", waitTime=0.5)
            zoom_fit = zoom_text(viewer)
            uia.SendKeys("{Ctrl}1", waitTime=0.5)
            zoom_before = zoom_text(viewer)
            check("切到 1:1 后能读到与「适应窗口」不同的缩放值",
                  zoom_fit.startswith("适应窗口") and bool(zoom_before)
                  and zoom_before != zoom_fit,
                  "适应窗口=%s 1:1=%s" % (zoom_fit, zoom_before))

            select(crop)
            time.sleep(0.9)
            marker = text_named(viewer, "裁剪框")
            check("点「裁剪」分类就出现裁剪框（不用再点面板按钮）",
                  marker is not None and not offscreen(marker))
            check("进入裁剪后视图回到适应窗口", zoom_text(viewer).startswith("适应窗口"),
                  "缩放=%s" % zoom_text(viewer))
            size = crop_pixels(viewer)
            img_px = image_pixels(viewer)
            check("裁剪框默认等于整张图大小（不再比图片小一圈）",
                  size is not None and img_px is not None and size == img_px,
                  "裁剪=%s 图片=%s" % (size, img_px))

            if basic is not None:
                select(basic)
                time.sleep(0.8)
            marker2 = text_named(viewer, "裁剪框")
            check("从裁剪切到「基础」后裁剪框消失",
                  marker2 is None or offscreen(marker2))
            check("切走后缩放恢复到进入前", zoom_text(viewer) == zoom_before,
                  "进入前=%s 现在=%s" % (zoom_before, zoom_text(viewer)))

        # ---- U3 点「文字」分类立刻弹输入框；不敲字切走不留痕 ----
        text_cat = radio(viewer, "文字")
        priv = radio(viewer, "打码")
        if text_cat is not None:
            select(text_cat)
            time.sleep(0.9)
        box = edit_named(viewer, "文字输入框")
        check("点「文字」分类就出现输入框（不用再点面板按钮）",
              box is not None and not offscreen(box))

        if priv is not None:
            select(priv)
            time.sleep(0.8)
        box2 = edit_named(viewer, "文字输入框")
        check("一个字没敲就切到打码，文本框消失", box2 is None or offscreen(box2))
        check("文档里没有留下「输入文字」占位串",
              text_named(viewer, "输入文字") is None)
        undo = undo_button(viewer)
        check("没敲字就切走不会产生撤销记录", undo is not None and not undo.IsEnabled)

        # ---- U4 打码画笔提示圈：出现、随直径变大、切走隐藏 ----
        img = biggest_image(viewer)
        check("查看器里能找到画面区域", img is not None)
        if img is not None:
            hover(img)
            probe = probe_named(viewer, "画笔大小提示圈")
            check("打码界面移动鼠标出现画笔提示圈",
                  probe is not None and not offscreen(probe))
            check("画笔提示圈名字固定，不带直径（读屏不会跟着鼠标刷屏）",
                  probe is None or name_of(probe) == "画笔大小提示圈",
                  "名字=%r" % (name_of(probe) if probe is not None else None))
            size1 = probe_size(probe)
            slider = slider_named(viewer, "画笔大小")
            check("打码面板里有「画笔大小」滑块", slider is not None)
            if slider is not None:
                try:
                    slider.GetRangeValuePattern().SetValue(80.0)
                except Exception:
                    pass
                time.sleep(0.5)
                hover(img)
                size2 = probe_size(probe_named(viewer, "画笔大小提示圈"))
                check("画笔调大后圆圈直径跟着变大",
                      size1 is not None and size2 is not None and size2 > size1,
                      "%s → %s" % (size1, size2))
            if basic is not None:
                select(basic)
                time.sleep(0.8)
            hover(img)
            probe3 = probe_named(viewer, "画笔大小提示圈")
            check("切回「基础」后画笔提示圈隐藏",
                  probe3 is None or offscreen(probe3))

        # ---- U5 真敲字后要正常落库（别把「先挂起」做成「永远不落库」） ----
        if text_cat is not None:
            select(text_cat)
            time.sleep(0.9)
        box3 = edit_named(viewer, "文字输入框")
        check("再次点「文字」分类还能弹出输入框", box3 is not None)
        if box3 is not None:
            try:
                box3.SetFocus()
            except Exception:
                pass
            uia.SendKeys("验证文字", waitTime=0.8)
        if priv is not None:
            select(priv)
            time.sleep(0.8)
        undo2 = undo_button(viewer)
        check("敲了字再切走会留下一次可撤销的添加",
              undo2 is not None and undo2.IsEnabled)
        if undo2 is not None and undo2.IsEnabled:
            press(undo2)
            time.sleep(0.9)
            left = text_named(viewer, "验证文字")
            check("撤销一次就把这段文字撤干净（不用按两次）",
                  left is None or offscreen(left),
                  "还残留「验证文字」" if left is not None and not offscreen(left) else "已撤掉")
            # 只撤一次就该到底：文字没了、撤销按钮也变灰（否则「要按两次」的旧毛病会漏网）
            undo_gone = undo_button(viewer)
            check("撤销一次后「撤销」按钮变灰（到底了，不用按两次）",
                  undo_gone is not None and not undo_gone.IsEnabled,
                  "按钮=%s 可用=%s"
                  % ("在" if undo_gone is not None else "没了",
                     undo_gone.IsEnabled if undo_gone is not None else None))

        # ---- U6 画一笔马赛克 → 切到文字 → 切回来，笔迹还在 ----
        img = biggest_image(viewer)
        if img is not None:
            choose_combo(viewer, "打码工具", "画笔涂抹")
            brush_drag(img)
            before = overlay_count(viewer)
            check("画一笔马赛克后预览出现覆盖层", before >= 1, "覆盖层=%s" % before)

            if text_cat is not None:
                select(text_cat)
                time.sleep(0.9)
            if priv is not None:
                select(priv)
                time.sleep(0.9)
            after = overlay_count(viewer)
            check("切到文字再切回打码，马赛克笔迹还在",
                  before >= 1 and after >= before,
                  "切走前=%s 切回后=%s" % (before, after))

            undo6 = undo_button(viewer)
            check("马赛克这一笔可以撤销", undo6 is not None and undo6.IsEnabled)
            if undo6 is not None and undo6.IsEnabled:
                press(undo6)
                time.sleep(0.8)

        # ---- U7 切「拖一个矩形」时画笔圈消失，切回画笔再出现 ----
        if img is not None:
            ok_rect = choose_combo(viewer, "打码工具", "拖一个矩形")
            check("打码工具里能切到「拖一个矩形」", ok_rect)
            hover(img)
            probe_rect = probe_named(viewer, "画笔大小提示圈")
            check("切到矩形打码后画笔提示圈消失",
                  probe_rect is None or offscreen(probe_rect))
            choose_combo(viewer, "打码工具", "画笔涂抹")
            hover(img)
            probe_brush = probe_named(viewer, "画笔大小提示圈")
            check("切回画笔打码后提示圈又出现",
                  probe_brush is not None and not offscreen(probe_brush))

        # ---- U8 按住 \ 看原图：裁剪框、画笔圈这些辅助线都不许露 ----
        if crop is not None and img is not None:
            select(crop)
            time.sleep(0.9)
            layer_on = text_named(viewer, "裁剪框图层")
            check("看原图前：裁剪模式里裁剪层可见",
                  layer_on is not None and not offscreen(layer_on))

            force_foreground(viewer.NativeWindowHandle)
            key_down(0xDC)          # OemBackslash
            time.sleep(0.8)
            layer_off = text_named(viewer, "裁剪框图层")
            check("按住 \\ 看原图时裁剪层隐藏",
                  layer_off is None or offscreen(layer_off))
            key_up(0xDC)
            time.sleep(0.8)
            layer_back = text_named(viewer, "裁剪框图层")
            check("松开 \\ 后裁剪层恢复",
                  layer_back is not None and not offscreen(layer_back))

            if priv is not None:
                select(priv)
                time.sleep(0.9)
            hover(img)
            probe_on = probe_named(viewer, "画笔大小提示圈")
            check("看原图前：打码画笔圈可见",
                  probe_on is not None and not offscreen(probe_on))
            force_foreground(viewer.NativeWindowHandle)
            key_down(0xDC)
            time.sleep(0.8)
            probe_off = probe_named(viewer, "画笔大小提示圈")
            check("按住 \\ 看原图时画笔圈隐藏",
                  probe_off is None or offscreen(probe_off))
            key_up(0xDC)
            time.sleep(0.6)
            hover(img)
            probe_back = probe_named(viewer, "画笔大小提示圈")
            check("松开 \\ 后画笔圈跟着鼠标重新出现",
                  probe_back is not None and not offscreen(probe_back))
    finally:
        kill_app()
        restore_db()
        print("库文件已还原")


def main():
    print("===== 静态段 =====")
    run_static()
    print("\n===== 工具段（真渲染像素比对）=====")
    run_tool()
    print("\n===== UIA 实机段 =====")
    run_uia()
    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    import sys
    sys.exit(main())
