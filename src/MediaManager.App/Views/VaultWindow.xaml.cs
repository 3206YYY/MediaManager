using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Enums;
using MediaManager.Core.Media;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views;

/// <summary>
/// 私密空间的独立窗口。
///
/// 为什么单独开窗口而不是在主界面里切视图：
/// 保险箱应该是「打开—用—关上」的一个**场所**，关掉窗口就是「关上」这个动作本身，
/// 不需要在主界面里维护一套「我正在看私密内容吗」的状态（状态机越少，泄露的口子越少）。
///
/// 功能与主网格保持同一水平：搜索、类型筛选、排序、分区、收藏、多选、双击打开、
/// 重命名、另存副本、右键菜单、拖拽移入、移出、删除 —— 只是把数据源限定成 IsPrivate=1。
/// </summary>
public partial class VaultWindow : Window, INotifyPropertyChanged
{
    /// <summary>无操作多久自动退出。和主窗口的旧行为一致（写死，不提供关闭开关）。</summary>
    private static readonly TimeSpan AutoCloseAfter = TimeSpan.FromMinutes(5);

    private readonly IMediaRepository _repo;
    private readonly IThumbnailService _thumbs;
    private readonly IVaultService _vault;
    private readonly IFileOperationService _fileOps;
    private readonly IOperationLogService _opLog;

    private readonly ObservableCollection<ThumbnailItemViewModel> _items = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _idleTimer;
    private readonly List<Window> _childWindows = new();

    private int _loadGeneration;
    private DateTime _lastInputUtc = DateTime.UtcNow;

    /// <summary>当前选中的分区（"all" / "fav" / "unsectioned" / "section:&lt;id&gt;"）。</summary>
    private VaultSectionEntry? _currentEntry;

    // 拖出到分区的起点判定
    private Point? _dragOrigin;
    private bool _dragArmed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public VaultWindow()
    {
        InitializeComponent();

        _repo = App.Services.GetRequiredService<IMediaRepository>();
        _thumbs = App.Services.GetRequiredService<IThumbnailService>();
        _vault = App.Services.GetRequiredService<IVaultService>();
        _fileOps = App.Services.GetRequiredService<IFileOperationService>();
        _opLog = App.Services.GetRequiredService<IOperationLogService>();

        DataContext = this;

        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await LoadAsync();
        };

        _thumbs.ThumbnailReady += OnThumbnailReady;
        Closed += (_, _) => _thumbs.ThumbnailReady -= OnThumbnailReady;

        // 右键菜单里「放进分区」的候选每次打开时重建：分区随时可能新增/改名
        ItemMenu.Opened += OnItemMenuOpened;

        Loaded += async (_, _) =>
        {
            await ReloadSectionsAsync();
            await LoadAsync();
        };

        // 无操作自动退出：私密窗口比主窗口更需要这个 —— 人走开了，窗口自己关上
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastInputUtc >= AutoCloseAfter)
            {
                _idleTimer.Stop();
                Close();
            }
        };
        _idleTimer.Start();

        PreviewMouseDown += (_, _) => _lastInputUtc = DateTime.UtcNow;
        PreviewMouseMove += (_, _) => _lastInputUtc = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastInputUtc = DateTime.UtcNow;
    }

    // ---------- 绑定 ----------

    public ObservableCollection<ThumbnailItemViewModel> Items => _items;

    /// <summary>左侧分区列表（固定项「全部 / 收藏 / 未分区」+ 自定义分区）。</summary>
    public ObservableCollection<VaultSectionEntry> Sections { get; } = new();

    private double _thumbnailSize = 170;
    public double ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            if (Math.Abs(_thumbnailSize - value) < 0.5) return;
            _thumbnailSize = value;
            Raise();
        }
    }

    private string _countText = string.Empty;
    public string CountText
    {
        get => _countText;
        set
        {
            _countText = value;
            Raise();
        }
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---------- 分区 ----------

    /// <summary>重建左侧分区列表（保留当前选中项）。</summary>
    private async Task ReloadSectionsAsync()
    {
        var currentKey = _currentEntry?.Key ?? "all";

        List<VaultSection> sections;
        long unsectioned;
        long total;
        try
        {
            sections = await _repo.GetVaultSectionsAsync();
            unsectioned = await _repo.GetVaultUnsectionedCountAsync();
            total = await _repo.GetPrivateCountAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取私密分区失败");
            return;
        }

        Sections.Clear();
        Sections.Add(new VaultSectionEntry
        {
            Key = "all",
            DisplayNameOverride = "全部",
            ToolTipText = "私密空间里的所有内容",
            Count = (int)total,
        });
        Sections.Add(new VaultSectionEntry
        {
            Key = "fav",
            DisplayNameOverride = "⭐ 收藏",
            IsFavoriteOnly = true,
            ToolTipText = "只看打过星标的",
        });
        Sections.Add(new VaultSectionEntry
        {
            Key = "unsectioned",
            DisplayNameOverride = "未分区",
            IsUnsectioned = true,
            ToolTipText = "还没放进任何分区的",
            Count = (int)unsectioned,
        });

        foreach (var s in sections)
        {
            Sections.Add(new VaultSectionEntry
            {
                Key = "section:" + s.Id,
                SectionId = s.Id,
                Name = s.Name,
                Count = s.ItemCount,
                ToolTipText = $"分区「{s.Name}」 —— 把文件拖到这行上即可放进来",
            });
        }

        // 恢复选中项；分区被别处删掉时退回「全部」
        var restore = Sections.FirstOrDefault(e => e.Key == currentKey) ?? Sections[0];
        _currentEntry = restore;
        SectionList.SelectedItem = restore;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not VaultSectionEntry entry) return;

        var changed = !ReferenceEquals(entry, _currentEntry);
        _currentEntry = entry;

        // 勾选「⭐ 收藏」筛选时，选中「收藏」分组本身就是这个意思
        if (FavoriteOnly != null) FavoriteOnly.IsChecked = entry.IsFavoriteOnly;

        if (changed) _ = LoadAsync();
    }

    private async void OnNewSectionClick(object sender, RoutedEventArgs e)
    {
        var dlg = new TextPromptWindow("新建分区", "给这个分区起个名字（只在这个窗口里可见）：", string.Empty)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;

        try
        {
            var id = await _repo.CreateVaultSectionAsync(dlg.Value);
            await ReloadSectionsAsync();

            var entry = Sections.FirstOrDefault(x => x.SectionId == id);
            if (entry != null) SectionList.SelectedItem = entry;

            await LoadAsync();
            StatusText.Text = $"已新建分区「{dlg.Value}」";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "新建分区"), "新建分区失败");
        }
    }

    private void OnRenameSectionMenu(object sender, RoutedEventArgs e)
    {
        if (SectionList.SelectedItem is not VaultSectionEntry entry) return;
        if (entry.SectionId == null)
        {
            CustomMessageBox.Warn("「全部 / 收藏 / 未分区」是固定的分组，不能改名。\n\n"
                + "要分类管理，点左下角「＋ 新建分区」。", "这里不能改名");
            return;
        }

        _ = RenameSectionAsync(entry);
    }

    private async Task RenameSectionAsync(VaultSectionEntry entry)
    {
        if (entry.SectionId is not long id) return;

        var dlg = new TextPromptWindow("重命名分区", "新的名字：", entry.Name)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;

        try
        {
            await _repo.RenameVaultSectionAsync(id, dlg.Value);
            await ReloadSectionsAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "重命名分区"), "重命名失败");
        }
    }

    private async void OnDeleteSectionMenu(object sender, RoutedEventArgs e)
    {
        if (SectionList.SelectedItem is not VaultSectionEntry entry) return;
        if (entry.SectionId is not long id)
        {
            CustomMessageBox.Warn("「全部 / 收藏 / 未分区」是固定的分组，不能删除。", "这里不能删除");
            return;
        }

        var confirm = CustomMessageBox.Confirm(
            $"删除分区「{entry.Name}」？\n\n"
            + $"· 分区里的 {entry.Count} 个文件不会被删除，只是回到「未分区」\n"
            + "· 文件本身一点都不动",
            "删除分区", confirmText: "删除分区", danger: true);
        if (!confirm) return;

        try
        {
            await _repo.DeleteVaultSectionAsync(id);
            _currentEntry = null;
            await ReloadSectionsAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "删除分区"), "删除失败");
        }
    }

    // ---------- 数据 ----------

    private async Task LoadAsync()
    {
        // XAML 加载期间 ComboBoxItem 的 IsSelected=True 就会触发 SelectionChanged，
        // 那时 SearchBox 还没构建完；等窗口真正加载后再查
        if (SearchBox == null || !IsLoaded) return;

        var generation = ++_loadGeneration;

        var search = SearchBox.Text.Trim();
        var typeTag = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = _currentEntry;

        var filter = new MediaFilter
        {
            IncludePrivate = true,   // 只查保险箱内容；普通内容靠 IsPrivate=0 的默认过滤进不来
            SearchText = search.Length == 0 ? null : search,
            MediaTypes = string.IsNullOrEmpty(typeTag)
                ? null
                : new List<MediaType> { Enum.Parse<MediaType>(typeTag) },
            Limit = 5000,
        };

        if (entry?.IsFavoriteOnly == true) filter.Favorite = true;
        if (entry?.IsUnsectioned == true) filter.VaultUnsectioned = true;
        if (entry?.SectionId is long sid) filter.VaultSectionId = sid;

        var (sortField, sortDirection) = ParseSort();
        filter.SortField = sortField;
        filter.SortDirection = sortDirection;

        List<MediaItem> list;
        try
        {
            list = await _repo.QueryAsync(filter);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取私密空间内容失败");
            StatusText.Text = "读取失败：" + ex.Message;
            return;
        }

        if (generation != _loadGeneration) return;

        _items.Clear();
        foreach (var media in list)
        {
            _items.Add(new ThumbnailItemViewModel(media)
            {
                ThumbnailPath = media.ThumbnailPath,
                IsFavorite = media.Favorite,
            });
            if (string.IsNullOrEmpty(media.ThumbnailPath)) _thumbs.Enqueue(media);
        }

        UpdateEmptyState(entry);
        CountText = search.Length == 0
            ? $"共 {_items.Count} 项"
            : $"找到 {_items.Count} 项";
    }

    private (SortField Field, SortDirection Direction) ParseSort()
    {
        var tag = (SortBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(tag)) return (SortField.DateAdded, SortDirection.Descending);

        var parts = tag.Split(':');
        var field = Enum.TryParse<SortField>(parts[0], out var f) ? f : SortField.DateAdded;
        var dir = parts.Length > 1 && Enum.TryParse<SortDirection>(parts[1], out var d)
            ? d
            : SortDirection.Descending;
        return (field, dir);
    }

    private void UpdateEmptyState(VaultSectionEntry? entry)
    {
        var empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        if (entry?.SectionId != null)
        {
            EmptyTitle.Text = "🗂 这个分区还是空的";
            EmptyHint.Text = "把文件拖到左边这个分区名字上，或先选中文件 → 右键「放进分区」。";
        }
        else if (entry?.IsFavoriteOnly == true)
        {
            EmptyTitle.Text = "还没有收藏";
            EmptyHint.Text = "右键文件 →「⭐ 收藏」，之后从这里就能一眼找到。";
        }
        else if (entry?.IsUnsectioned == true)
        {
            EmptyTitle.Text = "全都归好类了";
            EmptyHint.Text = "这里只显示还没放进分区的文件。";
        }
        else
        {
            EmptyTitle.Text = "🔒 这里是空的";
            EmptyHint.Text = "从主界面右键「移入私密空间」，或把文件直接拖进这个窗口。";
        }
    }

    private void OnThumbnailReady(long mediaId, string thumbnailPath)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var vm = _items.FirstOrDefault(i => i.Id == mediaId);
            if (vm != null) vm.ThumbnailPath = thumbnailPath;
        }));
    }

    private async Task RefreshAfterChangeAsync()
    {
        await ReloadSectionsAsync();
        await LoadAsync();

        // 主界面侧栏的「私密空间 ( N )」计数要保持同步
        try
        {
            var main = App.Services.GetRequiredService<ViewModels.MainViewModel>();
            await main.RefreshPrivateCountAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "刷新主界面私密计数失败");
        }
    }

    // ---------- 顶栏 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnTypeChanged(object sender, SelectionChangedEventArgs e) => await LoadAsync();

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e) => await LoadAsync();

    private async void OnFavoriteFilterChanged(object sender, RoutedEventArgs e)
    {
        if (SectionList == null) return;

        // 勾上「收藏」时切到「收藏」分组；取消勾选回到「全部」
        var target = FavoriteOnly.IsChecked == true
            ? Sections.FirstOrDefault(x => x.IsFavoriteOnly)
            : Sections.FirstOrDefault(x => x.Key == "all");

        if (target != null && !ReferenceEquals(_currentEntry, target))
        {
            SectionList.SelectedItem = target;   // 由 OnSectionChanged 触发加载
            return;
        }

        await LoadAsync();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => ThumbnailSize = e.NewValue;

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAfterChangeAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenVaultFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_vault.VaultDirectory);
            ShellHelper.OpenFolder(_vault.VaultDirectory);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "打开文件夹"), "无法打开目录");
        }
    }

    // ---------- 拖拽：移入 / 归类 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        var ok = MediaDrop.CanAccept(e.Data);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!MediaDrop.CanAccept(e.Data)) { e.Handled = true; return; }
        e.Handled = true;
        await ImportDroppedAsync(e.Data);
    }

    /// <summary>分区行作为放置目标：把网格里选中的文件拖过来就归到这个分区。</summary>
    private void OnSectionDragOver(object sender, DragEventArgs e)
    {
        var target = FindEntryAt(e.OriginalSource as DependencyObject);
        var droppable = target != null && !target.IsFavoriteOnly && target.Key != "all"
                        && e.Data.GetDataPresent(MediaDrop.MediaIdsFormat);
        e.Effects = droppable ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSectionDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;   // 别再冒泡到窗口的「移入私密空间」处理器

        var target = FindEntryAt(e.OriginalSource as DependencyObject);
        if (target == null || target.IsFavoriteOnly || target.Key == "all") return;
        if (e.Data.GetData(MediaDrop.MediaIdsFormat) is not long[] { Length: > 0 } ids) return;

        try
        {
            await _repo.MoveToVaultSectionAsync(ids, target.SectionId);
            await ReloadSectionsAsync();
            await LoadAsync();
            StatusText.Text = target.SectionId == null
                ? $"已移出分区：{ids.Length} 项"
                : $"已放进「{target.Name}」：{ids.Length} 项";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "归类"), "归类失败");
        }
    }

    private static VaultSectionEntry? FindEntryAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: VaultSectionEntry entry }) return entry;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    // ---------- 从网格拖出到分区 ----------

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(VaultList);
        _dragArmed = VaultList.SelectedItems.Count > 0;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragOrigin is not Point origin) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragArmed = false; return; }

        var now = e.GetPosition(VaultList);
        if (Math.Abs(now.X - origin.X) < 6 && Math.Abs(now.Y - origin.Y) < 6) return;

        var ids = VaultList.SelectedItems.OfType<ThumbnailItemViewModel>().Select(i => i.Id).ToArray();
        _dragArmed = false;
        if (ids.Length == 0) return;

        var data = new DataObject();
        data.SetData(MediaDrop.MediaIdsFormat, ids);
        DragDrop.DoDragDrop(VaultList, data, DragDropEffects.Move);
    }

    /// <summary>
    /// 拖进来的东西分两种：主界面拖的（库里已有，直接挪进保险箱）和资源管理器拖的
    /// （库外文件，移入 + 入库一起做）。混着拖也各走各的路。
    /// </summary>
    private async Task ImportDroppedAsync(IDataObject data)
    {
        try
        {
            var moved = 0;
            var failures = new List<string>();

            if (data.GetData(MediaDrop.MediaIdsFormat) is long[] { Length: > 0 } ids)
            {
                var mediaList = await _repo.GetByIdsAsync(ids);
                if (mediaList.Count > 0)
                {
                    var result = await VaultOperations.MoveIntoVaultAsync(mediaList);
                    moved = result.Moved;
                    failures.AddRange(result.Failures);
                }
            }
            else if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                var (imported, skipped) = await VaultOperations.ImportIntoVaultAsync(files);
                moved = imported.Count;
                if (skipped > 0)
                    failures.Add($"{skipped} 个文件没能加入（类型不支持 / 文件不存在）");
            }
            else
            {
                return;
            }

            await RefreshAfterChangeAsync();
            await MediaDrop.RefreshSidebarAsync();

            if (failures.Count == 0)
                StatusText.Text = $"已移入私密空间：{moved} 项";
            else
                CustomMessageBox.Warn(
                    "下面这些没移成功：\n\n" + string.Join("\n", failures.Take(10)), "部分文件未移动");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "拖拽移入私密空间失败");
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "移入"), "移入失败");
        }
    }

    /// <summary>「＋ 导入文件」：选磁盘文件移入保险箱。</summary>
    private async void OnImportFilesClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要移入私密空间的文件（可多选）",
            Filter = "媒体文件|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp;*.mp4;*.mov;*.mkv;*.avi;*.webm|所有文件|*.*",
            Multiselect = true,
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var (imported, skipped) = await VaultOperations.ImportIntoVaultAsync(dlg.FileNames);
            await RefreshAfterChangeAsync();
            await MediaDrop.RefreshSidebarAsync();

            StatusText.Text = skipped == 0
                ? $"已移入私密空间：{imported.Count} 项"
                : $"已移入 {imported.Count} 项，{skipped} 个文件没能加入";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "导入"), "导入失败");
        }
    }

    // ---------- 打开 ----------

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = GetSelected();
        if (item == null) return;

        if (OpenInWindow(item.Media) is { } window) _childWindows.Add(window);
    }

    /// <summary>按类型开对应的独立窗口。窗口都不挂 Owner：最小化互不牵连。</summary>
    /// <remarks>
    /// 图片 / 视频用内置查看器（随机跳转、快捷键、编辑器这些卖点只在内置窗口里成立）；
    /// 音频 / 文档 / 压缩包一律交给系统默认程序，返回 null 表示「没有需要跟着一起关的窗口」。
    /// </remarks>
    private Window? OpenInWindow(MediaItem media)
    {
        // 音频 / 文档 / 压缩包：交给系统默认程序，返回 null（没有需要跟着一起关的窗口）
        if (MediaOpenStrategy.UseExternalApp(media.MediaType))
        {
            OpenWithExternalApp(media.Path);
            return null;
        }

        switch (media.MediaType)
        {
            case MediaType.Image:
            {
                var images = _items.Where(i => i.IsVisual && i.Media.MediaType == MediaType.Image).ToList();
                var index = Math.Max(0, images.FindIndex(i => i.Id == media.Id));

                var vm = new ViewerViewModel(App.Services.GetRequiredService<IMediaRepository>());
                var window = new ViewerWindow(vm) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.SetItems(images, index);
                window.Show();
                window.Activate();
                WindowThemeHelper.ApplyTitleBarDarkMode(window, App.IsDarkTheme);
                return window;
            }
            case MediaType.Video:
            {
                var videos = _items.Where(i => i.IsVideo).Select(i => i.Media.Path).ToList();
                var startIndex = Math.Max(0, videos.FindIndex(p =>
                    string.Equals(p, media.Path, StringComparison.OrdinalIgnoreCase)));

                var player = new VideoPlayerWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                player.SetPlaylist(videos, startIndex);
                player.Show();
                player.Activate();
                WindowThemeHelper.ApplyTitleBarDarkMode(player, App.IsDarkTheme);
                return player;
            }
            case MediaType.Audio:
            case MediaType.Text:
            case MediaType.Archive:
                OpenWithExternalApp(media.Path);
                return null;
            default:
                // 正常走不到（前面已按 MediaOpenStrategy 分流）：宁可交给系统，也别点了没反应
                OpenWithExternalApp(media.Path);
                return null;
        }
    }

    /// <summary>用系统默认关联程序打开（音频 / 文档 / 压缩包，以及右键菜单与 Ctrl+O）。</summary>
    private void OpenWithExternalApp(string? path)
    {
        if (ShellHelper.TryOpenWithDefaultApp(path, out var error)) return;
        CustomMessageBox.Warn(error ?? "打开失败。", "用系统默认程序打开");
    }

    // ---------- 右键菜单 ----------

    private ThumbnailItemViewModel? GetSelected()
        => VaultList.SelectedItem as ThumbnailItemViewModel
           ?? VaultList.SelectedItems.OfType<ThumbnailItemViewModel>().FirstOrDefault();

    private List<ThumbnailItemViewModel> GetSelectedList()
        => VaultList.SelectedItems.OfType<ThumbnailItemViewModel>().ToList();

    private void OnItemMenuOpened(object? sender, RoutedEventArgs e)
    {
        var items = GetSelectedList();
        if (items.Count == 0) return;

        // 收藏项文案跟随当前状态
        FavoriteMenu.Header = items.All(i => i.IsFavorite) ? "☆ 取消收藏" : "⭐ 收藏";

        // 「放进分区」候选：每次打开重建，分区随时可能在左侧新建或改名
        MoveToSectionMenu.Items.Clear();
        foreach (var entry in Sections.Where(x => x.SectionId != null || x.IsUnsectioned).ToList())
        {
            var target = entry;
            var mi = new MenuItem
            {
                Header = entry.SectionId == null ? "（移出分区）" : entry.Name,
                IsCheckable = entry.SectionId != null,
                IsChecked = entry.SectionId != null
                            && items.All(i => i.Media.VaultSectionId == entry.SectionId),
            };
            mi.Click += async (_, _) => await MoveToSectionAsync(items, target);
            MoveToSectionMenu.Items.Add(mi);
        }

        if (MoveToSectionMenu.Items.Count == 0)
            MoveToSectionMenu.Items.Add(new MenuItem { Header = "（还没有分区，点左下角新建）", IsEnabled = false });
    }

    private async Task MoveToSectionAsync(List<ThumbnailItemViewModel> items, VaultSectionEntry entry)
    {
        try
        {
            await _repo.MoveToVaultSectionAsync(items.Select(i => i.Id).ToList(), entry.SectionId);
            await ReloadSectionsAsync();
            await LoadAsync();
            StatusText.Text = entry.SectionId == null
                ? $"已移出分区：{items.Count} 项"
                : $"已放进「{entry.Name}」：{items.Count} 项";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "归类"), "归类失败");
        }
    }

    private async void OnRemoveFromSectionMenu(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedList();
        if (items.Count == 0) return;

        try
        {
            await _repo.MoveToVaultSectionAsync(items.Select(i => i.Id).ToList(), null);
            await ReloadSectionsAsync();
            await LoadAsync();
            StatusText.Text = $"已移出分区：{items.Count} 项";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "移出分区"), "操作失败");
        }
    }

    private async void OnFavoriteMenu(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedList();
        if (items.Count == 0) return;

        // 全都收藏了 → 这次是取消；否则一律设为收藏
        var target = !items.All(i => i.IsFavorite);

        try
        {
            foreach (var item in items)
            {
                await _repo.SetFavoriteAsync(item.Id, target);
                item.IsFavorite = target;
            }

            StatusText.Text = target ? $"已收藏：{items.Count} 项" : $"已取消收藏：{items.Count} 项";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "收藏"), "操作失败");
        }
    }

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        var item = GetSelected();
        if (item != null && OpenInWindow(item.Media) is { } w) _childWindows.Add(w);
    }

    /// <summary>右键「用系统默认程序打开」：私密文件也能丢给外部软件看，不经过内置窗口。</summary>
    private void OnOpenExternallyMenu(object sender, RoutedEventArgs e)
    {
        var item = GetSelected();
        if (item != null) OpenWithExternalApp(item.Media.Path);
    }

    private void OnOpenLocationMenu(object sender, RoutedEventArgs e)
    {
        var item = GetSelected();
        if (item == null) return;

        try
        {
            if (!File.Exists(item.Media.Path))
            {
                CustomMessageBox.Warn("文件不在磁盘上（可能已被移动或删除）。", "无法打开位置");
                return;
            }

            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Media.Path}\"");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "打开位置"), "无法打开位置");
        }
    }

    /// <summary>重命名文件本体（不是改分区名）。扩展名不让改 —— 改错了文件就打不开了。</summary>
    private async void OnRenameFileMenu(object sender, RoutedEventArgs e)
    {
        var item = GetSelected();
        if (item == null) return;

        var oldPath = item.Media.Path;
        var dir = Path.GetDirectoryName(oldPath) ?? string.Empty;
        var ext = Path.GetExtension(oldPath);
        var baseName = Path.GetFileNameWithoutExtension(oldPath);

        var dlg = new TextPromptWindow("重命名文件", $"新名字（扩展名保持 {ext}）：", baseName)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        var newBase = (dlg.Value ?? string.Empty).Trim();
        if (newBase.Length == 0 || string.Equals(newBase, baseName, StringComparison.Ordinal)) return;

        if (newBase.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            CustomMessageBox.Warn("文件名里不能有 \\ / : * ? \" < > | 这些字符。", "名字不合法");
            return;
        }

        var newPath = Path.Combine(dir, newBase + ext);
        if (File.Exists(newPath))
        {
            CustomMessageBox.Warn("这个名字的文件已经存在了，换一个。", "重名");
            return;
        }

        try
        {
            File.Move(oldPath, newPath);
            await _repo.UpdateMediaPathAsync(item.Id, newPath);
            _thumbs.Invalidate(item.Id);
            await _opLog.LogAsync("重命名", newPath, oldPath);
            await LoadAsync();
            StatusText.Text = $"已重命名为 {newBase + ext}";
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "重命名"), "重命名失败");
        }
    }

    /// <summary>另存副本：把文件复制到用户选的位置，保险箱里的原件不动。</summary>
    private async void OnSaveCopyMenu(object sender, RoutedEventArgs e)
    {
        var item = GetSelected();
        if (item == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存副本到…",
            FileName = item.Media.FileName,
            Filter = $"当前类型|*{Path.GetExtension(item.Media.Path)}|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            await Task.Run(() => File.Copy(item.Media.Path, dlg.FileName, overwrite: true));
            _lastInputUtc = DateTime.UtcNow;
            StatusText.Text = "副本已保存到：" + dlg.FileName;
            CustomMessageBox.Success($"副本已保存到：\n{dlg.FileName}\n\n保险箱里的原件不受影响。", "已保存副本");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "另存副本"), "保存失败");
        }
    }

    private async void OnMoveOutMenu(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedList();
        if (items.Count == 0) return;

        var confirm = CustomMessageBox.Confirm(
            $"把这 {items.Count} 项移出私密空间？\n\n"
            + "· 文件会移回移入前的位置（原目录已不在就留在保险箱里）\n"
            + "· 移出后主界面、搜索里就能看到它们了",
            "移出私密空间",
            confirmText: "移出");
        if (!confirm) return;

        var result = await VaultOperations.MoveOutOfVaultAsync(items.Select(i => i.Media).ToList());
        await RefreshAfterChangeAsync();

        if (result.Failures.Count == 0)
            StatusText.Text = $"已移出私密空间：{result.Moved} 项";
        else
            CustomMessageBox.Warn(
                "下面这些没能移回原位（仍安全地留在保险箱里）：\n\n"
                + string.Join("\n", result.Failures.Take(10)), "部分文件未移动");
    }

    private async void OnDeleteMenu(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedList();
        if (items.Count == 0) return;

        var confirm = CustomMessageBox.Confirm(
            $"把这 {items.Count} 项删除？文件会进入回收站（可在设置里改为永久删除）。",
            "删除", confirmText: "删除", danger: true);
        if (!confirm) return;

        var failures = new List<string>();
        foreach (var vm in items)
        {
            try
            {
                await _fileOps.DeleteToRecycleBinAsync(vm.Media.Path);
                await _opLog.LogAsync("删除到回收站", vm.Media.Path, null);
                await _repo.DeleteMediaAsync(vm.Id);
                _thumbs.Invalidate(vm.Id);
                _items.Remove(vm);
            }
            catch (Exception ex)
            {
                failures.Add($"{vm.FileName}：{ex.Message}");
            }
        }

        await RefreshAfterChangeAsync();

        if (failures.Count > 0)
            CustomMessageBox.Warn(
                "下面这些没删掉：\n\n" + string.Join("\n", failures.Take(10)), "部分文件未删除");
    }

    // ---------- 键盘 / 生命周期 ----------

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && VaultList.SelectedItems.Count > 0)
        {
            OnDeleteMenu(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && GetSelected() != null)
        {
            OnOpenMenu(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && GetSelected() != null)
        {
            OnRenameFileMenu(sender, e);
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc = 关上保险箱。私密空间里「关上」就该是最顺手的动作
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Ctrl+O：用系统默认程序打开选中项（与主界面同一个键位）
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnOpenExternallyMenu(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>关窗 = 退出私密空间：同时关掉从这里打开的查看器/播放器（里面都是私密内容）。</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        ItemMenu.Opened -= OnItemMenuOpened;

        foreach (var window in _childWindows.Where(w => w.IsLoaded))
            try { window.Close(); }
            catch { /* 关窗收尾不允许再抛 */ }

        _childWindows.Clear();

        try
        {
            if (_vault.IsUnlocked) _vault.Lock();
        }
        catch { /* 锁定失败不影响关窗 */ }
    }
}

/// <summary>左侧分区列表的一行。前三个是固定的「全部 / 收藏 / 未分区」，其余来自 VaultSection 表。</summary>
public sealed class VaultSectionEntry : INotifyPropertyChanged
{
    public required string Key { get; init; }

    /// <summary>自定义分区的 id；固定项为 null。</summary>
    public long? SectionId { get; init; }

    public bool IsFavoriteOnly { get; init; }

    public bool IsUnsectioned { get; init; }

    /// <summary>固定项写死显示名；自定义分区用 <see cref="Name"/>。</summary>
    public string? DisplayNameOverride { get; init; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            Raise(nameof(DisplayName));
        }
    }

    public string DisplayName => DisplayNameOverride ?? _name;

    public string? ToolTipText { get; init; }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            Raise(nameof(CountText));
        }
    }

    public string CountText => _count > 0 ? _count.ToString() : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>内容模板是 Grid + 两段文字，UI 自动化合成不出名字，这里给个正常文字。</summary>
    public override string ToString() => DisplayName;
}
