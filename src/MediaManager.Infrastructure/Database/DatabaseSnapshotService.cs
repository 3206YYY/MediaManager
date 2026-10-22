using MediaManager.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>
/// 历史快照（时光机）：把 <c>library.db</c> 整体退回某个 <c>library.db.bak.N</c>。
///
/// ⚠ 这是本项目里最危险的一段代码，改动前务必理解顺序：
///   1. 校验快照（quick_check）——不拿坏文件去覆盖好库；
///   2. 停掉使用方（调用方负责：取消扫描/缩略图、清空连接池），这里只做数据库自己的收尾；
///   3. 对当前库做 wal_checkpoint，让 -wal 里的数据落进主文件（否则复制出来的安全副本是旧的）；
///   4. 复制当前库 → library.before_restore.db；
///   5. 删掉 -wal / -shm —— **必须先删再覆盖**，否则旧 WAL 的帧会被套到新库上；
///   6. File.Copy 覆盖 library.db；
///   7. 重新打开校验；不合格就用安全副本回滚。
/// 整个过程不重试、不并发，串行做完再让调用方重启程序。
/// </summary>
public class DatabaseSnapshotService : IDatabaseSnapshotService
{
    /// <summary>自动备份保留份数，和 DatabaseBackupService 保持一致。</summary>
    private const int MaxSnapshots = 7;

    private readonly IDbConnectionFactory _dbFactory;
    private readonly IDatabaseBackupService _backup;
    private readonly ILogger<DatabaseSnapshotService> _logger;

    public DatabaseSnapshotService(
        IDbConnectionFactory dbFactory,
        IDatabaseBackupService backup,
        ILogger<DatabaseSnapshotService> logger)
    {
        _dbFactory = dbFactory;
        _backup = backup;
        _logger = logger;
    }

    public string DatabasePath => _dbFactory.DatabasePath;

    public string BackupDirectory => _backup.BackupDirectory;

    public string SafetyCopyPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath) ?? BackupDirectory,
            "library.before_restore.db");

    public IReadOnlyList<DatabaseSnapshot> ListSnapshots()
    {
        var list = new List<DatabaseSnapshot>();
        try
        {
            var dir = BackupDirectory;
            if (!System.IO.Directory.Exists(dir)) return list;

            // 不写死 7：早期版本可能留下更多文件，多认几个不会出错（界面只显示存在的）
            for (var i = 1; i <= MaxSnapshots + 8; i++)
            {
                var path = System.IO.Path.Combine(dir, $"library.db.bak.{i}");
                if (!System.IO.File.Exists(path)) continue;

                try
                {
                    var info = new System.IO.FileInfo(path);
                    if (info.Length <= 0) continue;
                    list.Add(new DatabaseSnapshot(path, i, info.LastWriteTime, info.Length));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取快照信息失败：{Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "列举历史快照失败");
        }

        // 时间从新到旧；时间相同按序号小的优先（bak.1 最新）
        return list
            .OrderByDescending(s => s.ModifiedLocal)
            .ThenBy(s => s.Index)
            .ToList();
    }

    public bool VerifySnapshot(string snapshotPath, out string? error)
    {
        error = null;
        if (!System.IO.File.Exists(snapshotPath))
        {
            error = "备份文件不存在";
            return false;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                error = $"备份文件校验未通过（{result}）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "备份文件无法打开：" + ex.Message;
            return false;
        }
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(string snapshotPath, IProgress<string>? progress = null)
    {
        // 文件操作 + SQLite 校验全部丢到线程池：UI 线程只负责画遮罩，不能卡
        return await Task.Run(() => RestoreSync(snapshotPath, progress));
    }

    private DatabaseRestoreResult RestoreSync(string snapshotPath, IProgress<string>? progress)
    {
        var dbPath = DatabasePath;

        // ---- 1) 先确认这个节点是好的，坏文件绝不覆盖好库 ----
        progress?.Report("校验备份文件…");
        if (!VerifySnapshot(snapshotPath, out var verifyError))
        {
            _logger.LogWarning("拒绝恢复：{Reason}", verifyError);
            return DatabaseRestoreResult.Fail(verifyError ?? "备份文件不可用");
        }

        if (!System.IO.File.Exists(dbPath))
            return DatabaseRestoreResult.Fail("当前数据库文件不存在，无法继续");

        try
        {
            // ---- 2) 让 -wal 落盘，保证安全副本是"最新状态" ----
            progress?.Report("保存当前状态…");
            CheckpointCurrentDatabase();
            SqliteConnection.ClearAllPools();

            // ---- 3) 后悔药：复制当前库 ----
            System.IO.File.Copy(dbPath, SafetyCopyPath, overwrite: true);
            _logger.LogInformation("恢复前已保存当前数据库副本：{Path}", SafetyCopyPath);

            // ---- 4) 覆盖：先把边车文件删干净，再复制 ----
            progress?.Report("写入备份数据…");
            DeleteSidecarFiles(dbPath);

            System.IO.File.Copy(snapshotPath, dbPath, overwrite: true);
            // 快照是 DELETE 日志模式备份出来的；目标库旁若残留旧 -wal，SQLite 会当成自己的日志应用
            DeleteSidecarFiles(dbPath);

            // ---- 5) 校验恢复结果 ----
            progress?.Report("校验恢复结果…");
            if (VerifySnapshot(dbPath, out var afterError))
            {
                _logger.LogInformation("数据库已恢复到快照 {Snapshot}", snapshotPath);
                return DatabaseRestoreResult.Ok();
            }

            _logger.LogError("恢复后校验失败：{Error}，开始回滚", afterError);
            progress?.Report("恢复异常，正在回滚…");
            var rolledBack = RollbackSync();
            return DatabaseRestoreResult.Fail(afterError ?? "恢复后的数据库校验失败", rolledBack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复数据库失败");
            // 走到这里文件可能只写了一半 → 必须回滚，否则下次启动就是坏库
            var rolledBack = RollbackSync();
            return DatabaseRestoreResult.Fail(ex.Message, rolledBack);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    public async Task<bool> RollbackFromSafetyCopyAsync()
        => await Task.Run(RollbackSync);

    private bool RollbackSync()
    {
        try
        {
            if (!System.IO.File.Exists(SafetyCopyPath))
            {
                _logger.LogError("找不到安全副本，无法回滚：{Path}", SafetyCopyPath);
                return false;
            }

            SqliteConnection.ClearAllPools();
            DeleteSidecarFiles(DatabasePath);
            System.IO.File.Copy(SafetyCopyPath, DatabasePath, overwrite: true);
            DeleteSidecarFiles(DatabasePath);

            var ok = VerifySnapshot(DatabasePath, out var error);
            if (ok) _logger.LogInformation("已从安全副本回滚数据库");
            else _logger.LogError("回滚后校验仍失败：{Error}", error);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从安全副本回滚失败");
            return false;
        }
    }

    /// <summary>把 WAL 合并回主文件。失败只记日志——后面复制安全副本仍然可用，只是可能少最后几条。</summary>
    private void CheckpointCurrentDatabase()
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 3000; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复前 checkpoint 失败（继续，用现有文件做安全副本）");
        }
    }

    private void DeleteSidecarFiles(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = dbPath + suffix;
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                // 删不掉就抛出去：带着旧 WAL 覆盖是明确的数据损坏风险，宁可不恢复
                _logger.LogError(ex, "删除边车文件失败：{Path}", path);
                throw;
            }
        }
    }
}
