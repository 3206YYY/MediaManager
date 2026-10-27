# -*- coding: utf-8 -*-
"""把 docs/promo/01-cover.png 右下角页脚的版本号重刷一遍。

为什么要单独写脚本：这张封面是位图，版本号和格式数量都烧进像素里了，
每发一版都得重刷。原始设计稿没留在仓库里，所以这里就地改：先把旧文字
所在的水平条按「列」用上方的背景色填掉（背景是渐变，逐列取色才能不留痕），
再用微软雅黑按原宽度 742px / 字高 37px 右对齐重写同一行。

版本号和扩展名数量都从代码里现读，发新版不用改这个脚本：
  - 版本号  ← src/MediaManager.App/MediaManager.App.csproj 的 <Version>
  - 扩展名数 ← MediaFormat.cs 里那 5 个 HashSet 去重后的并集大小

用法：
    python build/patch_cover_version.py            # 改 01-cover.png 并重建 social-preview
    python build/patch_cover_version.py --dry-run  # 只写到 build/_cover_preview.png 看效果
"""
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
COVER = os.path.join(ROOT, "docs", "promo", "01-cover.png")
PREVIEW = os.path.join(HERE, "_cover_preview.png")
CSPROJ = os.path.join(ROOT, "src", "MediaManager.App", "MediaManager.App.csproj")
MEDIA_FORMAT = os.path.join(ROOT, "src", "MediaManager.Infrastructure", "FileSystem", "MediaFormat.cs")
FONT_PATH = r"C:\Windows\Fonts\msyh.ttc"

TAIL = " 种格式 · .NET 8 / WPF · 开源免费"

# 旧文字的包围盒（在 3200×1800 原图上量出来的），重刷时以它为基准
TEXT_BOX = (2270, 1654, 3013, 1690)   # left, top, right, bottom
RIGHT_EDGE = 3013                     # 右对齐基准
CENTER_Y = (1654 + 1690) // 2         # 1672，墨迹垂直中心
FONT_SIZE = 37                        # size=37 时宽度 742px，与原文字 743px 对齐
TEXT_COLOR = (130, 138, 150)          # 采自原文字亮部 (123,132,143)，略提亮以免糊
BG_SAMPLE_Y = 1642                    # 从文字上方 12px 取该列背景色（避开抗锯齿余晖）

# 不参与计数的集合：SystemJunkNames 是「永远不该收录」的黑名单
SKIP_SETS = {"SystemJunkNames"}


def read_version():
    s = open(CSPROJ, encoding="utf-8").read()
    m = re.search(r"<Version>\s*([\d.]+)\s*</Version>", s)
    if not m:
        sys.exit("csproj 里没找到 <Version>")
    return m.group(1)


def read_extension_count():
    """数 MediaFormat.cs 里所有受支持扩展名（5 个 HashSet 的并集）。"""
    s = open(MEDIA_FORMAT, encoding="utf-8").read()
    exts = set()
    for name, body in re.findall(
        r"HashSet<string>\s+(\w+)\s*=\s*new\(StringComparer\.OrdinalIgnoreCase\)\s*\{(.*?)\};",
        s, re.S,
    ):
        if name in SKIP_SETS:
            continue
        exts.update(re.findall(r'"(\.[a-z0-9]+)"', body, re.I))
    return len(exts)


def clear_band(im):
    """按列用上方背景色填掉整条文字带，保留背景的水平渐变。"""
    px = im.load()
    left, top, right, bottom = TEXT_BOX
    x0, x1 = left - 8, right + 8
    y0, y1 = top - 6, bottom + 10
    x1 = min(x1, im.size[0] - 1)
    for x in range(x0, x1 + 1):
        bg = px[x, BG_SAMPLE_Y]
        for y in range(y0, y1 + 1):
            px[x, y] = bg


def main():
    dry = "--dry-run" in sys.argv
    if not os.path.exists(COVER):
        sys.exit(
            f"找不到 {COVER}\n"
            "  docs/promo/ 被 .gitignore 排除（宣传物料不进仓库），这张原图只在出图的机器上。\n"
            "  换机器要重刷封面时，先把 3200×1800 的原图放回 docs/promo/01-cover.png。"
        )
    new_text = f"v{read_version()} · {read_extension_count()}{TAIL}"

    im = Image.open(COVER).convert("RGB")
    clear_band(im)

    f = ImageFont.truetype(FONT_PATH, FONT_SIZE)
    left, top, right, bottom = f.getbbox(new_text)
    ink_w, ink_h = right - left, bottom - top
    x = RIGHT_EDGE - ink_w
    y = CENTER_Y - ink_h // 2 - top
    ImageDraw.Draw(im).text((x, y), new_text, font=f, fill=TEXT_COLOR)

    out = PREVIEW if dry else COVER
    im.save(out, format="PNG", optimize=True)
    print(f"{'预览' if dry else '已写回'} {out}  {os.path.getsize(out) / 1048576:.2f} MB")
    print(f"  文字: {new_text}")
    print(f"  墨迹 {ink_w}×{ink_h}px，右对齐 x={RIGHT_EDGE}，墨迹中心 y={CENTER_Y}")
    if dry:
        print("  确认无误后去掉 --dry-run 真正替换")


if __name__ == "__main__":
    main()
