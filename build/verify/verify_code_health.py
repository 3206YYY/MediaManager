# -*- coding: utf-8 -*-
"""守住主视图模型不再长回一个巨型文件。

背景：MainViewModel 原来是 2774 行、207 个成员挤在一个文件里。加一个功能
要在这堆东西里翻，改 A 的时候不小心碰到 B 是常态。v1.4.5 后按职责拆成
7 个 partial 文件，这个脚本负责「别又长回去」。

检查项：
  1. 7 个 partial 文件都在（少一个就说明有人合并回去了）
  2. 单文件不超过上限（默认 700 行）
  3. 主文件只放字段 / 构造 / 初始化，不放具体业务（有黑名单）
  4. 每个 partial 文件都带 XML 说明，讲清自己负责什么
  5. 类声明必须是 partial（否则根本编译不过，但顺手一起验）

用法：
    python build/verify/verify_code_health.py
    python build/verify/verify_code_health.py --max 800     # 临时放宽
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
VM_DIR = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels")

MAX_LINES = 700
CORE_MAX_LINES = 460

# 名字 → 该文件应该在的职责（只用于报错提示）
EXPECTED = {
    "MainViewModel.cs": "核心：字段 / 构造 / 初始化 / 集合属性",
    "MainViewModel.Browsing.cs": "浏览：导航、文件夹树、视图切换、搜索",
    "MainViewModel.Media.cs": "媒体：列表加载、缩略图、详情",
    "MainViewModel.Discovery.cs": "发现页：随机批次与筛选",
    "MainViewModel.Scanning.cs": "扫描：根目录、扫描、文件监视",
    "MainViewModel.Delete.cs": "删除：确认、回收站、批量删除、撤销",
    "MainViewModel.Organize.cs": "整理：重命名导出、标签分类、排序",
}

# 这些方法属于别的职责，出现在主文件里就说明主文件又开始膨胀了
CORE_BLACKLIST = [
    ("LoadDiscoveryAsync", "发现页"),
    ("GenerateDiscoveryBatchAsync", "发现页"),
    ("LoadNavigationAsync", "浏览"),
    ("BuildFolderTree", "浏览"),
    ("ScanFolderAsync", "扫描"),
    ("OnFileSystemChanged", "扫描"),
    ("DeleteOnTapAsync", "删除"),
    ("UndoDeleteAsync", "删除"),
    ("BatchExportAsync", "整理"),
    ("BatchRenameAsync", "整理"),
    ("LoadDetailCoreAsync", "媒体"),
]

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))


def read(path):
    return io.open(path, encoding="utf-8").read()


def count_lines(text):
    return len(text.split("\n"))


def main():
    if "--max" in sys.argv:
        global MAX_LINES
        MAX_LINES = int(sys.argv[sys.argv.index("--max") + 1])

    check("视图模型目录存在", os.path.isdir(VM_DIR), VM_DIR)
    if not os.path.isdir(VM_DIR):
        report()
        return 1

    missing = [n for n in EXPECTED if not os.path.exists(os.path.join(VM_DIR, n))]
    check("7 个 partial 文件齐全（没有被合并回一个巨型文件）", not missing,
          "缺：" + "、".join(missing) if missing else "7/7")

    stray = []
    for f in sorted(os.listdir(VM_DIR)):
        if re.match(r"^MainViewModel(\.[A-Za-z]+)?\.cs$", f) and f not in EXPECTED:
            stray.append(f)
    check("没有多余/命名不规范的 MainViewModel 分片", not stray,
          "多余：" + "、".join(stray) if stray else "无")

    total = 0
    for name, duty in EXPECTED.items():
        path = os.path.join(VM_DIR, name)
        if not os.path.exists(path):
            continue
        text = read(path)
        n = count_lines(text)
        total += n
        limit = CORE_MAX_LINES if name == "MainViewModel.cs" else MAX_LINES
        check("%s 行数 %d 未超上限 %d" % (name, n, limit), n <= limit,
              "超了 %d 行，该再拆一次" % (n - limit) if n > limit else "%d 行（%s）" % (n, duty))

        check("%s 类声明为 partial" % name,
              re.search(r"public\s+partial\s+class\s+MainViewModel", text) is not None)

        if name != "MainViewModel.cs":
            has_doc = "主窗口视图模型 ——" in text
            check("%s 开头写了职责说明" % name, has_doc,
                  "缺 XML 注释，后来的人看不出这文件管什么" if not has_doc else "有")

    core_path = os.path.join(VM_DIR, "MainViewModel.cs")
    if os.path.exists(core_path):
        core = read(core_path)
        hits = []
        for method, duty in CORE_BLACKLIST:
            # 只看「定义」，不看「调用」：InitializeAsync 里 await LoadNavigationAsync() 是正常的，
            # 真的长回来了是 `private async Task LoadNavigationAsync(` 这种定义行。
            pattern = (r"^\s{4}(?:public|private|internal|protected|static)[^\n]*\b"
                       + re.escape(method) + r"\s*\(")
            for line in core.split("\n"):
                if re.search(pattern, line) and "=>" not in line:
                    hits.append("%s（本该在%s）" % (method, duty))
                    break
        check("主文件没有混进具体业务方法", not hits,
              "不该出现定义：" + "、".join(hits) if hits else "只放状态与初始化")

    check("拆分后总行数没有暴涨（< 3300，说明没顺手抄一份）", total < 3300,
          "合计 %d 行" % total)

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
