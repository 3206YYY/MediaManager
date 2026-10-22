using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views.Dialogs;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>程序化同步左侧导航时置位，避免 SelectionChanged 再次触发加载。</summary>
    private bool _suppressNavSync;

    /// <summary>托盘图标服务（由 App 注入）。</summary>
    public TrayIconService? TrayService { get; set; }

    /// <summary>是否正在真正退出（避免递归触发 Closing）。</summary>
    private bool _isReallyClosing;

    // 框选状态
    private bool _isMarqueeSelecting;
    private Point _marqueeStart;
    private Rect _marqueeRect;
    private DispatcherTimer? _marqueeScrollTimer;
    private int _marqueeScrollDir;   // -1 向上 / 0 停 / 1 向下

    /// <summary>框选拖到边缘多少像素内开始自动滚动。</summary>
    private const double MarqueeEdge = 26;

    // 拖动排序状态
    private bool _reorderDragging;
    private bool _reorderArmed;          // 长按已触发，进入可拖动状态
    private ListBox? _reorderList;
    private int _reorderSourceIndex = -1;
    private Point _reorderStartPoint;
    private DispatcherTimer? _reorderTimer;
    private ListBoxItem? _reorderHighlightItem; // 拖动时高亮的 item

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        // 窗口句柄就绪后，按当前主题设置标题栏深浅（Win10/11 沉浸式深色标题栏）
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);

        // 输入法隔离：只有搜索框 / 备注框里才允许 IME 抢按键，
        // 否则 R 换一批、F 收藏、1-5 打星在中文输入法下会被吃掉（见 InputMethodGuard 注释）
        InputMethodGuard.Attach(this);

        // 拖动滚动条 / End 键跳到列表深处时，状态栏要立刻告诉用户「正在定位并加载…」，
        // 否则一片空白灰块看起来就像卡死了。ScrollChanged 是冒泡事件，挂在 ListBox 上即可收到。
        ThumbList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnThumbListScrollChanged));

        AttachPlugins();
        UpdateThemeGlyph();
        // 设置页里也可能改主题：回到主窗口时对齐图标，不要出现「明明是浅色却显示月亮」
        Activated += (_, _) => UpdateThemeGlyph();
    }

    // ===================================================================
    //  主题快捷开关（最常用，所以放主界面）
    // ===================================================================

    private void UpdateThemeGlyph()
    {
        if (MoonIconHost == null || SunIconHost == null) return;
        // 深色模式时显示太阳（点它回到浅色），浅色模式时显示月亮
        SunIconHost.Visibility = App.IsDarkTheme ? Visibility.Visible : Visibility.Collapsed;
        MoonIconHost.Visibility = App.IsDarkTheme ? Visibility.Collapsed : Visibility.Visible;
        ThemeButton.ToolTip = App.IsDarkTheme ? "切换到浅色模式" : "切换到深色模式";
    }

    private async void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.SetThemeAsync(!App.IsDarkTheme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("切换主题失败：" + ex.Message);
        }
        UpdateThemeGlyph();
    }

    // ===================================================================
    //  插件
    // ===================================================================

    private Plugins.PluginService? _plugins;

    /// <summary>
    /// 接上插件宿主：加载完成、插件增删时刷新入口按钮；
    /// 插件想提示用户时写到状态栏（不弹窗打断）。
    /// </summary>
    private void AttachPlugins()
    {
        try
        {
            _plugins = App.Services.GetRequiredService<Plugins.PluginService>();
        }
        catch
        {
            return;
        }

        _plugins.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshPluginEntry));
        _plugins.NotificationRequested += (_, message) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.StatusText = message;
        }));

        RefreshPluginEntry();
    }

    /// <summary>没有插件时整个按钮都不显示，避免用户点开一个空菜单。</summary>
    private void RefreshPluginEntry()
    {
        PluginButton.Visibility = _plugins is { Actions.Count: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPluginMenuClick(object sender, RoutedEventArgs e)
    {
        if (_plugins == null) return;

        var actions = _plugins.Actions;
        if (actions.Count == 0)
        {
            RefreshPluginEntry();
            return;
        }

        var menu = new ContextMenu { PlacementTarget = PluginButton, Placement = PlacementMode.Bottom };

        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = action.Title,
                ToolTip = string.IsNullOrWhiteSpace(action.Description)
                    ? $"来自插件：{action.PluginName}"
                    : $"{action.Description}\n（来自插件：{action.PluginName}）",
            };
            var captured = action;
            item.Click += (_, _) => captured.Handler(CurrentPluginSelection());
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>把当前选中的媒体包成插件能用的形状（插件看不到主程序的 ViewModel 类型）。</summary>
    private MediaManager.Core.Plugins.PluginSelection CurrentPluginSelection()
    {
        var items = GetSelectedItems();
        if (items.Count == 0) return MediaManager.Core.Plugins.PluginSelection.Empty;

        var paths = items
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MediaManager.Core.Plugins.PluginSelection(paths, items[0].Path);
    }

    /// <summary>
    /// 跨屏跳转（拖滚动条、End/Ctrl+End、点击滚动条空白区）时通知状态栏进入「定位中」。
    /// 阈值取 8 项或 1.2 屏：滚轮的一格（3 项）和 PageDown 一屏都不触发，避免提示条一直在闪。
    /// </summary>
    private void OnThumbListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;

        var threshold = Math.Max(8, e.ViewportHeight * 1.2);
        if (Math.Abs(e.VerticalChange) >= threshold)
            _viewModel.ThumbLoading.NotifyJump();
    }

    // ---------- 导航 ----------

    private void OnNavClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.Tag is not string key) return;

        // 「最近删除」不是媒体视图，而是打开独立窗口查看删除记录
        if (key == "trash")
        {
            item.IsSelected = false;
            OpenRecentlyDeleted();
            return;
        }

        _viewModel.SelectViewCommand.Execute(key);
    }

    /// <summary>打开「最近删除」窗口；关闭后刷新主列表（可能还原了文件）。</summary>
    private void OpenRecentlyDeleted()
    {
        var win = new RecentlyDeletedWindow { Owner = this };
        win.ShowDialog();
        _ = _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化同步导航（保存副本后自动切视图）时不要再触发一次加载
        if (_suppressNavSync) return;

        if (sender is ListBox lb && lb.SelectedItem is NavigationItemViewModel nav && nav.RelatedId.HasValue)
            _viewModel.SelectViewCommand.Execute($"folder:{nav.RelatedId.Value}");
    }

    /// <summary>保存副本后视图被自动切换时，把左侧导航的高亮同步过去（否则高亮还停在旧项上）。</summary>
    private void SyncNavToSavedView(long? folderId)
    {
        _suppressNavSync = true;
        try
        {
            if (folderId is long id)
            {
                NavList.SelectedIndex = -1;
                var nav = _viewModel.FolderItems.FirstOrDefault(f => f.RelatedId == id);
                FolderListBox.SelectedItem = nav;
            }
            else
            {
                FolderListBox.SelectedIndex = -1;
                var allItem = NavList.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => (i.Tag as string) == "all");
                if (allItem != null)
                {
                    NavList.SelectedIndex = -1;
                    allItem.IsSelected = true;
                }
            }
        }
        finally
        {
            _suppressNavSync = false;
        }
    }

    private void OnTagSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is NavigationItemViewModel nav && nav.RelatedId.HasValue)
            _viewModel.SelectViewCommand.Execute($"tag:{nav.RelatedId.Value}");
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Core.Models.Category cat)
            _viewModel.SelectViewCommand.Execute($"category:{cat.Id}");
    }

    private void OnCollectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Core.Models.Collection col)
            _viewModel.SelectViewCommand.Execute($"collection:{col.Id}");
    }

    // ---------- 发现页 ----------

    private void OnDiscoveryCollectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is Core.Models.Collection col)
            _ = _viewModel.SelectDiscoveryCollectionCommand.ExecuteAsync(col);
    }

    // ---------- 侧边栏折叠 ----------

    private void OnToggleSection(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb || tb.Tag is not string tag)
            return;

        var expanded = tb.IsChecked == true;
        (ListBox? list, System.Windows.UIElement? addPanel) = tag switch
        {
            "Tag" => (TagListBox, TagAddPanel),
            "Category" => (CategoryListBox, CategoryAddPanel),
            "Collection" => (CollectionListBox, CollectionAddPanel),
            _ => (null, null),
        };
        if (list == null) return;

        list.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (addPanel != null)
            addPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        // 更新箭头
        if (tb.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock arrow)
            arrow.Text = expanded ? "▼" : "▶";
    }

    // ---------- 双击打开查看器 ----------

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ThumbList.SelectedItem is ThumbnailItemViewModel item)
        {
            OpenViewer(item);
        }
    }

    // ---------- 鼠标框选（长按左键拖出矩形选择） ----------

    private void OnThumbListMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 仅在左键、且按下点在空白区（非某个缩略图上）时启动框选
        if (e.ChangedButton != MouseButton.Left) return;
        // 点击落在滚动条（thumb/track/箭头）上时，交给滚动条处理，不启动框选
        if (IsOverScrollBar(e.OriginalSource as DependencyObject)) return;
        if (IsOverItem(e.GetPosition(ThumbList))) return;

        // 不加修饰键时清空原选择；Ctrl/Shift 则叠加
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ThumbList.SelectedItems.Clear();
        }

        _isMarqueeSelecting = true;
        _marqueeStart = e.GetPosition(ThumbList);
        ThumbList.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>判断鼠标事件源是否落在滚动条控件上（避免框选拦截滚动条左键）。</summary>
    private static bool IsOverScrollBar(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ScrollBar || node is System.Windows.Controls.Primitives.Thumb)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnThumbListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMarqueeSelecting) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMarquee();
            return;
        }

        var pos = e.GetPosition(ThumbList);
        var rect = new Rect(_marqueeStart, pos);
        _marqueeRect = rect;
        UpdateMarqueeRect(rect);
        UpdateMarqueeSelection(rect);
        UpdateMarqueeAutoScroll(pos);
    }

    // ---------- 鼠标滚轮（含框选中的自动滚动） ----------

    /// <summary>
    /// 缩略图网格的滚轮。
    ///
    /// 正常状态不做任何事 —— 交给 ScrollViewer 的默认逻辑（它本来就是好的）。
    /// 只有框选时例外：<c>ThumbList</c> 这时挂着鼠标捕获，滚轮事件会从捕获元素往上冒泡到 Window，
    /// 而 ScrollViewer 是 ListBox 模板里的**子元素**，根本收不到 ——
    /// 表现就是「拖框选的时候滚不动，选不了视图外的图片」。
    /// 这里把滚轮事件重新抛给 ScrollViewer 自己处理，比手算偏移靠谱
    /// （VirtualizingWrapPanel 的偏移单位不是像素，LineDown() 实测是空操作）。
    /// </summary>
    private void OnThumbListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isMarqueeSelecting) return;

        ScrollGridByWheel(e.Delta);

        // 框选中：内容被挪动了，选框内命中的项要重算（否则选中的还是滚动前那几张）
        if (_marqueeRect.Width + _marqueeRect.Height > 0)
            UpdateMarqueeSelection(_marqueeRect);

        e.Handled = true;
    }

    /// <summary>把滚轮「借道」ScrollViewer 处理：手动再抛一次 MouseWheel 给它。</summary>
    private void ScrollGridByWheel(int delta)
    {
        var sv = FindVisualChild<ScrollViewer>(ThumbList);
        if (sv == null) return;

        var before = sv.VerticalOffset;
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sv,
        };
        sv.RaiseEvent(args);

        // 已经到头（或面板本身不接受这个方向）时不用记，方便以后排查"框选里滚不动"
        if (Math.Abs(sv.VerticalOffset - before) < 0.01)
            Serilog.Log.Debug("网格滚轮未产生位移：offset={Offset}（多半已到边界）", before);
    }

    /// <summary>框选拖到网格上/下边缘时开始自动滚动，方便一拖到底选中视图外的图。</summary>
    private void UpdateMarqueeAutoScroll(Point pos)
    {
        _marqueeScrollDir = pos.Y < MarqueeEdge ? -1
            : pos.Y > ThumbList.ActualHeight - MarqueeEdge ? 1
            : 0;

        if (_marqueeScrollDir == 0)
        {
            _marqueeScrollTimer?.Stop();
            return;
        }

        if (_marqueeScrollTimer == null)
        {
            // 必须建在 UI 线程；60ms 一格滚轮 ≈ 每秒 2 行，从顶部一路拖到底不会太暴力
            _marqueeScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };
            _marqueeScrollTimer.Tick += (_, _) => MarqueeAutoScrollTick();
        }

        if (!_marqueeScrollTimer.IsEnabled)
            _marqueeScrollTimer.Start();
    }

    private void MarqueeAutoScrollTick()
    {
        if (!_isMarqueeSelecting || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _marqueeScrollTimer?.Stop();
            return;
        }

        var sv = FindVisualChild<ScrollViewer>(ThumbList);
        if (sv == null || _marqueeScrollDir == 0) return;

        ScrollGridByWheel(_marqueeScrollDir * 120);

        // 指针可能已经离开网格，仍然按最后一次有效位置重算选框
        var pos = Mouse.GetPosition(ThumbList);
        var rect = new Rect(_marqueeStart, pos);
        _marqueeRect = rect;
        UpdateMarqueeRect(rect);
        UpdateMarqueeSelection(rect);
    }

    private void OnThumbListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMarqueeSelecting) return;
        var rect = new Rect(_marqueeStart, e.GetPosition(ThumbList));
        UpdateMarqueeSelection(rect);
        EndMarquee();
        e.Handled = true;
    }

    private void EndMarquee()
    {
        _isMarqueeSelecting = false;
        _marqueeScrollTimer?.Stop();
        _marqueeScrollDir = 0;
        SelectionRect.Visibility = Visibility.Collapsed;
        ThumbList.ReleaseMouseCapture();
    }

    private void UpdateMarqueeRect(Rect rect)
    {
        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        SelectionRect.Width = Math.Max(0, rect.Width);
        SelectionRect.Height = Math.Max(0, rect.Height);
    }

    private void UpdateMarqueeSelection(Rect rect)
    {
        var itemContainerGenerator = ThumbList.ItemContainerGenerator;
        if (rect.Width < 4 && rect.Height < 4) return; // 忽略误触

        for (int i = 0; i < ThumbList.Items.Count; i++)
        {
            if (itemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue; // 未实例化（虚拟化外）跳过

            var bounds = container.TransformToAncestor(ThumbList)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            if (rect.IntersectsWith(bounds))
            {
                if (!container.IsSelected)
                    container.IsSelected = true;
            }
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                     !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // 无修饰键时，框内不包含的项取消选择（实时反映）
                if (container.IsSelected)
                    container.IsSelected = false;
            }
        }
    }

    private bool IsOverItem(Point pos)
    {
        var hit = VisualTreeHelper.HitTest(ThumbList, pos);
        return FindAncestor<ListBoxItem>(hit?.VisualHit) != null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T t) return t;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OpenViewer(ThumbnailItemViewModel item)
    {
        // 记录最近查看
        _ = _viewModel.RecordViewAsync(item.Id);

        if (item.IsVideo)
        {
            // 视频：打开 LibVLC 播放器，并把当前列表里的所有视频交给它（支持播完自动连播）
            var videos = _viewModel.MediaItems.Where(i => i.IsVideo).Select(i => i.Path).ToList();
            var startIndex = videos.FindIndex(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));

            var player = new VideoPlayerWindow { Owner = this };
            player.SetPlaylist(videos, startIndex);
            player.Show();
            WindowThemeHelper.ApplyTitleBarDarkMode(player, App.IsDarkTheme);
            return;
        }

        var items = _viewModel.MediaItems.ToList();
        var index = items.IndexOf(item);

        var viewerVm = new ViewerViewModel(App.Services.GetRequiredService<Core.Services.IMediaRepository>());
        var window = new ViewerWindow(viewerVm)
        {
            Owner = this,
        };
        window.CopySaved += OnViewerCopySaved;
        window.SetItems(items, index);
        window.Show();
        WindowThemeHelper.ApplyTitleBarDarkMode(window, App.IsDarkTheme);
    }

    /// <summary>
    /// 编辑器保存后：刷新主网格并选中 / 滚动到这张图，
    /// 让用户关闭查看器时一眼看到结果（而不是以为没保存）。
    /// 编辑副本被原地覆盖时，还会让缩略图失效并重建。
    /// </summary>
    private async void OnViewerCopySaved(object? sender, MediaManager.App.Editing.SaveResult result)
    {
        var path = result.Path;
        try
        {
            var refreshed = await _viewModel.RefreshAfterSaveAsync(path, result.OverwroteSource);
            if (refreshed.ViewChanged)
                SyncNavToSavedView(refreshed.FolderId);

            var target = refreshed.Item;
            if (target == null)
            {
                Serilog.Log.Debug("副本已保存但不在库内（独立打开的文件）：{Path}", path);
                return;
            }

            // 先让列表完成布局，再滚动定位，否则 ScrollIntoView 会滚到旧位置
            await Dispatcher.InvokeAsync(() =>
            {
                ThumbList.SelectedItems.Clear();
                ThumbList.SelectedItem = target;
                ThumbList.ScrollIntoView(target);
                ThumbList.UpdateLayout();
                ThumbList.ScrollIntoView(target);
            }, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存副本后刷新主界面失败：{Path}", path);
        }
    }

    // ---------- 单文件独立查看（拖拽 / 打开文件，不进数据库） ----------

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图片或视频",
            Filter = "媒体文件|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.avif;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.ts|图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.avif|视频|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.ts|所有文件|*.*",
        };
        if (dlg.ShowDialog() == true)
            OpenStandaloneFile(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Length > 0
            && MediaFormat.GetMediaType(files[0]) != null)
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            OpenStandaloneFiles(files);
    }

    /// <summary>打开单个图片/视频到独立查看窗口（不进入数据库，编辑保存时另存副本）。</summary>
    public void OpenStandaloneFile(string path) => OpenStandaloneFiles(new[] { path });

    /// <summary>
    /// 打开一批文件：图片各开各的查看器；视频合并成一个播放列表（支持连播/随机）。
    /// 典型来源：资源管理器多选右键打开、命令行传多个文件、拖拽多文件。
    /// </summary>
    public void OpenStandaloneFiles(IReadOnlyList<string> paths)
    {
        if (paths is not { Count: > 0 }) return;

        var videos = new List<string>();
        string? firstImage = null;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                var root = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetPathRoot(path);
                var msg = !string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root)
                    ? $"设备已断开（可能是移动硬盘 / U 盘被拔出）：\n{path}"
                    : $"文件不存在：\n{path}";
                CustomMessageBox.Warn(msg, "打开文件");
                continue;
            }

            var type = MediaFormat.GetMediaType(path);
            if (type == null)
            {
                CustomMessageBox.Info($"不支持的文件类型：\n{path}", "打开文件");
                continue;
            }

            if (type == Core.Enums.MediaType.Video) videos.Add(path);
            else firstImage ??= path;
        }

        if (videos.Count > 0)
        {
            Serilog.Log.Debug("独立打开视频 x{Count}（第一个 {First}）", videos.Count, videos[0]);
            var player = new VideoPlayerWindow { Owner = this };
            player.SetPlaylist(videos, 0);
            player.Show();
            WindowThemeHelper.ApplyTitleBarDarkMode(player, App.IsDarkTheme);
            return;
        }

        if (firstImage != null) OpenImageStandalone(firstImage);
    }

    private void OpenImageStandalone(string path)
    {
        Serilog.Log.Debug("独立打开文件：{Path}", path);

        var item = CreateStandaloneItem(path);
        var viewerVm = new ViewerViewModel(App.Services.GetRequiredService<Core.Services.IMediaRepository>());
        var window = new ViewerWindow(viewerVm) { Owner = this };
        window.CopySaved += OnViewerCopySaved;
        window.SetItems(new List<ThumbnailItemViewModel> { item }, 0);
        window.Show();
        WindowThemeHelper.ApplyTitleBarDarkMode(window, App.IsDarkTheme);
    }

    /// <summary>由磁盘文件路径构造一个不依赖数据库的缩略图项（Id=0）。</summary>
    private static ThumbnailItemViewModel CreateStandaloneItem(string path)
    {
        var type = MediaFormat.GetMediaType(path) ?? Core.Enums.MediaType.Image;
        var info = new System.IO.FileInfo(path);

        var media = new Core.Models.MediaItem
        {
            Id = 0,
            RootFolderId = 0,
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            Extension = System.IO.Path.GetExtension(path),
            MediaType = type,
            SizeBytes = info.Exists ? info.Length : 0,
        };

        if (type == Core.Enums.MediaType.Image)
        {
            var size = ReadImageSize(path);
            if (size is { } s)
            {
                media.Width = s.W;
                media.Height = s.H;
            }
        }

        return new ThumbnailItemViewModel(media);
    }

    /// <summary>读取图片像素尺寸（供裁剪/文字坐标映射，失败返回 null）。</summary>
    private static (int W, int H)? ReadImageSize(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 快捷键 ----------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 焦点在输入框里时，单键快捷键一律不生效。
        // 否则在搜索框里打字（尤其打中文时的拼音串）会顺手触发收藏 / 打星 / 换一批。
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        // ⚠ 中文输入法激活时，字母和数字不会以 Key.R 进来，而是 Key.ImeProcessed，
        // 真实按键在 ImeProcessedKey 里。不做这一步转换，R 换一批 / F 收藏 / 1-5 打星
        // 对绝大多数中文用户都是失效的（输入法会先弹候选框把按键吃掉）。
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (key)
            {
                case Key.A:
                    ThumbList.SelectAll();
                    break;
                case Key.F:
                    // 聚焦搜索框
                    break;
                case Key.Enter:
                    _ = OpenSelectedLocationAsync();
                    e.Handled = true;
                    break;
            }
            return;
        }

        // R 换一批（仅发现页有效，且不在输入框内）
        if (key == Key.R && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            && Keyboard.FocusedElement is not TextBox)
        {
            _viewModel.RefreshDiscoveryCommand.Execute(null);
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.F5:
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                _ = _viewModel.BatchDeleteAsync(GetSelectedItems());
                break;
            case Key.F2:
                _viewModel.RenameCommand.Execute(null);
                break;
            case Key.Enter:
                if (ThumbList.SelectedItem is ThumbnailItemViewModel item)
                    OpenViewer(item);
                break;
            case Key.Space:
                if (ThumbList.SelectedItem is ThumbnailItemViewModel spaceItem)
                    OpenViewer(spaceItem);
                break;
            case Key.F:
                _ = _viewModel.BatchToggleFavoriteAsync(GetSelectedItems());
                break;
            case Key.D1:
                _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), 1);
                break;
            case Key.D2:
                _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), 2);
                break;
            case Key.D3:
                _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), 3);
                break;
            case Key.D4:
                _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), 4);
                break;
            case Key.D5:
                _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), 5);
                break;
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettingsDialog();

    /// <summary>打开设置（主界面按钮和全局快捷键共用一份实现）。</summary>
    public void OpenSettingsDialog()
    {
        // 快捷键可能在设置窗口已经开着的时候被按下：再 ShowDialog 一次会套出第二个模态窗，
        // 关一个还有一个，很容易让人以为程序卡住。已经开着就把它提到前面。
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var settings = new SettingsWindow(_viewModel) { Owner = this };
        _settingsWindow = settings;
        settings.Closed += (_, _) => _settingsWindow = null;
        settings.ShowDialog();
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// 随机打开一张（全局快捷键调用）。
    /// 只在「当前已加载的列表」里随机 —— 不额外查库，也就不会因为条件不同
    /// 打开一张界面上根本看不到的素材，让人一头雾水。
    /// </summary>
    public void OpenRandomMedia()
    {
        var items = _viewModel.MediaItems;
        if (items.Count == 0)
        {
            _viewModel.StatusText = "当前列表是空的，先选个文件夹或换个筛选条件。";
            return;
        }

        var index = items.Count == 1 ? 0 : Random.Shared.Next(items.Count);
        if (items.Count > 1 && index == _lastRandomIndex) index = (index + 1) % items.Count;
        _lastRandomIndex = index;

        var item = items[index];
        _viewModel.StatusText = $"随机打开：{item.FileName}";
        OpenViewer(item);
    }

    /// <summary>上一次随机到的下标，避免连按两次还是同一张。</summary>
    private int _lastRandomIndex = -1;

    private void OnFindDuplicates(object sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetRequiredService<DuplicateViewModel>();
        var window = new DuplicateWindow(vm)
        {
            Owner = this,
        };
        window.Show();
    }

    // ---------- 右键菜单 ----------

    /// <summary>收集所有选中项（右键菜单操作作用于全部选中项）。</summary>
    private IReadOnlyList<ThumbnailItemViewModel> GetSelectedItems()
    {
        var list = new List<ThumbnailItemViewModel>();
        foreach (var obj in ThumbList.SelectedItems)
        {
            if (obj is ThumbnailItemViewModel item)
                list.Add(item);
        }
        // 若未多选，退回单选项
        if (list.Count == 0 && ThumbList.SelectedItem is ThumbnailItemViewModel single)
            list.Add(single);
        return list;
    }

    private void OnFavoriteMenu(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.BatchToggleFavoriteAsync(GetSelectedItems());
    }

    private void OnRenameMenu(object sender, RoutedEventArgs e)
    {
        _viewModel.RenameCommand.Execute(null);
    }

    /// <summary>批量重命名：选中多张时按「前缀 + 序号」统一改名。</summary>
    private void OnBatchRenameMenu(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0) return;

        var items = selected.Select(x => x.Media).ToList();
        var dlg = new BatchRenameWindow(items) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Options != null)
            _ = _viewModel.BatchRenameAsync(items, dlg.Options);
    }

    /// <summary>批量导出：统一尺寸 / 统一压缩，导出副本不动原图。</summary>
    private void OnBatchExportMenu(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0) return;

        var items = selected.Select(x => x.Media).ToList();
        var dlg = new BatchExportWindow(items) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Options != null)
            _ = _viewModel.BatchExportAsync(items, dlg.Options);
    }

    private void OnOpenLocationMenu(object sender, RoutedEventArgs e)
    {
        _ = OpenSelectedLocationAsync();
    }

    /// <summary>在资源管理器中定位到选中文件（多选时定位第一个）。</summary>
    private async Task OpenSelectedLocationAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            if (ThumbList.SelectedItem is ThumbnailItemViewModel single)
                selected = new List<ThumbnailItemViewModel> { single };
            else
                return;
        }

        await OpenLocationAsync(selected[0]);
    }

    /// <summary>打开文件所在位置（供网格、详情、查看器共用）。</summary>
    public async System.Threading.Tasks.Task OpenLocationAsync(ThumbnailItemViewModel item)
    {
        var fullPath = item.Path;
        if (string.IsNullOrWhiteSpace(fullPath)) return;

        // 文件不存在 → 提示并标记 IsMissing
        if (!System.IO.File.Exists(fullPath))
        {
            await _viewModel.MarkMissingAsync(item);
            CustomMessageBox.Info($"文件已不在原位置：\n{fullPath}", "打开所在位置");
            return;
        }

        // 路径不可达（移动硬盘离线等）
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            CustomMessageBox.Warn($"路径不可达（可能是移动硬盘未连接）：\n{dir}", "打开所在位置");
            return;
        }

        // 长路径加 \\?\ 前缀
        var target = fullPath;
        if (target.Length >= 260 && !target.StartsWith(@"\\?\"))
            target = @"\\?\" + target;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(Core.Diagnostics.StorageErrors.WithAction(ex, "打开所在位置"), "打开失败");
        }
    }

    private void OnDeleteMenu(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.BatchDeleteAsync(GetSelectedItems());
    }

    private void OnRateMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string rating && int.TryParse(rating, out var r))
            _ = _viewModel.BatchSetRatingAsync(GetSelectedItems(), r);
    }

    private void OnRenameTagMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is NavigationItemViewModel tag)
            _viewModel.RenameTagCommand.Execute(tag);
    }

    private void OnDeleteTagMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is NavigationItemViewModel tag)
            _ = _viewModel.DeleteTagCommand.ExecuteAsync(tag);
    }

    private void OnRenameCategoryMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is Core.Models.Category cat)
            _ = _viewModel.RenameCategoryCommand.ExecuteAsync(cat);
    }

    private void OnDeleteCategoryMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is Core.Models.Category cat)
            _ = _viewModel.DeleteCategoryCommand.ExecuteAsync(cat);
    }

    private void OnRenameCollectionMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is Core.Models.Collection col)
            _ = _viewModel.RenameCollectionCommand.ExecuteAsync(col);
    }

    private void OnDeleteCollectionMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is Core.Models.Collection col)
            _ = _viewModel.DeleteCollectionCommand.ExecuteAsync(col);
    }

    /// <summary>文件夹右键：从库中移除。</summary>
    private void OnRemoveFolderMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is NavigationItemViewModel folder)
            _ = _viewModel.DeleteFolderCommand.ExecuteAsync(folder);
    }

    /// <summary>文件夹右键：打开所在位置。</summary>
    private void OnOpenFolderMenu(object sender, RoutedEventArgs e)
    {
        if (GetContextListSelectedItem(sender) is not NavigationItemViewModel folder) return;
        var path = folder.Title;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!System.IO.Directory.Exists(path))
        {
            CustomMessageBox.Warn($"文件夹不存在或已不可达：\n{path}", "打开所在位置");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(Core.Diagnostics.StorageErrors.WithAction(ex, "打开文件夹"), "打开失败");
        }
    }

    /// <summary>从右键菜单的 PlacementTarget（侧栏 ListBox）取当前选中项。</summary>
    private static object? GetContextListSelectedItem(object sender)
    {
        if (sender is not MenuItem mi || mi.Parent is not ContextMenu cm)
            return null;
        if (cm.PlacementTarget is not ListBox list)
            return null;
        return list.SelectedItem;
    }

    private void OnAssignCategoryMenu(object sender, RoutedEventArgs e)
    {
        var cat = FindMenuItemData<Core.Models.Category>(e.OriginalSource);
        if (cat != null)
            _ = _viewModel.BatchAssignCategoryAsync(GetSelectedItems(), cat.Id);
    }

    /// <summary>右键菜单打开时，动态重建「分类」子菜单（勾选已加入项，点击切换）。</summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        var selected = GetSelectedItems();
        var single = selected.Count == 1 ? selected[0] : null;

        // 批量菜单：把选中数量写进标题，避免"到底会处理几张"的疑惑
        var n = selected.Count;
        BatchRenameMenu.IsEnabled = n > 0;
        BatchExportMenu.IsEnabled = n > 0;
        BatchRenameMenu.Header = n > 1 ? $"✏ 批量重命名（{n} 项）…" : "✏ 批量重命名…";
        BatchExportMenu.Header = n > 1 ? $"📤 批量导出 / 统一尺寸（{n} 项）…" : "📤 批量导出 / 统一尺寸…";

        CategoryMenu.Items.Clear();

        if (single == null)
        {
            // 多选/无选中：仍可批量加入分类
            CategoryMenu.Header = "📁 分类";
        }
        else
        {
            var joined = single.CategoryIds;
            CategoryMenu.Header = joined.Count > 0
                ? $"📁 分类（已加入 {joined.Count} 个）"
                : "📁 分类（未分类）";
        }

        if (_viewModel.CategoryItems.Count == 0)
        {
            var none = new MenuItem { Header = "（暂无分类，先在左侧新建）", IsEnabled = false };
            CategoryMenu.Items.Add(none);
            return;
        }

        foreach (var cat in _viewModel.CategoryItems)
        {
            var mi = new MenuItem
            {
                Header = cat.Name,
                Tag = cat.Id,
                IsCheckable = true,
                IsChecked = single != null && single.CategoryIds.Contains(cat.Id),
            };
            mi.Click += OnCategoryToggleMenu;
            CategoryMenu.Items.Add(mi);
        }
    }

    /// <summary>点击分类子项：单张切换，多选批量加入。</summary>
    private void OnCategoryToggleMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not long catId) return;
        var selected = GetSelectedItems();
        if (selected.Count == 1)
        {
            _ = _viewModel.ToggleCategoryAsync(selected[0], catId);
        }
        else
        {
            _ = _viewModel.BatchAssignCategoryAsync(selected, catId);
        }
    }

    private void OnAssignCollectionMenu(object sender, RoutedEventArgs e)
    {
        var col = FindMenuItemData<Core.Models.Collection>(e.OriginalSource);
        if (col != null)
            _ = _viewModel.BatchAssignCollectionAsync(GetSelectedItems(), col.Id);
    }

    /// <summary>从点击源头向上找 MenuItem，取其 DataContext。</summary>
    private static T? FindMenuItemData<T>(object? source) where T : class
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node is MenuItem mi && mi.DataContext is T data)
                return data;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node) as DependencyObject
                ?? (node as FrameworkContentElement)?.Parent as DependencyObject;
        }
        return null;
    }

    // ---------- 侧栏拖动排序 ----------

    private void OnReorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.ChangedButton != MouseButton.Left) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;

        _reorderList = list;
        _reorderSourceIndex = list.Items.IndexOf(item.DataContext);
        _reorderStartPoint = e.GetPosition(list);
        _reorderArmed = false;
        _reorderDragging = false;

        // 250ms 长按触发
        _reorderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reorderTimer.Tick += (_, _) =>
        {
            _reorderTimer?.Stop();
            // 用实时鼠标状态，而非事件参数快照
            if (Mouse.LeftButton == MouseButtonState.Pressed && !_reorderDragging)
            {
                _reorderArmed = true;
                _viewModel.StatusText = "已按住，上下拖动可调整顺序";
                ShowReorderTip(list, "↕ 松手拖动可调整顺序");
            }
        };
        _reorderTimer.Start();
    }

    private void OnReorderMouseMove(object sender, MouseEventArgs e)
    {
        if (_reorderList == null) return;

        // 未进入长按状态前，若移动超过阈值则取消（视为普通点击）
        if (!_reorderArmed)
        {
            var pos = e.GetPosition(_reorderList);
            if (Math.Abs(pos.X - _reorderStartPoint.X) > 12 || Math.Abs(pos.Y - _reorderStartPoint.Y) > 12)
            {
                _reorderTimer?.Stop();
                _reorderList = null;
            }
            return;
        }

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndReorder();
            return;
        }

        if (!_reorderDragging)
        {
            _reorderDragging = true;
            _reorderList.CaptureMouse();
            _reorderList.Cursor = Cursors.SizeNS;
            _viewModel.StatusText = "正在拖动排序，松手确认位置…";
            ShowReorderTip(_reorderList, "↕ 拖动中，松手确认");
            HighlightReorderItem(_reorderList, _reorderSourceIndex);
        }
        UpdateReorderTipPosition(_reorderList, e);
    }

    private void OnReorderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_reorderList == null) return;

        if (_reorderDragging && _reorderArmed)
        {
            var targetIndex = GetReorderTargetIndex(_reorderList, e.GetPosition(_reorderList));
            if (targetIndex >= 0 && targetIndex != _reorderSourceIndex)
                _ = ApplyReorderAsync(_reorderList, _reorderSourceIndex, targetIndex);
        }

        EndReorder();
    }

    private void EndReorder()
    {
        _reorderTimer?.Stop();
        _reorderTimer = null;
        if (_reorderList != null)
        {
            _reorderList.ReleaseMouseCapture();
            _reorderList.Cursor = Cursors.Arrow;
        }
        ClearReorderHighlight();
        HideReorderTip();
        _reorderList = null;
        _reorderArmed = false;
        _reorderDragging = false;
        _reorderSourceIndex = -1;
    }

    private async Task ApplyReorderAsync(ListBox list, int from, int to)
    {
        var tag = list.Tag as string;
        // 目标索引在 from 之后时，Move 语义需调整（先移除再插入）
        switch (tag)
        {
            case "Tag":
                _viewModel.MoveTag(from, to);
                await _viewModel.SaveTagOrderAsync();
                break;
            case "Category":
                _viewModel.MoveCategory(from, to);
                await _viewModel.SaveCategoryOrderAsync();
                break;
            case "Collection":
                _viewModel.MoveCollection(from, to);
                await _viewModel.SaveCollectionOrderAsync();
                break;
        }
        _viewModel.StatusText = "顺序已保存";
    }

    private int GetReorderTargetIndex(ListBox list, Point pos)
    {
        // 根据鼠标 Y 坐标找到最近的 item 索引
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container)
            {
                var bounds = container.TransformToAncestor(list)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                if (pos.Y <= bounds.Bottom)
                    return i;
            }
        }
        return list.Items.Count - 1;
    }

    // ---------- 拖动排序的视觉反馈 ----------

    /// <summary>显示跟随鼠标的浮动提示。</summary>
    private void ShowReorderTip(ListBox list, string text)
    {
        ReorderTipText.Text = text;
        ReorderTip.Visibility = Visibility.Visible;
        UpdateReorderTipPosition(list, Mouse.GetPosition(this));
    }

    private void UpdateReorderTipPosition(ListBox list, MouseEventArgs e)
    {
        UpdateReorderTipPosition(list, e.GetPosition(this));
    }

    private void UpdateReorderTipPosition(ListBox list, Point windowPos)
    {
        // 提示显示在鼠标右下方，避免遮挡
        Canvas.SetLeft(ReorderTip, windowPos.X + 16);
        Canvas.SetTop(ReorderTip, windowPos.Y + 16);
    }

    private void HideReorderTip()
    {
        ReorderTip.Visibility = Visibility.Collapsed;
    }

    /// <summary>高亮当前拖动的侧栏项（醒目背景色）。</summary>
    private void HighlightReorderItem(ListBox list, int index)
    {
        ClearReorderHighlight();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            return;

        // 保存原背景，设置高亮背景
        _reorderHighlightItem = container;
        container.Background = new SolidColorBrush(Color.FromArgb(0x50, 0x4A, 0x90, 0xD9));
    }

    private void ClearReorderHighlight()
    {
        if (_reorderHighlightItem != null)
        {
            _reorderHighlightItem.Background = Brushes.Transparent;
        }
        _reorderHighlightItem = null;
    }

    // ---------- 关闭窗口：退出 or 最小化到托盘 ----------

    /// <summary>关闭行为：Ask=每次询问，Exit=直接退出，Tray=最小化到托盘。</summary>
    private enum CloseBehavior { Ask, Exit, Tray }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 已在真正退出流程中，直接放行
        if (_isReallyClosing) return;

        var behavior = ReadCloseBehavior();

        if (behavior == CloseBehavior.Tray)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        if (behavior == CloseBehavior.Ask)
        {
            var result = CustomMessageBox.ShowClosePrompt(out var remember);

            if (result == MsgBoxResult.Cancel || result == MsgBoxResult.None)
            {
                e.Cancel = true;
                return;
            }

            // 勾选「记住我的选择」后写回设置，下次不再询问
            if (remember)
            {
                var value = result == MsgBoxResult.Tray ? "Tray" : "Exit";
                try
                {
                    var repo = App.Services.GetRequiredService<Core.Services.IMediaRepository>();
                    repo.SetSettingAsync("CloseBehavior", value).GetAwaiter().GetResult();
                }
                catch { /* 写设置失败不影响关闭流程 */ }
            }

            if (result == MsgBoxResult.Tray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            // Exit：继续走真正退出
        }

        // 真正退出
        _isReallyClosing = true;
    }

    private CloseBehavior ReadCloseBehavior()
    {
        try
        {
            var repo = App.Services.GetRequiredService<Core.Services.IMediaRepository>();
            var val = repo.GetSettingAsync("CloseBehavior").GetAwaiter().GetResult();
            return val switch
            {
                "Exit" => CloseBehavior.Exit,
                "Tray" => CloseBehavior.Tray,
                _ => CloseBehavior.Ask,
            };
        }
        catch
        {
            return CloseBehavior.Ask;
        }
    }

    private void MinimizeToTray()
    {
        Hide();
        TrayService?.ShowBalloon("媒体管理器", "程序已最小化到托盘，双击图标恢复。");
    }

    /// <summary>
    /// 备注框内部自带 ScrollViewer，会吞掉鼠标滚轮，导致外层详情面板滚不动。
    /// 当备注框自身无需滚动时，把滚轮转给外层详情面板。
    /// </summary>
    private void OnNoteBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (NoteBox == null || DetailScroll == null) return;

        // 找到备注框内部的 ScrollViewer，判断它当前是否可滚动
        var inner = FindVisualChild<ScrollViewer>(NoteBox);
        if (inner != null && inner.ScrollableHeight > 0)
            return; // 备注框自己可滚动，交给它自己处理

        // 备注框无需滚动，把滚轮转给外层详情面板
        DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>深度优先查找指定类型的可视化子元素。</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
