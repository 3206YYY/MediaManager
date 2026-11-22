# -*- coding: utf-8 -*-
"""生成 MediaManager 程序图标（multi-size .ico + 一套纯净 PNG）。

用法：
    python build/gen_icon.py

产物：
    src/MediaManager.App/MediaManager.ico      —— 程序内嵌图标（多尺寸）
    docs/icon/icon_1024.png ... icon_16.png    —— 透明背景纯净 PNG（圆角外全透明）
    docs/icon/icon_1024_on_white.png           —— 白底版（部分商店/文档要求白底）
    docs/icon/icon_1024_flat.png               —— 铺满方形版（不圆角裁切，供系统做圆角时用）

图标说明：蓝紫渐变圆角方块（#4F6BFF → #7B5CFF）+ 白色太阳 + 白色山峰 + 底部视频格。
注意：图标本身不带版本号，任何地方都不要往上加字。
"""
import os

from PIL import Image, ImageDraw

SIZE = 1024  # 以 1024 为基准，缩放出各尺寸

# 主题色：科技蓝紫 #4F6BFF -> #7B5CFF
TOP = (79, 107, 255)
BOTTOM = (123, 92, 255)
RADIUS = 220


def _gradient_square(size: int) -> Image.Image:
    """竖直线性渐变铺满整个方形（不裁圆角）。"""
    bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    bgd = ImageDraw.Draw(bg)
    for y in range(size):
        t = y / size
        r = int(TOP[0] + (BOTTOM[0] - TOP[0]) * t)
        g = int(TOP[1] + (BOTTOM[1] - TOP[1]) * t)
        b = int(TOP[2] + (BOTTOM[2] - TOP[2]) * t)
        bgd.line([(0, y), (size, y)], fill=(r, g, b, 255))
    return bg


def build_icon(size: int = SIZE, rounded: bool = True) -> Image.Image:
    """绘制 1024 基准的图标；rounded=False 时铺满方形（透明处仅为外部留白）。"""
    size = SIZE

    bg = _gradient_square(size)
    if rounded:
        mask = Image.new("L", (size, size), 0)
        ImageDraw.Draw(mask).rounded_rectangle(
            [0, 0, size - 1, size - 1], radius=RADIUS, fill=255
        )
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        canvas.paste(bg, (0, 0), mask)
    else:
        canvas = bg

    draw = ImageDraw.Draw(canvas)

    # 太阳/圆点（左上角，代表媒体播放）
    sun = (0.28, 0.30)
    sr = 90
    draw.ellipse(
        [size * sun[0] - sr, size * sun[1] - sr, size * sun[0] + sr, size * sun[1] + sr],
        fill=(255, 255, 255, 235),
    )

    # 山形1（后山，较浅）
    draw.polygon(
        [
            (size * 0.12, size * 0.82),
            (size * 0.40, size * 0.40),
            (size * 0.62, size * 0.70),
            (size * 0.88, size * 0.30),
            (size * 0.92, size * 0.82),
        ],
        fill=(255, 255, 255, 120),
    )

    # 山形2（前山，较深/白）
    draw.polygon(
        [
            (size * 0.05, size * 0.82),
            (size * 0.30, size * 0.52),
            (size * 0.52, size * 0.78),
            (size * 0.72, size * 0.60),
            (size * 0.90, size * 0.82),
        ],
        fill=(255, 255, 255, 210),
    )

    # 底部：胶卷孔（代表视频/媒体）
    film_y = size * 0.88
    for i in range(5):
        cx = size * (0.22 + i * 0.14)
        draw.rounded_rectangle(
            [cx - 22, film_y - 22, cx + 22, film_y + 22], radius=10, fill=(79, 107, 255, 255)
        )

    if size != SIZE:
        canvas = canvas.resize((size, size), Image.LANCZOS)
    return canvas


def main() -> None:
    HERE = os.path.dirname(os.path.abspath(__file__))
    ROOT = os.path.dirname(HERE)

    base = build_icon()

    # 1) 多尺寸 ico（程序内嵌图标，原有产物不变）
    ico_sizes = [256, 128, 64, 48, 32, 16]
    imgs = [base.resize((s, s), Image.LANCZOS) for s in ico_sizes]
    ico_out = os.path.join(ROOT, "src", "MediaManager.App", "MediaManager.ico")
    imgs[0].save(
        ico_out, format="ICO", sizes=[(s, s) for s in ico_sizes], append_images=imgs[1:]
    )
    print("图标已生成:", ico_out)

    # 2) 纯净 PNG 导出（透明背景，圆角外全透明）
    png_dir = os.path.join(ROOT, "docs", "icon")
    os.makedirs(png_dir, exist_ok=True)
    for s in [1024, 512, 256, 128, 64, 48, 32, 16]:
        img = base if s == 1024 else base.resize((s, s), Image.LANCZOS)
        p = os.path.join(png_dir, f"icon_{s}.png")
        img.save(p, format="PNG")
        print("  PNG:", p, f"({s}x{s})")

    # 3) 白底版（部分商店/文档要求白底）
    white = Image.new("RGBA", (1024, 1024), (255, 255, 255, 255))
    white.alpha_composite(base)
    p = os.path.join(png_dir, "icon_1024_on_white.png")
    white.save(p, format="PNG")
    print("  PNG:", p, "(1024x1024 白底)")

    # 4) 铺满方形版（不裁圆角，交给系统自己裁）
    flat = build_icon(rounded=False)
    p = os.path.join(png_dir, "icon_1024_flat.png")
    flat.save(p, format="PNG")
    print("  PNG:", p, "(1024x1024 铺满方形)")


if __name__ == "__main__":
    main()
