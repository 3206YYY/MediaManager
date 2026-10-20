using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaManager.App.Views;
using MediaManager.Core.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace MediaManager.App.ViewModels;

/// <summary>图片查看器视图模型（缩放、拖拽、全屏、翻页、EXIF）。</summary>
public partial class ViewerViewModel : ObservableObject
{
    private readonly IMediaRepository _repo;
    private List<ThumbnailItemViewModel> _items;
    private int _index;

    /// <summary>解码像素上限，防止超大图解码撑爆内存、阻塞 UI。</summary>
    private const int MaxDecodePixel = 4096;

    /// <summary>加载序号：快速翻页时旧任务完成后不得覆盖新图。</summary>
    private int _loadToken;

    public ViewerViewModel(IMediaRepository repo)
    {
        _repo = repo;
        _items = new List<ThumbnailItemViewModel>();
    }

    public void Initialize(List<ThumbnailItemViewModel> items, int index)
    {
        _items = items;
        _index = index;
        LoadCurrent();
    }

    [ObservableProperty]
    private ImageSource? _imageSource;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _infoText = string.Empty;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private string _resolutionText = string.Empty;

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private string _loadStatusText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>是否有上一张（用于悬浮翻页按钮的启用态）。</summary>
    [ObservableProperty]
    private bool _hasPrevious;

    /// <summary>是否有下一张。</summary>
    [ObservableProperty]
    private bool _hasNext;

    /// <summary>当前显示的媒体项（供打开所在位置等使用）。</summary>
    public ThumbnailItemViewModel? CurrentItem =>
        (_index >= 0 && _index < _items.Count) ? _items[_index] : null;

    private void LoadCurrent()
    {
        if (_items.Count == 0 || _index < 0 || _index >= _items.Count) return;

        var item = _items[_index];
        var token = ++_loadToken;   // 本次加载的唯一序号

        // 立即更新文字信息，图片异步加载
        Title = item.FileName;
        InfoText = $"{_index + 1} / {_items.Count}";
        ResolutionText = $"{item.ResolutionDisplay}  ·  {item.SizeDisplay}";
        NoteText = item.Note ?? string.Empty;
        Zoom = 1.0;
        HasPrevious = _index > 0;
        HasNext = _index < _items.Count - 1;

        // 释放上一张图的引用（已 Freeze 的 BitmapImage 不占文件句柄，仅释放内存引用）
        ImageSource = null;
        IsLoading = true;
        LoadStatusText = "加载中…";

        var path = item.Path;
        _ = Task.Run(() => LoadImageFromFile(path, token));
    }

    /// <summary>
    /// 后台线程读取并解码图片，避免大图阻塞 UI 线程导致黑屏/无响应。
    /// 读完后立即关闭流、Freeze，不持有文件句柄。
    /// </summary>
    private async Task LoadImageFromFile(string path, int token)
    {
        BitmapImage? result = null;
        string? error = null;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                // File.Exists 对「盘已拔出」只会返回 false（不抛异常），这里区分一下原因
                var root = System.IO.Path.GetPathRoot(path);
                error = !string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root)
                    ? "设备已断开（可能是移动硬盘 / U 盘被拔出），请重新插好后重试。"
                    : "文件不存在或已移动";
            }
            else
            {
                // 先整体读入内存，彻底与文件句柄解耦（保证打开时也能被删除）
                var bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
                result = Decode(bytes);
            }
        }
        catch (Exception ex)
        {
            // 常见：移动硬盘被拔出、文件被删/改名
            error = MediaManager.Core.Diagnostics.StorageErrors.FriendlyMessage(ex);
        }

        // 若期间用户已翻到别的图，丢弃本次结果，避免错位
        if (token != _loadToken) return;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (token != _loadToken) return;
            IsLoading = false;
            if (result != null)
            {
                ImageSource = result;
                LoadStatusText = string.Empty;
            }
            else
            {
                LoadStatusText = error != null ? $"加载失败：{error}" : "无法加载该图片";
            }
        });
    }

    /// <summary>从内存字节解码，限制像素尺寸，Freeze 跨线程安全。</summary>
    private static BitmapImage Decode(byte[] bytes)
    {
        using var ms = new System.IO.MemoryStream(bytes, writable: false);

        // 只读帧元数据拿尺寸（DelayCreation 不解码像素，极快），避免双重解码拖慢打开速度
        int targetW = 0, targetH = 0;
        try
        {
            using var probeMs = new System.IO.MemoryStream(bytes, writable: false);
            var frame = BitmapFrame.Create(probeMs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w > MaxDecodePixel || h > MaxDecodePixel)
            {
                double scale = Math.Min((double)MaxDecodePixel / w, (double)MaxDecodePixel / h);
                targetW = (int)(w * scale);
                targetH = (int)(h * scale);
            }
        }
        catch
        {
            // 探测失败则按原图解码
        }

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (targetW > 0 && targetH > 0)
        {
            bmp.DecodePixelWidth = targetW;
            bmp.DecodePixelHeight = targetH;
        }
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    [RelayCommand]
    private void Next()
    {
        if (_index < _items.Count - 1)
        {
            _index++;
            LoadCurrent();
        }
    }

    [RelayCommand]
    private void Previous()
    {
        if (_index > 0)
        {
            _index--;
            LoadCurrent();
        }
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(8.0, Zoom * 1.25);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.1, Zoom / 1.25);

    [RelayCommand]
    private void FitToWindow() => Zoom = 1.0;

    [RelayCommand]
    private void ActualSize() => Zoom = 1.0;
}
