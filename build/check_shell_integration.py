# -*- coding: utf-8 -*-
"""右键集成的「安全红线」静态检查。

资源管理器右键集成是这个项目里唯一碰注册表的地方，一旦写错就是
「把用户的默认看图软件偷偷改掉」这种级别的锅。这里不开机跑，只读源码做断言：

1. 只写 HKEY_CURRENT_USER（用户级）—— 出现 LocalMachine / ClassesRoot 直接判死
2. 绝不碰 shell\\open（那是默认关联）和 UserChoice（Win10+ 的默认程序选择）
3. 每个 CreateSubKey 的路径都必须落在允许的前缀里（加新键要来这里登记）
4. 安装包的卸载脚本里必须真的调用了清理过程，且顺手清掉开关标记值

用法：python build/check_shell_integration.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVICE = os.path.join(ROOT, "src", "MediaManager.App", "Services", "ShellIntegrationService.cs")
INSTALLER = os.path.join(ROOT, "build", "installer.iss")

# 允许写入的注册表前缀（都在 HKCU 下）。加新键必须同时改这里。
ALLOWED_PREFIXES = (
    "Software\\Classes\\SystemFileAssociations\\",   # 每个媒体扩展名一个 verb
    "Software\\MediaManager",                       # 开关标记 + 已注册扩展名清单
)

# 出现即违规（不区分大小写的子串）
FORBIDDEN = (
    ("Registry.LocalMachine", "只能写当前用户，不能写 HKLM"),
    ("HKEY_LOCAL_MACHINE", "只能写当前用户，不能写 HKLM"),
    ("Registry.ClassesRoot", "不能直接写 HKCR"),
    ("UserChoice", "不能改默认打开程序（UserChoice 由系统维护）"),
    ("shell\\open", "不能碰 shell\\open（那是默认关联）"),
    ("shell\\\\open", "不能碰 shell\\\\open（C# 转义写法也算）"),
)

problems = []


def strip_cs_comments(text):
    """去掉 C# 注释：注释里写 `shell\\open`、`UserChoice` 是正常的说明，不该算违规。"""
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    return text


def check_service():
    if not os.path.exists(SERVICE):
        problems.append(f"找不到 {SERVICE}")
        return

    raw_src = open(SERVICE, encoding="utf-8").read()
    src = strip_cs_comments(raw_src)          # 只看真正会执行的代码
    low = src.lower()
    name = os.path.basename(SERVICE)

    for needle, why in FORBIDDEN:
        if needle.lower() in low:
            problems.append(f"{name} 的代码里出现 `{needle}` —— {why}")

    # 所有写注册表的地方必须是 CurrentUser
    for m in re.finditer(r"Registry\.(\w+)\.(CreateSubKey|OpenSubKey|DeleteSubKeyTree)", src):
        if m.group(1) != "CurrentUser":
            line = src[:m.start()].count("\n") + 1
            problems.append(f"{name}:{line} 用了 Registry.{m.group(1)} —— 只允许 CurrentUser")

    # CreateSubKey / OpenSubKey 的路径字面量必须落在允许前缀里
    for m in re.finditer(r'(?:Create|Open)SubKey\(\s*\$?@?"((?:[^"\\]|\\.)*)"', src):
        raw = m.group(1).replace("\\\\", "\\")
        line = src[:m.start()].count("\n") + 1
        # 不含反斜杠的短名是相对子键（例如在 MediaManagerOpen 下建 command），父键已校验过
        if "\\" not in raw and "{" not in raw:
            continue
        if not any(raw.startswith(p) for p in ALLOWED_PREFIXES):
            problems.append(
                f"{name}:{line} 写入了未登记的键 `{raw}` —— 确认安全后把前缀加进 ALLOWED_PREFIXES")

    # 注释里必须留一句「不改默认关联」，让后来者知道这是刻意的
    if "默认关联" not in raw_src:
        problems.append(f"{name} 缺少「不改默认关联」的说明注释")


def check_installer():
    if not os.path.exists(INSTALLER):
        problems.append(f"找不到 {INSTALLER}")
        return

    src = open(INSTALLER, encoding="utf-8").read()

    if "DeleteShellIntegrationKeys" not in src:
        problems.append("installer.iss 缺少 DeleteShellIntegrationKeys 清理过程")
    elif not re.search(r"CurUninstallStepChanged[\s\S]*?DeleteShellIntegrationKeys\(\)", src):
        problems.append("installer.iss 定义了清理过程，但卸载流程里没调用")

    # 删的是「值」不是「键」：Inno 里没有 RegDeleteKeyValue 这个函数（上一版写成它，
    # [Code] 段根本编不过，等于卸载清理从没真正跑过）。正确函数是 RegDeleteValue。
    if "RegDeleteValue(HKCU, 'Software\\MediaManager', 'ShellIntegration')" not in src:
        problems.append("installer.iss 卸载时没清掉 ShellIntegration 标记值（会留空壳键）")

    # 卸载要么按记录清单清（ShellExtensions），要么硬编码扩展名，二者至少有一个
    if "ShellExtensions" not in src and "SystemFileAssociations" not in src:
        problems.append("installer.iss 完全没提右键集成，卸载后菜单项会残留")


def main():
    check_service()
    check_installer()

    if problems:
        print("✗ 右键集成安全检查未通过：")
        for p in problems:
            print("  -", p)
        return 1

    print("✓ 右键集成安全：只写 HKCU、不碰默认关联、卸载会清干净")
    print("   写入位置：HKCU\\Software\\Classes\\SystemFileAssociations\\<ext>\\shell\\MediaManagerOpen")
    print("   开关标记：HKCU\\Software\\MediaManager（ShellIntegration + ShellExtensions 清单）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
