using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 从媒体库里挑照片/视频（日记的「＋ 从媒体库」入口）。
///
/// 为什么不直接做一个文件夹选择器：写日记时用户脑子里想的是「上次那张海边的照片」，
/// 不是「它在 D:\照片\2024\」 —— 所以按缩略图看、按文件名搜，比翻目录快得多。
/// 只列图片和视频：日记的媒体格按缩略图排，音频/文档放进去只有一个空方块。
///
/// v1.4.0 起是**独立窗口**（不模态、选完不关）：挑几张 → 回日记接着写 → 想再挑随时回来。
/// 以前是 ShowDialog，选完就关，想再挑一批得从头再开一遍。
/// </summary>
public partial class MediaPickerWindow : Window
{
    /// <summary>一次最多列多少项。再多的靠搜索缩小范围，不做分页滚动加载。</summary>
    private const int MaxItems = 300;

    private readonly ObservableCollection<ThumbnailItemViewModel> _items = new();
    private readonly IMediaRepository _repo;
    private readonly IThumbnailService _thumbs;
    private readonly DispatcherTimer _debounce;

    /// <summary>搜索的并发代号：连着敲字时只认最后一次的结果，避免慢查询回来覆盖新结果。</summary>
    private int _loadGeneration;

    /// <summary>用户点「加入日记」后触发；窗口保持打开，可以继续挑下一批。</summary>
    public event Action<IReadOnlyList<MediaItem>>? MediaSelected;

    public MediaPickerWindow()
    {
        InitializeComponent();

        _repo = App.Services.GetRequiredService<IMediaRepository>();
        _thumbs = App.Services.GetRequiredService<IThumbnailService>();

        Items.ItemsSource = _items;

        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);

        // 输入防抖：每敲一个字就查一次库，键盘快的人会打出十几个查询
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await LoadAsync();
        };

        _thumbs.ThumbnailReady += OnThumbnailReady;
        Closed += (_, _) => _thumbs.ThumbnailReady -= OnThumbnailReady;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadAsync();
        };
    }

    // ---------- 数据 ----------

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;

        var search = SearchBox.Text.Trim();
        List<MediaItem> list;
        try
        {
            list = await _repo.QueryAsync(new MediaFilter
            {
                SearchText = search.Length == 0 ? null : search,
                MediaTypes = new List<MediaType> { MediaType.Image, MediaType.Video },
                SortField = SortField.DateAdded,
                SortDirection = SortDirection.Descending,
                Limit = MaxItems + 1,   // 多取一条，用来判断「还有更多」
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取媒体库失败（选择器）");
            StatusText.Text = "读取失败：" + ex.Message;
            return;
        }

        if (generation != _loadGeneration) return;   // 已经有更新的搜索了，这批丢掉

        var more = list.Count > MaxItems;

        _items.Clear();
        foreach (var media in list.Take(MaxItems))
        {
            _items.Add(new ThumbnailItemViewModel(media) { ThumbnailPath = media.ThumbnailPath });

            // 老记录可能没缩略图（清过缓存 / 刚导入）：补一次，生成完由 ThumbnailReady 回填
            if (string.IsNullOrEmpty(media.ThumbnailPath)) _thumbs.Enqueue(media);
        }

        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = search.Length == 0 ? "媒体库里还没有照片或视频" : "没有匹配的照片或视频";

        StatusText.Text = more
            ? $"显示最近 {MaxItems} 项，用搜索找更早的"
            : search.Length == 0 ? "点一下选中，可以多选" : $"找到 {_items.Count} 项";

        UpdateCount();
    }

    private void OnThumbnailReady(long mediaId, string thumbnailPath)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var vm = _items.FirstOrDefault(i => i.Id == mediaId);
            if (vm != null) vm.ThumbnailPath = thumbnailPath;
        }));
    }

    private void UpdateCount()
    {
        var count = Items.SelectedItems.Count;
        CountText.Text = count == 0 ? string.Empty : $"已选 {count} 项";
    }

    // ---------- 交互 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var selected = Items.SelectedItems
            .OfType<ThumbnailItemViewModel>()
            .Select(vm => vm.Media)
            .ToList();

        if (selected.Count == 0)
        {
            StatusText.Text = "还没选任何内容 —— 点一下缩略图再点「加入日记」。";
            return;
        }

        MediaSelected?.Invoke(selected);
        StatusText.Text = $"已加入 {selected.Count} 项到日记。窗口没关，可以继续挑，用完点「关闭窗口」。";

        // 加完清掉选择，避免「我到底加没加过这批」的困惑：已加的不显示勾，没加的还在
        Items.SelectedItems.Clear();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
