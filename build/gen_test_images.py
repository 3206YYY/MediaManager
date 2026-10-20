# -*- coding: utf-8 -*-
"""生成 20 张「扁平插画风景」风格测试图到 C:\测试。
风格参照软件内缩略图：渐变天空 + 圆月 + 层叠波浪山峦，另含四色格 / 棋盘格。"""
import os, math, random
from PIL import Image, ImageDraw, ImageFont

FONT_PATH = r"C:\Windows\Fonts\msyh.ttc"
OUT = r"C:\测试"
os.makedirs(OUT, exist_ok=True)
random.seed(7)


def font(sz):
    return ImageFont.truetype(FONT_PATH, sz)


def text(d, xy, s, size=18, color=(255, 255, 255), anchor="la"):
    d.text(xy, s, font=font(size), fill=color, anchor=anchor)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


# ---------------------------------------------------------------- 风景卡片
def landscape(w, h, sky_top, sky_mid, sky_bot, moon, layers, stars=False):
    """sky_*: 渐变三段色; moon: (色, 半径比例) 或 None; layers: [(山色, 山顶高度比例, 起伏)]"""
    im = Image.new("RGB", (w, h), sky_top)
    d = ImageDraw.Draw(im)

    # 天空三段渐变
    hz = int(h * 0.62)  # 地平线
    for y in range(hz):
        t = y / hz
        if t < 0.55:
            c = lerp(sky_top, sky_mid, t / 0.55)
        else:
            c = lerp(sky_mid, sky_bot, (t - 0.55) / 0.45)
        d.line([(0, y), (w, y)], fill=c)

    # 星星
    if stars:
        for _ in range(int(w * h / 9000)):
            x, y = random.randint(0, w - 1), random.randint(0, int(hz * 0.7))
            r = random.choice([1, 1, 2])
            d.ellipse((x - r, y - r, x + r, y + r), fill=(255, 255, 240))

    # 月亮
    if moon:
        mc, mr = moon
        mx, my = int(w * 0.76), int(h * 0.22)
        r = int(min(w, h) * mr)
        # 光晕
        glow = int(r * 1.6)
        d.ellipse((mx - glow, my - glow, mx + glow, my + glow),
                  fill=lerp(mc, sky_mid, 0.55))
        d.ellipse((mx - r, my - r, mx + r, my + r), fill=mc)

    # 层叠波浪山
    for color, top_ratio, amp_ratio in layers:
        base = int(h * top_ratio)
        amp = int(h * amp_ratio)
        phase = random.uniform(0, math.pi * 2)
        pts = [(0, h), (0, base)]
        for x in range(0, w + 1, max(2, w // 200)):
            y = base + math.sin(x / w * math.pi * random.choice([2, 3]) + phase) * amp
            pts.append((x, y))
        pts.append((w, base))
        pts.append((w, h))
        d.polygon(pts, fill=color)

    return im


def with_label(im, label, dark=True):
    """右下角小字标签（浅色，仿截图文件名水印）。"""
    d = ImageDraw.Draw(im)
    w, h = im.size
    color = (255, 255, 255) if dark else (60, 60, 60)
    text(d, (w // 2, h - 26), label, 16, color, anchor="mm")
    return im


# ---------------------------------------------------------------- 四色格
def color_grid(w, h, colors):
    im = Image.new("RGB", (w, h), (245, 245, 245))
    d = ImageDraw.Draw(im)
    pad = int(min(w, h) * 0.06)
    cw = (w - pad * 3) // 2
    ch = (h - pad * 3) // 2
    pos = [(pad, pad), (pad * 2 + cw, pad),
           (pad, pad * 2 + ch), (pad * 2 + cw, pad * 2 + ch)]
    for (x, y), c in zip(pos, colors):
        d.rounded_rectangle((x, y, x + cw, y + ch), radius=int(min(cw, ch) * 0.06), fill=c)
    return im


# ---------------------------------------------------------------- 棋盘格
def checker(w, h, c1, c2, n=6):
    im = Image.new("RGB", (w, h), c1)
    d = ImageDraw.Draw(im)
    cw, ch = w / n, h / n
    for r in range(n):
        for c in range(n):
            if (r + c) % 2 == 0:
                d.rectangle((c * cw, r * ch, (c + 1) * cw, (r + 1) * ch), fill=c2)
    # 轻微模糊感：叠一层半透明圆点
    return im


# ---------------------------------------------------------------- 20 张定义
DARK1 = (28, 42, 78)      # 深蓝山 1
DARK2 = (36, 56, 100)     # 深蓝山 2
DARK3 = (48, 72, 128)     # 深蓝山 3

def mountains3(c1=DARK1, c2=DARK2, c3=DARK3):
    return [(c1, 0.60, 0.05), (c2, 0.70, 0.045), (c3, 0.80, 0.04)]


SPECS = [
    # (类型, 文件名, w, h, 参数)
    ("land", "风景_日落橙", 1280, 800,
     dict(sky_top=(247,178,116), sky_mid=(240,140,150), sky_bot=(170,110,180),
          moon=((250,245,225), 0.07), layers=mountains3())),
    ("land", "风景_黄昏紫", 1280, 800,
     dict(sky_top=(240,150,160), sky_mid=(200,120,190), sky_bot=(120,90,170),
          moon=((255,250,235), 0.065), layers=mountains3())),
    ("land", "风景_夜晚蓝", 1600, 900,
     dict(sky_top=(30,40,80), sky_mid=(60,70,130), sky_bot=(100,100,170),
          moon=((240,240,220), 0.06), layers=mountains3(), stars=True)),
    ("land", "风景_清晨粉", 1000, 1000,
     dict(sky_top=(255,205,190), sky_mid=(245,160,180), sky_bot=(190,130,190),
          moon=((255,255,245), 0.07), layers=mountains3())),
    ("land", "风景_薄荷绿", 1200, 900,
     dict(sky_top=(170,225,200), sky_mid=(120,195,180), sky_bot=(70,150,150),
          moon=((255,255,240), 0.06),
          layers=[(40,90,90),(55,115,110),(75,140,130)])),
    ("land", "风景_深海蓝", 1280, 720,
     dict(sky_top=(90,160,210), sky_mid=(70,120,190), sky_bot=(50,85,160),
          moon=((245,250,255), 0.065), layers=mountains3())),
    ("land", "风景_沙漠金", 1366, 768,
     dict(sky_top=(250,205,130), sky_mid=(240,160,110), sky_bot=(200,110,100),
          moon=((255,250,230), 0.06),
          layers=[(120,75,70),(150,95,75),(180,120,85)])),
    ("land", "风景_暮色玫瑰", 1080, 1350,
     dict(sky_top=(250,170,170), sky_mid=(225,120,160), sky_bot=(150,80,140),
          moon=((255,250,240), 0.06), layers=mountains3(), stars=True)),
    ("land", "风景_靛夜星", 800, 1200,
     dict(sky_top=(25,35,70), sky_mid=(55,60,115), sky_bot=(95,85,155),
          moon=((250,250,235), 0.07), layers=mountains3(), stars=True)),
    ("land", "风景_湖光青", 1500, 1000,
     dict(sky_top=(140,210,220), sky_mid=(100,170,200), sky_bot=(70,125,175),
          moon=((250,253,250), 0.055),
          layers=[(45,85,120),(60,105,140),(80,130,160)])),
    ("land", "风景_极光紫", 1280, 800,
     dict(sky_top=(90,60,140), sky_mid=(130,80,180), sky_bot=(180,110,190),
          moon=((250,245,255), 0.06), layers=mountains3(), stars=True)),
    ("land", "风景_暖冬橘", 1920, 1080,
     dict(sky_top=(255,190,120), sky_mid=(245,140,120), sky_bot=(200,100,130),
          moon=((255,252,240), 0.065), layers=mountains3())),

    ("grid", "色块_经典四色", 800, 800,
     [(226,88,74), (240,166,50), (66,110,180), (150,80,190)]),
    ("grid", "色块_清新四色", 1000, 800,
     [(80,180,150), (110,170,220), (250,190,90), (230,110,130)]),
    ("grid", "色块_深色四色", 900, 900,
     [(50,60,100), (70,90,140), (100,70,130), (140,90,80)]),

    ("check", "棋盘_蓝白", 800, 800, (240,245,250), (90,120,220)),
    ("check", "棋盘_绿白", 900, 700, (240,250,242), (80,180,120)),
    ("check", "棋盘_暖橙", 700, 900, (252,244,232), (235,140,70)),

    ("grad", "渐变_晨曦", 1200, 800, ((255,210,150), (250,140,140), (180,100,170))),
    ("grad", "渐变_深空", 1600, 900, ((20,28,60), (60,55,120), (130,80,160))),
]


def main():
    for i, spec in enumerate(SPECS, start=1):
        kind, name, w, h = spec[0], spec[1], spec[2], spec[3]
        if kind == "land":
            p = spec[4]
            im = landscape(w, h, p["sky_top"], p["sky_mid"], p["sky_bot"],
                           p.get("moon"), p["layers"], p.get("stars", False))
            im = with_label(im, f"{name}.png", dark=True)
        elif kind == "grid":
            im = color_grid(w, h, spec[4])
            d = ImageDraw.Draw(im)
            text(d, (w // 2, h - 26), f"{name}.png", 16, (90, 90, 90), anchor="mm")
        elif kind == "check":
            im = checker(w, h, spec[4], spec[5])
            d = ImageDraw.Draw(im)
            text(d, (w // 2, h - 26), f"{name}.png", 16, (100, 100, 100), anchor="mm")
        elif kind == "grad":
            top, mid, bot = spec[4]
            im = Image.new("RGB", (w, h), top)
            d = ImageDraw.Draw(im)
            for y in range(h):
                t = y / h
                c = lerp(top, mid, t / 0.5) if t < 0.5 else lerp(mid, bot, (t - 0.5) / 0.5)
                d.line([(0, y), (w, y)], fill=c)
            text(d, (w // 2, h - 26), f"{name}.png", 16, (255, 255, 255), anchor="mm")
        fn = os.path.join(OUT, f"{name}.png")
        im.save(fn, "PNG")
        print("saved", os.path.basename(fn))
    print("ALL 20 DONE")


if __name__ == "__main__":
    main()
