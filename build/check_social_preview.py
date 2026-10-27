# -*- coding: utf-8 -*-
"""验证仓库的 Social Preview 是否已经换成「当前这版」的封面。

做法：抓仓库页面里的 og:image（GitHub 把上传的 Social Preview 挂在
repository-images.githubusercontent.com 上），下载回来跟 docs/social-preview.png 比。

⚠ 别只比整图相似度：社交预览和本地图结构完全一样，只有右下角页脚那一行
「vX.Y.Z · NN 种格式」会变；整图缩到 64×36 一比，这点差异直接被平均成 0.000，
于是脚本欢快地判定「已生效」—— v1.4.0 就这么漏掉过一次（线上还挂着 v1.3.2）。
所以这里单独把页脚条带抠出来，按「文字像素掩膜」比，字形变了才算通过。
"""
import io
import os
import re
import urllib.request

from PIL import Image, ImageChops, ImageStat

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL = os.path.join(ROOT, "docs", "social-preview.png")
REPO_URL = "https://github.com/3206YYY/MediaManager"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}

# 页脚条带（相对整图的右下方块），版本号那一行就写在这里
FOOT_BOX = (0.66, 0.88, 1.0, 1.0)
TEXT_LEVEL = 70          # 亮度高于它算「文字像素」（背景最深只有 ~18）


def fetch(url, binary=False):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read()
    return data if binary else data.decode("utf-8", "ignore")


def diff_ratio(a, b):
    stat = ImageStat.Stat(ImageChops.difference(a, b))
    return sum(stat.mean) / 3 / 255.0


def foot_mask(im):
    """裁出页脚条带，把文字像素二值化成掩膜。"""
    w, h = im.size
    box = tuple(int(v * s) for v, s in zip(FOOT_BOX, (w, h, w, h)))
    g = im.convert("L").crop(box)
    return g.point(lambda v: 255 if v > TEXT_LEVEL else 0)


def mask_diff(a, b):
    """两张掩膜里「一个亮一个暗」的像素占比：0 = 字完全一样。"""
    if a.size != b.size:
        b = b.resize(a.size, Image.LANCZOS)
    diff = ImageChops.difference(a, b)
    # 掩膜是二值的：histogram()[255] 就是亮像素数；diff 的非零桶之和是差异像素数
    lit = a.histogram()[255] + b.histogram()[255]
    hit = sum(diff.histogram()[1:])
    return hit / max(lit, 1)


def main():
    html = fetch(REPO_URL)
    m = re.search(r'property="og:image"\s+content="([^"]+)"', html)
    if not m:
        print("页面里没找到 og:image")
        return
    og = m.group(1)
    print("og:image =", og)

    raw = fetch(og, binary=True)
    net = Image.open(io.BytesIO(raw)).convert("RGB")
    net.save(os.path.join(ROOT, "build", "og_preview_check.png"))

    local = Image.open(LOCAL).convert("RGB")
    if net.size != local.size:
        net = net.resize(local.size, Image.LANCZOS)

    full = diff_ratio(local.resize((640, 320), Image.LANCZOS),
                      net.resize((640, 320), Image.LANCZOS))
    glyph = mask_diff(foot_mask(local), foot_mask(net))

    print(f"整图差异度 = {full:.4f}（>0.06 说明压根不是这张图）")
    print(f"页脚字形差异 = {glyph:.3f}（>0.05 说明版本号那一行还没刷新）")

    if full > 0.06:
        print("结论：✗ 线上还是 GitHub 默认卡片，这张封面根本没上传")
    elif glyph > 0.05:
        print("结论：✗ 封面是同一张，但页脚版本号是旧的 —— 去 Settings → Social preview 重新上传 docs/social-preview.png")
    else:
        print("结论：✓ 社交预览已换成当前版本")


if __name__ == "__main__":
    main()
