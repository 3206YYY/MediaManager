# -*- coding: utf-8 -*-
"""生成 MediaManager 程序图标 (multi-size .ico)"""
from PIL import Image, ImageDraw

SIZE = 1024  # 以 1024 为基准，缩放出各尺寸

# 背景：圆角方形，蓝紫渐变
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# 圆角方形背景（渐变用多层近似）
# 主题色：科技蓝紫 #4F6BFF -> #7B5CFF
top = (79, 107, 255)
bottom = (123, 92, 255)
radius = 220
for y in range(SIZE):
    t = y / SIZE
    r = int(top[0] + (bottom[0] - top[0]) * t)
    g = int(top[1] + (bottom[1] - top[1]) * t)
    b = int(top[2] + (bottom[2] - top[2]) * t)
    draw.line([(0, y), (SIZE, y)], fill=(r, g, b, 255))

# 用圆角遮罩裁出圆角方形
mask = Image.new("L", (SIZE, SIZE), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=radius, fill=255)
bg = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
# 重新在透明层画圆角方形背景
bgd = ImageDraw.Draw(bg)
# 逐行渐变圆角
for y in range(SIZE):
    t = y / SIZE
    r = int(top[0] + (bottom[0] - top[0]) * t)
    g = int(top[1] + (bottom[1] - top[1]) * t)
    b = int(top[2] + (bottom[2] - top[2]) * t)
    bgd.line([(0, y), (SIZE, y)], fill=(r, g, b, 255))
# 应用圆角
rounded = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
rounded.paste(bg, (0, 0), mask)

draw = ImageDraw.Draw(rounded)

# 图形：图片山形（两个叠加的"山"代表图片）+ 底部胶卷/条纹
# 太阳/圆点（左上角，代表媒体播放）
sun = (0.28, 0.30)
sr = 90
draw.ellipse([SIZE*sun[0]-sr, SIZE*sun[1]-sr, SIZE*sun[0]+sr, SIZE*sun[1]+sr],
             fill=(255, 255, 255, 235))

# 山形1（后山，较浅）
draw.polygon([
    (SIZE*0.12, SIZE*0.82),
    (SIZE*0.40, SIZE*0.40),
    (SIZE*0.62, SIZE*0.70),
    (SIZE*0.88, SIZE*0.30),
    (SIZE*0.92, SIZE*0.82),
], fill=(255, 255, 255, 120))

# 山形2（前山，较深/白）
draw.polygon([
    (SIZE*0.05, SIZE*0.82),
    (SIZE*0.30, SIZE*0.52),
    (SIZE*0.52, SIZE*0.78),
    (SIZE*0.72, SIZE*0.60),
    (SIZE*0.90, SIZE*0.82),
], fill=(255, 255, 255, 210))

# 底部：胶卷孔（代表视频/媒体）
film_y = SIZE * 0.88
for i in range(5):
    cx = SIZE * (0.22 + i * 0.14)
    draw.rounded_rectangle([cx-22, film_y-22, cx+22, film_y+22], radius=10,
                           fill=(79, 107, 255, 255))

# 缩放到多尺寸生成 ico
sizes = [256, 128, 64, 48, 32, 16]
imgs = [rounded.resize((s, s), Image.LANCZOS) for s in sizes]
out = "C:/Users/upmen/WorkBuddy/图片视频管理工具/src/MediaManager.App/MediaManager.ico"
imgs[0].save(out, format="ICO", sizes=[(s, s) for s in sizes],
             append_images=imgs[1:])
print("图标已生成:", out)
