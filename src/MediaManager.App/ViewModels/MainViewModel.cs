using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Enums;
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

    private async Task LoadNavigationAsync()
    {
        // 一次后台往返把导航数据全部取回（SQLite 的"异步"实际同步执行，必须离开 UI 线程）
        var data = await DbAsync(async () => (
            Folders: await _repo.GetRootFoldersAsync(),
            Tags: await _repo.GetTagsAsync(),
            Categories: await _repo.GetCategoriesAsync(),
            FavoriteCount: await _repo.GetFavoriteCountAsync(),
            TagCount: await _repo.GetTagCountAsync(),
            CategoryCount: await _repo.GetCategoryCountAsync()));

        FolderItems.Clear();

        // 保险箱锚点目录（路径 = 数据目录下的 Vault）只为满足外键存在，
        // 不能出现在侧栏 —— 否则用户点进去看到一片空白，还暴露了保险箱目录位置
        var vaultDir = System.IO.Path.Combine(_config.DataDirectory, "Vault")
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        foreach (var f in data.Folders)
        {
            if (string.Equals(
                    f.Path?.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    vaultDir, StringComparison.OrdinalIgnoreCase))
                continue;

            var fullPath = f.Path ?? string.Empty;
            var (title, subTitle) = SplitFolderPath(fullPath);

            FolderItems.Add(new NavigationItemViewModel
            {
                // 侧栏只放末级目录名：以前直接显示整条路径，宽度不够被截成
                // 「C:\Users\xxx\Videos\Capt…」，两个目录看起来一模一样，选哪个全靠猜
                Title = title,
                SubTitle = subTitle,
                ToolTipText = fullPath,
                Icon = "📂",
                Key = "folder",
                RelatedId = f.Id,
            });
        }

        TagItems.Clear();
        foreach (var t in data.Tags)
        {
            TagItems.Add(new NavigationItemViewModel
            {
                Title = t.Name,
                Icon = "🔖",
                Key = "tag",
                RelatedId = t.Id,
            });
        }

        CategoryItems.Clear();
        foreach (var c in data.Categories)
            CategoryItems.Add(c);

        // 发现页「指定分类」的候选（v1.3.2 起收藏夹已并入分类，只列顶级分类）
        DiscoveryPoolCategories.Clear();
        foreach (var c in data.Categories.Where(c => c.ParentId == null))
            DiscoveryPoolCategories.Add(c);

        // 侧栏数量
        FavoriteCount = (int)data.FavoriteCount;
        TagCount = (int)data.TagCount;
        CategoryCount = (int)data.CategoryCount;
    }

    private void StartWatchers()
    {
        foreach (var f in FolderItems)
        {
            if (f.RelatedId.HasValue && System.IO.Directory.Exists(f.ToolTipText))
                _watcher.Start(f.RelatedId.Value, f.ToolTipText!);
        }
    }

    /// <summary>
    /// 把一条文件夹路径拆成「侧栏标题 + 副标题」。
    /// 例：<c>C:\Users\me\Pictures\Screenshots</c> → （<c>Screenshots</c>, <c>C:\…\Pictures</c>）。
    /// 盘根本身（<c>D:\</c>）没有末级目录名，直接用路径当标题。
    /// </summary>
    private static (string Title, string SubTitle) SplitFolderPath(string path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
            return (trimmed, "整个磁盘");

        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? string.Empty;
        var parentName = System.IO.Path.GetFileName(parent.TrimEnd('\\', '/'));
        var root = System.IO.Path.GetPathRoot(trimmed) ?? string.Empty;
        root = root.TrimEnd('\\', '/');

        // 副标题带盘符：不同盘上同名的两个目录（都有 Screenshots）靠它一眼分开
        var subTitle = string.IsNullOrEmpty(parentName)
            ? root
            : $"{root}\\…\\{parentName}";

        return (name, subTitle);
    }

    /// <summary>列表加载代次：并发刷新时只有最后一次的结果能落到界面，避免旧查询覆盖新结果。</summary>
    private int _loadGeneration;

    private async Task LoadMediaAsync()
    {
        var generation = ++_loadGeneration;
        CanLoadMore = false;
        LoadMoreText = string.Empty;

        if (!HasSelectedType)
        {
            if (_currentViewKey == "discovery") IsDiscoveryView = true;
            MediaItems.Clear();
            StatusText = "未选择任何类型（顶部「类型」里至少勾选一个）";
            return;
        }

        if (_currentViewKey == "discovery")
        {
            await LoadDiscoveryAsync();
            return;
        }
        if (_currentViewKey == "recent-view")
        {
            await LoadRecentViewAsync();
            return;
        }

        _currentFilter = BuildFilter();
        var swLoad = System.Diagnostics.Stopwatch.StartNew();

        // 查询放到线程池：SQLite 的 await 不会让出 UI 线程
        var (total, items) = await DbAsync(async () =>
        {
            var count = await _repo.CountAsync(_currentFilter);
            var list = await _repo.QueryAsync(_currentFilter);
            return (count, list);
        });
        if (generation != _loadGeneration) return; // 期间又触发了新加载，丢弃这次结果
        _logger.LogDebug("  · 查询 {Ms} ms（{Count} 条）", swLoad.ElapsedMilliseconds, items.Count);

        var vms = items.Select(ToViewModel).ToList();

        // 分类标记必须在绑定之前算好：绑定之后再改 IsCategorized 会让每一项都触发
        // Visibility 变化 → 布局重算，50 项就要 400ms+（实测），这也是首屏慢的主因之一。
        await ApplyCategoryMarksAsync(vms);
        if (generation != _loadGeneration) return;

        // 一次性替换：只触发一次布局，比逐条 Add 快很多
        MediaItems.ReplaceAll(vms);

        _totalCount = total;
        UpdateLoadMoreState();

        // 一次性把缺缩略图的排队（同目录只探测一次，去掉逐条系统调用）
        EnsureThumbnails(items);
        _logger.LogDebug("  · 列表绑定 {Ms} ms", swLoad.ElapsedMilliseconds);

        StatusText = total > MediaItems.Count
            ? $"已加载 {MediaItems.Count} / 共 {total} 项"
            : $"共 {total} 项";
        _logger.LogDebug("  · 列表就绪 {Ms} ms", swLoad.ElapsedMilliseconds);
    }

    /// <summary>
    /// 「加载更多」：只追加一页，保留当前滚动位置和已选项。
    /// 方案 A：按钮显式触发，不自动加载，避免滚动时位置跳动/重复请求。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_isLoadingMore || !CanLoadMore) return;
        if (_currentViewKey is "discovery" or "recent-view") return;

        _isLoadingMore = true;
        var generation = _loadGeneration;
        try
        {
            var filter = BuildFilter();
            filter.Offset = MediaItems.Count;
            filter.Limit = PageSize;

            var items = await DbAsync(() => _repo.QueryAsync(filter));
            if (generation != _loadGeneration) return;
            if (items.Count == 0)
            {
                // 库里的数据变少了：以实际为准，别让按钮一直挂着
                _totalCount = MediaItems.Count;
                UpdateLoadMoreState();
                return;
            }

            var vms = items.Select(ToViewModel).ToList();
            await ApplyCategoryMarksAsync(vms);
            if (generation != _loadGeneration) return;

            MediaItems.AddRange(vms);
            EnsureThumbnails(items);

            _totalCount = Math.Max(_totalCount, MediaItems.Count);
            UpdateLoadMoreState();
            StatusText = $"已加载 {MediaItems.Count} / 共 {_totalCount} 项";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载更多失败");
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private void UpdateLoadMoreState()
    {
        var remaining = _totalCount - MediaItems.Count;
        CanLoadMore = remaining > 0;
        LoadMoreText = remaining > 0 ? $"加载更多（还有 {remaining} 项）" : string.Empty;
    }

    /// <summary>MediaItem → 列表项（纯内存对象，不碰磁盘）。</summary>
    private static ThumbnailItemViewModel ToViewModel(MediaItem m) => new(m)
    {
        ThumbnailPath = m.ThumbnailPath,
        IsFavorite = m.Favorite,
        Rating = m.Rating,
        Note = m.Note,
    };

    /// <summary>
    /// 编辑器保存副本后调用：把副本补进库、刷新当前网格，并返回应当选中的列表项。
    ///
    /// 之前保存完只弹一个 2 秒的小 toast，主界面完全不动，用户会以为「保存没生效」。
    /// 这里保证副本一定能出现在列表里（当前筛选看不到时自动切到它所在的根目录）。
    /// </summary>
    public async Task<SaveRefreshResult> RefreshAfterSaveAsync(string savedPath, bool overwritten = false)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return SaveRefreshResult.None;

        var full = System.IO.Path.GetFullPath(savedPath);
        if (!System.IO.File.Exists(full)) return SaveRefreshResult.None;

        // 副本必须位于某个已加入库的根目录下；独立打开的文件（不在库里）直接跳过
        var roots = await DbAsync(() => _repo.GetRootFoldersAsync());
        var root = roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && IsUnder(full, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

        if (root == null) return SaveRefreshResult.None;

        try
        {
            var info = new System.IO.FileInfo(full);
            var item = new MediaItem
            {
                RootFolderId = root.Id,
                Path = full,
                FileName = info.Name,
                Extension = info.Extension,
                MediaType = MediaFormat.GetMediaType(full) ?? MediaType.Image,
                SizeBytes = info.Length,
                CreatedAtUtc = info.CreationTimeUtc.ToString("O"),
                ModifiedAtUtc = info.LastWriteTimeUtc.ToString("O"),
                LastScanUtc = DateTime.UtcNow.ToString("O"),
            };

            if (MediaSizeReader.TryReadImageSize(full, out var w, out var h))
            {
                item.Width = w;
                item.Height = h;
            }

            await DbAsync(() => _repo.UpsertMediaAsync(item));

            // 原地覆盖了副本：磁盘上的缩略图还是旧画面，必须让它失效并重建，
            // 否则列表里永远显示编辑前的样子。
            if (overwritten && item.Id > 0)
            {
                await DbAsync(() => _repo.InvalidateThumbnailAsync(item.Id));
                // 缓存文件名带内容签名，但旧文件还占着磁盘；删掉并同步清内存 LRU，
                // 这样无论 Id 是否被复用都不会命中上一轮的图。
                foreach (var stale in _thumbnailService.Invalidate(item.Id))
                    Services.ThumbnailMemoryCache.Remove(stale);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存副本入库失败：{Path}", full);
            return SaveRefreshResult.None;
        }

        // 刷新当前视图（发现页 / 最近查看 / 各种筛选都走各自的分支）
        await SafeReloadAsync();

        var vm = FindByPath(full);
        var viewChanged = false;
        long? folderId = null;

        // 当前视图看不到副本（典型：发现页、搜索过滤中）→ 切「全部媒体」，新副本按添加时间排在最前
        if (vm == null)
        {
            await SelectViewAsync("all");
            viewChanged = true;
            vm = FindByPath(full);
        }

        // 还被搜索词挡住 → 清掉搜索再找一次（用户刚保存完，最想看的就是这张图）
        if (vm == null && !string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
            await LoadMediaAsync();
            vm = FindByPath(full);
        }

        // 最后兜底：直接切到副本所在的根目录
        if (vm == null)
        {
            await SelectViewAsync($"folder:{root.Id}");
            viewChanged = true;
            folderId = root.Id;
            vm = FindByPath(full);
        }

        if (vm != null)
        {
            SelectedItem = vm;
            await RefreshStatusAsync();
            StatusText = overwritten ? $"已更新副本：{vm.FileName}" : $"已保存新副本：{vm.FileName}";
        }

        return new SaveRefreshResult(vm, viewChanged, folderId);
    }

    private async Task SafeReloadAsync()
    {
        try
        {
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            // 刷新失败不应该让「保存成功」变成异常提示，只记录日志
            _logger.LogWarning(ex, "保存副本后刷新列表失败");
        }
    }

    private ThumbnailItemViewModel? FindByPath(string fullPath)
        => MediaItems.FirstOrDefault(v =>
            string.Equals(v.Media.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string file, string folder)
    {
        var full = System.IO.Path.GetFullPath(folder);
        if (!full.EndsWith(System.IO.Path.DirectorySeparatorChar))
            full += System.IO.Path.DirectorySeparatorChar;

        return file.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 批量投递缩略图任务。相比逐条 <c>File.Exists</c>/<c>Directory.Exists</c>，
    /// 这里只对「同一个根目录」探测一次在线状态，500 项能省掉近千次系统调用。
    /// </summary>
    private void EnsureThumbnails(List<MediaItem> items)
    {
        Dictionary<string, bool>? rootOnline = null;
        foreach (var m in items)
        {
            // 音频/文档/压缩包不生成缩略图（列表用固定图标），避免 worker 空转
            if (m.MediaType is Core.Enums.MediaType.Audio or Core.Enums.MediaType.Text or Core.Enums.MediaType.Archive)
                continue;

            // 当前源文件（大小 + 修改时间）对应的缓存是否已经存在。
            // 存在就直接用；和库里记的旧路径不一致时切过去 —— 源文件被原地覆盖后
            // 旧缓存必须立刻作废，否则列表会一直显示编辑前的画面。
            var expected = _thumbnailService.GetCachedPath(m);
            if (expected != null)
            {
                if (!string.Equals(m.ThumbnailPath, expected, StringComparison.OrdinalIgnoreCase))
                    m.ThumbnailPath = expected;
                continue;
            }

            // 缓存不存在（首次入库 / 缩略图被删 / 源文件已变化）→ 清掉旧路径后重新生成
            m.ThumbnailPath = null;

            rootOnline ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var root = System.IO.Path.GetPathRoot(m.Path) ?? string.Empty;
            if (!rootOnline.TryGetValue(root, out var online))
            {
                // 源文件所在设备不在线（移动硬盘被拔）时先不排队，避免 worker 空转
                online = string.IsNullOrEmpty(root) || System.IO.Directory.Exists(root);
                rootOnline[root] = online;
            }
            if (!online) continue;

            _thumbnailService.Enqueue(m);
        }
    }

    /// <summary>
    /// 批量填充缩略图的分类标记（一次查询，避免逐条 N+1）。
    /// 只应在列表绑定到界面之前调用：绑定后改 IsCategorized 会触发逐项布局重算。
    /// </summary>
    private async Task ApplyCategoryMarksAsync(List<ThumbnailItemViewModel> targets)
    {
        if (targets.Count == 0) return;

        var ids = targets.Select(v => v.Id).ToList();
        var map = await DbAsync(() => _repo.GetMediaCategoryMapAsync(ids));
        var nameById = CategoryItems.ToDictionary(c => c.Id, c => c.Name);

        foreach (var vm in targets)
        {
            if (!map.TryGetValue(vm.Id, out var catIds) || catIds.Count == 0)
            {
                vm.CategoryIds.Clear();
                vm.CategoryNames = string.Empty;
                vm.RefreshCategoryState();
                continue;
            }

            vm.CategoryIds.Clear();
            var names = new List<string>();
            foreach (var cid in catIds)
            {
                vm.CategoryIds.Add(cid);
                if (nameById.TryGetValue(cid, out var n))
                    names.Add(n);
            }
            vm.CategoryNames = string.Join("、", names);
            vm.RefreshCategoryState();
        }
    }

    /// <summary>加载发现页（随机推荐，当日固定）。</summary>
    private async Task LoadDiscoveryAsync()
    {
        var generation = _loadGeneration;
        IsDiscoveryView = true;
        var swD = System.Diagnostics.Stopwatch.StartNew();
        var total = await DbAsync(() => _repo.GetTotalCountAsync());
        _logger.LogDebug("  [发现] 总数 {Ms} ms", swD.ElapsedMilliseconds);

        // 空库 → 欢迎引导
        if (total == 0)
        {
            MediaItems.Clear();
            DiscoveryShowWelcome = true;
            DiscoveryShowHint = false;
            StatusText = "欢迎使用媒体管理器";
            return;
        }
        DiscoveryShowWelcome = false;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var batch = await DbAsync(() => _repo.GetDiscoveryBatchAsync(today));
        _logger.LogDebug("  [发现] 批次 {Ms} ms", swD.ElapsedMilliseconds);

        List<MediaItem> media;
        var typeKey = GetDiscoveryTypeKey();
        if (batch != null && batch.MediaIds.Count > 0 &&
            string.Equals(batch.TypeFilter ?? "all", typeKey, StringComparison.Ordinal))
        {
            // 当天已有“同一类型范围”的批次，直接复用（同一天重开看到同一批）
            media = await DbAsync(() => _repo.GetByIdsAsync(batch.MediaIds));

            // 若批次里有媒体已被删除/还原后换了新 Id，查到的数量会变少，
            // 此时重新生成一批，避免发现页出现空白项
            if (media.Count < batch.MediaIds.Count)
            {
                media = await GenerateDiscoveryBatchAsync();
            }
        }
        else
        {
            // 没有批次，或类型筛选变了（旧批次只包含某个子集）→ 重新生成
            media = await GenerateDiscoveryBatchAsync();
        }

        var discoveryVms = media.Select(ToViewModel).ToList();
        await ApplyCategoryMarksAsync(discoveryVms);
        if (generation != _loadGeneration) return;
        MediaItems.ReplaceAll(discoveryVms);
        _logger.LogDebug("  [发现] 绑定 {Ms} ms（{Count} 项）", swD.ElapsedMilliseconds, media.Count);
        EnsureThumbnails(media);

        // 首次提示（仅一次）
        if (!_discoveryOnboardingShown && MediaItems.Count > 0)
        {
            DiscoveryShowHint = true;
            _discoveryOnboardingShown = true;
            await DbAsync(() => _repo.SetSettingAsync("DiscoveryHintShown", "1"));
        }
        else
        {
            DiscoveryShowHint = false;
        }

        StatusText = _discoveryFallbackToAll
            ? $"发现 · {MediaItems.Count} 项随机推荐（收藏还是空的，这次先看全库）"
            : $"发现 · {MediaItems.Count} 项随机推荐";
    }

    /// <summary>生成一批新的随机推荐。</summary>
    private async Task<List<MediaItem>> GenerateDiscoveryBatchAsync(bool forceRefresh = false)
    {
        var poolType = _discoveryPool;
        var count = _discoveryCount;
        var poolId = poolType == DiscoveryPoolType.Category ? _discoveryPoolId : null;
        var discoveryTypes = GetDiscoveryTypes();

        // 池子总数，用于决定是否排除最近 7 天已推
        var poolTotal = await DbAsync(() => _repo.CountDiscoveryPoolAsync(poolType, poolId, discoveryTypes));

        // 池子空着就回退到「全部」。
        // 默认推荐池是「收藏」，新用户一张都没收藏，发现页就一片空白 ——
        // 明明是整库几千张照片，点进来却像坏了，这个功能等于白做。
        if (poolTotal == 0 && poolType != DiscoveryPoolType.All)
        {
            _logger.LogInformation("发现页推荐池为空（{Pool}），本次回退到全部媒体", poolType);
            poolType = DiscoveryPoolType.All;
            poolId = null;
            _discoveryFallbackToAll = true;
            poolTotal = await DbAsync(() => _repo.CountDiscoveryPoolAsync(poolType, poolId, discoveryTypes));
        }
        else
        {
            _discoveryFallbackToAll = false;
        }

        List<long>? exclude = null;
        if (poolTotal >= count * 3)
        {
            var recentIds = await DbAsync(() => _repo.GetDiscoveryHistoryIdsAsync(7));
            if (recentIds.Count > 0)
                exclude = recentIds;
        }

        var media = await DbAsync(() => _repo.QueryDiscoveryAsync(poolType, poolId, count, exclude, discoveryTypes));

        // 若排除后不足 N，补齐（不排除）
        if (media.Count < count)
        {
            var existingIds = media.Select(m => m.Id).ToList();
            var more = await DbAsync(() => _repo.QueryDiscoveryAsync(poolType, poolId, count - media.Count,
                existingIds.Count > 0 ? existingIds : null, discoveryTypes));
            media.AddRange(more);
        }

        var ids = media.Select(m => m.Id).Distinct().ToList();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var typeKey = GetDiscoveryTypeKey();
        await DbAsync(async () =>
        {
            await _repo.SaveDiscoveryBatchAsync(today, ids, poolType, poolId, typeKey);
            await _repo.RecordDiscoveryHistoryAsync(ids);
        });

        return media;
    }

    /// <summary>加载最近查看。</summary>
    private async Task LoadRecentViewAsync()
    {
        var generation = _loadGeneration;
        var items = await DbAsync(() => _repo.GetRecentViewAsync(100));
        var recentVms = items.Select(ToViewModel).ToList();
        await ApplyCategoryMarksAsync(recentVms);
        if (generation != _loadGeneration) return;
        MediaItems.ReplaceAll(recentVms);
        EnsureThumbnails(items);
        StatusText = $"最近查看 · {MediaItems.Count} 项";
    }

    /// <summary>从 AppSetting 加载发现页配置。</summary>
    private async Task LoadDiscoverySettingsAsync()
    {
        var (poolStr, countStr, poolIdStr, hintStr) = await DbAsync(async () => (
            await _repo.GetSettingAsync("DiscoveryPoolType"),
            await _repo.GetSettingAsync("DiscoveryCount"),
            await _repo.GetSettingAsync("DiscoveryPoolId"),
            await _repo.GetSettingAsync("DiscoveryHintShown")));

        if (Enum.TryParse<DiscoveryPoolType>(poolStr, out var pool))
            _discoveryPool = pool;
        else
            _discoveryPool = DiscoveryPoolType.Favorite;

        if (int.TryParse(countStr, out var cnt) && cnt > 0)
            _discoveryCount = cnt;

        if (long.TryParse(poolIdStr, out var pid))
            _discoveryPoolId = pid;

        _discoveryOnboardingShown = hintStr == "1";

        DiscoveryPoolIndex = (int)_discoveryPool;
    }

    private MediaFilter BuildFilter()
    {
        var f = new MediaFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            SortField = SortField.DateAdded,
            SortDirection = SortDirection.Descending,
            Limit = PageSize,
            // 主界面所有视图（含搜索、类型筛选、文件夹、标签）一律查普通内容；
            // 保险箱内容由独立的 VaultWindow 用 IncludePrivate=true 自行查询
            IncludePrivate = false,
        };

        switch (_currentViewKey)
        {
            case "favorites":
                f.Favorite = true;
                break;
            case "unfiled":
                f.Unfiled = true;
                break;
            case "recent":
                f.RecentDays = 7;
                break;
            case "folder":
                f.RootFolderId = _currentRootFolderId;
                break;
            case "tag":
                f.TagId = _currentTagId;
                break;
            case "category":
                f.CategoryId = _currentCategoryId;
                break;
        }

        // 顶部类型多选：全部勾选 = 不筛选；部分勾选 = 只显示选中的类型
        var selectedTypes = TypeFilterOptions.Where(o => o.IsSelected).Select(o => o.MediaType).ToList();
        if (selectedTypes.Count > 0 && selectedTypes.Count < TypeFilterOptions.Count)
            f.MediaTypes = selectedTypes;

        if (EditedOnly)
            f.EditedOnly = true;

        return f;
    }

    // ---------- 顶部类型筛选变化 ----------

    partial void OnEditedOnlyChanged(bool value) => _ = DebounceFilterReloadAsync();

    /// <summary>是否有勾选任何类型。</summary>
    private bool HasSelectedType => TypeFilterOptions.Any(o => o.IsSelected);

    private void OnTypeFilterOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TypeFilterOption.IsSelected))
            _ = DebounceFilterReloadAsync(includeDiscovery: true);
    }

    /// <summary>全选类型。</summary>
    public void SelectAllTypes()
    {
        foreach (var o in TypeFilterOptions) o.IsSelected = true;
    }

    /// <summary>全不选类型（列表会显示为空）。</summary>
    public void ClearAllTypes()
    {
        foreach (var o in TypeFilterOptions) o.IsSelected = false;
    }

    /// <summary>发现页类型筛选键：全部 / 具体子集，用来判断缓存的每日批次是否还适用。</summary>
    private string GetDiscoveryTypeKey()
    {
        var selected = TypeFilterOptions.Where(o => o.IsSelected)
            .Select(o => o.MediaType)
            .OrderBy(t => t)
            .ToList();
        if (selected.Count == 0) return "none";
        if (selected.Count == TypeFilterOptions.Count) return "all";
        return string.Join(",", selected);
    }

    /// <summary>发现页当前允许的媒体类型：全部勾选返回 null（不限制）；部分勾选返回选中的；一个没选返回空。</summary>
    private IReadOnlyList<Core.Enums.MediaType>? GetDiscoveryTypes()
    {
        var selected = TypeFilterOptions.Where(o => o.IsSelected).Select(o => o.MediaType).ToList();
        if (selected.Count == 0) return Array.Empty<Core.Enums.MediaType>();
        if (selected.Count == TypeFilterOptions.Count) return null;
        return selected;
    }

    private CancellationTokenSource? _filterCts;
    private async Task DebounceFilterReloadAsync(bool includeDiscovery = false)
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;
        try
        {
            await Task.Delay(200, token);
            // 类型筛选对发现页也生效；其余属性筛选仍跳过独立视图
            if (!token.IsCancellationRequested &&
                (includeDiscovery || _currentViewKey is not "discovery" and not "recent-view"))
                await LoadMediaAsync();
        }
        catch (TaskCanceledException) { }
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        // 系统原生文件夹选择器（IFileOpenDialog + FOS_PICKFOLDERS）：
        // 左侧导航、网络位置、新建文件夹等行为与资源管理器完全一致，不再自绘选择器。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择媒体文件夹",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        // 统一成反斜杠的完整路径：否则 C:/a/b 与 C://a//b 会被当成两个不同路径，
        // 扫描时会往库里插入重复记录（尤其是重命名后再增量扫描）。
        var path = System.IO.Path.GetFullPath(dialog.FolderName);
        var serial = VolumeHelper.GetVolumeSerial(path);
        var relative = VolumeHelper.GetRelativePath(path);

        // 检测是否已添加过（AddRootFolderAsync 已幂等，这里提前提示更友好）
        var existing = FolderItems.FirstOrDefault(f => f.RelatedId.HasValue
            && string.Equals(f.ToolTipText, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            StatusText = $"文件夹已存在：{path}";
            // 直接跳到该文件夹视图
            _currentViewKey = "folder";
            _currentRootFolderId = existing.RelatedId;
            await LoadMediaAsync();
            return;
        }

        try
        {
            var id = await _repo.AddRootFolderAsync(path, serial, relative);
            await LoadNavigationAsync();
            _watcher.Start(id, path);

            await ScanFolderAsync(id);

            // 扫描后如果这个文件夹里没有任何受支持的文件，弹窗说明原因
            // （典型场景：关掉“扩展格式”后选了一个只有 zip 的文件夹）
            var count = await DbAsync(() => _repo.CountAsync(new MediaFilter { RootFolderId = id }));
            if (count == 0)
            {
                StatusText = "该文件夹里没有当前支持的媒体文件。";
                CustomMessageBox.Info(
                    "这个文件夹里没有当前支持的媒体文件。\n\n" +
                    "如果里面有音频 / 文档 / 压缩包，请到 设置 → 插件扩展 启用「扩展格式支持」，再扫描一次。",
                    "没有可管理的文件");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加文件夹失败：{Path}", path);
            StatusText = "添加文件夹失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(NavigationItemViewModel? folder)
    {
        if (folder?.RelatedId == null) return;

        var confirm = CustomMessageBox.Confirm(
            $"确定从媒体库中移除文件夹「{folder.Title}」？\n\n" +
            "只会移除该文件夹的收录记录，磁盘上的文件不会被删除。",
            "移除文件夹", confirmText: "移除");
        if (!confirm) return;

        var rootId = folder.RelatedId.Value;
        var path = folder.ToolTipText ?? folder.Title;

        // 停止对该目录的监控，避免删除过程中触发增量扫描
        _watcher.Stop(rootId);

        // 删除前先取出该文件夹下的媒体项，用于清理缩略图缓存
        List<MediaItem> toClean = new();
        try
        {
            toClean = await _repo.QueryAsync(new MediaFilter { RootFolderId = rootId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移除文件夹前查询媒体项失败：{Path}", path);
        }

        await _repo.RemoveRootFolderAsync(rootId);
        await _opLog.LogAsync("移除文件夹", path, null);

        // 清理缩略图缓存
        foreach (var m in toClean)
        {
            try
            {
                var cachePath = m.ThumbnailPath ?? _thumbnailService.GetCachedPath(m);
                if (!string.IsNullOrWhiteSpace(cachePath) && System.IO.File.Exists(cachePath))
                    System.IO.File.Delete(cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理缩略图缓存失败：{Path}", m.Path);
            }
        }

        // 若当前视图正是该文件夹，切回「全部媒体」，避免停留在已删除的文件夹视图
        if (_currentViewKey == "folder" && _currentRootFolderId == rootId)
        {
            _currentViewKey = "all";
            _currentRootFolderId = null;
            IsDiscoveryView = false;
        }

        await LoadNavigationAsync();
        await LoadMediaAsync();
        StatusText = $"已移除文件夹：{path}";
    }

    [RelayCommand]
    private async Task ScanAllAsync()
    {
        foreach (var f in FolderItems.Where(x => x.RelatedId.HasValue))
        {
            await ScanFolderAsync(f.RelatedId!.Value);
        }
    }

    private async Task ScanFolderAsync(long rootFolderId)
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanProgressText = "扫描中…";
        ScanProgress = 0;

        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressText = $"{p.ScannedFiles} 个文件";
            if (p.Total > 0)
                ScanProgress = p.IsCompleted ? 100 : (double)p.ScannedFiles / Math.Max(1, p.Total) * 100;
            StatusText = p.IsCompleted
                ? $"扫描完成：新增 {p.Added}，更新 {p.Updated}，缺失 {p.MarkedMissing}"
                : $"扫描中 {p.ScannedFiles} 项";
        });

        try
        {
            await _scanService.ScanRootFolderAsync(rootFolderId, progress, _scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            // 典型：扫描途中移动硬盘被拔出、或某个子目录无权限
            _logger.LogError(ex, "扫描失败：根目录 {Id}", rootFolderId);
            StatusText = StorageErrors.IsDeviceUnavailable(ex)
                ? "扫描中断：设备已断开，请重新连接移动硬盘后重新扫描"
                : "扫描失败：" + StorageErrors.FriendlyMessage(ex);
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = string.Empty;
            ScanProgress = 0;
            _scanCts?.Dispose();
            _scanCts = null;

            // 扫描后刷新缩略图（新项）
            await LoadMediaAsync();
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>
    /// 停掉所有会读写数据库 / 磁盘的界面侧后台任务。
    /// 只在「恢复历史快照」这种要直接覆盖 library.db 的场景调用 ——
    /// 通道里只要还有一条查询在跑，覆盖就可能撞上正在打开的连接。
    /// </summary>
    public void CancelBackgroundWork()
    {
        try { CancelScan(); } catch { /* 已结束 */ }
        try { _filterCts?.Cancel(); } catch { /* 已结束 */ }
        try { _searchCts?.Cancel(); } catch { /* 已结束 */ }
        try { _detailCts?.Cancel(); } catch { /* 已结束 */ }
    }

    [RelayCommand]
    private async Task SelectViewAsync(string key)
    {
        _currentRootFolderId = null;
        _currentTagId = null;
        _currentCategoryId = null;

        if (key.StartsWith("folder:"))
        {
            _currentViewKey = "folder";
            _currentRootFolderId = long.Parse(key.Substring(7));
        }
        else if (key.StartsWith("tag:"))
        {
            _currentViewKey = "tag";
            _currentTagId = long.Parse(key.Substring(4));
        }
        else if (key.StartsWith("category:"))
        {
            _currentViewKey = "category";
            _currentCategoryId = long.Parse(key.Substring(9));
        }
        else
        {
            _currentViewKey = key;
        }

        IsDiscoveryView = _currentViewKey == "discovery";
        await LoadMediaAsync();
    }

    /// <summary>
    /// 只重读侧栏（文件夹 / 标签 / 分类 / 数量），不动当前网格、不清缩略图内存缓存。
    /// 用于「导入了一张图，侧栏里凭空多了个文件夹」这种小变化 ——
    /// 走整表刷新会把已解码的缩略图全部丢掉，代价太大。
    /// </summary>
    public Task ReloadNavigationAsync() => LoadNavigationAsync();

    /// <summary>重新统计保险箱里的媒体数量（移入 / 移出 / 删除后调用）。</summary>
    public async Task RefreshPrivateCountAsync()
    {
        PrivateCount = await DbAsync(() => _repo.GetPrivateCountAsync());
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await LoadMediaAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        // 防抖搜索
        _ = DebounceSearchAsync(value);
    }

    private CancellationTokenSource? _searchCts;
    private async Task DebounceSearchAsync(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
                await LoadMediaAsync();
        }
        catch (TaskCanceledException) { }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ThumbnailItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item == null) return;
        item.IsFavorite = !item.IsFavorite;
        await _repo.SetFavoriteAsync(item.Id, item.IsFavorite);
        await RefreshDetailAsync(item);
    }

    [RelayCommand]
    private async Task SetRatingAsync(string? ratingStr)
    {
        if (SelectedItem == null || !int.TryParse(ratingStr, out var rating)) return;
        SelectedItem.Rating = rating;
        await _repo.SetRatingAsync(SelectedItem.Id, rating);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem == null) return;

        var confirm = CustomMessageBox.ConfirmDanger(
            $"将把下面的文件移入回收站：\n\n{SelectedItem.FileName}\n\n删除后 5 秒内可在底部撤销。",
            "确认删除");
        if (!confirm) return;

        try
        {
            await _fileOps.DeleteToRecycleBinAsync(SelectedItem.Path);
        }
        catch (Exception ex)
        {
            // 常见：移动硬盘被拔出（设备未就绪）、文件被占用、无权限
            _logger.LogWarning(ex, "删除文件失败：{Path}", SelectedItem.Path);
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "删除"), "删除失败");
            return;
        }

        await _opLog.LogAsync("删除到回收站", SelectedItem.Path, null);

        // 先记录快照（此时数据库里的标签/分类关联还在），再删库；
        // 缩略图缓存保留，撤销后可以立刻显示，也方便「最近删除」里预览
        var record = await _deletedItems.TrackAsync(SelectedItem.Media, SelectedItem.ThumbnailPath);

        // 同步删除数据库记录，保证状态一致（还原后由撤销逻辑重新写回）
        await _repo.DeleteMediaAsync(SelectedItem.Id);

        MediaItems.Remove(SelectedItem);
        SelectedItem = null;
        await RefreshStatusAsync();

        ShowUndoToast(new[] { record });
    }

    // ---------- 操作轻提示（只反馈，不带按钮） ----------

    /// <summary>底部轻提示是否显示。</summary>
    [ObservableProperty]
    private bool _isActionToastVisible;

    /// <summary>底部轻提示文案（如「✅ 已加入分类【旅行】」）。</summary>
    [ObservableProperty]
    private string _actionToastText = string.Empty;

    private System.Windows.Threading.DispatcherTimer? _actionToastTimer;

    /// <summary>
    /// 弹一条 1.8 秒的轻提示。连续调用会重置计时（拖 10 张进去不会叠 10 条，只留最后一条）。
    /// 与撤销条分工：撤销条带按钮、5 秒；轻提示只说结果。
    /// </summary>
    private void ShowActionToast(string text)
    {
        ActionToastText = text;
        IsActionToastVisible = true;

        _actionToastTimer?.Stop();
        _actionToastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1800),
        };
        _actionToastTimer.Tick += (_, _) =>
        {
            _actionToastTimer?.Stop();
            _actionToastTimer = null;
            IsActionToastVisible = false;
        };
        _actionToastTimer.Start();
    }

    // ---------- 最近删除 / 撤销 ----------

    /// <summary>底部撤销提示条是否显示。</summary>
    [ObservableProperty]
    private bool _isUndoToastVisible;

    /// <summary>撤销提示条文案（如「已删除 3 个项目」）。</summary>
    [ObservableProperty]
    private string _undoToastText = string.Empty;

    /// <summary>撤销倒计时秒数（5 → 0）。</summary>
    [ObservableProperty]
    private int _undoSecondsLeft = 5;

    private List<DeletedMedia> _pendingUndo = new();
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    /// <summary>删除成功后弹出 5 秒撤销条。</summary>
    private void ShowUndoToast(IReadOnlyList<DeletedMedia> records)
    {
        _pendingUndo = records.Where(r => r != null).ToList();
        if (_pendingUndo.Count == 0) return;

        UndoToastText = $"已删除 {_pendingUndo.Count} 个项目";
        UndoSecondsLeft = 5;
        IsUndoToastVisible = true;

        _undoTimer?.Stop();
        _undoTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _undoTimer.Tick += (_, _) =>
        {
            UndoSecondsLeft--;
            if (UndoSecondsLeft <= 0)
                HideUndoToast();
        };
        _undoTimer.Start();
    }

    private void HideUndoToast()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        IsUndoToastVisible = false;
        _pendingUndo = new List<DeletedMedia>();
    }

    /// <summary>点击「撤销」：把文件从系统回收站还原回原位置，并恢复媒体库记录。</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_pendingUndo.Count == 0)
        {
            HideUndoToast();
            return;
        }

        var records = _pendingUndo.ToList();
        _undoTimer?.Stop();
        _undoTimer = null;

        int ok = 0;
        var failures = new List<string>();

        foreach (var record in records)
        {
            try
            {
                var (success, message) = await _deletedItems.RestoreAsync(record);
                if (success) ok++;
                else failures.Add($"{record.FileName}：{message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "撤销删除失败：{Path}", record.Path);
                failures.Add($"{record.FileName}：{StorageErrors.FriendlyMessage(ex)}");
            }
        }

        HideUndoToast();
        await LoadMediaAsync();
        await RefreshStatusAsync();

        if (failures.Count == 0)
        {
            StatusText = $"已撤销删除，{ok} 个文件已还原";
        }
        else
        {
            StatusText = $"撤销完成：成功 {ok}，失败 {failures.Count}";
            CustomMessageBox.Warn(
                $"有 {failures.Count} 个文件没能还原：\n\n{string.Join("\n", failures.Take(5))}"
                + (failures.Count > 5 ? "\n…" : "") +
                "\n\n如果是移动硬盘已拔出，请重新插好后再到「最近删除」里还原。",
                "撤销失败");
        }
    }

    /// <summary>清理缩略图缓存后调用：清空内存与数据库里的缓存路径，并重新排队生成。</summary>
    public async Task ResetThumbnailCacheAsync()
    {
        try
        {
            await _repo.ClearThumbnailPathsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清空数据库缩略图路径失败");
        }

        foreach (var vm in MediaItems)
        {
            vm.ThumbnailPath = null;
            vm.Media.ThumbnailPath = null;
        }

        // 内存里的解码结果也一起丢掉，否则「清缓存」后仍占着上百 MB
        Services.ThumbnailMemoryCache.Clear();

        EnsureThumbnails(MediaItems.Select(v => v.Media).ToList());
    }

    /// <summary>
    /// 若缩略图路径为空、或文件已不存在（被清理/删除/磁盘整理），重新入队生成。
    /// 保证即使缓存文件丢失，也能自动重建，避免缩略图空白。
    /// </summary>
    private void EnsureThumbnail(MediaItem m)
    {
        if (!string.IsNullOrEmpty(m.ThumbnailPath) && System.IO.File.Exists(m.ThumbnailPath)) return;

        // 源文件所在设备不在线（移动硬盘被拔）时先不排队：
        // 否则每次刷新都会重新入队、worker 反复失败空转，白白吃 CPU。
        // 重新插上后重新扫描，即可正常生成。
        var root = System.IO.Path.GetPathRoot(m.Path);
        if (!string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root)) return;

        _thumbnailService.Enqueue(m);
    }

    /// <summary>缩略图内存缓存（解码后的位图）当前占用，供设置页显示。</summary>
    public static (long Bytes, int Count, long Budget) ThumbnailCacheInfo
        => (MediaManager.App.Services.ThumbnailMemoryCache.CurrentBytes,
            MediaManager.App.Services.ThumbnailMemoryCache.Count,
            MediaManager.App.Services.ThumbnailMemoryCache.BudgetBytes);

    /// <summary>按预算上限设置缩略图内存缓存（默认 200MB）。</summary>
    public static void SetThumbnailCacheBudget(long bytes)
        => MediaManager.App.Services.ThumbnailMemoryCache.BudgetBytes = bytes;

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedItem == null) return;
        var newName = CustomPrompt.Show("输入新的文件名（含扩展名）：", "重命名", SelectedItem.FileName);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedItem.FileName) return;

        try
        {
            await _fileOps.RenameAsync(SelectedItem.Path, newName);
            var newPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(SelectedItem.Path)!, newName);
            await _repo.UpdatePathAsync(SelectedItem.Id, newPath);
            await _opLog.LogAsync("重命名", SelectedItem.Path, newPath);
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重命名失败：{Path}", SelectedItem.Path);
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "重命名"), "重命名失败");
        }
    }

    // ---------- 批量重命名 / 批量导出 ----------

    private bool _isBatchRunning;

    /// <summary>批量重命名（文件改名 + 数据库路径同步 + 操作日志）。</summary>
    public async Task BatchRenameAsync(IReadOnlyList<MediaItem> items, BatchRenameOptions options)
    {
        if (items.Count == 0 || _isBatchRunning) return;

        var plan = BatchOperationService.BuildRenamePlan(items, options, out _);
        if (plan.Count == 0)
        {
            CustomMessageBox.Warn("没有可以重命名的文件。", "批量重命名");
            return;
        }

        _isBatchRunning = true;
        IsScanning = true;
        ScanProgress = 0;

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                ScanProgressText = $"重命名 {p.Done}/{p.Total}";
            });

            var result = await Task.Run(
                () => BatchOperationService.ExecuteRename(plan, progress));

            foreach (var (id, newPath) in result.RenamedItems)
                await _repo.UpdatePathAsync(id, newPath);

            var oldByNew = plan
                .GroupBy(p => p.NewPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().OldPath, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, newPath) in result.RenamedItems)
            {
                if (oldByNew.TryGetValue(newPath, out var oldPath))
                    await _opLog.LogAsync("批量重命名", oldPath, newPath);
            }

            await LoadMediaAsync();
            ScanProgressText = string.Empty;

            StatusText = $"批量重命名完成：成功 {result.Renamed}，跳过 {result.Skipped}，失败 {result.Failed}";

            if (result.Failed > 0)
            {
                CustomMessageBox.Warn(
                    $"已完成 {result.Renamed} 个，{result.Failed} 个失败：\n\n" +
                    string.Join("\n", result.Errors.Take(6)) +
                    (result.Errors.Count > 6 ? "\n…" : "") +
                    "\n\n常见原因：文件正被其他程序占用、或所在磁盘已拔出。",
                    "批量重命名");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量重命名失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "批量重命名"), "批量重命名失败");
        }
        finally
        {
            _isBatchRunning = false;
            IsScanning = false;
            ScanProgress = 0;
        }
    }

    /// <summary>批量导出：统一尺寸 / 统一压缩，输出到指定目录（不修改原图）。</summary>
    public async Task BatchExportAsync(IReadOnlyList<MediaItem> items, BatchExportOptions options)
    {
        if (items.Count == 0 || _isBatchRunning) return;

        _isBatchRunning = true;
        IsScanning = true;
        ScanProgress = 0;

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                ScanProgressText = $"导出 {p.Done}/{p.Total}";
            });

            var result = await Task.Run(
                () => BatchOperationService.ExportAsync(items, options, progress));

            ScanProgressText = string.Empty;
            StatusText = $"导出完成：成功 {result.Ok}，跳过 {result.Skipped}，失败 {result.Failed}";

            var detail = $"成功导出 {result.Ok} 个文件到：\n{result.OutputDir}";
            if (result.Skipped > 0) detail += $"\n\n跳过 {result.Skipped} 个（视频或文件已不存在）。";
            if (result.Failed > 0)
            {
                detail += $"\n\n失败 {result.Failed} 个：\n" +
                          string.Join("\n", result.Errors.Take(5)) +
                          (result.Errors.Count > 5 ? "\n…" : "");
            }

            if (result.Failed > 0)
                CustomMessageBox.Warn(detail, "批量导出");
            else
                CustomMessageBox.Success(detail + "\n\n原图没有被修改。", "批量导出完成");

            if (result.Ok > 0)
                Services.ShellHelper.OpenFolder(result.OutputDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量导出失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "批量导出"), "批量导出失败");
        }
        finally
        {
            _isBatchRunning = false;
            IsScanning = false;
            ScanProgress = 0;
        }
    }

    [RelayCommand]
    private Task RefreshDetailAsync(ThumbnailItemViewModel? item)
        => LoadDetailCoreAsync(item ?? SelectedItem, debounce: false);

    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// 详情面板按选中项加载。数据库与 EXIF 读盘都放到后台，并且带 120ms 防抖 +
    /// 可取消：用户按住方向键快速翻图时，只为最终停留的那张读 EXIF，不会排队卡顿。
    /// </summary>
    private async Task LoadDetailCoreAsync(ThumbnailItemViewModel? item, bool debounce)
    {
        if (item == null) return;

        // 名称/尺寸/日期都是内存里的字段，先立刻显示，面板不会有空白期
        DetailName = item.FileName;
        DetailMeta = item.Media.MediaType == Core.Enums.MediaType.Image
            ? $"{item.Media.Width}×{item.Media.Height}  {item.SizeDisplay}"
            : $"{item.TypeLabel}  ·  {item.SizeDisplay}";
        DetailDate = item.Media.ModifiedAtUtc ?? string.Empty;
        DetailIsVideo = item.IsVideo;
        NoteEditor = item.Note ?? string.Empty;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _detailCts, cts);
        previous?.Cancel();   // 不 Dispose：可能仍被上一轮的 Task.Delay 引用

        var ct = cts.Token;
        try
        {
            if (debounce) await Task.Delay(120, ct);

            // 已加入的分类（v1.3.2 起收藏夹已并入这里，只有一个列表）
            var catIds = await DbAsync(() => _repo.GetMediaCategoryIdsAsync(item.Id));
            ct.ThrowIfCancellationRequested();

            SelectedCategories.Clear();
            foreach (var id in catIds)
            {
                var cat = CategoryItems.FirstOrDefault(c => c.Id == id);
                if (cat != null) SelectedCategories.Add(cat);
            }

            // EXIF（仅图片）：命中缓存时几乎零成本，未命中才真正读文件
            ExifItems.Clear();
            if (item.Media.MediaType == Core.Enums.MediaType.Image)
            {
                var exif = await _exifService.ReadExifAsync(item.Path);
                ct.ThrowIfCancellationRequested();
                foreach (var kv in exif.Take(30))
                    ExifItems.Add(kv);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户切到别的图了，本轮作废
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取详情失败：{Path}", item.Path);
        }
    }

    partial void OnSelectedItemChanged(ThumbnailItemViewModel? value)
    {
        if (value != null)
            _ = LoadDetailCoreAsync(value, debounce: true);
    }

    private async Task RefreshStatusAsync()
    {
        var total = await DbAsync(() => _repo.GetTotalCountAsync());
        StatusText = $"共 {total} 项";
    }

    // ---------- 文件监控 ----------

    private async void OnFileSystemChanged(object? sender, FileChangeBatch batch)
    {
        // 该事件来自 FileSystemWatcher 的后台线程。DispatcherTimer 必须建在 UI 线程的
        // Dispatcher 上，否则计时器没有消息循环、永远不会 Tick（提示条也就永远不会自动加入）。
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => OnFileSystemChanged(sender, batch));
            return;
        }

        var root = FolderItems.FirstOrDefault(x => x.RelatedId == batch.RootFolderId);
        if (root == null) return;

        // 提示条还没处理完时，后续的 Changed/Size 事件会再触发一次静默刷新，
        // 那会把文件直接加进库（等于绕过用户选择）。这里直接忽略。
        if (_pendingNewFiles.ContainsKey(batch.RootFolderId)) return;

        // 有新增的图片/视频：不直接入静默库，先在角落问一句「是否加入库」，
        // 让用户明确知道刚才拷进来的东西被发现了（而不是要他自己去点刷新）。
        if (batch.NewMediaFiles > 0)
        {
            ShowNewFilesToast(batch.RootFolderId, batch.NewMediaFiles);
            return;
        }

        await SilentRefreshAsync(batch.RootFolderId);
    }

    /// <summary>静默增量刷新（删除/重命名/修改内容时用，不需要打扰用户）。</summary>
    private async Task SilentRefreshAsync(long rootFolderId)
    {
        var root = FolderItems.FirstOrDefault(x => x.RelatedId == rootFolderId);
        if (root == null) return;

        try
        {
            await _scanService.ScanIncrementalAsync(rootFolderId, root.ToolTipText ?? root.Title);
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "增量扫描失败：根目录 {Id}", rootFolderId);
        }
    }

    // ---------- 「发现 N 个新文件」提示条 ----------

    [ObservableProperty]
    private bool _isNewFilesToastVisible;

    [ObservableProperty]
    private string _newFilesToastText = string.Empty;

    [ObservableProperty]
    private int _newFilesSecondsLeft;

    /// <summary>待确认的根目录及其新增数量。</summary>
    private readonly Dictionary<long, int> _pendingNewFiles = new();
    private System.Windows.Threading.DispatcherTimer? _newFilesTimer;

    private const int NewFilesTimeoutSeconds = 12;

    private void ShowNewFilesToast(long rootFolderId, int count)
    {
        _pendingNewFiles.TryGetValue(rootFolderId, out var old);
        _pendingNewFiles[rootFolderId] = old + count;

        var total = _pendingNewFiles.Values.Sum();
        NewFilesToastText = $"发现 {total} 个新文件，是否加入库？";
        NewFilesSecondsLeft = NewFilesTimeoutSeconds;
        IsNewFilesToastVisible = true;

        // 已有计时器时只重置倒计时，不重建（避免提示条闪烁）
        if (_newFilesTimer == null)
        {
            _newFilesTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _newFilesTimer.Tick += (_, _) =>
            {
                NewFilesSecondsLeft--;
                if (NewFilesSecondsLeft > 0) return;

                // 超时视为「加入」：用户可能正埋头干别的，但库不能一直缺文件
                _ = AcceptNewFilesAsync(auto: true);
            };
        }

        _newFilesTimer.Stop();
        _newFilesTimer.Start();
    }

    private void HideNewFilesToast()
    {
        _newFilesTimer?.Stop();
        _newFilesTimer = null;
        IsNewFilesToastVisible = false;
        _pendingNewFiles.Clear();
    }

    /// <summary>用户点「加入库」。</summary>
    [RelayCommand]
    private Task JoinNewFilesAsync() => AcceptNewFilesAsync(auto: false);

    /// <summary>把新发现的文件加入媒体库（auto=true 表示是超时自动执行）。</summary>
    private async Task AcceptNewFilesAsync(bool auto)
    {
        var roots = _pendingNewFiles.Keys.ToList();
        var total = _pendingNewFiles.Values.Sum();
        HideNewFilesToast();
        if (roots.Count == 0) return;

        StatusText = auto ? $"正在自动加入 {total} 个新文件…" : $"正在加入 {total} 个新文件…";

        foreach (var id in roots)
            await SilentRefreshAsync(id);

        StatusText = auto
            ? $"已自动加入 {total} 个新文件（可随时在列表里查看）"
            : $"已加入 {total} 个新文件";
        await RefreshStatusAsync();
    }

    /// <summary>忽略这批新文件（不入库，下次再有变动时会重新提示）。</summary>
    [RelayCommand]
    private void IgnoreNewFiles()
    {
        HideNewFilesToast();
        StatusText = "已忽略这批新文件（文件仍在磁盘上，随时可手动扫描加入）";
    }

    // ---------- 缩略图大小 ----------

    partial void OnThumbnailSizeChanged(int value)
    {
        // 触发网格重排（绑定到 ItemWidth/Height）
        OnPropertyChanged(nameof(ThumbnailSize));
    }

    // ---------- 标签管理 ----------

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = CustomPrompt.Show("给这个标签起个名字：", "新建标签");
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.AddTagAsync(new Tag { Name = name.Trim() });
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task RenameTagAsync(NavigationItemViewModel? tag)
    {
        if (tag?.RelatedId == null) return;
        var name = CustomPrompt.Show("输入新的标签名称：", "重命名标签", tag.Title);
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.RenameTagAsync(tag.RelatedId.Value, name.Trim());
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task DeleteTagAsync(NavigationItemViewModel? tag)
    {
        if (tag?.RelatedId == null) return;
        var confirm = CustomMessageBox.ConfirmDanger($"确定删除标签「{tag.Title}」？", "删除标签");
        if (!confirm) return;

        await _repo.DeleteTagAsync(tag.RelatedId.Value);
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task AssignTagAsync(string? tagIdStr)
    {
        if (SelectedItem == null || !long.TryParse(tagIdStr, out var tagId)) return;
        await _repo.AddMediaTagAsync(SelectedItem.Id, tagId);
    }

    // ---------- 分类管理 ----------

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = CustomPrompt.Show("给这个分类起个名字：", "新建分类");
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.AddCategoryAsync(new Category { Name = name.Trim() });
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task RenameCategoryAsync(Category? category)
    {
        if (category == null) return;
        var name = CustomPrompt.Show("输入新的分类名称：", "重命名分类", category.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.RenameCategoryAsync(category.Id, name.Trim());
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync(Category? category)
    {
        if (category == null) return;
        var confirm = CustomMessageBox.ConfirmDanger($"确定删除分类「{category.Name}」？", "删除分类");
        if (!confirm) return;

        await _repo.DeleteCategoryAsync(category.Id);
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task AssignCategoryAsync(string? categoryIdStr)
    {
        if (SelectedItem == null || !long.TryParse(categoryIdStr, out var catId)) return;

        await _repo.AddMediaCategoryAsync(SelectedItem.Id, catId);

        // 缩略图角标 + 详情面板同时跟上，别等下次刷新
        if (!SelectedItem.CategoryIds.Contains(catId))
            SelectedItem.CategoryIds.Add(catId);
        UpdateCategoryMark(SelectedItem);

        var name = CategoryItems.FirstOrDefault(c => c.Id == catId)?.Name ?? "分类";
        ShowActionToast($"✅ 已加入分类【{name}】");
        await RefreshDetailAsync(SelectedItem);
    }

    // ---------- 备注 ----------

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (SelectedItem == null) return;
        var note = string.IsNullOrWhiteSpace(NoteEditor) ? null : NoteEditor.Trim();
        SelectedItem.Note = note;
        await _repo.SetNoteAsync(SelectedItem.Id, note);
        StatusText = string.IsNullOrEmpty(note) ? "已清除备注" : "备注已保存";
    }

    // ---------- 刷新 ----------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // 手动刷新要连解码后的内存缓存一起丢掉：只重读数据库的话，
        // 源文件已被外部修改、缩略图文件却还命中的旧图会继续显示。
        Services.ThumbnailMemoryCache.Clear();
        await LoadNavigationAsync();
        await LoadMediaAsync();
        StatusText = "已刷新";
    }

    // ---------- 批量操作（供右键菜单 / 快捷键调用，作用于所有选中项） ----------

    /// <summary>批量收藏：全部设为收藏（若全部已收藏则取消收藏）。</summary>
    public async Task BatchToggleFavoriteAsync(IReadOnlyList<ThumbnailItemViewModel> items)
    {
        if (items.Count == 0) return;
        var allFav = items.All(x => x.IsFavorite);
        foreach (var item in items)
        {
            item.IsFavorite = !allFav;
            await _repo.SetFavoriteAsync(item.Id, item.IsFavorite);
        }
        StatusText = allFav ? $"已取消收藏 {items.Count} 项" : $"已收藏 {items.Count} 项";

        FavoriteCount = (int)await _repo.GetFavoriteCountAsync();
        if (!allFav)
            CheckFavoriteMilestone(FavoriteCount);
    }

    private static readonly long[] FavoriteMilestones = { 10, 50, 100, 500, 1000 };
    private readonly HashSet<long> _milestonesShown = new();

    /// <summary>收藏数跨过阈值时状态栏闪一行提示，2 秒后消失。</summary>
    private void CheckFavoriteMilestone(int count)
    {
        foreach (var m in FavoriteMilestones)
        {
            if (count >= m && _milestonesShown.Add(m))
            {
                _ = FlashStatusAsync($"你已收藏 {m} 张");
                break;
            }
        }
    }

    private async Task FlashStatusAsync(string message)
    {
        var prev = StatusText;
        StatusText = message;
        await Task.Delay(2000);
        if (StatusText == message)
            StatusText = prev;
    }

    public async Task BatchAssignCategoryAsync(IReadOnlyList<ThumbnailItemViewModel> items, long categoryId)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            await _repo.AddMediaCategoryAsync(item.Id, categoryId);
            if (!item.CategoryIds.Contains(categoryId))
                item.CategoryIds.Add(categoryId);
        }
        foreach (var item in items)
            UpdateCategoryMark(item);

        var name = CategoryItems.FirstOrDefault(c => c.Id == categoryId)?.Name ?? "分类";
        ShowActionToast(items.Count == 1
            ? $"✅ 已加入分类【{name}】"
            : $"✅ 已将 {items.Count} 项加入分类【{name}】");
        StatusText = $"已将 {items.Count} 项加入分类「{name}」";

        // 拖拽/批量也走这条路：如果当前详情面板正开着被拖的其中之一，
        // 「所属分类」必须立刻把新分类列出来，否则右侧看起来像没生效。
        if (SelectedItem != null && items.Any(i => i.Id == SelectedItem.Id))
            await RefreshDetailAsync(SelectedItem);
    }

    /// <summary>切换单张图片是否属于某分类（已加入→移出，未加入→加入）。</summary>
    public async Task ToggleCategoryAsync(ThumbnailItemViewModel item, long categoryId)
    {
        if (item.CategoryIds.Contains(categoryId))
        {
            await _repo.RemoveMediaCategoryAsync(item.Id, categoryId);
            item.CategoryIds.Remove(categoryId);
        }
        else
        {
            await _repo.AddMediaCategoryAsync(item.Id, categoryId);
            item.CategoryIds.Add(categoryId);
        }
        UpdateCategoryMark(item);
    }

    /// <summary>刷新单个缩略图的分类标记（名称 + 是否已分类）。</summary>
    private void UpdateCategoryMark(ThumbnailItemViewModel item)
    {
        var nameById = CategoryItems.ToDictionary(c => c.Id, c => c.Name);
        var names = item.CategoryIds
            .Where(id => nameById.ContainsKey(id))
            .Select(id => nameById[id])
            .ToList();
        item.CategoryNames = string.Join("、", names);
        item.RefreshCategoryState();
    }

    /// <summary>批量打标签（把网格里选中的媒体拖到侧栏标签上）。重复的由 INSERT OR IGNORE 忽略。</summary>
    public async Task BatchAssignTagAsync(IReadOnlyList<ThumbnailItemViewModel> items, long tagId)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
            await _repo.AddMediaTagAsync(item.Id, tagId);

        // 和「拖到分类」保持同一种反馈：底部轻提示 + 状态栏，不再只有状态栏
        var name = TagItems.FirstOrDefault(t => t.RelatedId == tagId)?.Title ?? "标签";
        ShowActionToast(items.Count == 1
            ? $"✅ 已打上标签【{name}】"
            : $"✅ 已给 {items.Count} 项打上标签【{name}】");
        StatusText = $"已给 {items.Count} 项打上标签「{name}」";
    }

    public async Task BatchSetRatingAsync(IReadOnlyList<ThumbnailItemViewModel> items, int rating)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            item.Rating = rating;
            await _repo.SetRatingAsync(item.Id, rating);
        }
        StatusText = $"已给 {items.Count} 项打 {rating} 星";
    }

    public async Task BatchDeleteAsync(IReadOnlyList<ThumbnailItemViewModel> items)
    {
        if (items.Count == 0) return;
        var confirm = CustomMessageBox.ConfirmDanger(
            $"将把选中的 {items.Count} 个文件移入回收站。\n\n删除后 5 秒内可在底部撤销。", "确认删除");
        if (!confirm) return;

        var deleted = new List<ThumbnailItemViewModel>();
        var failures = new List<(string Path, Exception Error)>();
        var records = new List<DeletedMedia>();
        var batchId = Guid.NewGuid().ToString("N");

        foreach (var item in items)
        {
            try
            {
                await _fileOps.DeleteToRecycleBinAsync(item.Path);
                await _opLog.LogAsync("删除到回收站", item.Path, null);

                // 快照要在删库之前抓，否则标签/分类关联已经没了
                records.Add(await _deletedItems.TrackAsync(item.Media, item.ThumbnailPath, batchId));

                await _repo.DeleteMediaAsync(item.Id);
                deleted.Add(item);
            }
            catch (Exception ex)
            {
                // 单个失败不影响其余文件继续删（典型：某个盘被拔出、文件被占用）
                _logger.LogWarning(ex, "批量删除失败：{Path}", item.Path);
                failures.Add((item.Path, ex));
            }
        }

        foreach (var item in deleted)
            MediaItems.Remove(item);
        SelectedItem = null;
        await RefreshStatusAsync();

        if (failures.Count > 0)
        {
            var detail = StorageErrors.FriendlyMessage(failures[0].Error);
            var summary = $"已删除 {deleted.Count} 个，{failures.Count} 个失败。\n\n{detail}";
            if (failures.Count == 1)
                summary += $"\n\n失败文件：{System.IO.Path.GetFileName(failures[0].Path)}";
            else
                summary += $"\n\n失败示例：{System.IO.Path.GetFileName(failures[0].Path)}";

            CustomMessageBox.Warn(summary, "部分文件删除失败");
            StatusText = $"删除完成：成功 {deleted.Count}，失败 {failures.Count}";
        }
        else
        {
            StatusText = $"已删除 {deleted.Count} 个文件";
        }

        if (records.Count > 0)
            ShowUndoToast(records);
    }

    // ---------- 移出分类（多分类支持：详情面板里可以单独移出某一个） ----------

    [RelayCommand]
    private async Task RemoveCategoryAsync(Category? category)
    {
        if (category == null || SelectedItem == null) return;

        await _repo.RemoveMediaCategoryAsync(SelectedItem.Id, category.Id);
        SelectedItem.CategoryIds.Remove(category.Id);
        UpdateCategoryMark(SelectedItem);
        ShowActionToast($"已从分类【{category.Name}】移出");
        await RefreshDetailAsync(SelectedItem);
    }

    /// <summary>标记单个文件为缺失（文件已不在原位置时调用）。</summary>
    public async Task MarkMissingAsync(ThumbnailItemViewModel item)
    {
        item.Media.IsMissing = true;
        await _repo.MarkMissingSingleAsync(item.Id);
        MediaItems.Remove(item);
        StatusText = $"已标记缺失：{item.FileName}";
    }

    /// <summary>记录一次查看（最近查看）。</summary>
    public async Task RecordViewAsync(long mediaId)
    {
        await _repo.RecordRecentViewAsync(mediaId);
    }

    // ---------- 拖动排序 ----------

    /// <summary>保存标签顺序（拖动排序后调用）。</summary>
    public async Task SaveTagOrderAsync()
    {
        var orders = new List<(long Id, int SortOrder)>();
        for (int i = 0; i < TagItems.Count; i++)
        {
            if (TagItems[i].RelatedId is long id)
                orders.Add((id, i + 1));
        }
        if (orders.Count > 0)
            await _repo.UpdateTagSortOrderAsync(orders);
    }

    /// <summary>保存分类顺序（拖动排序后调用）。</summary>
    public async Task SaveCategoryOrderAsync()
    {
        var orders = new List<(long Id, int SortOrder)>();
        for (int i = 0; i < CategoryItems.Count; i++)
            orders.Add((CategoryItems[i].Id, i + 1));
        if (orders.Count > 0)
            await _repo.UpdateCategorySortOrderAsync(orders);
    }

    /// <summary>将标签移动到指定索引（拖动松手时调用）。</summary>
    public void MoveTag(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= TagItems.Count) return;
        if (toIndex < 0 || toIndex >= TagItems.Count) return;
        if (fromIndex == toIndex) return;
        var item = TagItems[fromIndex];
        TagItems.Move(fromIndex, toIndex);
    }

    /// <summary>将分类移动到指定索引。</summary>
    public void MoveCategory(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= CategoryItems.Count) return;
        if (toIndex < 0 || toIndex >= CategoryItems.Count) return;
        if (fromIndex == toIndex) return;
        var item = CategoryItems[fromIndex];
        CategoryItems.Move(fromIndex, toIndex);
    }

    // ---------- 发现页命令 ----------

    /// <summary>换一批（仅发现页有效）。</summary>
    [RelayCommand]
    private async Task RefreshDiscoveryAsync()
    {
        if (!IsDiscoveryView) return;
        var media = await GenerateDiscoveryBatchAsync(forceRefresh: true);
        MediaItems.Clear();
        foreach (var m in media)
        {
            var vm = new ThumbnailItemViewModel(m)
            {
                ThumbnailPath = m.ThumbnailPath,
                IsFavorite = m.Favorite,
                Rating = m.Rating,
                Note = m.Note,
            };
            MediaItems.Add(vm);
            EnsureThumbnail(m);
        }
        StatusText = _discoveryFallbackToAll
            ? $"已换一批 · {MediaItems.Count} 项（收藏还是空的，这次看全库）"
            : $"已换一批 · {MediaItems.Count} 项";
    }

    /// <summary>池子类型切换。</summary>
    partial void OnDiscoveryPoolIndexChanged(int value)
    {
        if (value < 0 || value >= DiscoveryPoolOptions.Count) return;
        var pool = (DiscoveryPoolType)value;
        if (pool == _discoveryPool) return;
        _discoveryPool = pool;
        _ = SaveDiscoveryPoolAndReloadAsync();
    }

    /// <summary>「指定分类」选择变化。</summary>
    [RelayCommand]
    private async Task SelectDiscoveryCategoryAsync(Category? category)
    {
        if (category == null) return;
        _discoveryPoolId = category.Id;
        await _repo.SetSettingAsync("DiscoveryPoolId", category.Id.ToString());
        if (_discoveryPool == DiscoveryPoolType.Category)
            await LoadMediaAsync();
    }

    private async Task SaveDiscoveryPoolAndReloadAsync()
    {
        await _repo.SetSettingAsync("DiscoveryPoolType", _discoveryPool.ToString());
        if (_currentViewKey == "discovery")
            await LoadMediaAsync();
    }
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
