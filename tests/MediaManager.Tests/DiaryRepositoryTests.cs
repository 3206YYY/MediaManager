using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>日记仓储：增删改查、一天多篇、媒体关联与顺序、日历计数。</summary>
public class DiaryRepositoryTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _media;
    private readonly IDiaryRepository _diary;
    private readonly string _tempDir;

    public DiaryRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerDiaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPortableModeDetector>(
            new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir));
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMediaRepository, MediaRepository>();
        services.AddSingleton<IDiaryRepository, DiaryRepository>();

        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<DatabaseInitializer>().Initialize();

        _media = _provider.GetRequiredService<IMediaRepository>();
        _diary = _provider.GetRequiredService<IDiaryRepository>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    private async Task<long> AddMediaAsync(string name)
    {
        var rootId = await _media.AddRootFolderAsync(_tempDir, null, null);
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[] { 1 });

        return await _media.UpsertMediaAsync(new MediaItem
        {
            RootFolderId = rootId,
            Path = path,
            FileName = name,
            Extension = Path.GetExtension(name).TrimStart('.'),
            MediaType = MediaType.Image,
            SizeBytes = 1,
            DateAddedUtc = DateTime.UtcNow.ToString("O"),
        });
    }

    [Fact]
    public async Task SaveAndRead_KeepsMediaOrder()
    {
        var a = await AddMediaAsync("a.jpg");
        var b = await AddMediaAsync("b.jpg");
        var c = await AddMediaAsync("c.jpg");

        var id = await _diary.SaveEntryAsync(new DiaryEntry
        {
            EntryDate = "2026-10-26",
            Title = "海边",
            Content = "风很大",
            Mood = "😄",
            Media = new List<MediaItem> { new() { Id = c }, new() { Id = a }, new() { Id = b } },
        });

        var entries = await _diary.GetEntriesAsync("2026-10-26", "2026-10-26");
        var entry = Assert.Single(entries);

        Assert.Equal(id, entry.Id);
        Assert.Equal("海边", entry.Title);
        Assert.Equal("😄", entry.Mood);
        // 顺序就是写入顺序，不能被 JOIN 打乱
        Assert.Equal(new[] { "c.jpg", "a.jpg", "b.jpg" }, entry.Media.Select(m => m.FileName));
    }

    [Fact]
    public async Task MultipleEntriesOnSameDay_AreAllReturned()
    {
        await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-26", Content = "第一篇" });
        await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-26", Content = "第二篇" });

        var entries = await _diary.GetEntriesAsync("2026-10-26", "2026-10-26");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Content == "第一篇");
        Assert.Contains(entries, e => e.Content == "第二篇");
    }

    [Fact]
    public async Task Update_ReplacesMediaSet()
    {
        var a = await AddMediaAsync("a.jpg");
        var b = await AddMediaAsync("b.jpg");

        var id = await _diary.SaveEntryAsync(new DiaryEntry
        {
            EntryDate = "2026-10-26",
            Content = "原文",
            Media = new List<MediaItem> { new() { Id = a } },
        });

        await _diary.SaveEntryAsync(new DiaryEntry
        {
            Id = id,
            EntryDate = "2026-10-26",
            Content = "改过",
            Media = new List<MediaItem> { new() { Id = b } },
        });

        var entry = await _diary.GetEntryAsync(id);
        Assert.NotNull(entry);
        Assert.Equal("改过", entry!.Content);
        Assert.Equal(new[] { "b.jpg" }, entry.Media.Select(m => m.FileName));
    }

    [Fact]
    public async Task DateCounts_GroupByDay()
    {
        await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-01", Content = "a" });
        await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-01", Content = "b" });
        await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-05", Content = "c" });

        var counts = await _diary.GetDateCountsAsync("2026-10-01", "2026-10-31");

        Assert.Equal(2, counts["2026-10-01"]);
        Assert.Equal(1, counts["2026-10-05"]);
        Assert.Equal(3, await _diary.GetEntryCountAsync());
    }

    [Fact]
    public async Task Delete_RemovesEntryAndItsLinks_ButKeepsMedia()
    {
        var a = await AddMediaAsync("keep.jpg");
        var id = await _diary.SaveEntryAsync(new DiaryEntry
        {
            EntryDate = "2026-10-26",
            Content = "要删的",
            Media = new List<MediaItem> { new() { Id = a } },
        });

        await _diary.DeleteEntryAsync(id);

        Assert.Empty(await _diary.GetEntriesAsync("2026-10-26", "2026-10-26"));
        Assert.Equal(0, await _diary.GetEntryCountAsync());

        // 照片本身必须还在库里 —— 删日记不等于删文件
        var media = await _media.GetByIdAsync(a);
        Assert.NotNull(media);
    }

    [Fact]
    public async Task MoveEntry_ChangesDate()
    {
        var id = await _diary.SaveEntryAsync(new DiaryEntry { EntryDate = "2026-10-26", Content = "写错日期了" });

        await _diary.MoveEntryAsync(id, "2026-10-20");

        Assert.Empty(await _diary.GetEntriesAsync("2026-10-26", "2026-10-26"));
        var moved = Assert.Single(await _diary.GetEntriesAsync("2026-10-20", "2026-10-20"));
        Assert.Equal(id, moved.Id);
    }

    [Fact]
    public async Task MissingMedia_IsSkipped_NotCrashing()
    {
        var a = await AddMediaAsync("gone.jpg");
        var id = await _diary.SaveEntryAsync(new DiaryEntry
        {
            EntryDate = "2026-10-26",
            Content = "照片被删了",
            Media = new List<MediaItem> { new() { Id = a } },
        });

        // 模拟：文件在磁盘上没了、扫描标成缺失
        await _media.MarkMissingSingleAsync(a);

        var entry = await _diary.GetEntryAsync(id);
        Assert.NotNull(entry);
        Assert.Empty(entry!.Media);
        Assert.Equal("照片被删了", entry.Content);
    }
}
