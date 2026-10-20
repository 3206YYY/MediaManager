using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>数据库自动备份：启动时把 library.db 复制为 library.db.bak.N，循环保留最近 7 份。</summary>
public class DatabaseBackupService : IDatabaseBackupService
{
    private const int MaxBackups = 7;

    private readonly IDbConnectionFactory _dbFactory;
    private readonly IPortableModeDetector _detector;
    private readonly IMediaRepository _repo;
    private readonly ILogger<DatabaseBackupService> _logger;

    public DatabaseBackupService(
        IDbConnectionFactory dbFactory,
        IPortableModeDetector detector,
        IMediaRepository repo,
        ILogger<DatabaseBackupService> logger)
    {
        _dbFactory = dbFactory;
        _detector = detector;
        _repo = repo;
        _logger = logger;
    }

    public string DataDirectory => _detector.DataDirectory;

    /// <summary>备份目录：用户配置的「BackupDirectory」设置优先，未配置则用数据目录。</summary>
    public string BackupDirectory
    {
        get
        {
            var configured = ReadConfiguredDirectory();
            return string.IsNullOrWhiteSpace(configured) ? _detector.DataDirectory : configured;
        }
    }

    /// <summary>读取用户配置的备份目录（可能为空 = 未配置）。异常时返回 null。</summary>
    private string? ReadConfiguredDirectory()
    {
        try
        {
            return _repo.GetSettingAsync("BackupDirectory").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取备份目录设置失败");
            return null;
        }
    }

    /// <summary>上次备份时间：取最新的 library.db.bak.N 的写入时间。</summary>
    public DateTime? LastBackupTime
    {
        get
        {
            try
            {
                var dir = BackupDirectory;
                if (!System.IO.Directory.Exists(dir)) return null;

                DateTime? latest = null;
                for (int i = 1; i <= MaxBackups; i++)
                {
                    var path = System.IO.Path.Combine(dir, $"library.db.bak.{i}");
                    if (System.IO.File.Exists(path))
                    {
                        var t = System.IO.File.GetLastWriteTime(path);
                        if (latest == null || t > latest) latest = t;
                    }
                }
                return latest;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>执行一次备份（循环覆盖最近 7 份）。异常不抛出，仅记录日志。</summary>
    public async Task BackupAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Run(() => BackupSync());
        }
        catch (Exception ex)
        {
            // U 盘 / 移动硬盘没插时不该报「错误」刷屏，说明跳过即可
            if (MediaManager.Core.Diagnostics.StorageErrors.IsDeviceUnavailable(ex))
                _logger.LogWarning("数据库备份跳过：备份目录所在设备不可用（可能是 U 盘 / 移动硬盘未连接或已拔出）");
            else
                _logger.LogWarning(ex, "数据库备份失败");
        }
    }

    private void BackupSync()
    {
        var source = _dbFactory.DatabasePath;
        if (!System.IO.File.Exists(source))
        {
            _logger.LogInformation("数据库文件不存在，跳过备份：{Path}", source);
            return;
        }

        var dir = BackupDirectory;
        System.IO.Directory.CreateDirectory(dir);

        // 循环：bak.7 删除，bak.6 -> bak.7，...，bak.1 -> bak.2，源 -> bak.1
        var bak7 = System.IO.Path.Combine(dir, $"library.db.bak.{MaxBackups}");
        if (System.IO.File.Exists(bak7))
            System.IO.File.Delete(bak7);

        for (int i = MaxBackups - 1; i >= 1; i--)
        {
            var from = System.IO.Path.Combine(dir, $"library.db.bak.{i}");
            var to = System.IO.Path.Combine(dir, $"library.db.bak.{i + 1}");
            if (System.IO.File.Exists(from))
                System.IO.File.Move(from, to, overwrite: true);
        }

        // 使用 SQLite 在线备份 API，保证一致性（即使有连接正在写）
        var target = System.IO.Path.Combine(dir, "library.db.bak.1");
        using (var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={source};Mode=ReadWrite;Cache=Shared"))
        using (var targetConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target}"))
        {
            sourceConn.Open();
            targetConn.Open();
            sourceConn.BackupDatabase(targetConn);
        }

        _logger.LogInformation("数据库已备份到 {Path}", target);

        // 让快照自包含，避免只拷贝单个 .bak 文件时丢失还在 -wal 里的数据
        NormalizeSnapshot(target);
    }

    /// <summary>
    /// 把快照文件从 WAL 模式归一到 DELETE 模式：先把 -wal 合并回主文件，再清理边车文件。
    /// 这样用户只拷贝单个 .db / .mmdb 文件到新电脑时，数据是完整的。
    /// </summary>
    private static void NormalizeSnapshot(string dbPath)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 归一失败不影响快照本身（主文件仍是完整备份），忽略
        }
        finally
        {
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                try
                {
                    var side = dbPath + suffix;
                    if (System.IO.File.Exists(side) && new System.IO.FileInfo(side).Length == 0)
                        System.IO.File.Delete(side);
                }
                catch { /* 边车文件清理失败不影响数据 */ }
            }
        }
    }

    /// <summary>导出数据库到指定文件（如 U 盘），用 SQLite 在线备份保证一致性。</summary>
    public async Task<string> ExportAsync(string destinationPath)
    {
        return await System.Threading.Tasks.Task.Run(() => ExportSync(destinationPath));
    }

    private string ExportSync(string destinationPath)
    {
        var source = _dbFactory.DatabasePath;
        if (!System.IO.File.Exists(source))
            throw new System.IO.FileNotFoundException("数据库文件不存在", source);

        var dir = System.IO.Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        using (var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={source};Mode=ReadWrite;Cache=Shared"))
        using (var targetConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destinationPath}"))
        {
            sourceConn.Open();
            targetConn.Open();
            sourceConn.BackupDatabase(targetConn);
        }

        // 导出的文件必须「单文件即完整」：把 WAL 合并回主文件、删掉边车文件，
        // 否则用户从 U 盘只拷走 .mmdb 时，可能丢掉还在 -wal 里的最新数据。
        NormalizeSnapshot(destinationPath);

        _logger.LogInformation("数据库已导出到 {Path}", destinationPath);
        return destinationPath;    }

    /// <summary>从备份文件恢复数据库（反向在线备份，覆盖当前数据）。恢复前先备份当前库兜底。</summary>
    public async Task ImportAsync(string sourcePath)
    {
        await System.Threading.Tasks.Task.Run(() => ImportSync(sourcePath));
    }

    private void ImportSync(string sourcePath)
    {
        if (!System.IO.File.Exists(sourcePath))
            throw new System.IO.FileNotFoundException("备份文件不存在", sourcePath);

        // 先备份当前库，作为恢复前的安全兜底
        try
        {
            BackupSync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复前备份当前库失败，继续导入");
        }

        var target = _dbFactory.DatabasePath;

        // 反向备份：把 source（备份文件）完整恢复到 target（当前库）
        using (var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={sourcePath};Mode=ReadOnly"))
        using (var targetConn = new Microsoft.Data.Sqlite.SqliteConnection(
                   $"Data Source={target};Mode=ReadWrite;Cache=Shared"))
        {
            sourceConn.Open();
            targetConn.Open();
            sourceConn.BackupDatabase(targetConn);
        }

        _logger.LogInformation("数据库已从 {Path} 恢复", sourcePath);
    }
}
