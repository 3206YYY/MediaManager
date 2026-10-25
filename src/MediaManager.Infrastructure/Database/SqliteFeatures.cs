namespace MediaManager.Infrastructure.Database;

/// <summary>当前 SQLite 引擎支持的可选能力（启动时探测一次）。</summary>
public static class SqliteFeatures
{
    /// <summary>
    /// 是否成功创建了 FTS5 trigram 索引（需要 SQLite 3.34+）。
    /// 不支持时子串搜索自动退回 <c>instr</c> 全表扫描，功能不受影响。
    /// </summary>
    public static bool TrigramFts { get; internal set; }
}
