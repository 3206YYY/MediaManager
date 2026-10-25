using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>数据库初始化 + 仓库层核心功能测试。</summary>
public class MediaRepositoryTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly DatabaseInitializer _initializer;
    private readonly MediaRepository _repo;
    private readonly string _tempDir;

    public MediaRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "portable.marker"), "");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Core.Services.IPortableModeDetector>(_ =>
            new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir));
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<Core.Services.IMediaRepository, MediaRepository>();

        _provider = services.BuildServiceProvider();
        _initializer = _provider.GetRequiredService<DatabaseInitializer>();
        _repo = (MediaRepository)_provider.GetRequiredService<Core.Services.IMediaRepository>();
        _initializer.Initialize();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task AddRootFolder_ShouldPersist()
    {
        var id = await _repo.AddRootFolderAsync("C:\\Photos", "ABCD1234", "Photos");
        Assert.True(id > 0);

        var folders = await _repo.GetRootFoldersAsync();
        Assert.Single(folders);
        Assert.Equal("C:\\Photos", folders[0].Path);
        Assert.Equal("ABCD1234", folders[0].VolumeSerial);
    }

    [Fact]
    public async Task UpsertMedia_ShouldInsertThenUpdate()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var item = new MediaItem
        {
            RootFolderId = rootId,
            Path = "C:\\Photos\\a.jpg",
            FileName = "a.jpg",
            Extension = "jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1024,
        };

        var id1 = await _repo.UpsertMediaAsync(item);
        Assert.True(id1 > 0);

        // 更新
        item.SizeBytes = 2048;
        var id2 = await _repo.UpsertMediaAsync(item);
        Assert.Equal(id1, id2);

        var loaded = await _repo.GetByIdAsync(id1);
        Assert.Equal(2048, loaded!.SizeBytes);
    }

    [Fact]
    public async Task FavoriteAndRating_ShouldPersist()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var item = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\b.jpg", FileName = "b.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var id = await _repo.UpsertMediaAsync(item);

        await _repo.SetFavoriteAsync(id, true);
        await _repo.SetRatingAsync(id, 5);

        var loaded = await _repo.GetByIdAsync(id);
        Assert.True(loaded!.Favorite);
        Assert.Equal(5, loaded.Rating);
    }

    [Fact]
    public async Task TagAndCategory_ShouldLinkMedia()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var item = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\c.jpg", FileName = "c.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var mediaId = await _repo.UpsertMediaAsync(item);

        var tagId = await _repo.AddTagAsync(new Tag { Name = "旅行" });
        var catId = await _repo.AddCategoryAsync(new Category { Name = "风景" });

        await _repo.AddMediaTagAsync(mediaId, tagId);
        await _repo.AddMediaCategoryAsync(mediaId, catId);

        var tagIds = await _repo.GetMediaTagIdsAsync(mediaId);
        var catIds = await _repo.GetMediaCategoryIdsAsync(mediaId);

        Assert.Contains(tagId, tagIds);
        Assert.Contains(catId, catIds);
    }

    [Fact]
    public async Task UnfiledView_ShouldExcludeCategorized()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var m1 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\1.jpg", FileName = "1.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var m2 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\2.jpg", FileName = "2.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var id1 = await _repo.UpsertMediaAsync(m1);
        var id2 = await _repo.UpsertMediaAsync(m2);

        var catId = await _repo.AddCategoryAsync(new Category { Name = "家人" });
        await _repo.AddMediaCategoryAsync(id1, catId);

        var unfiled = await _repo.QueryAsync(new MediaFilter { Unfiled = true, Limit = 100 });
        Assert.Single(unfiled);
        Assert.Equal(id2, unfiled[0].Id);
    }

    [Fact]
    public async Task SearchFts_ShouldFindByFileName()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var m1 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\beach.jpg", FileName = "beach.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var m2 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\mountain.jpg", FileName = "mountain.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        await _repo.UpsertMediaAsync(m1);
        await _repo.UpsertMediaAsync(m2);

        var results = await _repo.QueryAsync(new MediaFilter { SearchText = "beach", Limit = 100 });
        Assert.Single(results);
        Assert.Equal("beach.jpg", results[0].FileName);
    }

    [Fact]
    public async Task MarkMissing_ShouldFlagRemovedFiles()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Photos", null, null);
        var m1 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\gone.jpg", FileName = "gone.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        var m2 = new MediaItem { RootFolderId = rootId, Path = "C:\\Photos\\kept.jpg", FileName = "kept.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 };
        await _repo.UpsertMediaAsync(m1);
        await _repo.UpsertMediaAsync(m2);

        var existing = new HashSet<string> { "C:\\Photos\\kept.jpg" };
        var marked = await _repo.MarkMissingAsync(rootId, existing);
        Assert.Equal(1, marked);

        var loaded = await _repo.GetByIdAsync(m1.Id);
        Assert.True(loaded!.IsMissing);
    }

    [Fact]
    public async Task UpsertMediaBatch_ShouldInsertThenUpdate()
    {
        var rootId = await _repo.AddRootFolderAsync("C:\\Batch", null, null);

        var items = new List<MediaItem>
        {
            new() { RootFolderId = rootId, Path = "C:\\Batch\\a.jpg", FileName = "a.jpg", Extension = "jpg", MediaType = MediaType.Image, SizeBytes = 100 },
            new() { RootFolderId = rootId, Path = "C:\\Batch\\b.mp3", FileName = "b.mp3", Extension = "mp3", MediaType = MediaType.Audio, SizeBytes = 200 },
        };

        await _repo.UpsertMediaBatchAsync(items);
        Assert.All(items, i => Assert.True(i.Id > 0));

        var all = await _repo.QueryAsync(new MediaFilter { RootFolderId = rootId, Limit = 100 });
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.MediaType == MediaType.Audio);

        // 同一路径再写一次：应该走 UPDATE，而不是重复插入
        items[0].SizeBytes = 999;
        await _repo.UpsertMediaBatchAsync(new[] { items[0] });

        var again = await _repo.QueryAsync(new MediaFilter { RootFolderId = rootId, Limit = 100 });
        Assert.Equal(2, again.Count);
        Assert.Equal(999, again.First(m => m.Path.EndsWith("a.jpg", StringComparison.OrdinalIgnoreCase)).SizeBytes);
    }
}
