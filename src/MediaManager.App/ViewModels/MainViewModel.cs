using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Enums;
using MediaManager.Core.Media;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.App.Collections;
using MediaManager.App.Views.Dialogs;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.ViewModels;

/// <summary>主窗口视图模型。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;

    private readonly IMediaRepository _repo;

    private readonly IScanService _scanService;

    private readonly IThumbnailService _thumbnailService;

    private readonly IFileOperationService _fileOps;

    private readonly IOperationLogService _opLog;

    private readonly IExifService _exifService;

    private readonly IAppConfigService _config;

    private readonly IFileWatcherService _watcher;

    private readonly MediaManager.App.Services.DeletedItemsService _deletedItems;

    private CancellationTokenSource? _scanCts;

    private MediaFilter _currentFilter = new();

    private string _currentViewKey = "discovery";

    private long? _currentRootFolderId;

    /// <summary>
    /// 当前正在看的文件夹路径。选中根目录时为空（用 RootFolderId 过滤），
    /// 选中子文件夹时是完整路径 —— 子文件夹没有独立 RootFolder 记录，只能按路径查。
    /// </summary>
    private string? _currentFolderPath;

    private long? _currentTagId;

    private long? _currentCategoryId;

    /// <summary>
    /// 当前是否在看私密空间。
    /// 刻意从 <c>_currentViewKey</c> 派生而不是单独存一个标志位 ——
    /// 两个状态源很容易错位（视图已经切回「全部」，标志位还在 true），
    /// 那意味着 BuildFilter 会继续查私密内容，是泄露级的 bug。
    /// v1.4.0 起私密空间是独立窗口，主界面永远不在私密视图；保留此属性只为兼容旧引用。
    /// </summary>
    public bool IsVaultView => false;

    /// <summary>列表分页大小（「加载更多」一次追加的条数）。</summary>
    private const int PageSize = 2000;

    private int _totalCount;

    private bool _isLoadingMore;

    // 发现页状态
    private DiscoveryPoolType _discoveryPool = DiscoveryPoolType.Favorite;

    private int _discoveryCount = 30;

    private long? _discoveryPoolId;

    private bool _discoveryOnboardingShown;

    /// <summary>本批推荐是否因为「池子空着」而回退到了全部媒体（用于状态栏提示）。</summary>
    private bool _discoveryFallbackToAll;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IMediaRepository repo,
        IScanService scanService,
        IThumbnailService thumbnailService,
        IFileOperationService fileOps,
        IOperationLogService opLog,
        IExifService exifService,
        IAppConfigService config,
        IFileWatcherService watcher,
        MediaManager.App.Services.DeletedItemsService deletedItems)
    {
        _logger = logger;
        _repo = repo;
        _scanService = scanService;
        _thumbnailService = thumbnailService;
        _fileOps = fileOps;
        _opLog = opLog;
        _exifService = exifService;
        _config = config;
        _watcher = watcher;
        _deletedItems = deletedItems;

        _watcher.Changed += OnFileSystemChanged;
        _thumbnailService.ThumbnailReady += OnThumbnailReady;

        // 类型筛选：监听 IsSelected 变化（而不是 CheckBox 的 Checked/Unchecked，
        // 后者的触发时机可能早于 TwoWay 绑定写回，导致反向点击时筛选不生效）
        foreach (var option in TypeFilterOptions)
            option.PropertyChanged += OnTypeFilterOptionChanged;

        // 必须在 UI 线程构造：内部 DispatcherTimer 绑定构造线程的 Dispatcher，
        // 在后台线程 new 出来的 Timer 永远不会 Tick。
        ThumbLoading = new MediaManager.App.Services.ThumbnailLoadMonitor();
    }

    /// <summary>缩略图加载状态（状态栏指示：骨架屏之外的第二层反馈）。</summary>
    public MediaManager.App.Services.ThumbnailLoadMonitor ThumbLoading { get; }

    private int _initState;   // 0=未开始 1=已完成/进行中（防重复初始化）

    /// <summary>
    /// 初始化（加载配置、导航、媒体列表、开启监控）。
    /// 注意：不要在构造函数里调用。构造函数运行在 UI 线程，而 SQLite 的「异步」API
    /// 实际是同步执行、await 不会让出 UI 线程，会直接把窗口显示堵在这条链路上
    /// （实测 3.3 秒才出窗口）。改由 App 在窗口显示后调用，并把查询放到线程池。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initState, 1) == 1) return;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 配置读取里有多次 DB 查询 + PATH 目录探测，全部挪出 UI 线程
            await Task.Run(async () => await _config.EnsureLoadedAsync());
            _logger.LogDebug("配置加载完成 {Ms} ms", sw.ElapsedMilliseconds);

            await LoadDiscoverySettingsAsync();

            // 极简模式（只留基础入口）。放在列表加载之前，免得先按全量视图渲染一遍再切
            var simpleMode = await DbAsync(() => _repo.GetSettingAsync(SimpleModeSettingKey));
            IsSimpleMode = simpleMode == "1";

            await RefreshPrivateCountAsync();

            // 扩展格式（音频/文档/压缩包）开关：默认开启，可在 设置 → 插件扩展 关闭
            var extSetting = await DbAsync(() => _repo.GetSettingAsync("EnableExtendedFormats"));
            MediaFormat.SetExtendedFormatsEnabled(extSetting != "0");

            await LoadNavigationAsync();
            _logger.LogDebug("导航加载完成 {Ms} ms", sw.ElapsedMilliseconds);

            await LoadMediaAsync();
            _logger.LogDebug("媒体列表加载完成 {Ms} ms", sw.ElapsedMilliseconds);

            StartWatchers();
            _logger.LogInformation("MainViewModel 初始化完成（{Ms} ms）", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MainViewModel 初始化失败");
            StatusText = "初始化失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把同步实现的数据访问挪到线程池。
    /// Microsoft.Data.Sqlite 没有真正的异步 IO：QueryAsync 会在调用线程上同步跑完，
    /// 在 UI 线程上 await 它等于阻塞 UI。这里统一改到后台线程执行。
    /// </summary>
    private static Task<T> DbAsync<T>(Func<Task<T>> work) => Task.Run(work);

    private static Task DbAsync(Func<Task> work) => Task.Run(work);

    private void OnThumbnailReady(long mediaId, string thumbnailPath)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        // 枚举/修改 MediaItems 必须都在 UI 线程，否则后台线程枚举时集合正被 UI 线程 Clear/Add，
        // 会抛 "Collection was modified" 导致缩略图更新失败（表现为滚动后大片空白）。
        if (dispatcher.CheckAccess())
            ApplyThumbnail(mediaId, thumbnailPath);
        else
            dispatcher.Invoke(() => ApplyThumbnail(mediaId, thumbnailPath));
    }

    private void ApplyThumbnail(long mediaId, string thumbnailPath)
    {
        var item = MediaItems.FirstOrDefault(x => x.Id == mediaId);
        if (item == null) return;
        if (item.ThumbnailPath == thumbnailPath) return;
        item.ThumbnailPath = thumbnailPath;
    }

    // ---------- 可观察集合 ----------

    public RangeObservableCollection<ThumbnailItemViewModel> MediaItems { get; } = new();

    public ObservableCollection<NavigationItemViewModel> FolderItems { get; } = new();

    public ObservableCollection<NavigationItemViewModel> TagItems { get; } = new();

    public ObservableCollection<Category> CategoryItems { get; } = new();

    /// <summary>当前选中项已加入的分类。</summary>
    public ObservableCollection<Category> SelectedCategories { get; } = new();

    public ObservableCollection<KeyValuePair<string, string>> ExifItems { get; } = new();

    // ---------- 属性 ----------

    [ObservableProperty]
    private string _searchText = string.Empty;

    // ---------- 顶部属性筛选（叠加过滤，与左侧导航交叉组合） ----------

    /// <summary>仅显示已编辑副本。</summary>
    [ObservableProperty]
    private bool _editedOnly;

    /// <summary>顶部「类型」多选筛选项（图片/视频/音频/文档/压缩包）。</summary>
    public ObservableCollection<TypeFilterOption> TypeFilterOptions { get; } = new()
    {
        new() { Name = "图片", MediaType = Core.Enums.MediaType.Image },
        new() { Name = "视频", MediaType = Core.Enums.MediaType.Video },
        new() { Name = "音频", MediaType = Core.Enums.MediaType.Audio },
        new() { Name = "文档", MediaType = Core.Enums.MediaType.Text },
        new() { Name = "压缩包", MediaType = Core.Enums.MediaType.Archive },
    };

    [ObservableProperty]
    private int _thumbnailSize = 160;

    /// <summary>极简模式：只留最常用的入口，其余高级导航隐藏（文件本身不受任何影响）。</summary>
    [ObservableProperty]
    private bool _isSimpleMode;

    /// <summary>保险箱里的媒体数量（侧栏徽标）。</summary>
    [ObservableProperty]
    private long _privateCount;

    /// <summary>极简模式的设置键。</summary>
    public const string SimpleModeSettingKey = "Ui.SimpleMode";

    /// <summary>
    /// 极简模式藏起来的视图。注意「发现」不在里面 ——
    /// 对轻度用户来说「打开就有照片看」比一堆整理概念更好懂，藏了反而是减分。
    /// </summary>
    private static readonly string[] SimpleModeHiddenViews =
        { "unfiled", "recent-view", "recent", "trash" };

    partial void OnIsSimpleModeChanged(bool value)
    {
        // 正停在被藏起来的视图上（比如刚在「最近添加」里）→ 退回「全部媒体」，
        // 否则界面上一个高亮都没有，看着像卡死了
        if (value && SimpleModeHiddenViews.Contains(_currentViewKey))
            _ = SelectViewAsync("all");
    }

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>列表是否还有未加载的项（控制「加载更多」按钮）。</summary>
    [ObservableProperty]
    private bool _canLoadMore;

    /// <summary>「加载更多」按钮文案（含剩余数量）。</summary>
    [ObservableProperty]
    private string _loadMoreText = string.Empty;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private ThumbnailItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _detailName = string.Empty;

    [ObservableProperty]
    private string _detailMeta = string.Empty;

    [ObservableProperty]
    private string _detailDate = string.Empty;

    [ObservableProperty]
    private bool _detailIsVideo;

    [ObservableProperty]
    private string _noteEditor = string.Empty;

    // ---------- 发现页 ----------

    /// <summary>发现页池子类型（收藏/全部/未分类/指定分类）。</summary>
    [ObservableProperty]
    private int _discoveryPoolIndex;

    /// <summary>发现页是否显示空库引导。</summary>
    [ObservableProperty]
    private bool _discoveryShowWelcome;

    /// <summary>发现页是否显示首次提示。</summary>
    [ObservableProperty]
    private bool _discoveryShowHint;

    /// <summary>首次提示的文案。数量必须跟实际批次一致——库里只有 5 张时写死「30 张」是骗人。</summary>
    [ObservableProperty]
    private string _discoveryHintText = "这是随机挑出来的一批。按 R 换一批，按 F 收藏喜欢的。";

    /// <summary>发现页当前是否在展示（用于控制 R 换一批等）。</summary>
    [ObservableProperty]
    private bool _isDiscoveryView;

    /// <summary>发现页可选的指定分类（池子类型=指定时用）。</summary>
    public ObservableCollection<Category> DiscoveryPoolCategories { get; } = new();

    /// <summary>发现页下拉框选项。</summary>
    public IReadOnlyList<string> DiscoveryPoolOptions { get; } = new[]
    {
        "收藏", "全部", "未分类", "指定分类",
    };

    // ---------- 侧栏数量显示 ----------

    [ObservableProperty]
    private int _favoriteCount;

    [ObservableProperty]
    private int _tagCount;

    [ObservableProperty]
    private int _categoryCount;

    /// <summary>最近查看数量。</summary>
    [ObservableProperty]
    private int _recentViewCount;

    // ---------- 初始化 ----------
}

/// <summary>
/// 保存副本后刷新主界面的结果。
/// <para><see cref="Item"/> 为 null 表示副本不在库内（独立打开的文件）。</para>
/// <para><see cref="ViewChanged"/> 为 true 时，界面需要同步左侧导航的选中项；
/// <see cref="FolderId"/> 为 null 表示切到了「全部媒体」，否则是切到了该根目录。</para>
/// </summary>
public sealed record SaveRefreshResult(ThumbnailItemViewModel? Item, bool ViewChanged, long? FolderId)
{
    public static readonly SaveRefreshResult None = new(null, false, null);
}
