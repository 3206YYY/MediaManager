namespace MediaManager.Core.Services;

/// <summary>
/// 一个自动备份快照（<c>library.db.bak.N</c>）。
/// 时间用本地时间：用户在界面上看到的就是"这台电脑上的几点几分"。
/// </summary>
public sealed record DatabaseSnapshot(string Path, int Index, DateTime ModifiedLocal, long SizeBytes)
{
    /// <summary>完整时间（秒级），界面与确认弹窗都用这个格式。</summary>
    public string TimeText => ModifiedLocal.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>相对时间描述，例如「3 分钟前」「昨天 14:30」，让人一眼判断新旧。</summary>
    public string RelativeText
    {
        get
        {
            var span = DateTime.Now - ModifiedLocal;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 2) return "昨天";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return "更早";
        }
    }

    /// <summary>文件大小，KB/MB 自适应。</summary>
    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
    };

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>恢复结果。<see cref="RolledBack"/> 表示失败后已经用安全副本还原回原库。</summary>
public sealed record DatabaseRestoreResult(bool Success, bool RolledBack, string? Error)
{
    public static DatabaseRestoreResult Ok() => new(true, false, null);
    public static DatabaseRestoreResult Fail(string error, bool rolledBack = false) => new(false, rolledBack, error);
}

/// <summary>
/// 历史快照（时光机）：列出自动备份节点、校验节点可用性、把库整体退回某个节点。
///
/// 实现约定（改之前请先读）：
/// 1. 恢复前必须先把当前库复制成 <c>library.before_restore.db</c>（后悔药）；
/// 2. 覆盖前必须清掉 SQLite 连接池与 <c>-wal</c>/<c>-shm</c>，
///    否则旧 WAL 里的帧会被套到新库上，直接损坏数据；
/// 3. 覆盖后要校验新库能打开、完整（quick_check），失败立刻回滚。
/// </summary>
public interface IDatabaseSnapshotService
{
    /// <summary>当前正在使用的库文件路径。</summary>
    string DatabasePath { get; }

    /// <summary>快照所在目录（跟随「备份目录」设置）。</summary>
    string BackupDirectory { get; }

    /// <summary>恢复前的安全副本路径（<c>library.before_restore.db</c>）。</summary>
    string SafetyCopyPath { get; }

    /// <summary>列出所有可用快照，按时间从新到旧。</summary>
    IReadOnlyList<DatabaseSnapshot> ListSnapshots();

    /// <summary>校验快照文件是否是一个完整可用的数据库。不合格的节点不允许用来恢复。</summary>
    bool VerifySnapshot(string snapshotPath, out string? error);

    /// <summary>把库整体恢复到指定快照。返回结果，不抛异常（异常也包装成 Fail）。</summary>
    Task<DatabaseRestoreResult> RestoreAsync(string snapshotPath, IProgress<string>? progress = null);

    /// <summary>用安全副本把库还原回去（恢复失败时兜底）。</summary>
    Task<bool> RollbackFromSafetyCopyAsync();
}
