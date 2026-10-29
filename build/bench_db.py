#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""数据库基准：合成 N 条元数据，跑仓库里真实的几条 SQL，输出 p50 / p95。

## 为什么要有这个脚本

README 曾经写过「实测十万张图滚动丝滑，内存占用硬顶 200MB」。仓库里没有任何测量
能支撑那句话 —— 它是从「开了 WAL + FTS5 + 虚拟化」推想出来的，而本机日志显示
只有 20 张缩略图时工作集就 215MB、峰值 364MB。那句话已经删掉，换成这个脚本的输出。

## 覆盖什么

只覆盖 **SQL 层**：列表分页、深翻页、计数、FTS 关键词搜索、trigram 子串搜索、按文件夹分组。
这些是「十万张卡不卡」的第一道关口，也是唯一能稳定重复测量的部分。

## 不覆盖什么

WPF 的滚动渲染、缩略图解码、内存峰值 —— 没法无头测量，别把这里的数字当成整机体验。

## 用法

    python build/bench_db.py             # 10 万条（默认）
    python build/bench_db.py 200000      # 自定义条数
    python build/bench_db.py --keep      # 保留工作库，方便自己 EXPLAIN QUERY PLAN

工作库是源库的**副本**，改的是副本，不碰你的真实库。
"""

import json
import os
import random
import shutil
import sqlite3
import sys
import time
from datetime import datetime, timedelta

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
WORK = os.path.join(HERE, "bench_work.db")
RESULT = os.path.join(HERE, "bench_result.json")

LOCAL_DB = os.path.join(os.environ.get("LOCALAPPDATA", ""), "MediaManager", "library.db")
PORTABLE_DB = os.path.join(ROOT, "src", "MediaManager.App", "bin", "Debug",
                           "net8.0-windows", "Data", "library.db")

# 文件名里混一些能搜的词，让 MATCH / trigram 有真实的选择性
BRANDS = ["DSC", "IMG", "PXL", "MVIMG", "Screenshot", "WeChat"]
SCENES = ["海边日落", "生日聚会", "山间徒步", "城市夜景", "猫", "咖啡", "会议白板", "毕业照"]
FORMATS = [
    ("jpg", "Image"), ("jpeg", "Image"), ("png", "Image"), ("heic", "Image"),
    ("mp4", "Video"), ("mov", "Video"), ("mkv", "Video"),
    ("mp3", "Audio"), ("flac", "Audio"),
    ("pdf", "Text"), ("txt", "Text"),
    ("zip", "Archive"),
]


def find_source_db(explicit=None):
    if explicit:
        return explicit if os.path.exists(explicit) else None
    for path in (LOCAL_DB, PORTABLE_DB):
        if path and os.path.exists(path) and os.path.getsize(path) > 0:
            return path
    return None


def prepare_work_db(source):
    """复制一份源库，保留 schema / 索引 / FTS 表，清空数据。"""
    shutil.copy2(source, WORK)
    conn = sqlite3.connect(WORK)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")

    # 关联表先清，否则 MediaItem 的旧行会被外键引用（python 默认不开外键，这里仍按顺序清）
    for table in ("MediaTag", "MediaCategory", "DiaryMedia", "DeletedMedia", "MediaItem"):
        try:
            conn.execute(f"DELETE FROM {table}")
        except sqlite3.OperationalError:
            pass
    conn.commit()
    return conn


def generate_rows(n):
    """固定随机种子 → 同一台机器上每次生成的数据完全一致，数字可比。"""
    rnd = random.Random(20261029)
    now = datetime(2026, 10, 29, 16, 0, 0)
    earliest = now - timedelta(days=1100)

    rows = []
    for i in range(1, n + 1):
        ext, media_type = rnd.choice(FORMATS)
        folder = rnd.randint(1, 40)
        name = "%s_%06d_%s.%s" % (rnd.choice(BRANDS), i, rnd.choice(SCENES), ext)
        path = "D:\\照片\\2024\\%02d\\%s" % (folder, name)
        taken = earliest + timedelta(seconds=rnd.randint(0, 1100 * 86400))
        stamp = taken.isoformat(timespec="seconds")
        rows.append((
            folder, path, name, ext, media_type,
            rnd.randint(200_000, 40_000_000), stamp, stamp,
            # Favorite / Rating：约 5% 收藏、评分 0-5
            1 if rnd.random() < 0.05 else 0, rnd.randint(0, 5),
        ))
    return rows


def bulk_insert(conn, rows, batch=5000):
    sql = """
        INSERT INTO MediaItem
            (RootFolderId, Path, FileName, Extension, MediaType, SizeBytes,
             DateAddedUtc, ModifiedAtUtc, Favorite, Rating, IsMissing, IsPrivate)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 0)
    """
    t0 = time.perf_counter()
    for start in range(0, len(rows), batch):
        conn.executemany(sql, rows[start:start + batch])
        conn.commit()
    return time.perf_counter() - t0


def rebuild_fts(conn):
    """照抄 MediaRepository.RebuildFtsAsync：delete-all 再 SELECT 回灌。
    不能直接用 'rebuild' —— MediaFts 的索引里有 Tags / Categories 两个不在
    MediaItem 里的虚拟列，rebuild 会报 `no such column: T.Tags`。"""
    t0 = time.perf_counter()
    conn.execute("INSERT INTO MediaFts(MediaFts) VALUES('delete-all')")
    conn.execute("""
        INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
        SELECT m.Id, m.FileName, m.Path,
          (SELECT group_concat(t.Name, ' ') FROM MediaTag mt JOIN Tag t ON t.Id=mt.TagId WHERE mt.MediaId=m.Id),
          (SELECT group_concat(c.Name, ' ') FROM MediaCategory mc JOIN Category c ON c.Id=mc.CategoryId WHERE mc.MediaId=m.Id)
        FROM MediaItem m
    """)
    conn.execute("INSERT INTO MediaFtsTrigram(MediaFtsTrigram) VALUES('delete-all')")
    conn.execute("INSERT INTO MediaFtsTrigram(rowid, FileName, Path) SELECT Id, FileName, Path FROM MediaItem")
    conn.commit()
    return time.perf_counter() - t0


# 全部抄自 MediaRepository / MediaFilter 的真实语句，不是另编的
QUERIES = [
    ("列表首页 100 条",
     "SELECT m.* FROM MediaItem m WHERE m.IsMissing=0 AND m.IsPrivate=0 "
     "ORDER BY m.DateAddedUtc DESC, m.Id DESC LIMIT 100 OFFSET 0", ()),

    ("深翻页 OFFSET 50000",
     "SELECT m.* FROM MediaItem m WHERE m.IsMissing=0 AND m.IsPrivate=0 "
     "ORDER BY m.DateAddedUtc DESC, m.Id DESC LIMIT 100 OFFSET 50000", ()),

    ("总数统计",
     "SELECT COUNT(*) FROM MediaItem m WHERE m.IsMissing=0 AND m.IsPrivate=0", ()),

    ("按名称排序首页",
     "SELECT m.* FROM MediaItem m WHERE m.IsMissing=0 AND m.IsPrivate=0 "
     "ORDER BY m.FileName COLLATE NOCASE ASC, m.Id DESC LIMIT 100 OFFSET 0", ()),

    ("FTS 关键词搜索「海边」",
     "SELECT m.Id FROM MediaItem m WHERE m.Id IN "
     "(SELECT rowid FROM MediaFts WHERE MediaFts MATCH ?) LIMIT 100", ('"海边"',)),

    ("trigram 子串搜索「005000」",
     "SELECT m.Id FROM MediaItem m WHERE m.Id IN "
     "(SELECT rowid FROM MediaFtsTrigram WHERE MediaFtsTrigram MATCH ?) LIMIT 100", ('"005000"',)),

    ("按文件夹分组计数",
     "SELECT RootFolderId, COUNT(*) FROM MediaItem WHERE IsMissing=0 GROUP BY RootFolderId", ()),
]


def timeit(conn, sql, params=(), runs=30):
    samples = []
    for _ in range(runs):
        t0 = time.perf_counter()
        conn.execute(sql, params).fetchall()
        samples.append((time.perf_counter() - t0) * 1000)
    samples.sort()
    return {
        "p50_ms": round(samples[len(samples) // 2], 3),
        "p95_ms": round(samples[min(len(samples) - 1, int(len(samples) * 0.95))], 3),
        "min_ms": round(samples[0], 3),
        "max_ms": round(samples[-1], 3),
    }


def main():
    argv = sys.argv[1:]
    keep = "--keep" in argv
    explicit_db = None
    if "--db" in argv:
        idx = argv.index("--db")
        if idx + 1 < len(argv):
            explicit_db = argv[idx + 1]
            argv = argv[:idx] + argv[idx + 2:]

    args = [a for a in argv if not a.startswith("--")]
    count = int(args[0]) if args else 100_000

    source = find_source_db(explicit_db)
    if source is None:
        print("✗ 找不到源数据库。先运行一次程序（或 dotnet build 后跑 Debug 版）让它建库；"
              "也可以用 --db <路径> 指定。")
        return 1

    print("源库 : %s" % source)
    print("工作库: %s（副本，不碰真实数据）" % WORK)
    print("条数 : %d" % count)

    conn = prepare_work_db(source)
    rows = generate_rows(count)

    insert_s = bulk_insert(conn, rows)
    print("灌入 : %.2f 秒（%.0f 条/秒）" % (insert_s, count / max(insert_s, 1e-6)))

    fts_s = rebuild_fts(conn)
    print("建索引: %.2f 秒" % fts_s)

    db_mb = os.path.getsize(WORK) / 1024 / 1024
    print("库体积: %.1f MB\n" % db_mb)

    results = []
    print("%-26s %10s %10s %10s" % ("查询", "p50(ms)", "p95(ms)", "max(ms)"))
    print("-" * 60)
    for name, sql, params in QUERIES:
        stat = timeit(conn, sql, params)
        stat["name"] = name
        results.append(stat)
        print("%-26s %10.3f %10.3f %10.3f"
              % (name, stat["p50_ms"], stat["p95_ms"], stat["max_ms"]))

    payload = {
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "rows": count,
        "db_size_mb": round(db_mb, 1),
        "insert_seconds": round(insert_s, 2),
        "fts_rebuild_seconds": round(fts_s, 2),
        "python": sys.version.split()[0],
        "sqlite": sqlite3.sqlite_version,
        "queries": results,
        "note": "仅 SQL 层，不含 WPF 渲染 / 缩略图解码 / 内存峰值",
    }
    with open(RESULT, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
    print("\n已写入 %s" % RESULT)

    conn.close()
    if keep:
        print("工作库保留在 %s" % WORK)
    else:
        for suffix in ("", "-wal", "-shm"):
            try:
                os.remove(WORK + suffix)
            except OSError:
                pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
