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

    /// <summary>
    /// 测试员现场：父目录和子目录都被添加过，子目录里的文件早就扫进父级了。
    /// 这时扫子目录必须是「更新 0 新增」，绝不能报「新增 27」——
    /// 老逻辑按「这个根目录下已知路径」判断新旧，报出来的新增条数纯属幻觉，
    /// 用户看到的就是「说导入了 27 个，点进去共 0 项」。
    /// </summary>
    [Fact]
    public async Task ScanNestedRoot_DoesNotClaimNewItemsAlreadyOwnedByParent()
    {
        var parent = Path.Combine(_tempDir, "父");
        var child = Path.Combine(parent, "子");
        Directory.CreateDirectory(child);
        File.WriteAllBytes(Path.Combine(child, "a.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllBytes(Path.Combine(child, "b.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        // 先扫父目录：两条记录归属父级
        var parentId = await _repo.AddRootFolderAsync(parent, null, null);
        await _scanner.ScanRootFolderAsync(parentId);

        // 再把子目录当独立根目录加进来扫一遍
        var childId = await _repo.AddRootFolderAsync(child, null, null);
        var progress = new RecordingProgress();
        await _scanner.ScanRootFolderAsync(childId, progress);

        var last = progress.Last!;
        // 扫完必须报出真实文件数：<500 个文件时原来 ScannedFiles 一直是 0，状态栏显示「0 个文件」
        Assert.Equal(2, last.ScannedFiles);
        Assert.Equal(0, last.Added);
        Assert.Equal(2, last.Updated);

        // 归属没被改：父级名下还是两条
        var byParent = await _repo.QueryAsync(new MediaFilter { RootFolderId = parentId, Limit = 100 });
        Assert.Equal(2, byParent.Count);

        // 但按路径查子目录能看到它们（侧栏点进去要显示内容，靠的就是这个）
        var byPath = await _repo.QueryAsync(new MediaFilter { FolderPath = child, Limit = 100 });
        Assert.Equal(2, byPath.Count);
    }

    /// <summary>同步记录最后一次进度：Progress&lt;T&gt; 会 post 到线程池，断言会飘。</summary>
    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        public ScanProgress? Last { get; private set; }
        public void Report(ScanProgress value) => Last = value;
    }
}
