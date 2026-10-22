# -*- coding: utf-8 -*-
"""发布前隐私体检：确认发布目录里没有本机数据 / 个人路径 / 调试残留。

用法：
    python build/check_privacy.py dist/MediaManager-win-x64

检查两类问题：
1. 不该出现的文件（数据库、日志、缩略图缓存、便携标记、插件目录…）；
2. 文件内容里出现本机用户名 / 本机绝对路径 / 测试素材路径。
"""
import io
import os
import re
import sys

# 本机特征串（UTF-8 与 UTF-16LE 都会搜，.NET 程序集里的字符串是 UTF-16）
# ⚠ 两条经验：
#   1. 只用「带路径的完整串」，别用裸用户名 —— libvlccore.dll 里的
#      "intf-popupmenu" 就含用户名的后 5 个字母，第一次扫直接误报；
#   2. 用户名从环境变量取，别写死在仓库里（否则这个脚本本身就成了隐私泄漏点）。
_USER = os.environ.get("USERNAME") or os.environ.get("USER") or ""
NEEDLES = [
    "C:\\Users\\" + _USER,
    "C:/Users/" + _USER,
    "/c/Users/" + _USER,
    "C:\\测试".encode("utf-8"),
    "C:\\测试".encode("utf-16-le"),
    b"MediaManagerSnapshotTests",
    b"MediaManagerBackupTests",
]
NEEDLES = [n if isinstance(n, bytes) else n.encode("utf-8") for n in NEEDLES if n]

# 文件名 / 目录名黑名单（发布包里出现即为问题）
BAD_NAMES = re.compile(
    r"(\.db$|\.db-shm$|\.db-wal$|\.mmdb$|\.log$|\.bak(\.|$)|^portable\.marker$|^ui\.theme$|^editor-presets\.json$)",
    re.IGNORECASE,
)
BAD_DIRS = {"logs", "thumbnails", "plugins", "Data", "db_backup"}

# 这些是正常的：LibVLC 自带 lua 脚本与插件目录都叫 plugins
ALLOW_PREFIXES = ("libvlc", os.path.join("libvlc", "win-x64"))


def scan(root):
    problems = []
    content_hits = []
    total = 0
    total_size = 0

    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root)
        for d in list(dirnames):
            if d in BAD_DIRS and not rel_dir.lower().startswith(ALLOW_PREFIXES):
                problems.append(("目录", os.path.join(rel_dir, d)))
        for f in filenames:
            total += 1
            path = os.path.join(dirpath, f)
            rel = os.path.relpath(path, root)
            try:
                size = os.path.getsize(path)
            except OSError:
                continue
            total_size += size

            if BAD_NAMES.search(f):
                problems.append(("文件", rel))
                continue
            if size > 180 * 1024 * 1024:
                continue

            try:
                with io.open(path, "rb") as fh:
                    data = fh.read()
            except OSError:
                continue
            for needle in NEEDLES:
                if needle in data:
                    content_hits.append((rel, needle.decode("utf-8", "ignore")))
                    break

    return total, total_size, problems, content_hits


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join("dist", "MediaManager-win-x64")
    if not os.path.isdir(root):
        sys.exit("目录不存在：" + root)

    total, size, problems, hits = scan(root)
    print(f"扫描目录：{os.path.abspath(root)}")
    print(f"文件数：{total}    总大小：{size / 1048576:.1f} MB")
    print()

    if problems:
        print(f"✗ 不该包含的路径（{len(problems)} 个）：")
        for kind, rel in problems[:40]:
            print(f"   [{kind}] {rel}")
    else:
        print("✓ 没有数据库 / 日志 / 缩略图缓存 / 便携标记 / 插件目录")

    if hits:
        print()
        print(f"✗ 内容里出现本机特征串（{len(hits)} 个文件）：")
        for rel, needle in hits[:40]:
            print(f"   {rel}  <- {needle}")
    else:
        print("✓ 没有发现本机用户名 / 本机路径 / 测试素材路径")

    print()
    ok = not problems and not hits
    print("结论：" + ("通过，可以打包" if ok else "不通过，先清理再打包"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
