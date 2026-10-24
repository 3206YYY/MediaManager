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
/// v1.3.2「收藏夹并入分类」的一次性迁移。
///
/// 这几条断言就是这次重构的安全边界：老用户升级后，收藏夹里的东西必须一条不少地出现在
/// 同名分类下；已经有同名分类的不能建出重复的；重复启动不能重复搬。
/// </summary>
public class CollectionMergeMigrationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly IDbConnectionFactory _factory;
    private readonly string _tempDir;

    public CollectionMergeMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerMergeTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task LegacyCollections_ShouldBecomeTopLevelCategories_WithMediaKept()
    {
        var mediaId = await SeedMediaAsync(@"C:\temp\merge\trip.jpg");

        InsertLegacyCollection(1, "旅行", sortOrder: 1);
        InsertLegacyCollection(2, "家人", sortOrder: 2);
        LinkLegacy(mediaId, 1);

        Reinitialize();

        // 每个旧收藏夹 → 一个同名顶级分类
        var categories = await _repo.GetCategoriesAsync();
        var trip = Assert.Single(categories.Where(c => c.Name == "旅行"));
        var family = Assert.Single(categories.Where(c => c.Name == "家人"));
        Assert.Null(trip.ParentId);
        Assert.Null(family.ParentId);

        // 媒体归属搬过来了，原收藏夹 id 也还能映射
        Assert.Contains(trip.Id, await _repo.GetMediaCategoryIdsAsync(mediaId));
        Assert.Equal(new[] { trip.Id }, await _repo.MapLegacyCollectionIdsAsync(new[] { 1L }));

        // 旧表保留（数据不删，只是不再被使用）
        Assert.Equal(2, CountRows("SELECT COUNT(*) FROM Collection;"));
        Assert.Equal(1, CountRows("SELECT COUNT(*) FROM MediaCollection;"));
    }

    [Fact]
    public async Task ExistingCategoryWithSameName_ShouldBeReused_NotDuplicated()
    {
        var mediaId = await SeedMediaAsync(@"C:\temp\merge\sea.jpg");
        var existing = await _repo.AddCategoryAsync(new Category { Name = "旅行" });

        InsertLegacyCollection(7, "旅行");
        LinkLegacy(mediaId, 7);

        Reinitialize();

        // 一个名字只能有一个顶级分类：既没多建，媒体也落到了原来那个分类里
        var categories = await _repo.GetCategoriesAsync();
        Assert.Single(categories.Where(c => c.Name == "旅行"));
        Assert.Contains(existing, await _repo.GetMediaCategoryIdsAsync(mediaId));
        Assert.Equal(new[] { existing }, await _repo.MapLegacyCollectionIdsAsync(new[] { 7L }));
    }

    [Fact]
    public async Task Migration_ShouldBeIdempotent()
    {
        var mediaId = await SeedMediaAsync(@"C:\temp\merge\dog.jpg");
        InsertLegacyCollection(3, "宠物");
        LinkLegacy(mediaId, 3);

        Reinitialize();
        var firstRun = await _repo.GetCategoriesAsync();
        var pet = Assert.Single(firstRun.Where(c => c.Name == "宠物"));

        // 再启动两次：分类不会重复，映射关系不变
        Reinitialize();
        Reinitialize();

        var afterRuns = await _repo.GetCategoriesAsync();
        Assert.Single(afterRuns.Where(c => c.Name == "宠物"));
        Assert.Equal(new[] { pet.Id }, await _repo.MapLegacyCollectionIdsAsync(new[] { 3L }));
        Assert.Single(await _repo.GetMediaCategoryIdsAsync(mediaId));
    }

    [Fact]
    public async Task FreshDatabase_ShouldMarkMigrationApplied_WithoutCreatingCategories()
    {
        // 全新库/从没用过收藏夹：不该凭空造出分类，但台账要记上，避免每次启动都去扫
        Assert.Empty(await _repo.GetCategoriesAsync());
        Assert.Equal(1, CountRows(
            $"SELECT COUNT(*) FROM SchemaMigration WHERE Name = '{DatabaseInitializer.CollectionMergeMigration}';"));
    }

    [Fact]
    public async Task OrphanLegacyLink_ShouldNotBreakMigration()
    {
        // 指向「已经不存在的媒体」的残留关联行：老版本一旦关过 foreign_keys，
        // 删媒体时就会留下这种行。SQLite 的 OR IGNORE **不忽略外键冲突**，
        // 所以迁移必须先 JOIN MediaItem 把这些行滤掉，否则整批回滚、每次启动都失败。
        var mediaId = await SeedMediaAsync(@"C:\temp\merge\real.jpg");
        InsertLegacyCollection(11, "旧相册");
        LinkLegacy(mediaId, 11);
        Exec("PRAGMA foreign_keys=OFF; INSERT OR IGNORE INTO MediaCollection(MediaId, CollectionId) VALUES(999999, 11);");

        Reinitialize();

        // 迁移照常完成：正常那条归属在，孤儿行被丢掉，台账已记
        var album = Assert.Single((await _repo.GetCategoriesAsync()).Where(c => c.Name == "旧相册"));
        Assert.Equal(new[] { album.Id }, await _repo.GetMediaCategoryIdsAsync(mediaId));
        Assert.Equal(1, CountRows(
            $"SELECT COUNT(*) FROM SchemaMigration WHERE Name = '{DatabaseInitializer.CollectionMergeMigration}';"));
    }

    [Fact]
    public async Task EmptyLegacyCollection_ShouldStillMigrateCategory()
    {
        // 空收藏夹也要变成分类：用户建它是有意义的（马上要往里放东西），不能静默丢掉
        InsertLegacyCollection(9, "待整理");
        Reinitialize();

        var categories = await _repo.GetCategoriesAsync();
        Assert.Single(categories.Where(c => c.Name == "待整理"));
    }

    // ---------- 辅助 ----------

    private async Task<long> SeedMediaAsync(string path)
    {
        var rootId = await _repo.AddRootFolderAsync(@"C:\temp\merge", null, null);
        return await _repo.UpsertMediaAsync(new MediaItem
        {
            RootFolderId = rootId,
            Path = path,
            FileName = Path.GetFileName(path),
            Extension = ".jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1024,
            DateAddedUtc = DateTime.UtcNow.ToString("O"),
        });
    }

    private void InsertLegacyCollection(long id, string name, int sortOrder = 0)
    {
        Exec("INSERT INTO Collection(Id, Name, SortOrder, CreatedAtUtc) VALUES(@Id, @Name, @Sort, @At);",
            ("@Id", id), ("@Name", name), ("@Sort", sortOrder), ("@At", DateTime.UtcNow.ToString("O")));
    }

    private void LinkLegacy(long mediaId, long collectionId)
        => Exec("INSERT OR IGNORE INTO MediaCollection(MediaId, CollectionId) VALUES(@M, @C);",
            ("@M", mediaId), ("@C", collectionId));

    /// <summary>
    /// 抹掉迁移台账后再跑一次 Initialize，模拟「老库第一次升到 v1.3.2」。
    /// 只删台账、不动分类：这样第二次跑如果不去重，就会立刻多出一个同名分类，测得出问题。
    /// </summary>
    private void Reinitialize()
    {
        Exec($"DELETE FROM SchemaMigration WHERE Name = '{DatabaseInitializer.CollectionMergeMigration}';");
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();
    }

    private int CountRows(string sql)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
