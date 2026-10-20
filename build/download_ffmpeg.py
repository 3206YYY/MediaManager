"""下载 FFmpeg 到 tools/ffmpeg/ 目录，供媒体管理器生成视频/特殊格式缩略图使用。

用法：python download_ffmpeg.py

优先使用 winget（Windows 自带），失败则从 gyan.dev 下载 essentials 版并解压提取 ffmpeg.exe/ffprobe.exe。
"""
import os
import shutil
import subprocess
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET = os.path.join(ROOT, "tools", "ffmpeg")


def ensure_target():
    os.makedirs(TARGET, exist_ok=True)


def try_winget() -> bool:
    """尝试用 winget 安装 ffmpeg 到系统，再复制 exe。"""
    try:
        r = subprocess.run(
            ["winget", "--version"], capture_output=True, text=True, timeout=15
        )
        if r.returncode != 0:
            return False
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return False

    print("[1/2] 正在用 winget 安装 ffmpeg（可能弹窗）...")
    r = subprocess.run(
        ["winget", "install", "--id", "Gyan.FFmpeg", "-e",
         "--accept-source-agreements", "--accept-package-agreements"],
        capture_output=True, text=True,
    )
    # winget 安装后 ffmpeg 可能不在 PATH，尝试常见位置复制
    for exe in ("ffmpeg.exe", "ffprobe.exe"):
        for loc in (
            os.path.join(os.environ.get("LOCALAPPDATA", ""),
                         "Microsoft", "WinGet", "Packages",
                         "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe",
                         "ffmpeg-*-full_build", "bin", exe),
        ):
            import glob
            for p in glob.glob(loc):
                shutil.copy2(p, os.path.join(TARGET, exe))
                print(f"已复制 {exe}")
    return has_exes()


def has_exes() -> bool:
    return os.path.exists(os.path.join(TARGET, "ffmpeg.exe"))


def try_download() -> bool:
    """从 gyan.dev 下载 essentials 版并解压。"""
    url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    zip_path = os.path.join(TARGET, "ffmpeg.zip")
    print(f"[2/2] 从 gyan.dev 下载（约 90MB）...\n{url}")

    def report(block, bs, total):
        done = block * bs
        if total > 0:
            pct = done * 100 // total
            sys.stdout.write(f"\r下载中 {pct}% ({done//1024//1024}MB)")
            sys.stdout.flush()

    try:
        urllib.request.urlretrieve(url, zip_path, report)
        print()
    except Exception as e:
        print(f"下载失败：{e}")
        return False

    print("解压中...")
    with zipfile.ZipFile(zip_path) as z:
        for name in z.namelist():
            base = os.path.basename(name)
            if base in ("ffmpeg.exe", "ffprobe.exe"):
                with z.open(name) as src, open(os.path.join(TARGET, base), "wb") as dst:
                    shutil.copyfileobj(src, dst)
                print(f"已提取 {base}")

    os.remove(zip_path)
    return has_exes()


def main():
    ensure_target()
    if has_exes():
        print(f"FFmpeg 已存在：{TARGET}")
        return 0

    if not try_winget() and not try_download():
        print("\n自动下载失败。请手动下载：")
        print("  https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip")
        print("  解压后把 bin/ffmpeg.exe 和 bin/ffprobe.exe 复制到：")
        print(f"  {TARGET}")
        return 1

    print(f"\n完成！FFmpeg 已就绪：{TARGET}")
    print("重新启动媒体管理器即可生成视频缩略图。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
