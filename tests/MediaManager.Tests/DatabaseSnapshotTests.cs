using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 历史快照（时光机）测试。
/// 重点不是"能回退"，而是**回退失败时不会把好数据搞坏**：
/// 坏节点必须被拒绝、覆盖前必须有安全副本、覆盖后必须校验。
/// </summary>
public class DatabaseSnapshotTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly DatabaseBackupService _backup;
    private readonly DatabaseSnapshotService _snapshots;
    private readonly string _tempDir;

    public DatabaseSnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerSnapshotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var detector = new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir);

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
        services.AddSingleton<IDatabaseBackupService>(sp => sp.GetRequiredService<DatabaseBackupService>());
        services.AddSingleton<IDatabaseSnapshotService>(sp => new DatabaseSnapshotService(
            sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<IDatabaseBackupService>(),
            NullLogger<DatabaseSnapshotService>.Instance));

        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
        _repo = _provider.GetRequiredService<IMediaRepository>();
        _backup = (DatabaseBackupService)_provider.GetRequiredService<IDatabaseBackupService>();
        _snapshots = (DatabaseSnapshotService)_provider.GetRequiredService<IDatabaseSnapshotService>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* 清理失败忽略 */ }
    }

    // ---------------------------------------------------------------- 列表

    [Fact]
    public async Task ListSnapshots_ShouldBeNewestFirst_AndSkipMissingNumbers()
    {
        await _repo.AddCategoryAsync(new Category { Name = "A" });
        await _backup.BackupAsync();   // -> bak.1
        await Task.Delay(20);
        await _backup.BackupAsync();   // bak.1 -> bak.2，新 bak.1
        await Task.Delay(20);
        await _backup.BackupAsync();   // 再滚一次 -> bak.1/2/3

        // 手工改写入时间，模拟"三天前捕的快照"
        File.SetLastWriteTime(Path.Combine(_tempDir, "library.db.bak.3"), DateTime.Now.AddDays(-3));

        var list = _snapshots.ListSnapshots();

        Assert.Equal(3, list.Count);
        Assert.True(list[0].ModifiedLocal >= list[1].ModifiedLocal);
        Assert.True(list[1].ModifiedLocal >= list[2].ModifiedLocal);
        // 最旧那份被我们改成了三天前
        Assert.Equal(3, list[^1].Index);
        Assert.True(list[0].SizeBytes > 0);
    }

    [Fact]
    public void ListSnapshots_ShouldBeEmpty_WhenNoBackupsExist()
        => Assert.Empty(_snapshots.ListSnapshots());

    // ---------------------------------------------------------------- 校验

    [Fact]
    public void VerifySnapshot_ShouldRejectGarbageFile()
    {
        var fake = Path.Combine(_tempDir, "library.db.bak.1");
        File.WriteAllText(fake, "this is definitely not a sqlite database");

        Assert.False(_snapshots.VerifySnapshot(fake, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void VerifySnapshot_ShouldRejectMissingFile()
        => Assert.False(_snapshots.VerifySnapshot(Path.Combine(_tempDir, "nope.db"), out _));

    // ---------------------------------------------------------------- 回退

    [Fact]
    public async Task Restore_ShouldRevertData_AndKeepSafetyCopy()
    {
        await _repo.AddCategoryAsync(new Category { Name = "备份时就有" });
        await _backup.BackupAsync();                       // 节点：只有「备份时就有」

        await _repo.AddCategoryAsync(new Category { Name = "备份后才加" });
        Assert.Equal(2, (await _repo.GetCategoriesAsync()).Count);

        var snapshot = _snapshots.ListSnapshots().First();
        Assert.True(_snapshots.VerifySnapshot(snapshot.Path, out _));

        var result = await _snapshots.RestoreAsync(snapshot.Path);
        Assert.True(result.Success, result.Error);

        // 库回到快照那一刻：只剩一条
        SqliteConnection.ClearAllPools();
        var categories = await _repo.GetCategoriesAsync();
        Assert.Single(categories);
        Assert.Equal("备份时就有", categories[0].Name);

        // 后悔药必须在：里面是"恢复前"的两条数据
        Assert.True(File.Exists(_snapshots.SafetyCopyPath));
        using var conn = new SqliteConnection($"Data Source={_snapshots.SafetyCopyPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Category";
        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));

        // 覆盖后不能留下旧 WAL / SHM，否则下次启动会把旧帧套到新库上
        Assert.False(File.Exists(_snapshots.DatabasePath + "-wal"));
        Assert.False(File.Exists(_snapshots.DatabasePath + "-shm"));
    }

    [Fact]
    public async Task Restore_ShouldRefuseCorruptSnapshot_AndLeaveDataUntouched()
    {
        await _repo.AddCategoryAsync(new Category { Name = "重要数据" });
        await _backup.BackupAsync();

        var snapshot = _snapshots.ListSnapshots().First();
        // 备份服务用连接池打开过这个快照，池里的连接会占着文件句柄 → 先清池才能写坏它
        SqliteConnection.ClearAllPools();
        File.WriteAllText(snapshot.Path, "corrupted");

        var result = await _snapshots.RestoreAsync(snapshot.Path);

        Assert.False(result.Success);
        Assert.False(result.RolledBack);   // 连覆盖都没做，不需要回滚

        SqliteConnection.ClearAllPools();
        var categories = await _repo.GetCategoriesAsync();
        Assert.Single(categories);
        Assert.Equal("重要数据", categories[0].Name);
    }

    [Fact]
    public async Task RollbackFromSafetyCopy_ShouldRestorePreviousDatabase()
    {
        await _repo.AddCategoryAsync(new Category { Name = "原始" });
        await _backup.BackupAsync();
        var snapshot = _snapshots.ListSnapshots().First();

        // 先做一次正常恢复，生成安全副本（此时安全副本 == 原始数据）
        Assert.True((await _snapshots.RestoreAsync(snapshot.Path)).Success);

        await _repo.AddCategoryAsync(new Category { Name = "恢复之后又加的" });
        SqliteConnection.ClearAllPools();
        Assert.Equal(2, (await _repo.GetCategoriesAsync()).Count);

        // 手动回滚：应回到安全副本那一刻
        Assert.True(await _snapshots.RollbackFromSafetyCopyAsync());

        SqliteConnection.ClearAllPools();
        Assert.Single(await _repo.GetCategoriesAsync());
    }

    // ---------------------------------------------------------------- 展示字段

    [Fact]
    public void SnapshotDisplayFields_ShouldBeHumanReadable()
    {
        var snap = new DatabaseSnapshot(@"C:\x\library.db.bak.1", 1, new DateTime(2026, 10, 22, 14, 30, 0), 3_500_000);

        Assert.Equal("2026-10-22 14:30:00", snap.TimeText);
        Assert.Equal("3.3 MB", snap.SizeText);
        Assert.Equal("library.db.bak.1", snap.FileName);
        Assert.False(string.IsNullOrWhiteSpace(snap.RelativeText));
    }
}
