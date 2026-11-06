# -*- coding: utf-8 -*-
"""V1.4.5 回归 runner：串行跑 build/verify 下所有实机脚本，汇总退出码与结论行。"""
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
VERIFY = os.path.join(ROOT, "build", "verify")

SCRIPTS = sys.argv[1:] or [
    "verify_multi_subfolder.py",
    "verify_folder_nested.py",
    "verify_offline_folder.py",
    "verify_first_run.py",
    "verify_light_theme.py",
    "verify_diary_calendar.py",
    "verify_diary_attachment.py",
    "verify_external_open.py",
    "verify_vault_password.py",
    "verify_plugin_breaker.py",
    "verify_diagnostic_info.py",
    "verify_video_player.py",
]

summary = []
for name in SCRIPTS:
    path = os.path.join(VERIFY, name)
    if not os.path.exists(path):
        print("SKIP missing", name, flush=True)
        continue
    # 每个脚本前把上一轮残留的进程清干净：子窗口（日记 / 查看器 / 私密空间 / 设置）
    # 和残留的 Notepad 会抢前台焦点，导致下一个脚本「点不动 / 窗口打不开」的假失败。
    subprocess.run(["taskkill", "/F", "/IM", "MediaManager.App.exe"], capture_output=True)
    subprocess.run(["taskkill", "/F", "/IM", "notepad.exe"], capture_output=True)
    time.sleep(1.5)
    t0 = time.time()
    print("\n===== %s =====" % name, flush=True)
    try:
        r = subprocess.run([sys.executable, path], cwd=ROOT, capture_output=True,
                           encoding="utf-8", errors="ignore", timeout=900)
        out = (r.stdout or "") + (r.stderr or "")
        lines = [l for l in out.splitlines() if l.strip()]
        tail = lines[-3:]
        for l in lines:
            if l.startswith("FAIL") or l.startswith("ERROR"):
                print("  " + l, flush=True)
        print("  exit=%s  %.0fs" % (r.returncode, time.time() - t0), flush=True)
        for l in tail:
            print("  | " + l, flush=True)
        summary.append((name, r.returncode, tail[-1] if tail else "", time.time() - t0))
    except subprocess.TimeoutExpired:
        print("  TIMEOUT", flush=True)
        summary.append((name, -9, "TIMEOUT", time.time() - t0))

print("\n\n========== 汇总 ==========", flush=True)
bad = 0
for name, code, last, dt in summary:
    flag = "OK " if code == 0 else "BAD"
    if code != 0:
        bad += 1
    print("%s %-34s %4.0fs  %s" % (flag, name, dt, last), flush=True)
print("失败 %d / %d" % (bad, len(summary)), flush=True)
