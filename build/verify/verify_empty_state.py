# -*- coding: utf-8 -*-
"""中央空状态 + 详情三态守卫（纯静态）。

背景：列表 0 项时以前只有状态栏一句「共 0 项」，中央是一片空白；详情面板在
「没选 / 选一张 / 选多张」三种情况下长得一样。这次补上之后，最容易回退的点是：
  · 空状态文案自己另写一份（和 ExplainEmptyResultAsync 的诊断漂移）
  · 离线 / 不可达 / 筛选残留这几类分支被删掉
  · 详情面板三态少绑一个，或者多选态又偷偷加一套删除按钮（应该复用 SelectionBar）

检查项：
  1 中央空状态覆盖层存在且绑 IsCenterEmptyVisible / CenterEmptyTitle / CenterEmptyText
  2 空状态说明直接取 StatusText（同一句诊断，不各写一份）
  3 离线、缺失、筛选三类诊断文案还在（verify_offline_folder 依赖这些说法）
  4 加载失败有兜底（catch 里写「读取媒体库失败」+ StorageErrors.WithAction）
  5 详情三态：VM 三个属性 + XAML 三处绑定齐全
  6 多选态复用底部 SelectionBar：面板里只复用 OnClearSelection，不再复制删除入口
  7 首屏加载遮罩绑 IsCenterLoadingVisible

用法：
    python build/verify/verify_empty_state.py
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
XAML_FILE = os.path.join(ROOT, "src", "MediaManager.App", "Views", "MainWindow.xaml")
VM_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.cs")
MEDIA_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.Media.cs")

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
    xaml = re.sub(r"<!--.*?-->", "", raw, flags=re.S)
    vm = io.open(VM_FILE, encoding="utf-8").read()
    media = io.open(MEDIA_FILE, encoding="utf-8").read()

    # 1 中央空状态覆盖层
    missing = [k for k in ("IsCenterEmptyVisible", "CenterEmptyTitle", "CenterEmptyText")
               if k not in xaml]
    check("中央空状态覆盖层存在（标题 / 说明 / 可见性都绑上）", not missing,
          ("缺绑定：" + "、".join(missing)) if missing else "3 处绑定齐全")

    # 2 文案同源
    same = len(re.findall(r"CenterEmptyText\s*=\s*StatusText\s*;", media))
    check("中央空状态说明直接取 StatusText（不另写一份诊断）", same >= 2,
          "CenterEmptyText = StatusText 共 %d 处（folder / 普通至少各 1）" % same)

    # 3 三类诊断文案还在
    NEED = ["磁盘未连接", "暂不可达", "清除筛选后再试试"]
    gone = [k for k in NEED if k not in media]
    check("离线 / 缺失 / 筛选残留三类诊断文案还在 VM", not gone,
          ("丢了：" + "、".join(gone)) if gone else "、".join(NEED))

    # 4 加载失败兜底
    err = ("读取媒体库失败" in media
           and re.search(r"StorageErrors\.WithAction\(ex,\s*\"刷新列表\"\)", media) is not None
           and re.search(r"catch\s*\(Exception ex\)", media) is not None)
    check("加载失败有兜底：catch 里写「读取媒体库失败」+ 可操作建议", err,
          "catch / 标题 / 建议" if err else "缺 catch 或文案")

    # 5 详情三态
    vm_missing = [p for p in ("IsDetailEmpty", "IsDetailSingle", "IsDetailMultiple") if p not in vm]
    xaml_missing = [p for p in ("IsDetailEmpty", "IsDetailSingle", "IsDetailMultiple")
                    if ("{Binding %s," % p) not in xaml]
    check("详情三态：VM 属性 + XAML 绑定齐全", not vm_missing and not xaml_missing,
          "VM 缺 %s / XAML 缺 %s" % ("、".join(vm_missing) or "无",
                                    "、".join(xaml_missing) or "无"))

    # 6 多选态复用 SelectionBar：面板里不许再复制删除入口
    # 用未剥注释的原文定位区间（两个标记都在注释里），再剥掉注释看真正的按钮
    panel = re.search(r"右侧详情面板.*?多选操作条", raw, re.S)
    panel_src = re.sub(r"<!--.*?-->", "", panel.group(0), flags=re.S) if panel else ""
    has_clear = 'Click="OnClearSelection"' in panel_src
    dup_delete = "OnDeleteSelected" in panel_src
    check("多选态复用底部 SelectionBar（只复用「取消选择」，不复制删除入口）",
          has_clear and not dup_delete,
          "取消选择%s / 重复删除入口%s" % ("在" if has_clear else "丢了",
                                          "有" if dup_delete else "无"))

    # 7 首屏加载遮罩
    load_ok = "IsCenterLoadingVisible" in vm and "{Binding IsCenterLoadingVisible," in xaml
    check("首屏加载遮罩（只在列表空着时盖）", load_ok,
          "VM + XAML 都在" if load_ok else "缺定义或绑定")

    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    sys.exit(main())
