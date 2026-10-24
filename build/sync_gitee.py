# -*- coding: utf-8 -*-
"""把 GitHub 主仓库同步到 Gitee 镜像（描述 / 代码 / 标签 / Release 安装包）。

为什么需要这个脚本：Gitee 那次是用「导入 GitHub 仓库」建的，导入只搬了当时那一刻的快照，
之后 GitHub 上再提交它不会自己跟上 —— 所以 v1.3.1 的代码和安装包，Gitee 那边都没有。

用法（先建一个「私人令牌」，勾 projects 权限）：
    set GITEE_TOKEN=xxxxxxxxxxxxxxxx
    python build/sync_gitee.py            # 描述 + 代码 + 标签 + Release 全做
    python build/sync_gitee.py --dry-run  # 只打印要做什么，不改任何东西
    python build/sync_gitee.py meta       # 只改仓库描述 / 官网链接
    python build/sync_gitee.py push       # 只推代码和标签
    python build/sync_gitee.py release    # 只建 Release 并上传安装包

个人主页简介、精选项目这些 API 改不了，脚本最后会打印手点步骤。
"""
import json
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GIT = r"C:\AIGC\workbuddy\resources\vendor\PortableGit\cmd\git.exe"
API = "https://gitee.com/api/v5"
OWNER, REPO = "yyy3206", "MediaManager"
REMOTE = f"https://gitee.com/{OWNER}/{REPO}.git"

TAG = "v1.3.1"
RELEASE_NAME = "v1.3.1 — 扩展格式支持（音频 / 文档 / 压缩包）"
BODY_FILE = os.path.join(ROOT, "更新说明_V1.3.1.md")
EXE = os.path.join(ROOT, "dist", "MediaManager-Setup-1.3.1.exe")

# Gitee 的仓库介绍要跟 GitHub 对齐（那边已经改成「媒体管理器」了）
DESCRIPTION = (
    "免费开源的 Windows 媒体管理器：一个窗口管完图片 / 视频 / 音频 / 文档 / 压缩包，"
    "纯本地、无广告，内置轻量编辑器与「时光机」备份回退。"
)
HOMEPAGE = "https://github.com/3206YYY/MediaManager"

DRY = "--dry-run" in sys.argv


def token():
    t = os.environ.get("GITEE_TOKEN", "").strip()
    if not t:
        sys.exit("缺少 GITEE_TOKEN。到 https://gitee.com/profile/personal_access_tokens 建一个，"
                 "勾上 projects 权限，然后 set GITEE_TOKEN=...")
    return t


def api(method, path, fields=None):
    """Gitee v5：GET 走 query string，写操作走表单；access_token 一起带上。

    dry-run 时读接口照常发（公开仓库不需要令牌），写接口只打印。
    """
    url = API + path
    if method == "GET":
        req = urllib.request.Request(url + "?" + urllib.parse.urlencode(fields or {}), method="GET")
    elif DRY:
        print(f"  [dry-run] {method} {path} {list(fields) if fields else ''}")
        return 200, {}
    else:
        data = {"access_token": token()}
        data.update(fields or {})
        req = urllib.request.Request(
            url, data=urllib.parse.urlencode(data).encode("utf-8"),
            headers={"Content-Type": "application/x-www-form-urlencoded"}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            # Gitee 查单个资源查不到时会给 200 + 字面量 null，不能直接当 dict 用
            return r.status, json.loads(r.read().decode("utf-8") or "{}") or {}
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "ignore")
        print(f"  HTTP {e.code}  {raw[:300]}")
        return e.code, {}


# ---------------------------------------------------------------- 1. 仓库描述
def sync_meta():
    print("① 仓库描述 / 官网链接")
    st, cur = api("GET", f"/repos/{OWNER}/{REPO}")
    if st == 200:
        print(f"  现在：{cur.get('description')!r}  homepage={cur.get('homepage')!r}")
    st, _ = api("PATCH", f"/repos/{OWNER}/{REPO}",
                {"description": DESCRIPTION, "homepage": HOMEPAGE})
    print("  " + ("✓ 已更新" if st == 200 else f"✗ 失败（{st}）"))


# ---------------------------------------------------------------- 2. 代码 + 标签
def _askpass_env(tk):
    """把令牌塞进临时 askpass，避免密码出现在命令行参数里。"""
    d = tempfile.mkdtemp(prefix="gitee_")
    p = os.path.join(d, "askpass.sh")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write('#!/bin/sh\ncase "$1" in\n'
                '  *sername*) echo "$GITEE_USER" ;;\n'
                '  *assword*) echo "$GITEE_PASS" ;;\n'
                'esac\n')
    env = dict(os.environ)
    env.update({"GITEE_USER": OWNER, "GITEE_PASS": tk,
                "GIT_ASKPASS": p, "GIT_TERMINAL_PROMPT": "0"})
    return env, d


def sync_code():
    print("② 代码与标签")
    if DRY:
        print("  [dry-run] git push gitee main --tags")
        return
    env, d = _askpass_env(token())
    for args in (["-c", "credential.helper=", "push", REMOTE, "main"],
                 ["-c", "credential.helper=", "push", REMOTE, "--tags"]):
        r = subprocess.run([GIT] + args, cwd=ROOT, env=env,
                           capture_output=True, text=True, encoding="utf-8", errors="ignore")
        out = (r.stderr or r.stdout).strip().splitlines()
        print("  " + ("✓ " if r.returncode == 0 else "✗ ") + (out[-1] if out else ""))
    subprocess.run(["rm", "-rf", d])


# ---------------------------------------------------------------- 3. Release
def sync_release():
    print("③ Release + 安装包")
    if not os.path.exists(EXE):
        print(f"  ✗ 找不到安装包 {EXE}")
        return
    size_mb = os.path.getsize(EXE) / 1024 / 1024
    body = open(BODY_FILE, encoding="utf-8").read() if os.path.exists(BODY_FILE) else ""
    body += ("\n\n---\n\n**国内下载**：本页附件即安装包，Gitee 下载速度正常。"
             "源码与 issue 以 GitHub 为主仓库：https://github.com/3206YYY/MediaManager\n")

    st, rel = api("GET", f"/repos/{OWNER}/{REPO}/releases/tags/{TAG}")
    if st == 200 and rel.get("id"):
        print(f"  Release 已存在（id={rel['id']}），跳过创建")
    else:
        st, rel = api("POST", f"/repos/{OWNER}/{REPO}/releases", {
            "tag_name": TAG, "name": RELEASE_NAME, "body": body,
            "target_commitish": "main", "prerelease": "false",
        })
        if st not in (200, 201):
            print(f"  ✗ 建 Release 失败（{st}）")
            return
        print(f"  ✓ 已建 Release（tag={TAG}{'' if DRY else ', id=' + str(rel.get('id'))}）")

    if DRY:
        print(f"  [dry-run] 上传 {os.path.basename(EXE)}（{size_mb:.1f} MB）")
        return

    rid = rel.get("id")
    # 附件可能很大（100 MB 级），用 curl 流式上传，配置从 stdin 读，令牌不进命令行
    cfg = (f'url = "{API}/repos/{OWNER}/{REPO}/releases/{rid}/attach_files'
           f'?access_token={token()}"\n'
           f'form = "file=@{EXE}"\n'
           f'silent\nshow-error\n')
    r = subprocess.run(["curl", "-K", "-"], input=cfg, text=True,
                       capture_output=True, encoding="utf-8", errors="ignore")
    print(f"  {'✓' if r.returncode == 0 else '✗'} 上传结果："
          f"{(r.stdout or r.stderr).strip()[:300]}")


MANUAL = """
手动几步（API 做不到，得点网页）：

1. Gitee 个人简介
   https://gitee.com/profile  →  「个人资料」→ 简介填：
   大学生 / 独立开发者。做纯本地、能离线跑的 Windows 工具：MediaManager（一个窗口管完所有盘）

2. Gitee 精选项目
   https://gitee.com/yyy3206  → 「热门项目」区块右上角「自定义精选项目」→ 勾上 MediaManager

3. GitHub 置顶仓库
   https://github.com/3206YYY  → 登录后「Pinned」区块右上角「Customize your pins」→ 勾 MediaManager

4. GitHub 社交预览图（换掉那张 v1.3.0 的）
   https://github.com/3206YYY/MediaManager/settings  → 最下面 Social preview  →  Upload
   上传 docs/social-preview.png（1280×640，370 KB，已生成好）
"""


def main():
    want = [a for a in sys.argv[1:] if not a.startswith("-")] or ["meta", "push", "release"]
    print(f"Gitee 同步 → {OWNER}/{REPO}" + ("（DRY RUN，不改任何东西）" if DRY else ""))
    if "meta" in want:
        sync_meta()
    if "push" in want:
        sync_code()
    if "release" in want:
        sync_release()
    print(MANUAL)


if __name__ == "__main__":
    main()
