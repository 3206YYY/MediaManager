using System.Text.Json;
using System.Text.Json.Serialization;
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
/// 最近删除 / 撤销删除：快照能落库读回，还原后 Id、收藏、评分、标签都要回来。
/// </summary>
public class RecentlyDeletedTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly string _tempDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public RecentlyDeletedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerDeletedTests_" + Guid.NewGuid().ToString("N"));
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
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task DeleteSnapshot_ShouldSurviveRoundtrip_AndRestoreMedia()
    {
        var rootId = await _repo.AddRootFolderAsync(@"C:\temp\media", null, null);
        var item = new MediaItem
        {
            RootFolderId = rootId,
            Path = @"C:\temp\media\a.jpg",
            FileName = "a.jpg",
            Extension = ".jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1234,
            Favorite = true,
            Rating = 3,
            Note = "海边的照片",
        };
        var mediaId = await _repo.UpsertMediaAsync(item);

        var tagId = await _repo.AddTagAsync(new Tag { Name = "风景" });
        await _repo.AddMediaTagAsync(mediaId, tagId);

        var deleted = new DeletedMedia
        {
            OriginalMediaId = mediaId,
            RootFolderId = rootId,
            Path = item.Path,
            FileName = item.FileName,
            MediaType = item.MediaType.ToString(),
            SizeBytes = item.SizeBytes,
            DeletedAtUtc = DateTime.UtcNow.ToString("O"),
            SnapshotJson = JsonSerializer.Serialize(item, JsonOptions),
            TagIdsJson = JsonSerializer.Serialize(new List<long> { tagId }),
            CategoryIdsJson = JsonSerializer.Serialize(new List<long>()),
            CollectionIdsJson = JsonSerializer.Serialize(new List<long>()),
        };
        await _repo.AddDeletedAsync(deleted);

        await _repo.DeleteMediaAsync(mediaId);
        Assert.Null(await _repo.GetByIdAsync(mediaId));

        // 最近删除能读回，且字段映射正确（枚举没被写成数字）
        var recent = await _repo.GetRecentlyDeletedAsync();
        var record = Assert.Single(recent);
        Assert.Equal(item.Path, record.Path);
        Assert.Equal("Image", record.MediaType);
        Assert.False(record.IsRestored);
        Assert.NotNull(record.SnapshotJson);

        // 还原：Id、收藏、评分、备注、标签全部回来
        var snapshot = JsonSerializer.Deserialize<MediaItem>(record.SnapshotJson!, JsonOptions)!;
        var newId = await _repo.RestoreMediaAsync(snapshot,
            new List<long> { tagId }, new List<long>(), new List<long>());

        Assert.Equal(mediaId, newId);
        var restored = await _repo.GetByIdAsync(newId);
        Assert.NotNull(restored);
        Assert.True(restored!.Favorite);
        Assert.Equal(3, restored.Rating);
        Assert.Equal("海边的照片", restored.Note);
        Assert.Contains(tagId, await _repo.GetMediaTagIdsAsync(newId));

        // 标记已还原后，不再出现在最近删除里
        await _repo.MarkDeletedRestoredAsync(record.Id);
        Assert.Empty(await _repo.GetRecentlyDeletedAsync());
    }

    [Fact]
    public async Task ClearThumbnailPaths_ShouldResetAllCachedPaths()
    {
        var rootId = await _repo.AddRootFolderAsync(@"C:\temp\media2", null, null);
        var item = new MediaItem
        {
            RootFolderId = rootId,
            Path = @"C:\temp\media2\b.jpg",
            FileName = "b.jpg",
            Extension = ".jpg",
            MediaType = MediaType.Image,
            ThumbnailPath = @"C:\temp\thumb\b.jpg",
        };
        var id = await _repo.UpsertMediaAsync(item);
        await _repo.UpdateMediaAsync(new MediaItem { Id = id, ThumbnailPath = item.ThumbnailPath });

        await _repo.ClearThumbnailPathsAsync();
        var reloaded = await _repo.GetByIdAsync(id);
        Assert.True(string.IsNullOrEmpty(reloaded!.ThumbnailPath));
    }
}
