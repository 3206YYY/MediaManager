# -*- coding: utf-8 -*-
"""守住设计系统的地基：主题字典只剩颜色、模板只留一份、颜色不许写死。

背景：Light.xaml / Dark.xaml 原来各 489 行，其中 **462 行逐字相同** —— 两套主题
各自抄了一整套控件模板，只有 12 个颜色不同。结果是想调一个按钮的圆角要改两处，
漏改一处就在另一套主题下"看着有点怪"（这种 bug 截图很难发现）。
现在拆成三层：
    Tokens.xaml   度量令牌（圆角档位），与主题无关
    Styles.xaml   控件模板，深浅共用（只此一份）
    Light/Dark    纯颜色赋值（键名必须一一对应）
    Icons.xaml    功能图标矢量（关窗 / 翻页 / 收藏星）

**这一版做到哪一步，别自己骗自己**：键名仍是 PrimaryBrush / BgBrush / TextBrush 这类
"物理"命名（说的是它画在哪），只补了 OnPrimary / Danger / OnDanger 三个语义键。
完整语义层（Surface / Text / Accent / Success / Focus 六态）没做 —— 那要重命名 30 个键、
改 ~1200 处引用，属于阶段 B/C 的活，不是"地基"该顺手干的。

检查项：
  A 主题字典结构：9 项（不许有模板、键名对齐、语义键成对、不许长回去、App.xaml 挂载顺序、
                        不许有隐式 <Style TargetType="Window">）
  B 模板层纪律：4 项（Styles 与 Controls 都不许 StaticResource 引画刷、都不许写死色值）
  C 硬编码台账：3 项（颜色 / 字号 / 间距，全部只许减不许增）
  D 令牌不留死账：2 项（Tokens 的度量键、主题画刷键，都扫全量 XAML）
  E 功能图标：3 项（不用字符当图标、图标都被引用、矢量按钮必须有 AutomationProperties.Name）
  F 验收脚本的文本依赖：1 项（锚点直接从 verify_first_run.py 读，不手抄）
  G 汇总：1 项

台账口径（**和 v1.4.8 那版不同，看这里别按旧数字排工时**）：
  计入 = 6 位不透明 hex + 8 位但 alpha=FF 的 hex（#FFFFFFFF 就是纯白，不许豁免）
         + 命名色 White / Black
  不计 = 8 位且 alpha≠FF（半透明遮罩，压在画面上深浅同色是对的）
         阴影（<DropShadowEffect ... /> 整段跳过，阴影色按设计不跟主题走）
         主题字典 / Icons / FileTypeIconView（图标内容色）
         THEME_INDEPENDENT 里列出的文件（压在媒体画面上的浮层，设计上就该与主题无关）

用法：
    python build/verify/verify_design_tokens.py
    python build/verify/verify_design_tokens.py --update   # 打印当前欠账，用于下调台账
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))   # build/verify/ → 仓库根
APP = os.path.join(ROOT, "src", "MediaManager.App")
THEMES = os.path.join(APP, "Resources", "Themes")

LIGHT = os.path.join(THEMES, "Light.xaml")
DARK = os.path.join(THEMES, "Dark.xaml")
TOKENS = os.path.join(THEMES, "Tokens.xaml")
STYLES = os.path.join(THEMES, "Styles.xaml")
CONTROLS = os.path.join(THEMES, "Controls.xaml")
ICONS = os.path.join(APP, "Resources", "Icons.xaml")
APP_XAML = os.path.join(APP, "App.xaml")
APP_CS = os.path.join(APP, "App.xaml.cs")
MAIN_XAML = os.path.join(APP, "Views", "MainWindow.xaml")
FIRST_RUN = os.path.join(HERE, "verify_first_run.py")

# 主题字典不许超过这个行数。原来 489，拆分后 ≈62；留到 80 是给注释的余量，
# 谁要是往主题字典里塞回一个 <Style>，A 段第一项会先红（这项是防"慢慢长回去"）。
THEME_MAX_LINES = 80

# 硬编码色台账：**只许减不许增**。阶段 B 每迁移一个窗口就往下调一个数，归零就删条目。
# 数字按本文件头写的口径统计（含命名色 White/Black、含 alpha=FF 的 8 位），
# 比 v1.4.8 第一版（只数 6 位不透明色）**更严**，所以数字变大不代表变差了 —— 是口径对齐了。
#
# 剩下这 74 处大致两类，阶段 B 别把它们混着排工期：
#   ① 该换成主题令牌的：Settings/TextEdit/各类小弹窗里的边框与文字色（≈27 处）
#   ② 压在媒体画面上的叠加层：查看器 24 处、主窗口的角标/拖放高亮、白字投影（≈47 处）
#      这一类**不会进主题字典**（进主题反而错），而是阶段 B 新建一个与主题无关的
#      Overlay.xaml 收编。在它建起来之前先记成欠账，不豁免 ——
#      "还没想清楚"和"设计如此"是两件事，混在一起台账就没人看了。
HARDCODED_BUDGET = {
    "Views/Dialogs/BatchExportWindow.xaml": 1,
    "Views/Dialogs/BatchRenameWindow.xaml": 2,
    "Views/Dialogs/DialogWindow.xaml": 3,
    "Views/Dialogs/FolderPickerWindow.xaml": 1,
    "Views/Dialogs/FolderPreviewWindow.xaml": 1,
    "Views/Dialogs/SaveAsWindow.xaml": 1,
    "Views/Dialogs/TextPromptWindow.xaml": 1,
    "Views/Dialogs/UpdateWindow.xaml": 1,
    "Views/Dialogs/VaultPasswordWindow.xaml": 2,
    "Views/MainWindow.xaml": 14,
    "Views/RecentlyDeletedWindow.xaml": 3,
    "Views/SettingsWindow.xaml": 8,
    "Views/TextEditWindow.xaml": 7,
    "Views/VaultWindow.xaml": 5,
    "Views/ViewerWindow.xaml": 24,
}

# 字号 / 间距欠账：口径 = 属性写法（FontSize="12"、Padding="8"）与
# 样式写法（<Setter Property="FontSize" Value="12" />）都算。同样**只许减不许增**，
# 迁完一个窗口就 `--update` 把数字贴回来。0 的条目直接删掉，别留一堆空账。
FONT_SIZE_BUDGET = {
    "Controls/FileTypeIconView.xaml": 1,
    "Resources/Themes/Controls.xaml": 4,
    "Views/Dialogs/BatchExportWindow.xaml": 3,
    "Views/Dialogs/BatchRenameWindow.xaml": 5,
    "Views/Dialogs/DialogWindow.xaml": 3,
    "Views/Dialogs/FirstRunWindow.xaml": 5,
    "Views/Dialogs/FolderPickerWindow.xaml": 5,
    "Views/Dialogs/FolderPreviewWindow.xaml": 6,
    "Views/Dialogs/MediaPickerWindow.xaml": 6,
    "Views/Dialogs/SaveAsWindow.xaml": 6,
    "Views/Dialogs/TextPromptWindow.xaml": 2,
    "Views/Dialogs/UpdateWindow.xaml": 10,
    "Views/Dialogs/VaultPasswordWindow.xaml": 6,
    "Views/DiaryEditWindow.xaml": 7,
    "Views/DiaryWindow.xaml": 10,
    "Views/DuplicateWindow.xaml": 2,
    "Views/RecentlyDeletedWindow.xaml": 8,
    "Views/SettingsWindow.xaml": 3,
    "Views/VaultWindow.xaml": 16,
    "Views/VideoPlayerWindow.xaml": 19,
    "Views/ViewerWindow.xaml": 10,
}

SPACING_BUDGET = {
    "Resources/Themes/Controls.xaml": 14,
    "Resources/Themes/Styles.xaml": 16,
    "Views/Dialogs/BatchExportWindow.xaml": 15,
    "Views/Dialogs/BatchRenameWindow.xaml": 18,
    "Views/Dialogs/DialogWindow.xaml": 9,
    "Views/Dialogs/FirstRunWindow.xaml": 7,
    "Views/Dialogs/FolderPickerWindow.xaml": 12,
    "Views/Dialogs/FolderPreviewWindow.xaml": 7,
    "Views/Dialogs/MediaPickerWindow.xaml": 14,
    "Views/Dialogs/SaveAsWindow.xaml": 14,
    "Views/Dialogs/TextPromptWindow.xaml": 8,
    "Views/Dialogs/UpdateWindow.xaml": 14,
    "Views/Dialogs/VaultPasswordWindow.xaml": 16,
    "Views/DiaryEditWindow.xaml": 22,
    "Views/DiaryWindow.xaml": 21,
    "Views/DuplicateWindow.xaml": 11,
    "Views/MainWindow.xaml": 115,
    "Views/RecentlyDeletedWindow.xaml": 13,
    "Views/SettingsWindow.xaml": 96,
    "Views/TextEditWindow.xaml": 8,
    "Views/VaultWindow.xaml": 28,
    "Views/VideoPlayerWindow.xaml": 25,
    "Views/ViewerWindow.xaml": 151,
}

# 这些文件整体不参与台账。
#  - 主题字典：写死是本职
#  - Icons / FileTypeIconView：只有路径和"文件类型内容色"，不是 UI 色
WHITELIST = {
    "Resources/Themes/Light.xaml",
    "Resources/Themes/Dark.xaml",
    "Resources/Themes/Tokens.xaml",
    "Resources/Icons.xaml",
    "Controls/FileTypeIconView.xaml",
}

# 压在媒体画面上的浮层：与主题无关是**设计**，不是欠账。
# 列在这里是为了"显式"——以前这层豁免是"只数 6 位不透明色"的副作用，没人说得清。
THEME_INDEPENDENT = {
    "Views/VideoPlayerWindow.xaml": "播放器：根背景 #08090A + 半透明黑浮层 + 白字，压在视频上，深浅同色",
    "Views/Dialogs/MediaPickerWindow.xaml": "缩略图角标：#B0000000 圆底 + 白字，压在图片上",
}

# 阶段 B 想"先把语义键铺好、再逐个窗口替换引用"时，把键名加到这里，
# 否则 D 段会把它当成死键打回。**用完就清空**：留在里面等于放弃了死键检查。
PENDING_MIGRATION = set()

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def read(path):
    return io.open(path, encoding="utf-8").read()


def strip_comments(xaml):
    """去掉 XML 注释再数颜色，否则解释性注释里的色值会被当成硬编码。"""
    return re.sub(r"<!--.*?-->", "", xaml, flags=re.S)


def strip_shadows(xaml):
    """阴影色不跟主题走（黑投影在深浅两套下都该是黑的），整段跳过。

    这是**显式规则**，不是"只数 6 位"的副作用 —— 以前 DropShadowEffect 的
    Color="#000000" 会被算成欠账，永远归不了零，台账就没人看了。
    """
    return re.sub(r"<DropShadowEffect\b[^>]*/>", "", xaml)


HEX6 = re.compile(r"#[0-9A-Fa-f]{6}(?![0-9A-Fa-f])")
HEX8_OPAQUE = re.compile(r"#FF[0-9A-Fa-f]{6}")
NAMED = re.compile(r'="(?:White|Black)"')


def count_literal_colors(text):
    """当前口径：6 位不透明 + 8 位但 alpha=FF + 命名色 White/Black。"""
    body = strip_shadows(strip_comments(text))
    return len(HEX6.findall(body)) + len(HEX8_OPAQUE.findall(body)) + len(NAMED.findall(body))


# 字号 / 间距只认"数字字面量"。写成 {StaticResource ...} 或绑定的不算欠账。
FS_ATTR = re.compile(r'FontSize="([0-9.]+)"')
FS_SETTER = re.compile(r'Property="FontSize"\s+Value="([0-9.]+)"')
SP_ATTR = re.compile(r'(?:Margin|Padding)="([0-9.,\-]+)"')
SP_SETTER = re.compile(r'Property="(?:Margin|Padding)"\s+Value="([0-9.,\-]+)"')


def count_font_sizes(text):
    body = strip_comments(text)
    return len(FS_ATTR.findall(body)) + len(FS_SETTER.findall(body))


def count_spacings(text):
    body = strip_comments(text)
    return len(SP_ATTR.findall(body)) + len(SP_SETTER.findall(body))


def keys_of(text):
    return set(re.findall(r'x:Key="([^"]+)"', text))


def iter_xaml():
    for dirpath, dirnames, filenames in os.walk(APP):
        parts = dirpath.replace("\\", "/").split("/")
        if "obj" in parts or "bin" in parts:
            continue
        for f in filenames:
            if f.endswith(".xaml"):
                full = os.path.join(dirpath, f)
                yield os.path.relpath(full, APP).replace("\\", "/"), full


def check_ledger(label, budgets, counter):
    """按文件统计欠账，和台账逐项比。返回 current 供 --update 打印。"""
    current = {}
    for rel, full in iter_xaml():
        if rel == "Resources/Themes/Tokens.xaml":
            continue
        n = counter(read(full))
        if n:
            current[rel] = n

    over = []
    for rel, n in sorted(current.items()):
        budget = budgets.get(rel)
        if budget is None:
            over.append("%s 新增 %d 处（不在台账里）" % (rel, n))
        elif n > budget:
            over.append("%s %d 处 > 上限 %d" % (rel, n, budget))
    check(label, not over, "；".join(over) if over else
          "当前欠账 %d 处 / %d 个文件" % (sum(current.values()), len(current)))
    return current


BUTTON_ELEMENT = re.compile(r"<Button\b[^>]*?/>|<Button\b[^>]*>.*?</Button>", re.S)


def icon_buttons(text):
    """返回 [(起始标签, 整块)]：Content 是 Icons.xaml 矢量的 Button。

    必须用整体匹配，不能"从 <Button 找到下一个 </Button>" —— 那样每遇到一个文字按钮，
    块都会一直延伸到**后面那个**图标按钮的闭合标签，于是所有文字按钮都被误判成没名字。
    """
    out = []
    for m in BUTTON_ELEMENT.finditer(text):
        block = m.group(0)
        if "{StaticResource Icon." not in block:
            continue
        head_end = block.find(">")
        out.append((block[:head_end + 1], block))
    return out


def main():
    update_mode = "--update" in sys.argv

    # ---------- A 主题字典结构 ----------
    for path, label in ((LIGHT, "Light"), (DARK, "Dark")):
        if not os.path.exists(path):
            check("%s.xaml 存在" % label, False, path)
            continue
        text = read(path)
        styles = re.findall(r"<Style\b", text)
        check("%s.xaml 里没有控件模板（模板只能有一份，在 Styles.xaml）" % label,
              not styles, "发现 %d 个 <Style>" % len(styles) if styles else "0 个")
        n = len(text.split("\n"))
        check("%s.xaml 没有长回去（≤%d 行）" % (label, THEME_MAX_LINES),
              n <= THEME_MAX_LINES, "%d 行" % n)

    lk, dk = keys_of(read(LIGHT)), keys_of(read(DARK))
    only_light, only_dark = sorted(lk - dk), sorted(dk - lk)
    check("两套主题的键名完全对齐", not only_light and not only_dark,
          ("仅浅色有：" + "、".join(only_light) if only_light else "")
          + ("　仅深色有：" + "、".join(only_dark) if only_dark else "")
          if (only_light or only_dark) else "%d 个键两边一致" % len(lk))

    # 语义键成对：有 XxxBrush 就该有对应的 On/Off 配对（这条挡的是"只补一半"）
    semantic_pairs = [("DangerBrush", "OnDangerBrush"), ("PrimaryBrush", "OnPrimaryBrush")]
    unpaired = ["%s 缺 %s" % (a, b) for a, b in semantic_pairs if a in lk and b not in lk]
    check("成对的语义键不落单（危险/主色都要有配套的前景色）", not unpaired,
          "；".join(unpaired) if unpaired else "%d 对齐全" % len(semantic_pairs))

    app_xaml = read(APP_XAML)
    m = re.search(r"<ResourceDictionary\.MergedDictionaries>(.*?)</ResourceDictionary\.MergedDictionaries>",
                  app_xaml, re.S)
    first_src = ""
    if m:
        mm = re.search(r'<ResourceDictionary\s+Source="([^"]+)"', m.group(1))
        first_src = mm.group(1) if mm else ""
    check("App.xaml 合并字典的第 0 项是主题字典（SetThemeAsync 靠替换 [0] 切主题）",
          "Light.xaml" in first_src, first_src or "没找到")

    app_cs = read(APP_CS)
    check("App.xaml.cs 保留「替换 MergedDictionaries[0]」这条切主题通路",
          "MergedDictionaries[0] =" in app_cs, "App.ApplyThemeNow")

    # 隐式 <Style TargetType="Window"> 一次都不会应用：WPF 用 element.GetType() 查整个具体类型那一个键，
    # **不做基类回退**，而本项目窗口全是 Window 的派生类。2026-11-22 实测（塞 Consolas 重建后无变化、
    # 运行时 Window.Style == null）确认后删掉的那条，别被谁"觉得有用"补回来。
    window_style = [label for path, label in ((STYLES, "Styles.xaml"), (CONTROLS, "Controls.xaml"))
                    if os.path.exists(path)
                    and re.search(r'<Style\s+TargetType="Window"', strip_comments(read(path)))]
    check('共享字典里没有 <Style TargetType="Window">（隐式样式按实际类型查，派生窗口永远拿不到）',
          not window_style, "出现在：" + "、".join(window_style) if window_style else "0 处")

    # ---------- B 模板层纪律（Styles 与 Controls 同等对待） ----------
    for path, label in ((STYLES, "Styles.xaml"), (CONTROLS, "Controls.xaml")):
        if not os.path.exists(path):
            check("%s 存在" % label, False, path)
            continue
        t = read(path)
        bad_static = re.findall(r"\{StaticResource\s+([A-Za-z.]*Brush)\}", t)
        check("%s 不用 StaticResource 引画刷（会把颜色焊死在加载那一刻，切主题不生效）" % label,
              not bad_static,
              "违规：" + "、".join(sorted(set(bad_static))) if bad_static else "全部 DynamicResource")
        lit = count_literal_colors(t)
        check("%s 里没有写死的色值（含命名色 White/Black）" % label,
              lit == 0, "%d 处" % lit if lit else "0 处")

    # ---------- C 硬编码色台账 ----------
    current = {}
    for rel, full in iter_xaml():
        if rel in WHITELIST or rel in THEME_INDEPENDENT:
            continue
        n = count_literal_colors(read(full))
        if n:
            current[rel] = n

    over = []
    for rel, n in sorted(current.items()):
        budget = HARDCODED_BUDGET.get(rel)
        if budget is None:
            over.append("%s 新增 %d 处（不在台账里）" % (rel, n))
        elif n > budget:
            over.append("%s %d 处 > 上限 %d" % (rel, n, budget))
    check("没有新增硬编码主题色（欠账只许减不许增）", not over, "；".join(over) if over else
          "当前欠账 %d 处 / %d 个文件" % (sum(current.values()), len(current)))

    # ---------- C 字号 / 间距台账 ----------
    # 和颜色分开计数：字号/间距迁到 Tokens.xaml 的 FontSize.* / Space.*，
    # 每迁一个窗口就 --update 往下调，归零的条目删掉。
    font_current = check_ledger("没有新增硬编码字号（欠账只许减不许增）",
                                FONT_SIZE_BUDGET, count_font_sizes)
    space_current = check_ledger("没有新增硬编码间距（欠账只许减不许增）",
                                 SPACING_BUDGET, count_spacings)

    # ---------- D 令牌不留死账 ----------
    # 扫**全量 XAML**（不只 Styles.xaml）：否则阶段 B 在窗口里写 {StaticResource Radius.Md}
    # 会被判成死键，等于把令牌锁死在模板层。
    all_xaml = "".join(read(p) for rel, p in iter_xaml()
                       if rel not in ("Resources/Themes/Light.xaml", "Resources/Themes/Dark.xaml"))

    token_keys = sorted(keys_of(read(TOKENS)))
    dead = [k for k in token_keys if k not in PENDING_MIGRATION
            and not re.search(r"\{(?:Static|Dynamic)Resource\s+%s\}" % re.escape(k), all_xaml)]
    check("Tokens.xaml 的每个令牌都有人用（不留空令牌）",
          not dead, "没人用：" + "、".join(dead) if dead else "%d 个令牌全在用" % len(token_keys))

    dead_brush = [k for k in sorted(k for k in lk if k.endswith("Brush")) if k not in PENDING_MIGRATION
                  and not re.search(r"\{(?:Static|Dynamic)Resource\s+%s\}" % re.escape(k), all_xaml)]
    check("主题画刷没有死键（定义了就一定有人在用）", not dead_brush,
          "没人用：" + "、".join(dead_brush) if dead_brush else "%d 个画刷全在用" % len(
              [k for k in lk if k.endswith("Brush")]))

    # ---------- E 功能图标 ----------
    emoji_icon = []
    naked_icon_button = []
    for rel, full in iter_xaml():
        if rel in WHITELIST:
            continue
        text = read(full)
        for ch in "✕▶◀":
            if re.search(r'Content\s*=\s*"%s"' % ch, text):
                emoji_icon.append("%s 里的 Content=\"%s\"" % (rel, ch))
        for head, _block in icon_buttons(text):
            if "AutomationProperties.Name" not in head:
                snippet = re.sub(r"\s+", " ", head)[:70]
                naked_icon_button.append("%s：%s…" % (rel, snippet))

    check("功能按钮不再用字符当图标（都走 Icons.xaml 的矢量）", not emoji_icon,
          "；".join(emoji_icon) if emoji_icon else "0 处")

    check("矢量图标按钮都有 AutomationProperties.Name（否则屏幕阅读器念不出、UIA 也找不着）",
          not naked_icon_button, "；".join(naked_icon_button) if naked_icon_button
          else "全部有名字")

    icon_keys = keys_of(read(ICONS))
    unused_icon = [k for k in icon_keys
                   if not re.search(r"\{StaticResource\s+%s\}" % re.escape(k), all_xaml)]
    check("Icons.xaml 每个图标都被引用（不留画了没人用的图标）", not unused_icon,
          "没人用：" + "、".join(unused_icon) if unused_icon else "%d 个图标全在用" % len(icon_keys))

    # ---------- F 验收脚本的文本依赖 ----------
    # 锚点**从 verify_first_run.py 里读真实值**，不手抄：手抄的 "🏠" 比真实的 "🏠 全部媒体"
    # 弱得多 —— 文案只剩 emoji、后面字被删了，守卫还绿着，实机照样红。
    anchors = []
    if os.path.exists(FIRST_RUN):
        mm = re.search(r"SIDEBAR_MUST_KEEP\s*=\s*\[(.*?)\]", read(FIRST_RUN), re.S)
        if mm:
            anchors = re.findall(r'"([^"]+)"', mm.group(1))
    main_text = read(MAIN_XAML) if os.path.exists(MAIN_XAML) else ""
    missing_anchor = [a for a in anchors if a not in main_text]
    check("侧栏这三项文案没被改掉（verify_first_run 按完整文本找，不是按 emoji）",
          bool(anchors) and not missing_anchor,
          ("丢了：" + "、".join(missing_anchor)) if missing_anchor
          else ("%d/%d 在" % (len(anchors), len(anchors)) if anchors else "没读到 verify_first_run 的锚点"))

    # ---------- G 汇总 ----------
    if update_mode:
        print("当前硬编码色欠账（可直接贴回 HARDCODED_BUDGET）：")
        for rel, n in sorted(current.items()):
            print('    "%s": %d,' % (rel, n))

        print("\n当前硬编码字号欠账（可直接贴回 FONT_SIZE_BUDGET）：")
        for rel, n in sorted(font_current.items()):
            print('    "%s": %d,' % (rel, n))

        print("\n当前硬编码间距欠账（可直接贴回 SPACING_BUDGET）：")
        for rel, n in sorted(space_current.items()):
            print('    "%s": %d,' % (rel, n))

        print("\n按设计豁免（不计入台账）：")
        for rel, why in sorted(THEME_INDEPENDENT.items()):
            print("    %s —— %s" % (rel, why))

    return report()


def report():
    passed = sum(1 for _, ok, _ in results if ok)
    print()
    for name, ok, detail in results:
        print("%s  %s%s" % ("[PASS]" if ok else "[FAIL]", name,
                           ("  —— " + detail) if detail else ""))
    print("\n结果：%d/%d 通过" % (passed, len(results)))
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
