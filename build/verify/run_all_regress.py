# -*- coding: utf-8 -*-
"""回归 runner：自动扫 build/verify 下所有 verify_*.py 串行跑，汇总退出码与结论行。

为什么不手写清单了：
  清单以前是手写的，v1.4.4 → v1.4.6 陆续加了 6 个守门脚本（批量删除 67 项 /
  代码健康 25 项 / 协议 18 项 / 更新提示 21 项 / 设置页 7 项 / 桌面图标 14 项），
  清单一直没跟上——等于这几道守门全靠人记得手敲文件名，漏一次就等于没守。

  现在「目录即清单」：脚本落地就自动进回归，不用改这个文件。
  只有明确不适合无脑全量的才写进 EXCLUDE，且每条必须注明原因；
  EXCLUDE 里写了但文件不存在会直接报错退出（改名后忘了清 = 静默少守一道）。

用法：
    python build/verify/run_all_regress.py                # 全量
    python build/verify/run_all_regress.py a.py b.py      # 只跑指定几个（可点名 EXCLUDE 里的）
"""
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
VERIFY = os.path.join(ROOT, "build", "verify")

# 不进「无脑全量」的脚本：每条都要写清原因，不要图省事往里塞。
EXCLUDE = {
    "verify_memory.py": "内存采样要反复起停程序取样，单次 3 分钟以上，且数值随机器状态浮动，单独跑",
}


def discover():
    """目录里的全部 verify_*.py，按名字排序，保证每次跑的顺序一致。"""
    return sorted(f for f in os.listdir(VERIFY)
                  if f.startswith("verify_") and f.endswith(".py"))


def cleanup():
    """每个脚本前把上一轮残留的进程清干净：子窗口（日记 / 查看器 / 私密空间 / 设置）
    和残留的 Notepad 会抢前台焦点，导致下一个脚本「点不动 / 窗口打不开」的假失败。"""
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "notepad.exe"], capture_output=True)
    time.sleep(1.5)


def run_once(path):
    """跑一次脚本，返回 (退出码, 非空输出行, 耗时秒)。"""
    t0 = time.time()
    try:
        r = subprocess.run([sys.executable, path], cwd=ROOT, capture_output=True,
                           encoding="utf-8", errors="ignore", timeout=900)
    except subprocess.TimeoutExpired:
        return -9, ["TIMEOUT"], time.time() - t0
    out = (r.stdout or "") + (r.stderr or "")
    return r.returncode, [l for l in out.splitlines() if l.strip()], time.time() - t0


def main(argv):
    on_disk = discover()

    stale = [n for n in EXCLUDE if n not in on_disk]
    if stale:
        print("EXCLUDE 里写了不存在的脚本（改名后忘了清？）：%s" % ", ".join(stale), flush=True)
        return 2

    scripts = argv or [n for n in on_disk if n not in EXCLUDE]
    if not scripts:
        print("一个脚本都没扫到，检查 build/verify 目录", flush=True)
        return 2

    skipped = [n for n in on_disk if n not in EXCLUDE and n not in scripts]
    print("共 %d 个脚本，本次跑 %d 个" % (len(on_disk), len(scripts)), flush=True)
    for n in EXCLUDE:
        print("  跳过 %s —— %s" % (n, EXCLUDE[n]), flush=True)
    if skipped:
        print("  手动点名模式，未跑另外 %d 个" % len(skipped), flush=True)

    summary = []
    for name in scripts:
        path = os.path.join(VERIFY, name)
        if not os.path.exists(path):
            print("SKIP missing", name, flush=True)
            continue
        cleanup()
        print("\n===== %s =====" % name, flush=True)
        code, lines, dt = run_once(path)

        # 脚本自己崩了（一条 FAIL 断言都没有，却是非 0 退出）多半是 uia / COM 的时序抖动
        # ——v1.4.7 回归时 verify_batch_delete_help 就这样挂过一次，单独重跑 67/67 全过。
        # 给一次重试机会；真正的断言失败不重试，免得把功能问题刷成「过」。
        if code != 0 and not any(l.startswith(("FAIL", "ERROR")) for l in lines):
            print("  脚本异常退出且无断言失败，重试一次", flush=True)
            cleanup()
            code, lines, dt = run_once(path)

        tail = lines[-3:]
        for l in lines:
            if l.startswith("FAIL") or l.startswith("ERROR"):
                print("  " + l, flush=True)
        print("  exit=%s  %.0fs" % (code, dt), flush=True)
        for l in tail:
            print("  | " + l, flush=True)
        summary.append((name, code, tail[-1] if tail else "", dt))

    print("\n\n========== 汇总 ==========", flush=True)
    bad = 0
    for name, code, last, dt in summary:
        flag = "OK " if code == 0 else "BAD"
        if code != 0:
            bad += 1
        print("%s %-34s %4.0fs  %s" % (flag, name, dt, last), flush=True)
    print("失败 %d / %d" % (bad, len(summary)), flush=True)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
