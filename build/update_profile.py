# -*- coding: utf-8 -*-
"""初始化 / 更新 GitHub 个人主页（profile README + 资料字段）。

个人主页 README 的原理：建一个和用户名同名的仓库 `3206YYY/3206YYY`，
里面的 README.md 会显示在 https://github.com/3206YYY 顶部。

用法：
    set GH_TOKEN=github_pat_xxx
    python build/update_profile.py            # 建仓库 + 推 README + 填资料
    python build/update_profile.py --dry-run  # 只看当前值和将要写入的值

需要的权限：
  * 建仓库 / 提交文件 —— classic PAT 的 `repo`，或 fine-grained 的
    Repository access: All repositories + Contents: Read and write
  * 改名字 / 简介 / 网站 —— classic PAT 的 `user`，或 fine-grained 的
    Account permissions -> Profile: Read and write
  权限不够时脚本不会中断：能做的做完，做不了的单独告诉你。
"""
import base64
import http.client
import json
import os
import sys

USER = "3206YYY"
HERE = os.path.dirname(os.path.abspath(__file__))
README_PATH = os.path.join(os.path.dirname(HERE), "docs", "profile", "README.md")

PROFILE = {
    "name": "3206YYY",
    "bio": "大学生 / 独立开发者。做纯本地、能离线跑的 Windows 工具：MediaManager（一个窗口管完所有盘）",
    "blog": "https://github.com/3206YYY/MediaManager",
    "location": "China",
}


def request(method, path, body=None, token=None):
    conn = http.client.HTTPSConnection("api.github.com", timeout=60)
    headers = {
        "Authorization": "Bearer " + token,
        "Accept": "application/vnd.github+json",
        "User-Agent": "MediaManager-Profile",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if isinstance(body, (dict, list)):
        body = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    conn.request(method, path, body=body, headers=headers)
    resp = conn.getresponse()
    raw = resp.read()
    conn.close()
    try:
        return resp.status, json.loads(raw.decode("utf-8")) if raw else {}
    except Exception:
        return resp.status, raw.decode("utf-8", "ignore")


def main():
    dry = "--dry-run" in sys.argv
    token = os.environ.get("GH_TOKEN", "").strip()
    if not token and not dry:
        sys.exit("缺少 GH_TOKEN 环境变量")

    st, me = request("GET", "/user", token=token) if token else (0, {})
    if st == 200:
        print("当前主页字段：")
        for k in ("name", "bio", "blog", "location"):
            print(f"  {k:<9}= {me.get(k)!r}")
        print()
    elif token:
        sys.exit(f"读取账号失败: {st} {me}")
    else:
        print("（无 token，跳过当前值读取）\n")

    if not os.path.exists(README_PATH):
        sys.exit(f"找不到 {README_PATH}")
    readme = open(README_PATH, encoding="utf-8").read()
    print(f"profile README：{README_PATH}（{len(readme)} 字符）")

    if dry:
        print("\n[dry-run] 将要写入：")
        for k, v in PROFILE.items():
            print(f"  {k:<9}= {v!r}")
        print(f"  仓库      = {USER}/{USER} 的 README.md")
        return

    # 1) profile README 仓库
    st, repo = request("GET", f"/repos/{USER}/{USER}", token=token)
    if st == 404:
        st, repo = request("POST", "/user/repos", {
            "name": USER,
            "description": "个人主页",
            "private": False,
            "auto_init": False,
            "has_issues": False,
            "has_wiki": False,
            "has_projects": False,
        }, token=token)
        if st not in (200, 201):
            print(f"✗ 建仓库失败: {st} {repo}")
            return
        print(f"✓ 已创建仓库 {USER}/{USER}")
    elif st == 200:
        print(f"· 仓库 {USER}/{USER} 已存在")
    else:
        print(f"✗ 查询仓库失败: {st} {repo}")
        return

    # 2) 写 README（已存在要带 sha，否则 422）
    path = f"/repos/{USER}/{USER}/contents/README.md"
    body = {
        "message": "docs: 个人主页 README",
        "content": base64.b64encode(readme.encode("utf-8")).decode("ascii"),
        "branch": repo.get("default_branch") or "main",
    }
    st, cur = request("GET", path, token=token)
    if st == 200 and isinstance(cur, dict):
        body["sha"] = cur["sha"]
        body["message"] = "docs: 更新个人主页 README"
    st, res = request("PUT", path, body, token=token)
    if st in (200, 201):
        print(f"✓ README 已提交 -> https://github.com/{USER}")
    else:
        print(f"✗ 提交 README 失败: {st} {res}")

    # 3) 资料字段
    st, res = request("PATCH", "/user", PROFILE, token=token)
    if st == 200:
        print("✓ 名字 / 简介 / 网站 / 地区 已更新")
        for k in ("name", "bio", "blog", "location"):
            print(f"  {k:<9}= {res.get(k)!r}")
    else:
        print(f"✗ 更新资料失败（多半是 token 缺 user/Profile 写权限）: {st} {res}")
        print("  → 手填地址：https://github.com/settings/profile")


if __name__ == "__main__":
    main()
