using MediaManager.Core.Enums;
using MediaManager.Core.Services;

namespace MediaManager.Infrastructure;

/// <summary>应用配置服务：从 AppSetting 表读取用户配置。</summary>
public class AppConfigService : IAppConfigService
{
    private readonly IMediaRepository _repo;
    private readonly IPortableModeDetector _detector;
    private readonly Lazy<Task> _loadTask;

    public AppConfigService(IMediaRepository repo, IPortableModeDetector detector)
    {
        _repo = repo;
        _detector = detector;
        _loadTask = new Lazy<Task>(LoadAsync);
    }

    public string DataDirectory => _detector.DataDirectory;
    public string ThumbnailDirectory => _detector.ThumbnailDirectory;

    private string? _ffmpegPath;
    private int _concurrency = 4;
    private AppTheme _theme = AppTheme.Light;
    private AppLanguage _language = AppLanguage.Chinese;
    private DeleteBehavior _deleteBehavior = DeleteBehavior.RecycleBin;

    public string? FfmpegPath => _ffmpegPath;
    public int Concurrency => _concurrency;
    public AppTheme Theme => _theme;
    public AppLanguage Language => _language;
    public DeleteBehavior DeleteBehavior => _deleteBehavior;

    public Task EnsureLoadedAsync() => _loadTask.Value;

    /// <summary>
    /// 运行时切换主题后同步缓存值（界面已经换了主题，这里把内存配置对齐）。
    /// 缺了这一步，设置窗口会一直显示启动时读到的旧主题。
    /// </summary>
    public void UpdateTheme(AppTheme theme) => _theme = theme;

    /// <summary>
    /// 重新从数据库拉一遍配置。设置窗口保存后调用，
    /// 避免内存快照过期导致「再保存一次其他设置就把刚才的改动冲掉」。
    /// </summary>
    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _ffmpegPath = await _repo.GetSettingAsync("FfmpegPath");
        // 若用户未手动设置，则自动探测 FFmpeg
        if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
            _ffmpegPath = DetectFfmpeg();

        _concurrency = ParseInt(await _repo.GetSettingAsync("Concurrency"), 4);
        _theme = ParseEnum(await _repo.GetSettingAsync("Theme"), AppTheme.Light);
        // 语言切换 UI 已隐藏（仅保留中文）：无论库中曾存何值，一律按中文加载。
        _language = AppLanguage.Chinese;
        _deleteBehavior = ParseEnum(await _repo.GetSettingAsync("DeleteBehavior"), DeleteBehavior.RecycleBin);
    }

    /// <summary>自动探测 FFmpeg：tools/ffmpeg/ → PATH → 常见安装位置。</summary>
    private static string? DetectFfmpeg()
    {
        var candidates = new List<string>();

        // 1. 程序目录旁的 tools/ffmpeg/
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "ffmpeg.exe"));
        // 开发环境：项目根目录的 tools/ffmpeg/
        var up = Directory.GetParent(baseDir);
        for (int i = 0; i < 5 && up != null; i++, up = up.Parent)
        {
            candidates.Add(Path.Combine(up.FullName, "tools", "ffmpeg", "ffmpeg.exe"));
        }

        // 2. PATH 中的 ffmpeg
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            candidates.Add(Path.Combine(dir, "ffmpeg.exe"));
        }

        // 3. 常见安装位置
        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        candidates.Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe");

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }
        return null;
    }

    private static int ParseInt(string? s, int def) => int.TryParse(s, out var v) ? v : def;

    private static T ParseEnum<T>(string? s, T def) where T : struct
        => Enum.TryParse<T>(s, out var v) ? v : def;
}
