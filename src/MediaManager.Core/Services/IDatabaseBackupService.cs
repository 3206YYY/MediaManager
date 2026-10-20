namespace MediaManager.Core.Services;

/// <summary>数据库自动备份服务。</summary>
public interface IDatabaseBackupService
{
    /// <summary>数据目录（含备份文件）。</summary>
    string DataDirectory { get; }

    /// <summary>备份目录（即数据目录）。</summary>
    string BackupDirectory { get; }

    /// <summary>上次备份时间（本地时间），未备份过返回 null。</summary>
    DateTime? LastBackupTime { get; }

    /// <summary>执行一次备份（循环保留最近 7 份）。异步、不抛异常。</summary>
    Task BackupAsync();

    /// <summary>导出数据库到指定文件（如 U 盘），用于换机迁移。返回导出的完整路径。</summary>
    Task<string> ExportAsync(string destinationPath);

    /// <summary>从指定备份文件恢复数据库（覆盖当前数据）。异步，失败抛异常。</summary>
    Task ImportAsync(string sourcePath);
}
