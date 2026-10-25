using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// v1.3.3「清理误入库的系统垃圾文件」一次性迁移。
///
/// 老版本的扫描器不过滤隐藏/系统属性，还把 .ini 当文本文档，
/// 于是每个目录的 desktop.ini 都进了图库，显示成一堆文档图标。
/// 这里守住两条：垃圾记录必须被清掉，正常媒体一条不能少。
/// </summary>
public class JunkMediaPurgeTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly IDbConnectionFactory _factory;
    private readonly string _tempDir;

    public JunkMediaPurgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerJunkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var detector = new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPortableModeDetector>(detector);
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMediaRepository, MediaRepository>();

        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
        _repo = _provider.GetRequiredService<IMediaRepository>();
        _factory = _provider.GetRequiredService<IDbConnectionFactory>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task PurgeRemovesDesktopIni_ButKeepsRealMedia()
    {
        var photo = await SeedAsync("trip.jpg", "jpg", MediaType.Image);
        var ini = await SeedAsync("desktop.ini", "ini", MediaType.Text);
        var thumbs = await SeedAsync("Thumbs.db", "db", MediaType.Text);

        // 抹掉台账，模拟老库第一次升级到本版本
        Exec($"DELETE FROM SchemaMigration WHERE Name = '{DatabaseInitializer.JunkMediaPurgeMigration}';");
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();

        Assert.NotNull(await _repo.GetByIdAsync(photo));
        Assert.Null(await _repo.GetByIdAsync(ini));
        Assert.Null(await _repo.GetByIdAsync(thumbs));

        Assert.Equal(1, CountRows(
            $"SELECT COUNT(*) FROM SchemaMigration WHERE Name = '{DatabaseInitializer.JunkMediaPurgeMigration}';"));
    }

    [Fact]
    public async Task PurgeShouldBeIdempotent_AndNotRunTwice()
    {
        await SeedAsync("desktop.ini", "ini", MediaType.Text);

        // 第一次清完并记台账
        Exec($"DELETE FROM SchemaMigration WHERE Name = '{DatabaseInitializer.JunkMediaPurgeMigration}';");
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
        Assert.Equal(0, CountRows("SELECT COUNT(*) FROM MediaItem;"));

        // 之后手动再塞一条垃圾：台账已在，迁移不会重跑（等下次触发重扫时由扫描器拦掉）
        await SeedAsync("desktop.ini", "ini", MediaType.Text);
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM MediaItem;"));
    }

    [Fact]
    public async Task FreshDatabase_MarksPurgeAppliedWithoutTouchingData()
    {
        var photo = await SeedAsync("a.png", "png", MediaType.Image);

        Assert.NotNull(await _repo.GetByIdAsync(photo));
        Assert.Equal(1, CountRows(
            $"SELECT COUNT(*) FROM SchemaMigration WHERE Name = '{DatabaseInitializer.JunkMediaPurgeMigration}';"));
    }

    // ---------- 辅助 ----------

    private async Task<long> SeedAsync(string fileName, string extension, MediaType type)
    {
        var rootId = await _repo.AddRootFolderAsync(@"C:\temp\junk", null, null);
        return await _repo.UpsertMediaAsync(new MediaItem
        {
            RootFolderId = rootId,
            Path = @"C:\temp\junk\" + fileName,
            FileName = fileName,
            Extension = extension,
            MediaType = type,
            SizeBytes = 128,
            DateAddedUtc = DateTime.UtcNow.ToString("O"),
        });
    }

    private int CountRows(string sql)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private void Exec(string sql)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
