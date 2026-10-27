using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.FileSystem;
using MediaManager.Infrastructure.Portable;
using MediaManager.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 私密空间分区。
///
/// 盯住三件事：
/// 1. 分区是保险箱内部的事，不能和普通「分类」串味（普通查询看不见、也不该被分类计数带出来）；
/// 2. 删分区绝不能删文件 —— 分区只是盒子；
/// 3. 移出保险箱要顺手清掉分区归属，否则下次移入会自动进到旧分区里。
/// </summary>
public class VaultSectionTests : IDisposable
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly VaultService _vault;
    private readonly MediaImportService _import;
    private readonly string _tempDir;

    public VaultSectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerVaultSectionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPortableModeDetector>(
            new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir));
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMediaRepository, MediaRepository>();

        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();

        _repo = _provider.GetRequiredService<IMediaRepository>();
        _vault = new VaultService(_repo, new StubConfig(_tempDir));
        _import = new MediaImportService(
            _repo, new StubThumbnails(), _vault, NullLogger<MediaImportService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task CreateVaultSection_DuplicateName_GetsSuffix()
    {
        var first = await _repo.CreateVaultSectionAsync("截图");
        var second = await _repo.CreateVaultSectionAsync("截图");

        Assert.NotEqual(first, second);

        var list = await _repo.GetVaultSectionsAsync();
        Assert.Contains(list, s => s.Name == "截图");
        Assert.Contains(list, s => s.Name == "截图 (2)");
    }

    [Fact]
    public async Task GetVaultSections_FillsItemCount()
    {
        var a = await _repo.CreateVaultSectionAsync("A");
        var b = await _repo.CreateVaultSectionAsync("B");

        var m1 = await ImportPrivateAsync("1.png");
        var m2 = await ImportPrivateAsync("2.png");
        var m3 = await ImportPrivateAsync("3.png");

        await _repo.MoveToVaultSectionAsync(new[] { m1.Id, m2.Id }, a);
        await _repo.MoveToVaultSectionAsync(new[] { m3.Id }, b);

        var list = await _repo.GetVaultSectionsAsync();
        Assert.Equal(2, list.Single(s => s.Id == a).ItemCount);
        Assert.Equal(1, list.Single(s => s.Id == b).ItemCount);
        Assert.Equal(0, await _repo.GetVaultUnsectionedCountAsync());
    }

    [Fact]
    public async Task FilterBySection_ReturnsOnlyThatSection()
    {
        var sectionId = await _repo.CreateVaultSectionAsync("只在这里");
        var inside = await ImportPrivateAsync("里面.png");
        var outside = await ImportPrivateAsync("外面.png");
        await _repo.MoveToVaultSectionAsync(new[] { inside.Id }, sectionId);

        var filtered = await _repo.QueryAsync(new MediaFilter
        {
            IncludePrivate = true,
            VaultSectionId = sectionId,
            Limit = 50,
        });

        Assert.Single(filtered);
        Assert.Equal(inside.Id, filtered[0].Id);

        var unsectioned = await _repo.QueryAsync(new MediaFilter
        {
            IncludePrivate = true,
            VaultUnsectioned = true,
            Limit = 50,
        });

        Assert.Single(unsectioned);
        Assert.Equal(outside.Id, unsectioned[0].Id);
    }

    [Fact]
    public async Task DeleteVaultSection_KeepsFiles_AndResetsToUnsectioned()
    {
        var sectionId = await _repo.CreateVaultSectionAsync("临时分区");
        var item = await ImportPrivateAsync("留着.png");
        await _repo.MoveToVaultSectionAsync(new[] { item.Id }, sectionId);

        await _repo.DeleteVaultSectionAsync(sectionId);

        // 分区没了，文件还在（磁盘上也还在）
        Assert.Empty(await _repo.GetVaultSectionsAsync());
        Assert.True(File.Exists(item.Path));
        Assert.Equal(1, await _repo.GetPrivateCountAsync());
        Assert.Equal(1, await _repo.GetVaultUnsectionedCountAsync());
    }

    [Fact]
    public async Task MoveOutOfVault_ClearsSectionAssignment()
    {
        var sectionId = await _repo.CreateVaultSectionAsync("分区");
        var item = await ImportPrivateAsync("要移出.png");
        await _repo.MoveToVaultSectionAsync(new[] { item.Id }, sectionId);

        // 移出保险箱（文件挪回原位置，库标记改回普通）
        var restored = await _vault.MoveOutOfVaultAsync(item.Path, item.OriginalPath);
        Assert.NotNull(restored);
        Assert.True(File.Exists(restored));

        await _repo.SetPrivateAsync(item.Id, false, restored!, null);

        var after = (await _repo.GetByIdsAsync(new[] { item.Id })).Single();
        Assert.False(after.IsPrivate);
        Assert.Null(after.VaultSectionId);
    }

    [Fact]
    public async Task SectionAssignment_DoesNotLeakIntoNormalQuery()
    {
        var sectionId = await _repo.CreateVaultSectionAsync("私密分区");
        var item = await ImportPrivateAsync("不该被看到.png");
        await _repo.MoveToVaultSectionAsync(new[] { item.Id }, sectionId);

        // 主界面的查询（默认 IncludePrivate=false）一条都不该返回
        var normal = await _repo.QueryAsync(new MediaFilter { Limit = 100 });
        Assert.DoesNotContain(normal, m => m.Id == item.Id);

        // 带上分区条件也一样（分区过滤不能绕过私密隔离）
        var sneaky = await _repo.QueryAsync(new MediaFilter
        {
            IncludePrivate = false,
            VaultSectionId = sectionId,
            Limit = 100,
        });
        Assert.DoesNotContain(sneaky, m => m.Id == item.Id);
    }

    // ---------- 辅助 ----------

    private async Task<MediaItem> ImportPrivateAsync(string fileName)
    {
        var dir = Path.Combine(_tempDir, "源_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPngBase64));

        var items = await _import.ImportIntoVaultAsync(new[] { path });
        return Assert.Single(items);
    }

    private sealed class StubThumbnails : IThumbnailService
    {
        public event Action<long, string>? ThumbnailReady
        {
            add { }
            remove { }
        }

        public void Enqueue(MediaItem item) { }
        public string? GetCachedPath(MediaItem item) => null;
        public IReadOnlyList<string> Invalidate(long mediaId) => Array.Empty<string>();
        public Task<long> CleanCacheAsync(CancellationToken ct = default) => Task.FromResult(0L);
    }

    private sealed class StubConfig : IAppConfigService
    {
        public StubConfig(string dataDirectory) => DataDirectory = dataDirectory;

        public string DataDirectory { get; }
        public string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbs");
        public string? FfmpegPath => null;
        public int Concurrency => 1;
        public AppTheme Theme => AppTheme.Light;
        public AppLanguage Language => AppLanguage.Chinese;
        public DeleteBehavior DeleteBehavior => DeleteBehavior.RecycleBin;

        public Task EnsureLoadedAsync() => Task.CompletedTask;
        public void UpdateTheme(AppTheme theme) { }
        public Task ReloadAsync() => Task.CompletedTask;
    }
}
