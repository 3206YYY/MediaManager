# -*- coding: utf-8 -*-
"""守住删除文案的唯一真值来源：XAML 不许断言「一定进回收站 / 一定能撤销」。

背景：v1.4.8 把「删除方式（回收站 / 永久删除 / 每次询问）」接成了真的，执行端
会按设置删；但界面上还散着一批写死的文案，用户在设置里选了永久删除后它们会变成假话：

    MainWindow.xaml  右键菜单「删除到回收站 (Delete)」      → 点下去是真删
    VaultWindow.xaml ToolTip「删除到回收站（默认）」
    SettingsWindow   帮助「文件进系统回收站，底部 5 秒撤销」
                     帮助「→ 文件进回收站 → 底部 5 秒内可点撤销」
                     帮助「删除不会立刻抹掉文件：先在回收站」
                     快捷键表「把选中的文件移到回收站，5 秒内可撤销」

`verify_batch_delete_help.py` 是真机 UIA 脚本，它点按钮、读确认框，但不会静态扫 XAML，
所以这几处一直没人守。这个脚本补的就是这个缺口：**纯静态、秒级、落地即进回归**。

规则：
  1. 剥掉 XML 注释再匹配（注释里举反例是正常的，别误伤）。
  2. 禁止在 XAML 里出现无条件的删除后果断言（见 BANNED）。
     「默认进回收站」「按设置里的删除方式」这类带限定词的说法是允许的 ——
     它们在任何档位下都是真话。
  3. MenuItem / Button 的 Header / Content 不许出现「回收站」：
     标题是断言式短语，没有上下文能限定它。
  4. DeletePolicy.cs 必须仍提供四处界面文案入口（防止被谁合并掉）。

用法：
    python build/verify/verify_delete_copy.py
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))   # build/verify/ → 仓库根
APP = os.path.join(ROOT, "src", "MediaManager.App")
POLICY = os.path.join(ROOT, "src", "MediaManager.Core", "Services", "DeletePolicy.cs")

# 无条件断言：出现即红。带「默认 / 按设置」限定的同类说法不在此列。
BANNED = [
    "删除到回收站",       # 菜单头断言「点了会进回收站」
    "移到回收站",         # 快捷键表断言
    "文件进系统回收站",   # 帮助页断言
    "文件进回收站",
    "先在回收站",         # 「删除不会立刻抹掉文件：先在回收站」
    "5 秒内可撤销",       # 永久删除档位下没有撤销条
    "5 秒内可点",         # 同上
    "5 秒「撤销」",       # 同上
]

# 标题类属性：没有上下文能加「默认」限定，出现「回收站」就是断言。
TITLE_ATTR = re.compile(
    r'<(?:MenuItem|Button)\b[^>]*?\b(?:Header|Content)="([^"]*回收站[^"]*)"', re.S)

# DeletePolicy 必须保留的界面文案入口（删一个 = 界面又开始各写各的）。
POLICY_API = [
    "DeleteModeHint",
    "BatchDeleteTooltip",
    "SelectionDeleteTooltip",
    "ContextDeleteHeader",
]

_failed = 0
_total = 0


def check(name, ok, detail=""):
    global _failed, _total
    _total += 1
    tag = "PASS" if ok else "FAIL"
    if not ok:
        _failed += 1
    print("[%s]  %s%s" % (tag, name, ("  —— " + detail) if detail else ""))
    return ok


def read(path):
    return io.open(path, encoding="utf-8").read()


def strip_comments(xaml):
    return re.sub(r"<!--.*?-->", "", xaml, flags=re.S)


def iter_xaml():
    for dirpath, dirnames, filenames in os.walk(APP):
        parts = dirpath.replace("\\", "/").split("/")
        if "obj" in parts or "bin" in parts:
            continue
        for f in filenames:
            if f.endswith(".xaml"):
                full = os.path.join(dirpath, f)
                yield os.path.relpath(full, APP).replace("\\", "/"), full


def main():
    hits = []       # [(相对路径, 行号, 禁止词)]
    title_hits = []  # [(相对路径, 行号, 标题)]

    for rel, full in sorted(iter_xaml(), key=lambda x: x[0]):
        text = strip_comments(read(full))
        for i, line in enumerate(text.splitlines(), 1):
            for word in BANNED:
                if word in line:
                    hits.append((rel, i, word))
            for m in TITLE_ATTR.finditer(line):
                title_hits.append((rel, i, m.group(1)))

    # A 无条件的删除后果断言
    detail = "；".join("%s:%d「%s」" % h for h in hits)
    check("XAML 里没有写死的「一定进回收站 / 一定能撤销」", not hits, detail or "0 处")

    # B 菜单 / 按钮标题不许出现「回收站」
    detail = "；".join("%s:%d「%s」" % h for h in title_hits)
    check("菜单 / 按钮标题不含「回收站」（标题无法加「默认」限定）",
          not title_hits, detail or "0 处")

    # C DeletePolicy 仍是唯一真值来源
    if not os.path.exists(POLICY):
        check("DeletePolicy.cs 存在", False, POLICY)
    else:
        src = read(POLICY)
        missing = [k for k in POLICY_API if not re.search(r"\b%s\b" % k, src)]
        check("DeletePolicy.cs 保留四处界面文案入口", not missing,
              "缺：" + "、".join(missing) if missing else "、".join(POLICY_API))

    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    sys.exit(main())
