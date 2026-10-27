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
/// 单文件导入（日记里从电脑拖一张图进来的那条路径）。
///
/// 这里盯的是「别把库搞乱」：不能重复插入、不能把保险箱里的东西搬回普通库、
/// 不能因为文件类型不认识就抛异常把整批拖垮。
/// </summary>
public class MediaImportServiceTests : IDisposable
{
    /// <summary>1×1 的合法 PNG：用来验证「导入时顺手读出宽高」。</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly VaultService _vault;
    private readonly MediaImportService _import;
    private readonly string _tempDir;

    public MediaImportServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerImportTests_" + Guid.NewGuid().ToString("N"));
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
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    // ---------- 辅助 ----------

    private string WriteFile(string directory, string name, byte[]? content = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content ?? new byte[] { 1, 2, 3 });
        return path;
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task ImportFile_AddsToLibrary_AndCreatesRootFolder()
    {
        var photoDir = Path.Combine(_tempDir, "随手拍");
        var path = WriteFile(photoDir, "海边.png", Convert.FromBase64String(OnePixelPngBase64));

        var item = await _import.ImportFileAsync(path);

        Assert.NotNull(item);
        Assert.Equal(path, item!.Path);
        Assert.Equal("png", item.Extension);
        Assert.Equal(MediaType.Image, item.MediaType);
        Assert.Equal(1, item.Width);      // 顺带读了宽高，详情面板不会显示「未知」
        Assert.Equal(1, item.Height);

        // 没有现成的根文件夹覆盖它 → 自动把这一层目录加进来
        var roots = await _repo.GetRootFoldersAsync();
        Assert.Contains(roots, r => string.Equals(r.Path, photoDir, StringComparison.OrdinalIgnoreCase));

        // 并且立刻能在常规视图里查到（不是「只有日记里看得到」的怪状态）
        var list = await _repo.QueryAsync(new MediaFilter { Limit = 50 });
        Assert.Contains(list, m => m.Id == item.Id);
    }

    [Fact]
    public async Task ImportFile_SameFileTwice_DoesNotDuplicate()
    {
        var path = WriteFile(_tempDir, "重复.jpg");

        var first = await _import.ImportFileAsync(path);
        var second = await _import.ImportFileAsync(path);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1, await _repo.GetTotalCountAsync());
    }

    [Fact]
    public async Task ImportFile_ReusesExistingRootFolder()
    {
        // 先把 D:\xxx（这里是临时目录）作为根文件夹收录
        var rootId = await _repo.AddRootFolderAsync(_tempDir, null, null);
        var path = WriteFile(Path.Combine(_tempDir, "子目录"), "a.jpg");

        var item = await _import.ImportFileAsync(path);

        Assert.NotNull(item);
        Assert.Equal(rootId, item!.RootFolderId);

        // 不能因为导入又凭空多出一个根文件夹
        Assert.Single(await _repo.GetRootFoldersAsync());
    }

    [Fact]
    public async Task ImportFile_UnsupportedType_OrMissingFile_ReturnsNull()
    {
        var weird = WriteFile(_tempDir, "安装包.xyz");
        Assert.Null(await _import.ImportFileAsync(weird));

        Assert.Null(await _import.ImportFileAsync(Path.Combine(_tempDir, "根本不存在.jpg")));

        Assert.Equal(0, await _repo.GetTotalCountAsync());
    }

    [Fact]
    public async Task ImportFile_InsideVault_IsRefused()
    {
        // 保险箱里的文件被拖回来时不能重新入库：那等于「拖一下就把私密内容公开」
        Directory.CreateDirectory(_vault.VaultDirectory);
        var secret = WriteFile(_vault.VaultDirectory, "private.jpg");

        Assert.Null(await _import.ImportFileAsync(secret));
        Assert.Equal(0, await _repo.GetTotalCountAsync());
    }

    [Fact]
    public async Task ImportFiles_MixedBatch_ReturnsOnlyUsableOnes()
    {
        var good1 = WriteFile(_tempDir, "1.jpg");
        var good2 = WriteFile(_tempDir, "2.mp4");
        var bad = WriteFile(_tempDir, "3.xyz");

        var items = await _import.ImportFilesAsync(new[] { good1, bad, good2, good1 });

        // 重复的 good1 只返回一次，bad 被跳过
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Path == good1);
        Assert.Contains(items, i => i.Path == good2);
    }

    // ---------- 桩 ----------

    private sealed class StubThumbnails : IThumbnailService
    {
        public event Action<long, string>? ThumbnailReady;

        /// <summary>只是为了让编译器不抱怨未使用（事件必须被引用才不会警告）。</summary>
        public void RaiseForTest(long id, string path) => ThumbnailReady?.Invoke(id, path);

        public List<MediaItem> Enqueued { get; } = new();

        public void Enqueue(MediaItem item) => Enqueued.Add(item);

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
