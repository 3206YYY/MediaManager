# -*- coding: utf-8 -*-
"""生成 GitHub 仓库 Social Preview 图（1280×640，<1 MB）。

GitHub 对这个图有硬限制：**小于 1 MB**、建议 **1280×640**（2:1）。
docs/promo/01-cover.png 是 3200×1800 / 1.2 MB，直接传会被拒，而且 16:9 的图
GitHub 会自己再上下裁一刀 —— 裁在谁手里都不如裁在自己手里，所以这里直接
按 2:1 裁好再缩。
"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "docs", "promo", "01-cover.png")
OUT_DIR = os.path.join(ROOT, "docs")

TARGET = (1280, 640)   # GitHub Social Preview 推荐尺寸（2:1）
LIMIT = 1024 * 1024    # GitHub 上限 1 MB


def main():
    im = Image.open(SRC).convert("RGB")
    # 等比铺满再裁切，避免变形
    sw, sh = im.size
    scale = max(TARGET[0] / sw, TARGET[1] / sh)
    im = im.resize((round(sw * scale), round(sh * scale)), Image.LANCZOS)
    w, h = im.size
    left = (w - TARGET[0]) // 2
    # 垂直方向故意偏下裁：封面页脚（版本号那一行）离底边只有 52/900，
    # 居中裁会把那行压到贴着边缘，所以上面多切一点、下面少切一点。
    top = (h - TARGET[1]) * 0.7
    im = im.crop((left, int(top), left + TARGET[0], int(top) + TARGET[1]))

    png = os.path.join(OUT_DIR, "social-preview.png")
    im.save(png, format="PNG", optimize=True, compress_level=9)
    size = os.path.getsize(png)
    print(f"PNG  {png}  {size / 1024:.0f} KB")

    if size >= LIMIT:
        jpg = os.path.join(OUT_DIR, "social-preview.jpg")
        im.save(jpg, format="JPEG", quality=88, optimize=True, progressive=True)
        jsize = os.path.getsize(jpg)
        print(f"PNG 超过 1 MB，改用 JPG {jpg}  {jsize / 1024:.0f} KB")
        os.remove(png)
        png, size = jpg, jsize

    print("可用于 GitHub Social Preview：", png, f"({size / 1024:.0f} KB < 1024 KB)")


if __name__ == "__main__":
    main()
