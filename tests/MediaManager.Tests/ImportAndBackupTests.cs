using System.IO.Compression;
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
/// 导入策略（不在库内的文件放哪里 / 直接入保险箱）+ 完整备份往返。
///
/// 重点盯三件事：
/// 1. 入保险箱是**移动**不是复制，且库里的标记必须跟文件真实位置一致；
/// 2. 「复制到导入文件夹」模式绝不能动用户的原件；
/// 3. 完整备份恢复后，私密文件必须在当前数据目录里、库路径必须改写到位。
/// </summary>
public class ImportAndBackupTests : IDisposable
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly VaultService _vault;
    private readonly MediaImportService _import;
    private readonly DatabaseBackupService _dbBackup;
    private readonly FullBackupService _fullBackup;
    private readonly string _tempDir;

    public ImportAndBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerVaultBackupTests_" + Guid.NewGuid().ToString("N"));
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
        var config = new StubConfig(_tempDir);
        _vault = new VaultService(_repo, config);

        _import = new MediaImportService(
            _repo, new StubThumbnails(), _vault, NullLogger<MediaImportService>.Instance);

        _dbBackup = new DatabaseBackupService(
            _provider.GetRequiredService<IDbConnectionFactory>(),
            _provider.GetRequiredService<IPortableModeDetector>(),
            _repo,
            NullLogger<DatabaseBackupService>.Instance);

        _fullBackup = new FullBackupService(
            _provider.GetRequiredService<IDbConnectionFactory>(),
            _dbBackup,
            _vault,
            _repo,
            NullLogger<FullBackupService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    // ---------- 入保险箱 ----------

    [Fact]
    public async Task ImportIntoVault_MovesFile_MarksPrivate_KeepsOriginalPath()
    {
        var source = WritePng(Path.Combine(_tempDir, "照片"), "私密照.png");

        var items = await _import.ImportIntoVaultAsync(new[] { source });
        Assert.NotEmpty(items);

        var item = Assert.Single(items);
        Assert.True(item.IsPrivate);
        Assert.Equal("私密照.png", item.OriginalPath is { } o ? Path.GetFileName(o) : null);

        // 文件真的挪进了 Vault 目录，原位置不再有文件
        Assert.StartsWith(_vault.VaultDirectory, item.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(item.Path));
        Assert.False(File.Exists(source));

        // 常规查询查不到（IsPrivate=0 的默认过滤），私密查询能查到
        var normal = await _repo.QueryAsync(new MediaFilter { Limit = 50 });
        Assert.DoesNotContain(normal, m => m.Id == item.Id);
        var priv = await _repo.QueryAsync(new MediaFilter { IncludePrivate = true, Limit = 50 });
        Assert.Contains(priv, m => m.Id == item.Id);
    }

    [Fact]
    public async Task ImportIntoVault_FileAlreadyInVault_IsSkipped()
    {
        var source = WritePng(Path.Combine(_tempDir, "照片"), "a.png");
        var first = await _import.ImportIntoVaultAsync(new[] { source });

        // 再导一次保险箱里的文件：必须被拒，不能复制出第二份
        var second = await _import.ImportIntoVaultAsync(new[] { first[0].Path });

        Assert.Empty(second);
        Assert.Equal(1, await _repo.GetPrivateCountAsync());
    }

    [Fact]
    public async Task ImportIntoVault_DoesNotAddSourceDirectoryAsMediaFolder()
    {
        var dir = Path.Combine(_tempDir, "桌面随手放");
        var source = WritePng(dir, "b.png");

        await _import.ImportIntoVaultAsync(new[] { source });

        // 从桌面拖两张图进保险箱，不该把桌面变成媒体文件夹
        var roots = await _repo.GetRootFoldersAsync();
        Assert.DoesNotContain(roots, r => string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 导入策略 ----------

    [Fact]
    public async Task FindUncovered_ReportsOnlyFilesOutsideMediaFolders()
    {
        var rootId = await _repo.AddRootFolderAsync(_tempDir, null, null);
        var inside = WritePng(Path.Combine(_tempDir, "子目录"), "in库.png");

        // 放在临时根目录**外面**，才能真正构成「不在库内」
        var outsideDir = Path.Combine(Path.GetTempPath(), "MMOutside_" + Guid.NewGuid().ToString("N"));
        var outside = WritePng(outsideDir, "out库.png");
        try
        {
            var uncovered = await _import.FindUncoveredAsync(new[] { inside, outside });

            Assert.Contains(uncovered, p => p == outside);
            Assert.DoesNotContain(uncovered, p => p == inside);
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ImportFiles_CopyToImportFolder_KeepsSourceUntouched()
    {
        var importFolder = Path.Combine(_tempDir, "导入文件夹");
        var source = WritePng(Path.Combine(_tempDir, "别处"), "原件.png");
        var originalBytes = await File.ReadAllBytesAsync(source);

        var options = new MediaImportOptions
        {
            UnknownFolderPolicy = UnknownFolderPolicy.CopyToImportFolder,
            ImportFolder = importFolder,
        };
        var items = await _import.ImportFilesAsync(new[] { source }, options);

        var item = Assert.Single(items);
        // 副本进了导入文件夹，原件分毫未动（内容 + 位置）
        Assert.StartsWith(importFolder, item.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));

        // 库里记录的是副本的路径，不是原件
        Assert.NotEqual(source, item.Path);
    }

    // ---------- 完整备份往返 ----------

    [Fact]
    public async Task FullBackup_ExportThenImport_RestoresVaultFilesAndRewritesPaths()
    {
        var source = WritePng(Path.Combine(_tempDir, "照片"), "要备份.png");
        var imported = Assert.Single(await _import.ImportIntoVaultAsync(new[] { source }));

        var zipPath = Path.Combine(_tempDir, "backup.mmbak");
        var exportResult = await _fullBackup.ExportAsync(zipPath);
        Assert.True(exportResult.Success, exportResult.Error);
        Assert.Equal(1, exportResult.VaultFiles);
        Assert.True(File.Exists(zipPath));

        // 包里确实有数据库 + 私密文件 + 清单
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(zip.GetEntry("library.db"));
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("vault/"));
        }

        // 把保险箱文件删掉，模拟「库记录还在、文件没了」的损坏状态，再用备份恢复
        File.Delete(imported.Path);

        var importResult = await _fullBackup.ImportAsync(zipPath);
        Assert.True(importResult.Success, importResult.Error);
        Assert.Equal(1, importResult.VaultFiles);

        var restored = await _repo.GetByIdAsync(imported.Id);
        Assert.NotNull(restored);
        Assert.True(restored!.IsPrivate);

        // 路径已被改写到当前数据目录的 Vault，文件真实存在
        Assert.StartsWith(_vault.VaultDirectory, restored.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(restored.Path));
        Assert.Equal("要备份.png", Path.GetFileName(restored.Path));

        // 搬移历史（OriginalPath）原样保留：以后「移出」还能回原位
        Assert.Equal("要备份.png", Path.GetFileName(restored.OriginalPath!));
        Assert.Equal(source, restored.OriginalPath);
    }

    [Fact]
    public async Task FullBackup_Export_NonExistentVaultFile_IsSkippedNotFatal()
    {
        var source = WritePng(Path.Combine(_tempDir, "照片"), "会丢.png");
        var imported = Assert.Single(await _import.ImportIntoVaultAsync(new[] { source }));

        // 文件先丢了
        File.Delete(imported.Path);

        var zipPath = Path.Combine(_tempDir, "backup2.mmbak");
        var result = await _fullBackup.ExportAsync(zipPath);

        Assert.True(result.Success);
        Assert.Equal(0, result.VaultFiles);
        Assert.True(File.Exists(zipPath));   // 数据库部分照样导出
    }

    // ---------- 桩 ----------

    private string WritePng(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPngBase64));
        return path;
    }

    private sealed class StubThumbnails : IThumbnailService
    {
        public event Action<long, string>? ThumbnailReady { add { } remove { } }
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
