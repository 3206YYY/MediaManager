using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaManager.App.Services;
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

    private bool _loaded;

    /// <summary>「写一篇」编辑框是否已经打开（非模态窗口里可能被二次点击触发）。</summary>
    private bool _editorOpen;

    /// <summary>从主界面带进来的媒体（打开窗口后直接进「新建」，不用再选一遍）。</summary>
    private readonly List<MediaItem> _presetMedia;

    public DiaryWindow(IEnumerable<MediaItem>? presetMedia = null)
    {
        InitializeComponent();

        _diary = App.Services.GetRequiredService<IDiaryRepository>();
        _presetMedia = presetMedia?.ToList() ?? new List<MediaItem>();

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
        // 日历一屏最多跨两个月（前后各补几天），按这个范围取标记就够
        var from = _month.AddDays(-7).ToString("yyyy-MM-dd");
        var to = _month.AddMonths(1).AddDays(14).ToString("yyyy-MM-dd");

        try
        {
            _counts = await _diary.GetDateCountsAsync(from, to);
            var total = await _diary.GetEntryCountAsync();
            TotalText.Text = total == 0 ? "还没有任何回忆" : $"共 {total} 篇";
        }
        catch (Exception ex)
        {
            TotalText.Text = "读取失败：" + ex.Message;
        }

        BuildCalendar();
        await LoadDayAsync();
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

    // ---------- 月份导航 ----------

    private async void OnPrevMonth(object sender, RoutedEventArgs e) => await ShiftMonthAsync(-1);

    private async void OnNextMonth(object sender, RoutedEventArgs e) => await ShiftMonthAsync(1);

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
