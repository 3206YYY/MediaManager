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
// 属性名 SortDirection 与枚举类型同名：表达式里 `SortDirection.Ascending` 会被解析成属性，
// 所以判断升/降序时统一走这个别名，避免「属性上找不到 Ascending」的编译错误。
using SortDir = MediaManager.Core.Enums.SortDirection;
// 属性名 ViewMode 与枚举类型同名，理由同上：用别名写枚举值才不会被解析成属性。
using ViewKind = MediaManager.Core.Enums.ViewMode;

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

    /// <summary>当前筛选条件下的总数（可能远大于已加载进列表的项数）。Ctrl+A 的提示要用它。</summary>
    public int TotalCount => _totalCount;

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

            // 两侧栏上次是不是收着（读不到就当展开）。加载阶段别回写，用标志位挡一下
            var leftCollapsed = await DbAsync(() => _repo.GetSettingAsync(LeftSidebarCollapsedKey));
            var rightCollapsed = await DbAsync(() => _repo.GetSettingAsync(RightSidebarCollapsedKey));
            _suppressSidebarSave = true;
            IsLeftSidebarCollapsed = leftCollapsed == "1";
            IsRightSidebarCollapsed = rightCollapsed == "1";
            _suppressSidebarSave = false;

            // 排序偏好（上次选的字段 / 方向）。读不到就用默认：添加时间 + 倒序。
            var sortFieldStr = await DbAsync(() => _repo.GetSettingAsync(SortFieldSettingKey));
            var sortDirStr = await DbAsync(() => _repo.GetSettingAsync(SortDirectionSettingKey));
            _suppressSortSave = true;
            if (Enum.TryParse<SortField>(sortFieldStr, out var savedField))
                SortField = savedField;
            if (Enum.TryParse<SortDir>(sortDirStr, out var savedDir))
                SortDirection = savedDir;
            _suppressSortSave = false;

            // 网格 / 列表偏好。读不到就当网格（老版本用户保持原样）。
            var viewModeStr = await DbAsync(() => _repo.GetSettingAsync(ViewModeSettingKey));
            _suppressViewModeSave = true;
            if (Enum.TryParse<ViewKind>(viewModeStr, out var savedMode))
                ViewMode = savedMode;
            _suppressViewModeSave = false;

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

    // ------------------------------------------------------------------
    //  两侧栏收起状态。纯粹是「想把缩略图放多大」的事，与库里的内容无关，
    //  所以只在窗口层改列宽；这里只负责记住上次的选择（下次开还是收着的）。
    //  加载时不要回写一遍库，用 _suppressSidebarSave 挡住。
    // ------------------------------------------------------------------
    [ObservableProperty]
    private bool _isLeftSidebarCollapsed;

    [ObservableProperty]
    private bool _isRightSidebarCollapsed;

    private bool _suppressSidebarSave;

    public const string LeftSidebarCollapsedKey = "Ui.LeftSidebarCollapsed";
    public const string RightSidebarCollapsedKey = "Ui.RightSidebarCollapsed";

    partial void OnIsLeftSidebarCollapsedChanged(bool value)
    {
        if (!_suppressSidebarSave)
            _ = SaveSettingAsync(LeftSidebarCollapsedKey, value ? "1" : "0");
    }

    partial void OnIsRightSidebarCollapsedChanged(bool value)
    {
        if (!_suppressSidebarSave)
            _ = SaveSettingAsync(RightSidebarCollapsedKey, value ? "1" : "0");
    }

    /// <summary>写一条设置。失败只记日志：布局偏好丢了不影响用，不该打断当前操作。</summary>
    private async Task SaveSettingAsync(string key, string value)
    {
        try
        {
            await Task.Run(() => _repo.SetSettingAsync(key, value));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存设置失败 {Key}", key);
        }
    }

    // ---------- 排序（普通视图；发现页 / 最近查看不适用） ----------

    public const string SortFieldSettingKey = "View.SortField";
    public const string SortDirectionSettingKey = "View.SortDirection";

    /// <summary>加载阶段读到的值别回写设置，也别触发重查（和两侧栏同一个套路）。</summary>
    private bool _suppressSortSave;

    /// <summary>
    /// 排序字段。写进 AppSetting 下次启动保留 ——
    /// 不存的话每次开程序都跳回「添加时间倒序」，用户改过的偏好白设。
    /// </summary>
    [ObservableProperty]
    private SortField _sortField = SortField.DateAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    private SortDir _sortDirection = SortDir.Descending;

    /// <summary>
    /// 当前视图能不能排序。发现页是随机批次、最近查看是内存里的访问 Top100，
    /// 这两个视图必须把控件禁用并说明原因 —— 否则就是「控件亮着、点了不生效」。
    /// </summary>
    [ObservableProperty]
    private bool _isSortEnabled = true;

    /// <summary>控件禁用时的原因；可排序时为空（ToolTip 也就不显示）。</summary>
    [ObservableProperty]
    private string _sortDisabledReason = string.Empty;

    public string SortDirectionText
        => SortDirection == SortDir.Ascending ? "↑ 升序" : "↓ 降序";

    /// <summary>下拉框选项。顺序即界面顺序，6 个枚举值一个不漏。</summary>
    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption(SortField.DateAdded, "添加时间"),
        new SortOption(SortField.Name, "文件名"),
        new SortOption(SortField.ModifiedAt, "修改时间"),
        new SortOption(SortField.Size, "文件大小"),
        new SortOption(SortField.MediaType, "文件类型"),
        new SortOption(SortField.Favorite, "收藏优先"),
    };

    partial void OnSortFieldChanged(SortField value)
    {
        if (_suppressSortSave) return;
        _ = SaveSettingAsync(SortFieldSettingKey, value.ToString());
        _ = ReloadForSortAsync();
    }

    partial void OnSortDirectionChanged(SortDir value)
    {
        if (_suppressSortSave) return;
        _ = SaveSettingAsync(SortDirectionSettingKey, value.ToString());
        _ = ReloadForSortAsync();
    }

    [RelayCommand]
    private void ToggleSortDirection()
        => SortDirection = SortDirection == SortDir.Ascending
            ? SortDir.Descending
            : SortDir.Ascending;

    /// <summary>排序变了只需重查列表，不用连侧栏一起刷（那是「刷新」按钮的事）。</summary>
    private async Task ReloadForSortAsync()
    {
        try
        {
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切换排序后重新加载失败");
        }
    }

    /// <summary>下拉框的一项：枚举值 + 中文名（ComboBox 的 ItemTemplate 绑 Name）。</summary>
    public sealed record SortOption(SortField Field, string Name);

    // 网格 / 列表的绑定属性与命令在 MainViewModel.Browsing.cs（浏览：视图切换）。

    // ---------- 内容区抬头：标题 / 结果数 / 面包屑 ----------
    // 结果数只读 _totalCount。**不要解析状态栏 StatusText** —— 它还承载诊断文案
    //（"共 0 项 —— 有 N 个文件暂不可达"、"已加载 N / 共 N 项"），文案一改就会静默错位。

    /// <summary>抬头第一行：当前视图的中文名。</summary>
    public string ViewTitle => _currentViewKey switch
    {
        "discovery" => "发现",
        "all" => "全部媒体",
        "favorites" => "收藏",
        "unfiled" => "未分类",
        "recent-view" => "最近查看",
        "recent" => "最近添加",
        "diary" => "日记",
        "trash" => "最近删除",
        "folder" => CurrentFolderTitle(),
        "tag" => TagItems.FirstOrDefault(t => t.RelatedId == _currentTagId)?.Title ?? "标签",
        "category" => CategoryItems.FirstOrDefault(c => c.Id == _currentCategoryId)?.Name ?? "分类",
        _ => "媒体库",
    };

    /// <summary>抬头第一行：结果数（与 <see cref="TotalCount"/> 同源）。</summary>
    public string ResultCountText => $"共 {_totalCount} 项";

    /// <summary>抬头第二行：面包屑（文件夹给路径，标签 / 分类给类别名，其余留空）。</summary>
    public string BreadcrumbText
    {
        get
        {
            var path = CurrentViewPath();
            if (path.Length > 0) return CompactPath(path);
            return _currentViewKey switch
            {
                "tag" => "标签",
                "category" => "分类",
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// 面包屑的完整路径，仅给 ToolTip 用。
    /// 绑定 ToolTipText 而不是 Title —— Title 只是末级目录名，拿它当全路径会丢上级。
    /// </summary>
    public string BreadcrumbTooltip => CurrentViewPath();

    public bool HasBreadcrumb => BreadcrumbText.Length > 0;

    /// <summary>当前视图对应的完整文件夹路径；非文件夹视图返回空串。</summary>
    private string CurrentViewPath()
    {
        if (_currentFolderPath is { Length: > 0 } sub) return sub;
        if (_currentViewKey != "folder" || !_currentRootFolderId.HasValue) return string.Empty;
        return FolderItems.FirstOrDefault(f => f.RelatedId == _currentRootFolderId)?.ToolTipText
               ?? string.Empty;
    }

    /// <summary>文件夹视图的抬头标题：子文件夹取末级目录名，根目录取侧栏标题。</summary>
    private string CurrentFolderTitle()
    {
        var path = CurrentViewPath();
        if (path.Length > 0)
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "文件夹";
    }

    /// <summary>
    /// 把长路径压成「盘符\…\末两级」。抬头宽度在侧栏折叠时会变，
    /// 只靠 XAML 的 TextTrimming 是从尾部截断，最深的那层目录反而看不见。
    /// </summary>
    private static string CompactPath(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var root = System.IO.Path.GetPathRoot(trimmed) ?? string.Empty;
        var rest = trimmed.Substring(root.Length).Trim('\\', '/');
        var parts = rest.Length == 0
            ? Array.Empty<string>()
            : rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return trimmed;
        return root.TrimEnd('\\', '/') + "\\…\\" + parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
    }

    /// <summary>视图切换 / 列表加载 / 导航重载后刷新抬头（标题、结果数、面包屑同一批通知）。</summary>
    private void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(BreadcrumbText));
        OnPropertyChanged(nameof(BreadcrumbTooltip));
        OnPropertyChanged(nameof(HasBreadcrumb));
        // 空状态跟着列表走：凡是刷新抬头的路径，列表内容都刚换过
        NotifyCenterStateChanged();
    }

    /// <summary>保险箱里的媒体数量（侧栏徽标）。</summary>
    [ObservableProperty]
    private long _privateCount;

    /// <summary>
    /// 设置里的删除方式（回收站 / 永久删除 / 每次询问）。
    /// 界面文案要跟着它走（见 DeletePolicy），所以暴露出来给窗口读；
    /// 别在 XAML 里写死「5 秒内可撤销」——执行端可能已经是永久删除了。
    /// </summary>
    public DeleteBehavior DeleteBehavior => _config.DeleteBehavior;

    /// <summary>
    /// 设置里的「永久删除提醒确认」。关掉后永久删除不再弹框，
    /// 界面文案也得跟着改口（见 <see cref="DeletePolicy"/>）——
    /// 删之前一句话都不说，界面上还写着「每次都会确认」，就是骗人。
    /// </summary>
    public bool ConfirmPermanentDelete => _config.ConfirmPermanentDelete;

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

    /// <summary>
    /// 当前选中项数量。选择状态存在 ListBox.SelectedItems 里（WPF 多选拿不到 VM），
    /// 由视图在 SelectionChanged 时同步过来，详情面板据此切三态。
    /// </summary>
    [ObservableProperty]
    private int _selectionCount;

    /// <summary>多选时的预览（前几个文件名 + 「等 N 项」），由视图拼好传进来。</summary>
    [ObservableProperty]
    private string _selectionPreviewText = string.Empty;

    /// <summary>详情面板：一个都没选。</summary>
    public bool IsDetailEmpty => SelectionCount == 0;

    /// <summary>详情面板：单选（只有这时才显示备注 / 分类 / EXIF 这些字段）。</summary>
    public bool IsDetailSingle => SelectionCount == 1;

    /// <summary>详情面板：多选。详情字段对多选没有意义，改显示批量操作入口。</summary>
    public bool IsDetailMultiple => SelectionCount > 1;

    public string SelectionSummaryText => $"已选中 {SelectionCount} 项";

    partial void OnSelectionCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsDetailEmpty));
        OnPropertyChanged(nameof(IsDetailSingle));
        OnPropertyChanged(nameof(IsDetailMultiple));
        OnPropertyChanged(nameof(SelectionSummaryText));
    }

    // ---------- 中央空状态 / 首屏加载 ----------

    /// <summary>列表正在加载。只在还没有任何内容时用来显示「正在加载…」。</summary>
    [ObservableProperty]
    private bool _isListLoading;

    /// <summary>中央空状态标题。</summary>
    [ObservableProperty]
    private string _centerEmptyTitle = "这里还没有内容";

    /// <summary>
    /// 中央空状态说明。和状态栏是同一句话（ExplainEmptyResultAsync 里一次拼好、两处赋值），
    /// 不各写一份 —— 否则「磁盘未连接」改了口径，总有一处还在说旧的。
    /// </summary>
    [ObservableProperty]
    private string _centerEmptyText = string.Empty;

    /// <summary>列表 0 项、且不是发现页空库引导（那有自己的欢迎页）时显示中央空状态。</summary>
    public bool IsCenterEmptyVisible => MediaItems.Count == 0 && !DiscoveryShowWelcome && !IsListLoading;

    /// <summary>首屏加载且还没有任何内容时才盖遮罩，已有内容刷新时不闪。</summary>
    public bool IsCenterLoadingVisible => IsListLoading && MediaItems.Count == 0 && !DiscoveryShowWelcome;

    partial void OnIsListLoadingChanged(bool value) => NotifyCenterStateChanged();

    partial void OnDiscoveryShowWelcomeChanged(bool value) => NotifyCenterStateChanged();

    private void NotifyCenterStateChanged()
    {
        OnPropertyChanged(nameof(IsCenterEmptyVisible));
        OnPropertyChanged(nameof(IsCenterLoadingVisible));
    }

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
