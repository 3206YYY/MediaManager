# -*- coding: utf-8 -*-
"""顶栏锚点守卫（纯静态）。

背景：实机脚本是从**主窗口 UIA 树按名字扫按钮**的，而 WPF Popup 会生成一个独立
窗口 —— 主窗口树里扫不到（verify_plugin_breaker.py 选 ContextMenu 而不是 Popup
就是因为这个区别）。所以顶栏收拢时，凡是被脚本锚定的按钮都不能收进溢出菜单。

检查项：
  1 被锚定的按钮文案都还在 MainWindow.xaml 里（没被改名 / 删掉）
  2 这些文案一个都不出现在任何 <Popup> 里（溢出菜单 = 回归红）
  3 可以收进溢出的低频项确实在 <Popup> 里（防止「说搬了其实没搬」）
  4 <Popup> 里每个 Button 都有 AutomationProperties.Name（没名字 UIA 同样找不着）
  5 发现页提示文案还在 VM 里（verify_discovery_hint 按「按 R 换一批」找 UIA）：
    默认文案在 MainViewModel.cs，运行时带张数的那句在 MainViewModel.Discovery.cs —— 两个都要扫，
    只守默认值的话改运行时的句子照样绿。改文案时这里会先红。

用法：
    python build/verify/verify_toolbar_anchors.py
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
XAML_FILE = os.path.join(ROOT, "src", "MediaManager.App", "Views", "MainWindow.xaml")
VM_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.cs")
# 运行时真正显示的是 Discovery.cs 里带张数的那句（LoadDiscoveryAsync 在 v1.4.9
# 从 Media.cs 拆到了 Discovery.cs，文案跟着走）；默认值在 MainViewModel.cs 兜底。
VM_FILES = [
    VM_FILE,
    os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.Discovery.cs"),
]

# 文案 -> 守着它的实机脚本（只写在这里做备忘，检查是纯静态的）
ANCHORED = {
    "批量删除": "verify_batch_delete_help(84) / verify_grid_selection(17) / verify_delete_confirm_switch",
    "扫描": "verify_multi_subfolder(collect maxd=6) / verify_offline_folder",
    "设置": "verify_update_prompt / verify_update_settings / verify_desktop_shortcut",
    "插件": "verify_plugin_breaker（还断言熔断后按钮整个消失）",
    "换一批": "「🎲 换一批」按钮在 XAML（本脚本守）；「按 R 换一批」提示文案在 "
              "MainViewModel.Discovery.cs（本脚本第 5 项 + verify_discovery_hint 守）",
    "网格视图": "verify_view_switch(UIA)：主窗口树按名字找；收进 ⋯ 就扫不到，静态段也会先红",
    "列表视图": "verify_view_switch(UIA)：主窗口树按名字找；收进 ⋯ 就扫不到，静态段也会先红",
}

# 允许（且应当）收进溢出菜单的低频项
OVERFLOW_ITEMS = ["去重", "快速查看", "缩略图大小"]

_failed = 0
_total = 0


def check(name, ok, detail=""):
    global _failed, _total
    _total += 1
    if not ok:
        _failed += 1
    print("[%s]  %s%s" % ("PASS" if ok else "FAIL", name, ("  —— " + detail) if detail else ""))


def main():
    raw = io.open(XAML_FILE, encoding="utf-8").read()
    # 先剥注释：说明文字里提到「批量删除」不算真的按钮
    src = re.sub(r"<!--.*?-->", "", raw, flags=re.S)

    popups = re.findall(r"<Popup\b.*?</Popup>", src, re.S)
    popup_text = "\n".join(popups)

    # 1 锚点文案还在
    missing = [k for k in ANCHORED if k not in src]
    check("被脚本锚定的按钮文案都还在", not missing,
          ("丢了：" + "、".join(missing)) if missing else "%d/%d 在" % (len(ANCHORED), len(ANCHORED)))

    # 2 锚点不许进 Popup
    trapped = ["%s（%s）" % (k, ANCHORED[k]) for k in ANCHORED if k in popup_text]
    check("被锚定的按钮不在任何 Popup 内（主窗口 UIA 树扫不到 Popup）",
          not trapped, "；".join(trapped) if trapped else "%d 个都在 Popup 外" % len(ANCHORED))

    # 3 该收的确实收进 Popup 了
    not_moved = [k for k in OVERFLOW_ITEMS if k not in popup_text]
    check("低频项确实收进溢出菜单", not not_moved,
          ("没搬：" + "、".join(not_moved)) if not_moved else "、".join(OVERFLOW_ITEMS))

    # 4 Popup 里的按钮都得有 AutomationProperties.Name
    naked = []
    for i, block in enumerate(popups, 1):
        for tag in re.findall(r"<Button\b[^>]*>", block):
            if "AutomationProperties.Name" not in tag:
                naked.append("Popup#%d：%s" % (i, re.sub(r"\s+", " ", tag)[:60]))
    check("Popup 里每个 Button 都有 AutomationProperties.Name", not naked,
          "；".join(naked) if naked else "%d 个 Popup 全部有名字" % len(popups))

    # 5 发现页提示文案在 VM 里（verify_discovery_hint 按这句去 UIA 树里找）
    missing_copy = [os.path.basename(p) for p in VM_FILES
                    if "按 R 换一批" not in io.open(p, encoding="utf-8").read()]
    has_bind = "{Binding DiscoveryHintText}" in src
    check("发现页提示文案仍在 VM（默认 + 运行时）里并被 XAML 绑定",
          not missing_copy and has_bind,
          "缺文案：%s / 绑定%s" % ("、".join(missing_copy) or "无",
                                 "在" if has_bind else "丢了"))

    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    sys.exit(main())
