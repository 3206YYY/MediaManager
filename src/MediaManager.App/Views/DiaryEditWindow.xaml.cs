using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaManager.App.Services;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views;

/// <summary>新建 / 编辑一篇日记。</summary>
public partial class DiaryEditWindow : Window
{
    private static readonly string[] Moods = { "😊", "😄", "🥰", "😐", "😔", "😭", "🤩", "😴", "🔥" };

    private readonly IDiaryRepository _diary;
    private readonly IThumbnailService _thumbs;
    private readonly DiaryEntry _entry;
    private readonly ObservableCollection<MediaItem> _media = new();
    private string? _mood;

    /// <summary>保存后的归属日期（yyyy-MM-dd），供调用方跟着跳转。</summary>
    public string EntryDate { get; private set; } = DateTime.Today.ToString("yyyy-MM-dd");

    public DiaryEditWindow(DiaryEntry? entry, string defaultDate)
    {
        InitializeComponent();

        _diary = App.Services.GetRequiredService<IDiaryRepository>();
        _thumbs = App.Services.GetRequiredService<IThumbnailService>();

        _entry = entry ?? new DiaryEntry { EntryDate = defaultDate };
        EntryDate = _entry.EntryDate;

        SourceInitialized += (_, _) => Services.WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);

        Title = entry == null ? "写一篇" : "编辑回忆";
        TitleBox.Text = _entry.Title ?? string.Empty;
        ContentBox.Text = _entry.Content ?? string.Empty;
        _mood = _entry.Mood;

        if (DateTime.TryParse(_entry.EntryDate, out var date))
            DateBox.SelectedDate = date;
        else
            DateBox.SelectedDate = DateTime.Today;

        foreach (var media in _entry.Media)
            _media.Add(media);

        MediaItems.ItemsSource = _media;
        UpdateMediaHeader();

        BuildMoodButtons();

        // 缩略图可能还没生成（刚导入的文件）：生成完回填到对应的格子里
        _thumbs.ThumbnailReady += OnThumbnailReady;
        Closed += (_, _) => _thumbs.ThumbnailReady -= OnThumbnailReady;

        Loaded += (_, _) =>
        {
            ContentBox.Focus();
            ContentBox.CaretIndex = ContentBox.Text.Length;
            EnsureThumbnails();
        };

        // Ctrl+Enter 直接保存：写日记时手不用离开键盘
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Save();
                e.Handled = true;
            }
        };
    }

    /// <summary>从主界面带进来的媒体（或编辑时把已有媒体重置成这一批）。</summary>
    public void SetMedia(IEnumerable<MediaItem> items) => AddMedia(items);

    private void AddMedia(IEnumerable<MediaItem> items)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (_media.All(m => m.Id != item.Id))
            {
                _media.Add(item);
                added++;
            }
        }

        UpdateMediaHeader();
        if (added > 0) EnsureThumbnails();
    }

    /// <summary>把还没缩略图的（通常是刚导入的）丢进生成队列。</summary>
    private void EnsureThumbnails()
    {
        foreach (var item in _media)
            if (string.IsNullOrEmpty(item.ThumbnailPath)) _thumbs.Enqueue(item);
    }

    private void OnThumbnailReady(long mediaId, string thumbnailPath)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = _media.FirstOrDefault(m => m.Id == mediaId);
            if (item == null) return;

            item.ThumbnailPath = thumbnailPath;

            // MediaItem 不实现 INotifyPropertyChanged（它是扫描时批量创建的值对象，
            // 加通知会给几万条记录都挂事件字段）。改完原地替换一次，
            // 让容器重建、AsyncThumbnail 重新读到新路径。
            var index = _media.IndexOf(item);
            if (index >= 0)
            {
                _media.RemoveAt(index);
                _media.Insert(index, item);
            }
        }));
    }

    private void UpdateMediaHeader()
    {
        MediaHeader.Text = _media.Count == 0
            ? "照片 / 视频（还没有，拖进来或点上面的按钮）"
            : $"照片 / 视频（{_media.Count}）";
    }

    private void BuildMoodButtons()
    {
        MoodPanel.Children.Clear();
        foreach (var mood in Moods)
        {
            var button = new Button
            {
                Content = mood,
                Style = (Style)FindResource("MoodButton"),
                Tag = mood,
                Opacity = mood == _mood ? 1 : 0.45,
            };
            button.Click += (_, _) =>
            {
                // 点同一个就取消（允许「今天没什么心情」）
                _mood = _mood == mood ? null : mood;
                foreach (Button child in MoodPanel.Children)
                    child.Opacity = (string?)child.Tag == _mood ? 1 : 0.45;
            };
            MoodPanel.Children.Add(button);
        }
    }

    private void OnRemoveMediaClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaItem media) return;

        _media.Remove(media);
        _entry.Media.RemoveAll(m => m.Id == media.Id);
        UpdateMediaHeader();
    }

    // ---------- 加照片 / 视频 ----------

    /// <summary>从媒体库选：写日记时最常见的路径（东西早就在库里了）。
    /// 选择器是独立窗口、选完不关 —— 挑几张，回来接着写，想再挑随时再点。</summary>
    private void OnPickFromLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new Dialogs.MediaPickerWindow();
        picker.MediaSelected += media =>
        {
            AddMedia(media);
            _ = MediaDrop.RefreshSidebarAsync();
        };
        picker.Show();
        picker.Activate();
    }

    /// <summary>从磁盘选：选中的文件会先入库（日记的媒体必须有关联记录）。</summary>
    private async void OnPickFromDisk(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择照片 / 视频",
            Multiselect = true,
            Filter = "照片和视频|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.avif;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.ts|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        await ImportAndAttachAsync(dialog.FileNames);
    }

    private async Task ImportAndAttachAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        try
        {
            // 有不在库里的文件时先问一次「放哪里」（记住选择后不再问）
            var options = await ImportPolicy.ResolveAsync(paths);
            if (options == null) return;

            var import = App.Services.GetRequiredService<IMediaImportService>();
            var items = await import.ImportFilesAsync(paths, options);

            AddMedia(items);
            if (items.Count > 0) _ = MediaDrop.RefreshSidebarAsync();

            var skipped = paths.Count - items.Count;
            if (skipped > 0)
            {
                Dialogs.CustomMessageBox.Warn(
                    $"有 {skipped} 个文件没能加入：只支持图片和视频（也可能是文件不存在或读不了）。",
                    "加入日记");
            }
        }
        catch (Exception ex)
        {
            Dialogs.CustomMessageBox.Warn("导入失败：" + ex.Message, "加入日记");
        }
    }

    // ---------- 拖拽 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 不是图片/视频（比如在正文里拖一段选中的文字）就完全不碰：
        // 一旦设了 Effects / Handled，文本框自己的拖放行为也会被我们抢掉。
        if (!MediaDrop.CanAccept(e.Data))
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!MediaDrop.CanAccept(e.Data)) return;
        e.Handled = true;

        var result = await MediaDrop.ResolveAsync(e.Data);
        AddMedia(result.Items);

        if (result.Skipped > 0)
        {
            HintText.Text = $"有 {result.Skipped} 个文件没加进来（只支持图片和视频）。";
        }
        else if (result.Items.Count > 0)
        {
            HintText.Text = "也可以直接把照片 / 视频拖到这里；从电脑拖进来的文件会先加入媒体库。";
        }
    }

    // ---------- 保存 ----------

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        var title = TitleBox.Text.Trim();
        var content = ContentBox.Text.Trim();

        if (title.Length == 0 && content.Length == 0 && _media.Count == 0)
        {
            Dialogs.CustomMessageBox.Warn("这篇还是空的：写点字，或者带上几张照片再保存。", "日记");
            return;
        }

        // DatePicker 里清空了日期也允许保存，退回今天，别让用户卡在这儿
        var date = DateBox.SelectedDate ?? DateTime.Today;
        EntryDate = date.ToString("yyyy-MM-dd");

        _entry.EntryDate = EntryDate;
        _entry.Title = title.Length == 0 ? null : title;
        _entry.Content = content.Length == 0 ? null : content;
        _entry.Mood = _mood;
        _entry.Media = _media.ToList();

        try
        {
            // 保存放到后台线程：SQLite 的「异步」其实是同步执行，直接在 UI 线程等会卡一下
            Task.Run(() => _diary.SaveEntryAsync(_entry)).GetAwaiter().GetResult();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Dialogs.CustomMessageBox.Warn("保存失败：" + ex.Message, "日记");
        }
    }
}
