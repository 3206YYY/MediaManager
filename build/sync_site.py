# -*- coding: utf-8 -*-
"""发版时把版本号 / 安装包体积同步进官网首页。

为什么需要：`docs/index.html` 里写死了三处会随版本变的东西 ——
按钮上的 `v1.3.1`、两处 `101 MB`、还有页脚更新日志的链接（带版本号的文件名）。
手改容易漏一处，漏了官网就会挂着旧版本号卖新包。

单一真源：`src/MediaManager.App/MediaManager.App.csproj` 的 `<Version>`。
安装包 `build/installer.iss` 里的 `#define MyAppVersion` 只做一致性校验，不参与取值。

用法：
    python build/sync_site.py            # 同步并打印改动
    python build/sync_site.py --check    # 只检查，不写文件（发版前跑这个）
"""
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSPROJ = os.path.join(ROOT, "src", "MediaManager.App", "MediaManager.App.csproj")
ISS = os.path.join(ROOT, "build", "installer.iss")
SITE = os.path.join(ROOT, "docs", "index.html")
DIST = os.path.join(ROOT, "dist")

GITEE_LIMIT_MIB = 100.0   # Gitee 单个发行版附件上限


def read_version():
    src = open(CSPROJ, encoding="utf-8").read()
    m = re.search(r"<Version>\s*([\d.]+)\s*</Version>", src)
    if not m:
        sys.exit("csproj 里找不到 <Version>")
    return m.group(1).strip()


def read_iss_version():
    if not os.path.exists(ISS):
        return None
    m = re.search(r'#define\s+MyAppVersion\s+"([\d.]+)"', open(ISS, encoding="utf-8").read())
    return m.group(1) if m else None


def installer_size_mib(version):
    exe = os.path.join(DIST, f"MediaManager-Setup-{version}.exe")
    if not os.path.exists(exe):
        return None, None
    n = os.path.getsize(exe)
    return n, n / 1048576


def sync(text, version, size_mb):
    """返回 (新文本, 改动列表)。只替换有明确语义的位置，不做全局通配。"""
    changes = []
    subs = [
        (r"(下载\s*)v\d+\.\d+\.\d+", rf"\g<1>v{version}"),
        (r"(<h2>下载\s*)v\d+\.\d+\.\d+(</h2>)", rf"\g<1>v{version}\g<2>"),
        (r"(\d+)\s*MB(\s*安装包)", (rf"{size_mb} MB\g<2>" if size_mb else None)),
        (r"(GitHub 下载（)\d+\s*MB(）)", (rf"\g<1>{size_mb} MB\g<2>" if size_mb else None)),
        (r"(docs/changelog/)v\d+\.\d+\.\d+(\.md)", rf"\g<1>v{version}\g<2>"),
    ]
    for pat, rep in subs:
        if rep is None:
            continue
        new, n = re.subn(pat, rep, text)
        if n and new != text:
            changes.append(f"  {pat}  ×{n}")
        text = new
    return text, changes


def main():
    check_only = "--check" in sys.argv
    version = read_version()
    iss = read_iss_version()
    raw, mib = installer_size_mib(version)

    print(f"版本（csproj）：{version}")
    print(f"版本（installer.iss）：{iss}")
    if iss and iss != version:
        print(f"  ⚠ 两处版本不一致，发版前先统一（installer.iss 改成 {version}）")
    if raw is None:
        print(f"  安装包 dist/MediaManager-Setup-{version}.exe 不存在 —— 体积那两处跳过")
        size_mb = None
    else:
        # 显示值跟 GitHub 的资产列表对齐（它按 MiB 向上取整显示），否则用户会以为下错了包
        size_mb = math.ceil(mib)
        print(f"安装包：{raw:,} 字节 = {mib:.1f} MiB  →  官网写 {size_mb} MB")
        if mib > GITEE_LIMIT_MIB:
            print(f"  ⚠ 超过 Gitee 附件上限 {GITEE_LIMIT_MIB:.0f} MiB —— 国内镜像传不上去")
        else:
            print(f"  ✓ 在 Gitee 附件上限（{GITEE_LIMIT_MIB:.0f} MiB）之内")

    old = open(SITE, encoding="utf-8").read()
    new, changes = sync(old, version, size_mb)
    if not changes:
        print("官网：已是最新，无需改动")
        return
    print("官网改动：")
    print("\n".join(changes))
    if check_only:
        print("（--check：不写文件）")
        return
    with open(SITE, "w", encoding="utf-8", newline="\n") as f:
        f.write(new)
    print(f"已写回 {os.path.relpath(SITE, ROOT)}")


if __name__ == "__main__":
    main()
