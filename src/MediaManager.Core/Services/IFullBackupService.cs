namespace MediaManager.Core.Services;

/// <summary>完整备份 / 恢复的结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
/// <param name="VaultFiles">打包 / 还原的保险箱文件数。</param>
/// <param name="MissingFiles">恢复时在包里找不到的保险箱文件数（导出前就丢了）。</param>
public sealed record FullBackupResult(
    bool Success,
    string? Error,
    int VaultFiles = 0,
    int MissingFiles = 0)
{
    public static FullBackupResult Ok(int vaultFiles, int missingFiles = 0)
        => new(true, null, vaultFiles, missingFiles);

    public static FullBackupResult Fail(string error) => new(false, error);
}

/// <summary>
/// 完整备份：数据库 + 私密空间文件 打成一个包。
///
/// 为什么不是两份东西分开拷：
/// <list type="bullet">
/// <item>日记、收藏、标签这些都在数据库里，导出库就能带走；</item>
/// <item>但私密空间的**文件**被挪进了数据目录下的 Vault 文件夹 —— 只导出数据库，
/// 拿到新电脑上日记和库记录都在，保险箱里的照片却丢了，这是最坑的一种「看起来备份成功了」。</item>
/// </list>
/// 包里同时带一份清单（媒体 id → 包内文件），恢复时按当前数据目录重写路径，
/// 所以换电脑、换数据目录都能复原。
/// </summary>
public interface IFullBackupService
{
    /// <summary>
    /// 导出完整备份到指定 .mmbak 文件（本质是 zip）。
    /// 内容：library.db（一致性快照）+ 私密空间全部文件 + 清单。
    /// </summary>
    Task<FullBackupResult> ExportAsync(string zipPath, IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 从 .mmbak 恢复：覆盖当前数据库，把私密空间文件放回当前数据目录的 Vault，
    /// 并把库里的私密路径改写成新位置。恢复前会先自动备份当前库兜底。
    /// </summary>
    Task<FullBackupResult> ImportAsync(string zipPath, IProgress<string>? progress = null,
        CancellationToken ct = default);
}
