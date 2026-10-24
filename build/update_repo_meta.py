# -*- coding: utf-8 -*-
"""更新 GitHub 仓库的 About 区（简介 / 主页链接 / Topics）。

用法：
    set GH_TOKEN=github_pat_xxx
    python build/update_repo_meta.py

主页链接指向官网首页（docs/ 目录托管在 GitHub Pages），下载按钮在首页和 Releases 都能点到；
赞助走 README 里的爱发电，不占 About 区的 Link。
"""
import http.client
import json
import os
import sys

REPO = "3206YYY/MediaManager"
DESCRIPTION = "免费开源的 Windows 媒体管理器：一个窗口管完图片 / 视频 / 音频 / 文档 / 压缩包，纯本地、无广告，内置轻量编辑器与「时光机」备份回退。"
HOMEPAGE = "https://3206yyy.github.io/MediaManager/"
TOPICS = [
    "windows", "wpf", "dotnet", "csharp", "photo-manager", "image-viewer",
    "image-editor", "video-player", "audio-player", "media-manager", "file-manager",
    "local-first", "desktop-app",
]


def request(method, path, body=None, host="api.github.com"):
    token = os.environ.get("GH_TOKEN", "").strip()
    if not token:
        sys.exit("缺少 GH_TOKEN 环境变量")
    conn = http.client.HTTPSConnection(host, timeout=60)
    headers = {
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "User-Agent": "MediaManager-Repo-Meta",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if isinstance(body, (dict, list)):
        body = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    conn.request(method, path, body=body, headers=headers)
    resp = conn.getresponse()
    data = resp.read()
    conn.close()
    try:
        return resp.status, json.loads(data.decode("utf-8")) if data else {}
    except Exception:
        return resp.status, data.decode("utf-8", "ignore")


def main():
    st, repo = request("GET", f"/repos/{REPO}")
    if st != 200:
        sys.exit(f"读取仓库信息失败: {st} {repo}")
    print("当前简介:", repo.get("description"))
    print("当前主页:", repo.get("homepage") or "(空)")
    print("当前 Topics:", ", ".join(repo.get("topics") or []) or "(空)")
    print()

    st, d = request("PATCH", f"/repos/{REPO}", {"description": DESCRIPTION, "homepage": HOMEPAGE})
    if st != 200:
        sys.exit(f"更新简介/主页失败: {st} {d}")
    print("✓ 简介与主页已更新")

    st, d = request("PUT", f"/repos/{REPO}/topics", {"names": TOPICS})
    if st != 200:
        print(f"! Topics 更新失败（不影响发布）: {st} {d}")
    else:
        print("✓ Topics:", ", ".join(d.get("names") or []))

    st, repo = request("GET", f"/repos/{REPO}")
    print()
    print("=== 现在的 About 区 ===")
    print("简介:", repo.get("description"))
    print("主页:", repo.get("homepage"))
    print("Topics:", ", ".join(repo.get("topics") or []))


if __name__ == "__main__":
    main()
