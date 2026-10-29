using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.Core.Media;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views;

/// <summary>
/// 日记窗口：左边自绘月历（有内容的日期会「亮」起来），右边看当天的回忆。
///
/// 为什么自绘月历而不是用 WPF 自带 Calendar：
/// 自带控件的日历格样式几乎不可改，做不到「有日记的日期下面点一个小圆点」，
/// 也套不上应用的深浅主题。这里 42 个格子用代码生成，反而更短、更可控。
///
/// 左边栏有两种视图，共用同一套翻页标题栏：
/// <list type="bullet">
/// <item><b>月视图</b>：42 格日历，有内容的日期带一个小圆点；</item>
/// <item><b>年视图</b>：12 个方块，一眼看完整年概况，点某个月跳回月视图。</item>
/// </list>
/// </summary>
public partial class DiaryWindow : Window
{
    private readonly IDiaryRepository _diary;

    /// <summary>当前显示月份（始终是 1 号）。</summary>
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>当前选中的日期。</summary>
    private DateTime _selectedDate = DateTime.Today;

    /// <summary>日期 → 篇数（日历标记用）。</summary>
    private Dictionary<string, int> _counts = new();

    // ---------- 年视图 ----------

    /// <summary>左边栏现在显示的是年视图吗。</summary>
    private bool _yearMode;

    /// <summary>年视图正在看哪一年。</summary>
    private int _year = DateTime.Today.Year;

    /// <summary>月份（1-12）→ 该月篇数，进年视图时按整年查一次再按月份汇总。</summary>
    private readonly Dictionary<int, int> _monthCounts = new();

    private bool _loaded;

    /// <summary>
    /// 当前这一天读出来的条目（刷新缩略图时要把同一个列表重新绑一次，
    /// 因为 <see cref="MediaItem"/> 没有实现 INotifyPropertyChanged）。
    /// </summary>
    private List<DiaryEntry> _dayEntries = new();

    /// <summary>当前这一天涉及的媒体 Id，用来过滤缩略图完成通知（别的日期的事件不管）。</summary>
    private readonly HashSet<long> _dayMediaIds = new();

    private readonly IThumbnailService? _thumbnails;

    /// <summary>缩略图陆续生成时合并重绑，避免一批图刷十几次界面。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _thumbRefreshTimer;

    /// <summary>「写一篇」编辑框是否已经打开（非模态窗口里可能被二次点击触发）。</summary>
    private bool _editorOpen;

    /// <summary>从主界面带进来的媒体（打开窗口后直接进「新建」，不用再选一遍）。</summary>
    private readonly List<MediaItem> _presetMedia;

    public DiaryWindow(IEnumerable<MediaItem>? presetMedia = null)
    {
        InitializeComponent();

        _diary = App.Services.GetRequiredService<IDiaryRepository>();
        _presetMedia = presetMedia?.ToList() ?? new List<MediaItem>();

        // 日记里的附件如果库里还没缩略图（清过缓存 / 导入时生成失败 / 备份恢复），
        // 以前就是几个永远空着的灰框。这里补上「生成 + 生成好之后重绑」。
        _thumbnails = App.Services.GetService<IThumbnailService>();
        if (_thumbnails != null) _thumbnails.ThumbnailReady += OnThumbnailReady;

        _thumbRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(280),
        };
        _thumbRefreshTimer.Tick += (_, _) =>
        {
            _thumbRefreshTimer.Stop();
            RebindEntries();
        };
        Closed += (_, _) =>
        {
            _thumbRefreshTimer.Stop();
            if (_thumbnails != null) _thumbnails.ThumbnailReady -= OnThumbnailReady;
        };

        SourceInitialized += (_, _) => Services.WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        Loaded += OnLoaded;

        // 兜底：从右键菜单 / 列表点击里开出来的独立窗口（没有 Owner）偶尔会被主界面
        // 抢回前台，用户看到的就是「点了没反应」。首帧渲染完再抢一次焦点。
        ContentRendered += (_, _) =>
        {
            if (IsActive) return;
            Activate();
            Focus();
        };
    }

    /// <summary>
    /// 从主界面「写进日记」再次叫起同一个窗口时用：直接开一个新编辑框带上这批媒体。
    /// 单实例 + 这个方法，才不用每点一次就多一个日记窗口。
    /// </summary>
    public void AddMediaAsNewEntry(IReadOnlyList<MediaItem> media)
    {
        if (media is not { Count: > 0 }) return;
        _ = OpenEditorAsync(null, media);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        await ReloadAsync();

        // 带着照片进来的场景：直接开编辑框，用户不用再找一遍「写一篇」
        if (_presetMedia.Count > 0)
        {
            var preset = _presetMedia.ToList();

            // 只给第一篇用。以前这份列表会一直留着，之后每点一次「写一篇」
            // 都会把同一批照片重新挂上去（连写两篇就会发现第二篇里也有上次那几张）。
            _presetMedia.Clear();

            await OpenEditorAsync(null, preset);
        }
    }

    // ---------- 数据 ----------

    private async Task ReloadAsync()
    {
        try
        {
            var total = await _diary.GetEntryCountAsync();
            TotalText.Text = total == 0 ? "还没有任何回忆" : $"共 {total} 篇";
        }
        catch (Exception ex)
        {
            TotalText.Text = "读取失败：" + ex.Message;
        }

        // 两种视图要的数据范围不一样：月视图只看前后一个多月，年视图要整年
        if (_yearMode) await ReloadYearAsync();
        else await ReloadMonthAsync();

        await LoadDayAsync();
    }

    /// <summary>月视图：取「本屏可能显示到的日期」（前后各补几天）→ 重画 42 格。</summary>
    private async Task ReloadMonthAsync()
    {
        // 日历一屏最多跨两个月（前后各补几天），按这个范围取标记就够
        var from = _month.AddDays(-7).ToString("yyyy-MM-dd");
        var to = _month.AddMonths(1).AddDays(14).ToString("yyyy-MM-dd");

        try
        {
            _counts = await _diary.GetDateCountsAsync(from, to);
        }
        catch (Exception)
        {
            // 标记读不出来不该把整屏卡住：日历照画，只是没有小圆点
            _counts = new Dictionary<string, int>();
        }

        BuildCalendar();
    }

    private async Task LoadDayAsync()
    {
        var date = _selectedDate.ToString("yyyy-MM-dd");
        DayTitle.Text = _selectedDate.ToString("M 月 d 日 ") + WeekdayName(_selectedDate);

        List<DiaryEntry> entries;
        try
        {
            entries = await _diary.GetEntriesAsync(date, date);
        }
        catch (Exception ex)
        {
            DayTitle.Text = "读取失败：" + ex.Message;
            entries = new List<DiaryEntry>();
        }

        EntryItems.ItemsSource = entries;
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _dayEntries = entries;
        _dayMediaIds.Clear();
        foreach (var m in entries.SelectMany(e => e.Media))
            if (m.Id != 0) _dayMediaIds.Add(m.Id);

        EnsureDayThumbnails(entries);
    }

    /// <summary>
    /// 给这一天里「库里还没有缩略图」的附件排上生成任务。
    /// 走系统默认程序打开的类型（音频/文档/压缩包）不需要缩略图，判定统一问
    /// <see cref="MediaOpenStrategy"/>，别在这里另抄一份类型列表。
    /// </summary>
    private void EnsureDayThumbnails(IReadOnlyList<DiaryEntry> entries)
    {
        if (_thumbnails == null) return;

        foreach (var media in entries.SelectMany(e => e.Media))
        {
            if (MediaOpenStrategy.UseExternalApp(media.MediaType)) continue;
            if (!string.IsNullOrEmpty(media.ThumbnailPath) && System.IO.File.Exists(media.ThumbnailPath))
                continue;
            _thumbnails.Enqueue(media);
        }
    }

    /// <summary>
    /// 缩略图生成好一个就重绑一次（合并 280ms 内的多个通知）。
    /// 事件在后台线程上触发，改界面必须切回 UI 线程。
    /// </summary>
    private void OnThumbnailReady(long mediaId, string thumbnailPath)
    {
        if (!_dayMediaIds.Contains(mediaId)) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 重绑会把滚动位置带回顶部，所以只在确实有图要换的时候做
            _thumbRefreshTimer.Stop();
            _thumbRefreshTimer.Start();
        }));
    }

    /// <summary>
    /// 把当前一天的条目重新绑一次，让 ThumbnailPath 的新值生效。
    /// 先置 null 再赋同一个实例：WPF 对「同一个引用」不会重建，等于什么都没发生。
    /// </summary>
    private void RebindEntries()
    {
        if (_dayEntries.Count == 0) return;
        EntryItems.ItemsSource = null;
        EntryItems.ItemsSource = _dayEntries;
    }

    private static string WeekdayName(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    // ---------- 日历 ----------

    private void BuildCalendar()
    {
        MonthTitle.Text = _month.ToString("yyyy 年 M 月");

        WeekHeader.Children.Clear();
        foreach (var name in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            WeekHeader.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Brush("TextSecondaryBrush"),
            });
        }

        DayGrid.Children.Clear();

        // 周一作为一周的第一天（中文习惯），所以 (DayOfWeek + 6) % 7
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var start = _month.AddDays(-offset);

        for (var i = 0; i < 42; i++)
            DayGrid.Children.Add(CreateDayCell(start.AddDays(i)));
    }

    private UIElement CreateDayCell(DateTime date)
    {
        var inMonth = date.Month == _month.Month && date.Year == _month.Year;
        var isToday = date.Date == DateTime.Today;
        var isSelected = date.Date == _selectedDate.Date;
        var count = _counts.GetValueOrDefault(date.ToString("yyyy-MM-dd"));

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        stack.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            // 非本月的日期压暗，但仍可点（点了会跳月）
            Foreground = isSelected
                ? Brushes.White
                : inMonth ? Brush("TextBrush") : Brush("TextSecondaryBrush"),
            Opacity = inMonth ? 1 : 0.45,
        });

        if (count > 0)
        {
            stack.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = isSelected ? Brushes.White : Brush("PrimaryBrush"),
            });
        }

        var cell = new Border
        {
            Height = 42,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = stack,
            Cursor = Cursors.Hand,
            Background = isSelected ? Brush("PrimaryBrush") : Brushes.Transparent,
            BorderBrush = isToday ? Brush("PrimaryBrush") : Brushes.Transparent,
            BorderThickness = new Thickness(isToday && !isSelected ? 1 : 0),
            Tag = date,
            ToolTip = count > 0 ? $"{date:M 月 d 日} · {count} 篇" : null,
        };

        if (!isSelected)
        {
            cell.MouseEnter += (_, _) => cell.Background = Brush("HoverBrush");
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        }

        cell.MouseLeftButtonDown += async (_, _) =>
        {
            var picked = (DateTime)cell.Tag;
            if (picked.Month != _month.Month || picked.Year != _month.Year)
                _month = new DateTime(picked.Year, picked.Month, 1);

            _selectedDate = picked;
            BuildCalendar();
            await LoadDayAsync();
        };

        return cell;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ---------- 年视图 ----------

    /// <summary>
    /// 年视图：把整年的日记按「天 → 月」汇总成 12 个方块。
    ///
    /// 复用 <see cref="IDiaryRepository.GetDateCountsAsync"/> 一次查全年，
    /// 在内存里按月份加总 —— 365 条 key 的字典求和是微秒级的事，
    /// 不值得为它往仓储层再加一个「按月统计」的 SQL。
    /// </summary>
    private async Task ReloadYearAsync()
    {
        var from = new DateTime(_year, 1, 1).ToString("yyyy-MM-dd");
        var to = new DateTime(_year, 12, 31).ToString("yyyy-MM-dd");

        _monthCounts.Clear();
        try
        {
            var raw = await _diary.GetDateCountsAsync(from, to);

            // 「某天几篇」→「某月几篇」，跨年 / 解析不了的行都在 Core 里被剔掉（有单测盯着）
            foreach (var pair in DiaryYearSummary.AggregateByMonth(raw, _year))
                _monthCounts[pair.Key] = pair.Value;

            YearHint.Text = DiaryYearSummary.Total(_monthCounts) == 0
                ? "这一年还没有回忆。换一年看看，或者点右上角「＋ 写一篇」。"
                : "亮起来的月份写过回忆，数字是当月篇数。点一下跳到那个月的日历。";
        }
        catch (Exception ex)
        {
            YearHint.Text = "读取失败：" + ex.Message;
        }

        BuildYearGrid();
    }

    private void BuildYearGrid()
    {
        var total = DiaryYearSummary.Total(_monthCounts);
        MonthTitle.Text = total > 0 ? $"{_year} 年 · 共 {total} 篇" : $"{_year} 年";

        MonthGrid.Children.Clear();
        for (var month = 1; month <= 12; month++)
            MonthGrid.Children.Add(CreateMonthCell(month));
    }

    private UIElement CreateMonthCell(int month)
    {
        var count = _monthCounts.GetValueOrDefault(month);
        var has = count > 0;

        // 高亮色直接取主题主色：换主题时年视图跟着换，不用另配一套颜色
        var accent = (Brush("PrimaryBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x25, 0x63, 0xEB);
        var accentBrush = new SolidColorBrush(accent);

        // 有内容：主色淡淡铺一层底 + 主色描边，月份深色字、篇数主色字
        // 没内容：透明底 + 主题边框，整体压成次要灰，一眼分得清哪些月是空的
        var background = has
            ? new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B))
            : Brushes.Transparent;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        stack.Children.Add(new TextBlock
        {
            Text = $"{month} 月",
            FontSize = 13,
            FontWeight = has ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = has ? Brush("TextBrush") : Brush("TextSecondaryBrush"),
        });

        stack.Children.Add(new TextBlock
        {
            Text = has ? $"{count} 篇" : "—",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = has ? accentBrush : Brush("TextSecondaryBrush"),
        });

        var cell = new Border
        {
            Height = 68,
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(7),
            Background = background,
            BorderBrush = has ? accentBrush : Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = month,
            ToolTip = has
                ? $"{_year} 年 {month} 月 · {count} 篇回忆"
                : $"{_year} 年 {month} 月 · 还没有回忆",
        };

        // 悬停底色要能还原回去，所以先把原始底色存下来
        cell.MouseEnter += (_, _) => cell.Background = Brush("HoverBrush");
        cell.MouseLeave += (_, _) => cell.Background = background;

        cell.MouseLeftButtonDown += async (_, _) => await JumpToMonthAsync((int)cell.Tag);

        return cell;
    }

    /// <summary>点年视图里的某个月 → 切回月视图并把那个月摆到眼前。</summary>
    private async Task JumpToMonthAsync(int month)
    {
        _month = new DateTime(_year, month, 1);

        // 跳到的正好是当下的年月，就顺手选中今天；否则选中 1 号
        _selectedDate = month == DateTime.Today.Month && _year == DateTime.Today.Year
            ? DateTime.Today
            : _month;

        SetYearMode(false);
        await ReloadAsync();
    }

    private async void OnToggleYearView(object sender, RoutedEventArgs e)
    {
        SetYearMode(!_yearMode);
        await ReloadAsync();
    }

    /// <summary>
    /// 切换左边栏的月 / 年视图。标题栏、翻页箭头是共用的，所以这里只管
    /// 两块面板的显隐和按钮文案 —— 不做「关掉再打开就回到月视图」之类的隐式跳转。
    /// </summary>
    private void SetYearMode(bool on)
    {
        _yearMode = on;
        if (on) _year = _month.Year;   // 进年视图先落到当前月份所在的年份

        MonthPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        YearPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        YearToggleButton.Content = on ? "📅 月视图" : "📅 年视图";
        YearToggleButton.ToolTip = on
            ? "回到月度日历"
            : "一次看完整年 12 个月：有回忆的月份会亮起来，点一下就跳过去";

        FadeIn(on ? YearPanel : (UIElement)MonthPanel);
    }

    /// <summary>切视图时淡入一下。160ms，够短，不挡手。</summary>
    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1,
            new Duration(TimeSpan.FromMilliseconds(160)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---------- 月份 / 年份导航 ----------

    private async void OnPrevMonth(object sender, RoutedEventArgs e) => await ShiftPageAsync(-1);

    private async void OnNextMonth(object sender, RoutedEventArgs e) => await ShiftPageAsync(1);

    /// <summary>翻页箭头：月视图翻月，年视图翻年（同一个按钮，两种语义）。</summary>
    private Task ShiftPageAsync(int delta)
        => _yearMode ? ShiftYearAsync(delta) : ShiftMonthAsync(delta);

    private async Task ShiftYearAsync(int delta)
    {
        _year += delta;
        await ReloadAsync();
    }

    private async Task ShiftMonthAsync(int delta)
    {
        _month = _month.AddMonths(delta);

        // 只移动月份、不动选中日期：选中本来就不在本月时，跳回本月 1 号
        if (_selectedDate.Month != _month.Month || _selectedDate.Year != _month.Year)
            _selectedDate = _month;

        await ReloadAsync();
    }

    private async void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _selectedDate = DateTime.Today;
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (_yearMode) _year = DateTime.Today.Year;
        await ReloadAsync();
    }

    // ---------- 日记增删改 ----------

    private async void OnNewEntry(object sender, RoutedEventArgs e)
        => await OpenEditorAsync(null, null);

    private async void OnEditEntryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiaryEntry entry) return;
        await OpenEditorAsync(entry, null);
    }

    private async void OnDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiaryEntry entry) return;

        var hasMedia = entry.Media.Count > 0;
        var confirm = Dialogs.CustomMessageBox.ConfirmDanger(
            "确定删掉这篇回忆吗？" + (hasMedia ? "\n\n照片/视频文件不会被删除，只是不再出现在日记里。" : ""),
            "删除回忆");
        if (!confirm) return;

        try
        {
            await _diary.DeleteEntryAsync(entry.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.CustomMessageBox.Warn("删除失败：" + ex.Message, "日记");
        }
    }

    /// <summary>
    /// 点日记里的附件缩略图 → 打开内置查看器 / 播放器（音频文档压缩包交给系统默认程序）。
    ///
    /// 以前这些缩略图纯粹是「一张图」，没绑任何事件，点上去毫无反应 —— 看上去像功能，
    /// 实际点不动，比不显示还糟。上一张 / 下一张的范围限定在**同一篇日记**的图片里，
    /// 不放整个媒体库（在回忆里翻照片，不该滑到三个月前去的别的东西）。
    /// </summary>
    private void OnAttachmentClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaItem media }) return;
        e.Handled = true;

        // 找这篇日记的全部附件作为翻页范围
        var siblings = (EntryItems.ItemsSource as IEnumerable<DiaryEntry>)
            ?.FirstOrDefault(en => en.Media.Any(m => ReferenceEquals(m, media) || (m.Id != 0 && m.Id == media.Id)))
            ?.Media ?? new List<MediaItem> { media };

        var scope = siblings.Select(m => new ThumbnailItemViewModel(m)).ToList();
        var target = scope.FirstOrDefault(i => media.Id != 0 && i.Id == media.Id)
                     ?? scope.FirstOrDefault(i => string.Equals(i.Path, media.Path, StringComparison.OrdinalIgnoreCase))
                     ?? new ThumbnailItemViewModel(media);

        MediaOpenLauncher.Open(scope, target);
    }

    private async Task OpenEditorAsync(DiaryEntry? entry, IReadOnlyList<MediaItem>? presetMedia)
    {
        // 编辑框是模态的（它对自己的 owner 日记窗口生效），同一时间只能有一个。
        // 主界面现在可以一边开着日记一边用，所以这条路径是真的会被点到的。
        if (_editorOpen)
        {
            Dialogs.CustomMessageBox.Info(
                "已经有一个「写一篇」窗口开着了，先在那边保存或取消，再打开新的。",
                "日记");
            Activate();
            return;
        }

        _editorOpen = true;
        try
        {
            var editor = new DiaryEditWindow(entry, _selectedDate.ToString("yyyy-MM-dd"))
            {
                Owner = this,
            };

            if (presetMedia is { Count: > 0 })
                editor.SetMedia(presetMedia);

            if (editor.ShowDialog() != true) return;

            // 编辑器里可能改了日期：跟过去，不然保存完看着像没写进去
            if (DateTime.TryParse(editor.EntryDate, out var newDate))
            {
                _selectedDate = newDate;
                _month = new DateTime(newDate.Year, newDate.Month, 1);
                if (_yearMode) _year = newDate.Year;   // 年视图也要跟着落到那一年
            }

            await ReloadAsync();
        }
        finally
        {
            _editorOpen = false;
        }
    }

    // ---------- 拖拽：把照片 / 视频直接拖进日记 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 只接管图片/视频；其它类型（比如拖一段文字）原样放过，不设 Handled
        if (!MediaDrop.CanAccept(e.Data)) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        // 日记窗口本身不显示拖拽提示（提示在「写一篇」里），这里无事可做
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!MediaDrop.CanAccept(e.Data)) return;
        e.Handled = true;

        var result = await MediaDrop.ResolveAsync(e.Data);
        if (result.Items.Count == 0)
        {
            Dialogs.CustomMessageBox.Warn(
                "拖进来的内容里没有能加的照片或视频。支持 jpg/png/gif/webp/heic 和 mp4/mov/mkv 等常见格式。",
                "加入日记");
            return;
        }

        Activate();
        await OpenEditorAsync(null, result.Items);
    }
}
