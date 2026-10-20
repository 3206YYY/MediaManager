# -*- coding: utf-8 -*-
"""用 PIL 自绘「媒体管理器」介绍用简单示意图，并用 ffmpeg 造测试视频。"""
import os
from PIL import Image, ImageDraw, ImageFont

FONT_PATH = r"C:\Windows\Fonts\msyh.ttc"
OUT = r"C:\测试"
os.makedirs(OUT, exist_ok=True)

W, H = 1280, 800


def font(sz, bold=False):
    # msyh.ttc index 0 常规, 1 粗体
    return ImageFont.truetype(FONT_PATH, sz)


def text(d, xy, s, size=20, color=(0, 0, 0), bold=False, anchor="la"):
    f = font(size, bold)
    d.text(xy, s, font=f, fill=color, anchor=anchor)


def round_rect(d, box, r, fill=None, outline=None, width=2):
    d.rounded_rectangle(box, radius=r, fill=fill, outline=outline, width=width)


# ---------------------------------------------------------------------------
# 1. 主界面（网格浏览）
# ---------------------------------------------------------------------------
def img_main():
    im = Image.new("RGB", (W, H), (245, 246, 248))
    d = ImageDraw.Draw(im)
    # 侧栏
    round_rect(d, (0, 0, 220, H), 0, fill=(43, 43, 48))
    text(d, (28, 34), "媒体管理器", 24, (255, 255, 255), True)
    nav = ["全部媒体", "收藏夹", "图片", "视频", "分类", "最近删除"]
    for i, t in enumerate(nav):
        y = 110 + i * 50
        if t == "全部媒体":
            d.rounded_rectangle((12, y - 6, 208, y + 38), radius=8, fill=(0, 120, 215))
        text(d, (40, y + 12), t, 18, (220, 220, 220) if t != "全部媒体" else (255, 255, 255))
    # 顶栏
    d.rectangle((220, 0, W, 70), fill=(255, 255, 255))
    round_rect(d, (250, 18, 620, 52), 8, outline=(200, 200, 200))
    text(d, (270, 35), "🔍 搜索文件名 / 标签…", 16, (150, 150, 150))
    # 网格照片
    cols, rows = 3, 2
    gx, gy = 250, 100
    gw, gh = 300, 230
    gap = 24
    palette = [(120, 170, 230), (230, 150, 120), (140, 200, 150),
               (210, 200, 120), (180, 140, 220), (120, 200, 210)]
    for idx in range(6):
        cx = gx + (idx % cols) * (gw + gap)
        cy = gy + (idx // cols) * (gh + gap)
        round_rect(d, (cx, cy, cx + gw, cy + gh), 10, fill=palette[idx])
        round_rect(d, (cx, cy, cx + gw, cy + gh), 10, outline=(220, 220, 220))
    # 状态栏
    d.rectangle((0, H - 36, W, H), fill=(235, 236, 238))
    text(d, (250, H - 24), "共 6 张媒体 · 3 张图片 · 3 个视频", 15, (110, 110, 110))
    im.save(os.path.join(OUT, "01_主界面_网格浏览.png"))
    print("saved 01")


# ---------------------------------------------------------------------------
# 2. 图片编辑器
# ---------------------------------------------------------------------------
def img_editor():
    im = Image.new("RGB", (W, H), (238, 240, 243))
    d = ImageDraw.Draw(im)
    # 顶栏工具
    d.rectangle((0, 0, W, 64), fill=(255, 255, 255))
    tools = ["裁剪", "旋转", "滤镜", "文字", "涂鸦", "马赛克", "保存副本"]
    for i, t in enumerate(tools):
        x = 40 + i * 150
        round_rect(d, (x, 14, x + 120, 50), 8, fill=(0, 120, 215))
        text(d, (x + 60, 32), t, 16, (255, 255, 255), anchor="mm")
    # 中央照片
    px, py, pw, ph = 320, 96, 640, 600
    round_rect(d, (px, py, px + pw, py + ph), 12, fill=(150, 190, 230))
    # 文字图层示意
    round_rect(d, (px + 120, py + 220, px + 480, py + 300), 6, fill=(255, 255, 255))
    text(d, (px + 300, py + 260), "旅行纪念 · 2026", 30, (40, 40, 40), anchor="mm")
    text(d, (px + 300, py + 285), "双击编辑文字", 16, (120, 120, 120), anchor="mm")
    # 右侧属性
    rx = 1000
    round_rect(d, (rx, 96, rx + 220, 696), 12, fill=(255, 255, 255), outline=(225, 225, 225))
    text(d, (rx + 20, 120), "属性", 20, (40, 40, 40), True)
    text(d, (rx + 20, 170), "字号  32", 16, (90, 90, 90))
    text(d, (rx + 20, 210), "颜色  #282C34", 16, (90, 90, 90))
    text(d, (rx + 20, 250), "不透明度 100%", 16, (90, 90, 90))
    im.save(os.path.join(OUT, "02_图片编辑器.png"))
    print("saved 02")


# ---------------------------------------------------------------------------
# 3. 视频播放器
# ---------------------------------------------------------------------------
def img_player():
    im = Image.new("RGB", (W, H), (10, 10, 12))
    d = ImageDraw.Draw(im)
    # 视频画面
    for y in range(90, 640):
        c = int(20 + (y - 90) / 550 * 60)
        d.line([(0, y), (W, y)], fill=(c, c + 30, c + 70))
    # 底部半透明控制栏
    cb = (0, 700, W, H)
    d.rectangle(cb, fill=(0, 0, 0))
    d.rectangle(cb, fill=(0, 0, 0))
    # 用半透明叠加近似：直接画深黑
    # 播放钮
    d.ellipse((40, 722, 80, 762), fill=(255, 255, 255))
    d.polygon([(54, 730), (54, 754), (74, 743)], fill=(18, 19, 22))
    # 进度条
    round_rect(d, (120, 738, 980, 748), 5, fill=(80, 80, 80))
    round_rect(d, (120, 738, 560, 748), 5, fill=(0, 120, 215))
    d.ellipse((550, 732, 568, 754), fill=(255, 255, 255))
    # 时间
    text(d, (995, 743), "00:23 / 01:30", 16, (230, 230, 230), anchor="lm")
    # 音量
    text(d, (1090, 743), "🔊", 20, (230, 230, 230), anchor="lm")
    round_rect(d, (1130, 739, 1180, 747), 4, fill=(80, 80, 80))
    # 倍速
    round_rect(d, (1190, 728, 1235, 758), 6, fill=(60, 60, 60))
    text(d, (1212, 743), "1x", 15, (230, 230, 230), anchor="mm")
    im.save(os.path.join(OUT, "03_视频播放器.png"))
    print("saved 03")


# ---------------------------------------------------------------------------
# 4. 设置页
# ---------------------------------------------------------------------------
def img_settings():
    im = Image.new("RGB", (W, H), (245, 246, 248))
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, W, 60), fill=(255, 255, 255))
    text(d, (40, 30), "设置", 24, (40, 40, 40), True)
    # 左侧分类
    cats = ["常规", "备份与迁移", "外观", "系统集成", "关于"]
    round_rect(d, (40, 90, 260, 90 + len(cats) * 56), 10, fill=(255, 255, 255), outline=(225, 225, 225))
    for i, t in enumerate(cats):
        y = 100 + i * 56
        if t == "备份与迁移":
            d.rounded_rectangle((48, y, 252, y + 44), radius=8, fill=(224, 238, 250))
        text(d, (70, y + 22), t, 17, (40, 40, 40) if t != "备份与迁移" else (0, 90, 180), anchor="lm")
    # 右侧表单
    rx = 320
    round_rect(d, (rx, 90, W - 40, 720), 10, fill=(255, 255, 255), outline=(225, 225, 225))
    text(d, (rx + 30, 130), "备份与迁移", 20, (40, 40, 40), True)
    text(d, (rx + 30, 185), "备份目录", 16, (90, 90, 90))
    round_rect(d, (rx + 30, 210, rx + 520, 244), 6, outline=(200, 200, 200))
    text(d, (rx + 50, 227), "D:\\MediaManagerBackup", 15, (120, 120, 120))
    text(d, (rx + 545, 227), "浏览", 15, (0, 120, 215), anchor="lm")
    text(d, (rx + 30, 285), "自动备份间隔", 16, (90, 90, 90))
    for i, t in enumerate(["关闭", "15 分钟", "30 分钟", "1 小时", "2 小时"]):
        x = rx + 30 + i * 110
        sel = (t == "30 分钟")
        round_rect(d, (x, 310, x + 100, 344), 6, fill=(0, 120, 215) if sel else (240, 240, 240))
        text(d, (x + 50, 327), t, 14, (255, 255, 255) if sel else (80, 80, 80), anchor="mm")
    im.save(os.path.join(OUT, "04_设置页_备份迁移.png"))
    print("saved 04")


# ---------------------------------------------------------------------------
# 5. 功能总览
# ---------------------------------------------------------------------------
def img_overview():
    im = Image.new("RGB", (W, H), (232, 240, 250))
    d = ImageDraw.Draw(im)
    text(d, (W // 2, 70), "媒体管理器", 40, (30, 40, 70), True, anchor="mm")
    text(d, (W // 2, 120), "本地图片 / 视频管理工具 · 简单易用", 20, (90, 100, 120), anchor="mm")
    cards = [
        ("图片网格浏览", "瀑布流查看全部照片"),
        ("视频播放器", "现代悬浮控制栏 · 连播"),
        ("图片编辑", "裁剪 / 文字 / 涂鸦 / 马赛克"),
        ("批量处理", "重命名 · 导出副本"),
        ("分类收藏", "打星 · 收藏夹 · 最近删除"),
        ("备份迁移", "一键换机 · 自动备份"),
    ]
    cw, ch = 360, 200
    gx, gy = 80, 180
    gap = 30
    colors = [(0, 120, 215), (46, 160, 120), (220, 140, 40),
              (180, 80, 160), (60, 130, 200), (40, 150, 160)]
    for i, (t, sub) in enumerate(cards):
        cx = gx + (i % 3) * (cw + gap)
        cy = gy + (i // 3) * (ch + gap)
        round_rect(d, (cx, cy, cx + cw, cy + ch), 14, fill=(255, 255, 255))
        d.ellipse((cx + 30, cy + 30, cx + 90, cy + 90), fill=colors[i])
        text(d, (cx + 60, cy + 60), str(i + 1), 30, (255, 255, 255), True, anchor="mm")
        text(d, (cx + 120, cy + 50), t, 22, (40, 40, 40), True)
        text(d, (cx + 120, cy + 86), sub, 15, (120, 120, 120))
    im.save(os.path.join(OUT, "05_功能总览.png"))
    print("saved 05")


if __name__ == "__main__":
    img_main()
    img_editor()
    img_player()
    img_settings()
    img_overview()
    print("ALL IMAGES DONE")
