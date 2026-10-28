using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Vault;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 私密空间的隔离性。
///
/// 这一组测试盯的是「泄露」而不是「好不好用」：
/// 只要有一条常规查询忘了排除 IsPrivate，私密文件就会出现在主网格、搜索、
/// 收藏统计或者发现页里。这类 bug 在界面上很难被自己发现（要刚好移到那一屏），
/// 所以全部用测试钉死。
/// </summary>
public class VaultIsolationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IMediaRepository _repo;
    private readonly VaultService _vault;
    private readonly string _tempDir;

    public VaultIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerVaultTests_" + Guid.NewGuid().ToString("N"));
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
        _vault.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    // ---------- 辅助 ----------

    private async Task<(long RootId, MediaItem Item)> AddMediaAsync(string fileName, bool favorite = false)
    {
        var rootId = await _repo.AddRootFolderAsync(_tempDir, null, null);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var id = await _repo.UpsertMediaAsync(new MediaItem
        {
            RootFolderId = rootId,
            Path = path,
            FileName = fileName,
            Extension = Path.GetExtension(fileName).TrimStart('.'),
            MediaType = MediaType.Image,
            SizeBytes = 3,
            Favorite = favorite,
            DateAddedUtc = DateTime.UtcNow.ToString("O"),
        });

        var item = await _repo.GetByIdAsync(id);
        return (rootId, item!);
    }

    /// <summary>把它挪进保险箱（走真实的文件移动 + 数据库标记）。</summary>
    private async Task MoveIntoVaultAsync(MediaItem item)
    {
        var newPath = await _vault.MoveIntoVaultAsync(item.Path);
        await _repo.SetPrivateAsync(item.Id, true, newPath, item.Path);
    }

    // ---------- 隔离 ----------

    [Fact]
    public async Task PrivateItem_DisappearsFromNormalQuery_AndOnlyShowsWithFlag()
    {
        var (_, item) = await AddMediaAsync("私密照.jpg");
        var (_, normal) = await AddMediaAsync("普通照.jpg");

        await MoveIntoVaultAsync(item);

        var normalList = await _repo.QueryAsync(new MediaFilter { Limit = 100 });
        Assert.Contains(normalList, m => m.FileName == "普通照.jpg");
        Assert.DoesNotContain(normalList, m => m.FileName == "私密照.jpg");

        var vaultList = await _repo.QueryAsync(new MediaFilter { Limit = 100, IncludePrivate = true });
        Assert.Contains(vaultList, m => m.FileName == "私密照.jpg");
        Assert.DoesNotContain(vaultList, m => m.FileName == "普通照.jpg");
    }

    [Fact]
    public async Task PrivateItem_IsExcludedFromCountsFavoritesRecentAndByIds()
    {
        var (_, item) = await AddMediaAsync("secret.jpg", favorite: true);
        await AddMediaAsync("open.jpg", favorite: true);

        await MoveIntoVaultAsync(item);
        await _repo.RecordRecentViewAsync(item.Id);

        Assert.Equal(1, await _repo.GetTotalCountAsync());
        Assert.Equal(1, await _repo.GetFavoriteCountAsync());
        Assert.Empty(await _repo.GetRecentViewAsync(50));
        Assert.Empty(await _repo.GetByIdsAsync(new[] { item.Id }));
        Assert.Equal(1, await _repo.GetPrivateCountAsync());
    }

    [Fact]
    public async Task PrivateItem_IsNotMarkedMissingByScan()
    {
        var (rootId, item) = await AddMediaAsync("in-vault.jpg");
        await MoveIntoVaultAsync(item);

        // 扫描时磁盘上确实找不到原路径（文件已经挪走），但不能因此判定「文件丢失」，
        // 否则用户移进保险箱、下次扫描一看全变灰的「丢失」。
        await _repo.MarkMissingAsync(rootId, new HashSet<string>());

        var after = await _repo.GetByIdAsync(item.Id);
        Assert.NotNull(after);
        Assert.False(after!.IsMissing);
        Assert.True(after.IsPrivate);
    }

    [Fact]
    public async Task Discovery_DoesNotPushPrivateItems()
    {
        var (_, item) = await AddMediaAsync("hidden.jpg", favorite: true);
        await AddMediaAsync("visible.jpg", favorite: true);
        await MoveIntoVaultAsync(item);

        var batch = await _repo.QueryDiscoveryAsync(DiscoveryPoolType.Favorite, null, 50, null);
        Assert.DoesNotContain(batch, m => m.FileName == "hidden.jpg");
        Assert.Contains(batch, m => m.FileName == "visible.jpg");
    }

    // ---------- 文件搬运 ----------

    [Fact]
    public async Task MoveIntoVault_RelocatesFile_AndMoveBackRestoresIt()
    {
        var (_, item) = await AddMediaAsync("海边.jpg");
        var originalPath = item.Path;

        await MoveIntoVaultAsync(item);

        Assert.False(File.Exists(originalPath));
        Assert.True(Directory.Exists(_vault.VaultDirectory));
        Assert.True(File.Exists(Path.Combine(_vault.VaultDirectory, "海边.jpg")));

        var back = await _vault.MoveOutOfVaultAsync(
            Path.Combine(_vault.VaultDirectory, "海边.jpg"), originalPath);
        Assert.Equal(originalPath, back);
        Assert.True(File.Exists(originalPath));
    }

    [Fact]
    public async Task MoveIntoVault_AddsSuffix_WhenNameAlreadyTaken()
    {
        var (_, first) = await AddMediaAsync("同名.jpg");
        await MoveIntoVaultAsync(first);

        // 原路径被挪走后再造一个同名文件，再移入：不能覆盖保险箱里的那一份
        var secondPath = Path.Combine(_tempDir, "同名.jpg");
        File.WriteAllBytes(secondPath, new byte[] { 9 });
        var secondId = await _repo.UpsertMediaAsync(new MediaItem
        {
            RootFolderId = first.RootFolderId,
            Path = secondPath,
            FileName = "同名.jpg",
            Extension = "jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            DateAddedUtc = DateTime.UtcNow.ToString("O"),
        });
        var second = await _repo.GetByIdAsync(secondId);

        await MoveIntoVaultAsync(second!);

        var files = Directory.GetFiles(_vault.VaultDirectory).Select(Path.GetFileName).ToList();
        Assert.Contains("同名.jpg", files);
        Assert.Contains("同名 (1).jpg", files);
        Assert.Equal(2, files.Count);
    }

    // ---------- 密码 ----------

    [Fact]
    public async Task Open_WithoutPassword_NeedsNoCredential()
    {
        // 默认形态是「没设密码」：点一下就能进，代码也不该顺手逼用户设密码
        Assert.False(_vault.IsConfigured);

        Assert.True(await _vault.OpenAsync());
        Assert.True(_vault.IsUnlocked);
        Assert.True(Directory.Exists(_vault.VaultDirectory));

        // 退出之后没有任何凭据，但再点一次仍然能进 —— 「防尴尬」要的是随手可进出
        _vault.Lock();
        Assert.False(_vault.IsUnlocked);

        Assert.True(await _vault.OpenAsync());
        Assert.True(_vault.IsUnlocked);
        Assert.False(_vault.IsConfigured);
    }

    [Fact]
    public async Task Password_SetupUnlockAndWrongPassword()
    {
        Assert.False(_vault.IsConfigured);

        Assert.True(await _vault.SetupAsync("1234"));
        Assert.True(_vault.IsConfigured);
        Assert.True(_vault.IsUnlocked);

        _vault.Lock();
        Assert.False(_vault.IsUnlocked);

        Assert.False(await _vault.UnlockAsync("wrong"));
        Assert.False(_vault.IsUnlocked);

        Assert.True(await _vault.UnlockAsync("1234"));
        Assert.True(_vault.IsUnlocked);
    }

    [Fact]
    public async Task Password_TooShort_IsRejected()
    {
        Assert.False(await _vault.SetupAsync("123"));
        Assert.False(_vault.IsConfigured);
    }

    [Fact]
    public async Task Password_IsStoredHashed_NotPlaintext()
    {
        await _vault.SetupAsync("my-secret");

        var stored = await _repo.GetSettingAsync(VaultService.CredentialSettingKey);
        Assert.NotNull(stored);
        Assert.DoesNotContain("my-secret", stored);
        Assert.StartsWith("pbkdf2-sha256$", stored);
    }

    [Fact]
    public async Task Password_ChangeRequiresOldOne()
    {
        await _vault.SetupAsync("old-pass");

        Assert.False(await _vault.ChangePasswordAsync("bad", "new-pass"));
        Assert.True(await _vault.ChangePasswordAsync("old-pass", "new-pass"));

        _vault.Lock();
        Assert.False(await _vault.UnlockAsync("old-pass"));
        Assert.True(await _vault.UnlockAsync("new-pass"));
    }

    /// <summary>
    /// 设了密码之后，OpenAsync 必须先返回 false，让界面去弹解锁窗 ——
    /// 如果这里放行了，密码就是一道假门：拦不住任何东西，却让用户以为自己被保护着。
    /// </summary>
    [Fact]
    public async Task Open_WithPassword_IsRefusedUntilUnlocked()
    {
        await _vault.SetupAsync("1234");
        _vault.Lock();

        Assert.False(await _vault.OpenAsync());
        Assert.False(_vault.IsUnlocked);

        // 解锁之后（本次会话内）再进就不用重复输
        Assert.True(await _vault.UnlockAsync("1234"));
        Assert.True(await _vault.OpenAsync());
        Assert.True(_vault.IsUnlocked);
    }

    /// <summary>重启（重新 Initialize）之后必须重新上锁，不能因为上次解锁过就一直开着。</summary>
    [Fact]
    public async Task Password_SurvivesRestart_AndComesBackLocked()
    {
        await _vault.SetupAsync("1234");
        Assert.True(_vault.IsUnlocked);

        await _vault.InitializeAsync();

        Assert.True(_vault.IsConfigured);
        Assert.False(_vault.IsUnlocked);
        Assert.False(await _vault.OpenAsync());
    }

    [Fact]
    public async Task RemovePassword_NeedsCurrentOne_ThenOpensFreelyAgain()
    {
        await _vault.SetupAsync("1234");

        Assert.False(await _vault.RemovePasswordAsync("wrong"));
        Assert.True(_vault.IsConfigured);

        Assert.True(await _vault.RemovePasswordAsync("1234"));
        Assert.False(_vault.IsConfigured);

        // 回到「不设密码」的默认形态：锁一下再进，一次就过
        _vault.Lock();
        Assert.True(await _vault.OpenAsync());

        // 数据库里也确实清掉了，不会重启后又冒出一道门
        var stored = await _repo.GetSettingAsync(VaultService.CredentialSettingKey);
        Assert.True(string.IsNullOrEmpty(stored));
    }

    /// <summary>只用到 DataDirectory 的极简配置桩。</summary>
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
