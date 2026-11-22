# -*- coding: utf-8 -*-
"""排序入口守卫（纯静态）。

数据层的 SortField 6 个值、MediaRepository 的 SQL 映射早就齐了，缺的只是界面与 VM 接线。
这个脚本守的是「接上之后别再退回去」：

   1 下拉框选项必须覆盖枚举全部值（漏一个 = 那个排序在界面里消失）
   2 BuildFilter 不许再写死 DateAdded / Descending（否则用户选了也不生效）
   3 偏好必须读回来（Enum.TryParse）+ 写下去（SaveSettingAsync）
   4 发现页（随机批次）与最近查看（内存 Top100）必须禁用并给出原因
   5 抬头控件三件套齐全：选项源、选中值、方向切换命令
   6 方向文案升降序都在（只有一个 = 按钮永远显示同一个方向）

用法：
    python build/verify/verify_sort.py
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

ENUM_FILE = os.path.join(ROOT, "src", "MediaManager.Core", "Enums", "SortField.cs")
VM_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.cs")
DISC_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.Discovery.cs")
MEDIA_FILE = os.path.join(ROOT, "src", "MediaManager.App", "ViewModels", "MainViewModel.Media.cs")
XAML_FILE = os.path.join(ROOT, "src", "MediaManager.App", "Views", "MainWindow.xaml")

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


def enum_values(src, enum_name):
    """取出 `enum Xxx { A, B, C, }` 里的成员名。"""
    m = re.search(r"enum\s+%s\s*\{(.*?)\}" % enum_name, src, re.S)
    if not m:
        return []
    body = re.sub(r"//[^\n]*", "", m.group(1))
    return [v.strip() for v in body.split(",") if v.strip()]


def main():
    enum_src = read(ENUM_FILE)
    vm_src = read(VM_FILE)
    disc_src = read(DISC_FILE)
    media_src = read(MEDIA_FILE)
    xaml_src = re.sub(r"<!--.*?-->", "", read(XAML_FILE), flags=re.S)

    fields = enum_values(enum_src, "SortField")
    dirs = enum_values(enum_src, "SortDirection")

    # 1 下拉框覆盖枚举全部值
    missing = [f for f in fields if not re.search(r"SortField\.%s\b" % f, vm_src)]
    check("SortOptions 覆盖 SortField 全部 %d 个值" % len(fields),
          len(fields) == 6 and not missing,
          ("枚举 %d 个；漏：" % len(fields) + "、".join(missing)) if missing else "6/6")

    # 2 BuildFilter 不再写死
    hard = [w for w in ("SortField.DateAdded", "SortDirection.Descending") if w in disc_src]
    check("BuildFilter 不再写死默认排序", not hard,
          ("仍写死：" + "、".join(hard)) if hard else "读 this.SortField / this.SortDirection")

    # 3 偏好读回来 + 写下去
    save_ok = all(("SaveSettingAsync(%s" % k) in vm_src
                  for k in ("SortFieldSettingKey", "SortDirectionSettingKey"))
    load_ok = ("Enum.TryParse<SortField>" in vm_src
               and "Enum.TryParse<SortDir>" in vm_src)
    check("排序偏好可持久化（AppSetting 读写都有）", save_ok and load_ok,
          "写 %s / 读 %s" % ("有" if save_ok else "缺", "有" if load_ok else "缺"))

    # 4 两个特殊视图禁用 + 说明原因
    disc_block = media_src[media_src.find('"discovery"'):media_src.find('"recent-view"')]
    recent_block = media_src[media_src.find('"recent-view"'):]
    disc_ok = "IsSortEnabled = false" in disc_block and "SortDisabledReason" in disc_block
    recent_ok = "IsSortEnabled = false" in recent_block[:900] and "SortDisabledReason" in recent_block[:900]
    check("发现页禁用排序并说明原因", disc_ok)
    check("最近查看禁用排序并说明原因", recent_ok)

    # 5 抬头控件三件套
    ui_ok = all(k in xaml_src for k in (
        'ItemsSource="{Binding SortOptions}"',
        'SelectedValuePath="Field"',
        'Command="{Binding ToggleSortDirectionCommand}"',
        'IsEnabled="{Binding IsSortEnabled}"',
    ))
    check("抬头控件：选项源 / 选中值 / 方向命令 / 禁用联动", ui_ok)

    # 6 升降序文案都在
    text_ok = '↑ 升序' in vm_src and '↓ 降序' in vm_src and len(dirs) == 2
    check("方向文案含升序与降序", text_ok, "枚举 %d 个" % len(dirs))

    print("\n结果：%d/%d 通过" % (_total - _failed, _total))
    return 1 if _failed else 0


if __name__ == "__main__":
    sys.exit(main())
