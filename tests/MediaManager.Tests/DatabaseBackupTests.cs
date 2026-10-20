using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 数据安全防线测试：导出文件必须「单文件即完整」（不依赖 -wal 边车文件），
/// 且导出 → 导入 能真正把数据恢复回来。
/// </summary>
public class DatabaseBackupTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly DatabaseBackupService _backup;
    private readonly string _tempDir;

    public DatabaseBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var detector = new PortableModeDetector(
            NullLogger<PortableModeDetector>.Instance, _tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPortableModeDetector>(detector);
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMediaRepository, MediaRepository>();
        services.AddSingleton(sp => new DatabaseBackupService(
            sp.GetRequiredService<IDbConnectionFactory>(),
            detector,
            sp.GetRequiredService<IMediaRepository>(),
            NullLogger<DatabaseBackupService>.Instance));

        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
        _repo = _provider.GetRequiredService<IMediaRepository>();
        _backup = _provider.GetRequiredService<DatabaseBackupService>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Export_ShouldProduceSelfContainedFile_WithoutWalSidecars()
    {
        await _repo.AddCategoryAsync(new Category { Name = "风景" });

        var dest = Path.Combine(_tempDir, "export", "backup.mmdb");
        await _backup.ExportAsync(dest);

        Assert.True(File.Exists(dest));
        // 关键：不能留下 -wal / -shm，否则用户只拷 .mmdb 会丢数据
        Assert.False(File.Exists(dest + "-wal"));
        Assert.False(File.Exists(dest + "-shm"));

        // 直接用另一个连接打开导出文件，确认数据真的在里面
        using var conn = new SqliteConnection($"Data Source={dest};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Category WHERE Name='风景'";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ExportThenImport_ShouldRestoreData()
    {
        await _repo.AddCategoryAsync(new Category { Name = "备份前分类" });

        var dest = Path.Combine(_tempDir, "backup_to_usb.mmdb");
        await _backup.ExportAsync(dest);

        // 模拟在新电脑上继续改动，然后从备份恢复
        await _repo.AddCategoryAsync(new Category { Name = "后来新增的分类" });
        Assert.Equal(2, (await _repo.GetCategoriesAsync()).Count);

        await _backup.ImportAsync(dest);

        // 导入会覆盖当前库：只剩备份里的那一条
        var categories = await _repo.GetCategoriesAsync();
        Assert.Single(categories);
        Assert.Equal("备份前分类", categories[0].Name);
    }

    [Fact]
    public async Task Backup_ShouldCreateRotatingSnapshots()
    {
        await _repo.AddCategoryAsync(new Category { Name = "分类A" });

        await _backup.BackupAsync();
        await _backup.BackupAsync();

        var bak1 = Path.Combine(_tempDir, "library.db.bak.1");
        Assert.True(File.Exists(bak1));
        Assert.False(File.Exists(bak1 + "-wal"));

        // 备份文件应能被独立打开，且包含数据
        using var conn = new SqliteConnection($"Data Source={bak1};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Category";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
