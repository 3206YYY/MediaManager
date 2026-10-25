using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.FileSystem;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 扫描器过滤系统垃圾文件。
///
/// 现场问题：D:\图片 里每个子目录的 desktop.ini 都被扫进了图库，
/// 在网格里显示成一堆文档图标。这里直接跑一次真实扫描，确认
/// desktop.ini / Thumbs.db 不进来，正常图片照常进来。
/// </summary>
public class ScanJunkFilterTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly ScanService _scanner;
    private readonly string _tempDir;

    public ScanJunkFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerScanTests_" + Guid.NewGuid().ToString("N"));
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
        _scanner = new ScanService(_repo, NullLogger<ScanService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Scan_SkipsDesktopIniAndThumbsDb_KeepsRealMedia()
    {
        var photoDir = Path.Combine(_tempDir, "旅行");
        Directory.CreateDirectory(photoDir);

        var photo = Path.Combine(photoDir, "海边.jpg");
        var note = Path.Combine(photoDir, "说明.txt");
        File.WriteAllBytes(photo, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });   // 最小 jpg 头
        File.WriteAllText(note, "hello");

        var desktopIni = Path.Combine(photoDir, "desktop.ini");
        File.WriteAllText(desktopIni, "[.ShellClassInfo]");
        File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);

        var thumbs = Path.Combine(photoDir, "Thumbs.db");
        File.WriteAllBytes(thumbs, new byte[] { 0 });

        var rootId = await _repo.AddRootFolderAsync(_tempDir, null, null);
        await _scanner.ScanRootFolderAsync(rootId);

        var names = (await _repo.QueryAsync(new MediaFilter { Limit = 1000 })).Select(m => m.FileName).ToList();

        Assert.Contains("海边.jpg", names);
        Assert.Contains("说明.txt", names);
        Assert.DoesNotContain("desktop.ini", names);
        Assert.DoesNotContain("Thumbs.db", names);
    }

    [Fact]
    public async Task IncrementalScan_AlsoSkipsJunk()
    {
        var dir = Path.Combine(_tempDir, "增量");
        Directory.CreateDirectory(dir);

        var photo = Path.Combine(dir, "a.png");
        File.WriteAllBytes(photo, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var desktopIni = Path.Combine(dir, "desktop.ini");
        File.WriteAllText(desktopIni, "x");
        File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);

        var rootId = await _repo.AddRootFolderAsync(_tempDir, null, null);
        await _scanner.ScanIncrementalAsync(rootId, dir);

        var names = (await _repo.QueryAsync(new MediaFilter { Limit = 1000 })).Select(m => m.FileName).ToList();
        Assert.Contains("a.png", names);
        Assert.DoesNotContain("desktop.ini", names);
    }
}
