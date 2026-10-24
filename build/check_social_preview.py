# -*- coding: utf-8 -*-
"""验证仓库的 Social Preview 是否已经换成我们那张封面。

做法：抓仓库页面里的 og:image（GitHub 通过 opengraph.githubassets.com 代理），
下载回来和 docs/social-preview.png 做缩略图比对 —— 相似度接近 1 就是上传成功了。
"""
import io
import os
import re
import ssl
import urllib.request

from PIL import Image, ImageChops, ImageStat

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL = os.path.join(ROOT, "docs", "social-preview.png")
REPO_URL = "https://github.com/3206YYY/MediaManager"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}


def fetch(url, binary=False):
    ctx = ssl.create_default_context()
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=60, context=ctx) as r:
        data = r.read()
    return data if binary else data.decode("utf-8", "ignore")


def thumb(im, size=(64, 36)):
    return im.convert("RGB").resize(size, Image.LANCZOS)


def diff_ratio(a, b):
    stat = ImageStat.Stat(ImageChops.difference(a, b))
    return sum(stat.mean) / 3 / 255.0   # 0 = 完全一样，1 = 完全不同


def main():
    html = fetch(REPO_URL)
    m = re.search(r'<meta\s+property="og:image"\s+content="([^"]+)"', html)
    if not m:
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
    d = diff_ratio(thumb(local), thumb(net))
    print(f"网络图 {net.size}  本地图 {local.size}  差异度 = {d:.3f}（<0.12 视为同一张）")
    print("结论：" + ("✓ 社交预览已经是封面图了" if d < 0.12 else "✗ 还是 GitHub 默认卡片，图上没生效"))


if __name__ == "__main__":
    main()
