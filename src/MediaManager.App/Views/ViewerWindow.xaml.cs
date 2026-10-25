using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaManager.App.Editing;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Editing;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace MediaManager.App.Views;

/// <summary>
/// 查看器 / 图片编辑器（V1.3.0）。
/// 右栏是「分类轨 + 属性面板」：基础 / 调色 / 局部 / 水印。
/// 所有编辑只改编辑文档（含快照撤销栈），预览与导出共用同一份状态：
/// 普通几何变换走 WPF 位图，颜色/滤镜走后台 ImageSharp 降采样预览，
/// 保存时再按原图尺寸烘焙一次，保证「所见即所得」且不毁原图。
/// </summary>
public partial class ViewerWindow : Window
{
    private enum ToolMode { None, Crop, Ink, Privacy, Shape }

    /// <summary>随机跳转的范围。</summary>
    private enum RandomScope { List, Folder, All, Favorite, Category, Tag }

    private const string RandomScopeSettingKey = "ViewerRandomScope";
    private const string RandomScopeIdSettingKey = "ViewerRandomScopeId";
    private const string RandomScopeNameSettingKey = "ViewerRandomScopeName";

    // 随机跳转
    private RandomScope _randomScope = RandomScope.List;
    private long? _randomScopeId;
    private string? _randomScopeName;
    private bool _randomJumping;
    private bool _randomSettingsLoaded;

    private readonly ViewerViewModel _viewModel;
    private readonly EditorPreviewService _previewService = new();

    // 视图状态
    private bool _isDragging;
    /// <summary>左键已按下但还没超过拖动死区（避免手抖把图挪歪）。</summary>
    private bool _dragArmed;
    private Point _dragStartPosition;
    /// <summary>拖动死区（DIP）：移动小于这个距离不算拖动。</summary>
    private const double DragDeadZone = 4.0;
    private Point _lastPosition;
    private bool _isFullScreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;

    // 编辑文档
    private EditorDocument? _doc;
    private ToolMode _mode = ToolMode.None;
    private bool _comparing;
    private bool _suppressBaseApply;
    private bool _suppressOverlayRebuild;
    private GeomSig? _lastGeometry;

    // 后台调色预览
    private readonly DispatcherTimer _previewTimer;
    private int _previewToken;

    /// <summary>最近一次「已调色」预览位图：打码取色要用它，才能和导出效果一致。</summary>
    private BitmapSource? _previewBitmap;

    // 连续调整（滑块拖动）合并为一次撤销
    private readonly DispatcherTimer _commitTimer;
    private string? _continuousName;
    private bool _suppressAdjustUi;

    // 文字（同一时刻只允许一个编辑会话）
    private TextOverlayItem? _activeText;
    private TextBox? _activeTextBox;
    private Border? _activeTextHost;
    private bool _editingText;
    private EditorSnapshot? _textEditSnapshot;
    private bool _textDirty;
    private bool _suppressTextPanel;

    private bool IsTextEditing => _editingText && _activeText != null;

    // 涂鸦
    private string _inkColor = "#FF3B3B";
    private double _inkWidthDisplay = 4;

    // 形状标注
    private ShapeKind _shapeKind = ShapeKind.Rect;
    private ShapeOverlayItem? _shapeDraft;
    private FrameworkElement? _shapeDraftElement;

    // 编辑预设
    private readonly List<EditorPreset> _presets = new();
    private EditorPresetStore? _presetStore;

    // 裁剪
    private CropBox _cropBox;
    private double? _cropAspect;

    // 打码
    private PrivacyMode _privacyMode = PrivacyMode.Mosaic;
    private bool _privacyRectTool;
    private double _privacyWidthDisplay = 40;
    /// <summary>马赛克颗粒（相对画布宽度的千分比）：17 ≈ 画布宽度 / 60。</summary>
    private double _privacyBlockPermille = 17;
    private PrivacyOverlayItem? _privacyDraft;
    private Point _privacyRectStart;
    private readonly List<FrameworkElement> _draftElements = new();

    // 图片水印
    private ImageWatermarkItem? _activeWatermark;
    private Image? _wmImageElement;
    private Border? _wmBorderElement;
    private FrameworkElement? _wmHandleElement;
    private Border? _wmHitElement;
    private ImageWatermarkItem? _wmDragItem;
    private Point _wmDragOrigin;
    private double _wmStartX, _wmStartY;
    private FrameworkElement? _wmDragSource;
    private bool _suppressMarkOpacity;

    // 平铺文字水印
    private bool _suppressTilePanel;

    // 输出尺寸
    private bool _suppressSizeUi;

    // 保存反馈
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _saveResultTimer;
    private bool _saving;
    private string? _lastSavedPath;

    /// <summary>
    /// 保存完成（参数：保存结果，含「是否原地覆盖了副本」）。
    /// 主界面据此刷新列表并定位到这张图；原地覆盖时还要重建缩略图。
    /// </summary>
    public event EventHandler<SaveResult>? CopySaved;

    public ViewerWindow(ViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _previewTimer.Tick += OnPreviewTick;

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _commitTimer.Tick += (_, _) => CommitContinuous();

        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ImageArea.SizeChanged += (_, _) => LayoutCanvas();
        ImageBox.SizeChanged += (_, _) =>
        {
            if (!_suppressOverlayRebuild) RebuildOverlays();
            // 窗口变大后旧的平移量可能已经越界（图片边缘会离开窗口），重新夹一次
            ClampPan();
        };

        BuildColorPalettes();
        BuildFilterButtons();
        WireSliders();
        InitializePresets();
        ResetSelections();

        UpdateHistoryButtons();
        ShowCategory("basic");
        Loaded += OnViewerLoaded;
        RandomMenu.Opened += (_, _) => UpdateRandomMenu();
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _commitTimer.Stop();
            _previewService.Dispose();
            DetachDocument();
        };
    }

    private void ResetSelections()
    {
        CropRatioCombo.SelectedIndex = 0;
        SizeCombo.SelectedIndex = 0;
        PrivacyModeCombo.SelectedIndex = 0;
        PrivacyToolCombo.SelectedIndex = 0;
        FontCombo.SelectedIndex = 0;
        TextBgColorCombo.SelectedIndex = 0;
        TextOutlineColorCombo.SelectedIndex = 0;
    }

    public void SetItems(List<ThumbnailItemViewModel> items, int index)
    {
        _viewModel.Initialize(items, index);
    }

    // ================= 加载 / 生命周期 =================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.ImageSource))
            LoadDocument();

        // 随机跳转/翻页后，标题栏也跟着显示当前文件名
        if (e.PropertyName == nameof(ViewerViewModel.Title) && !string.IsNullOrWhiteSpace(_viewModel.Title))
            Title = $"{_viewModel.Title} - 查看器";
    }

    private void LoadDocument()
    {
        DetachDocument();
        UpdateSaveButtonLabel();

        if (_viewModel.ImageSource is not BitmapSource bitmap)
        {
            _doc = null;
            PreviewImage.Source = null;
            ToolHint.Text = _viewModel.LoadStatusText;
            return;
        }

        var item = _viewModel.CurrentItem;
        if (item == null)
        {
            _doc = null;
            PreviewImage.Source = null;
            ToolHint.Text = "无法确定当前图片";
            return;
        }

        _previewService.Invalidate();
        _lastGeometry = null;

        _doc = new EditorDocument(bitmap, item.Path);
        _doc.BaseChanged += OnDocBaseChanged;
        _doc.OverlaysChanged += OnDocOverlaysChanged;
        _doc.History.Changed += OnHistoryChanged;

        ResetToolState();
        ApplyBase();
        ResetNavArrows();
        UpdateHistoryButtons();
        ToolHint.Text = HintText;
    }

    private static string HintText => "提示：右侧选择分类与工具，Ctrl+Z 撤销，按住 \\ 看原图";

    /// <summary>解绑旧文档事件，避免切换图片后旧文档/位图被事件引用导致内存泄漏。</summary>
    private void DetachDocument()
    {
        if (_doc == null) return;
        _doc.BaseChanged -= OnDocBaseChanged;
        _doc.OverlaysChanged -= OnDocOverlaysChanged;
        _doc.History.Changed -= OnHistoryChanged;
        _doc = null;

        MosaicLayer.Children.Clear();
        WatermarkLayer.Children.Clear();
        ShapeLayer.Children.Clear();
        TextLayer.Children.Clear();
        TileLayer.Children.Clear();
        InkLayer.Strokes.Clear();
        _activeText = null;
        _activeTextBox = null;
        _activeTextHost = null;
        _editingText = false;
        _textEditSnapshot = null;
        _activeWatermark = null;
        _wmImageElement = null;
        _wmBorderElement = null;
        _wmHandleElement = null;
        _wmHitElement = null;
        _privacyDraft = null;
        _draftElements.Clear();
        _shapeDraft = null;
        _shapeDraftElement = null;
    }

    private void OnDocBaseChanged(object? sender, EventArgs e) => ApplyBase();
    private void OnDocOverlaysChanged(object? sender, EventArgs e) => RebuildOverlays();
    private void OnHistoryChanged(object? sender, EventArgs e) => UpdateHistoryButtons();

    private void ResetToolState()
    {
        _mode = ToolMode.None;
        CropLayer.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        CropTool.IsChecked = false;
        TextTool.IsChecked = false;
        InkTool.IsChecked = false;
        MosaicTool.IsChecked = false;
        ShapeRectTool.IsChecked = false;
        ShapeArrowTool.IsChecked = false;
        ShapeEllipseTool.IsChecked = false;
        _shapeDraft = null;
        _shapeDraftElement = null;
        TextProps.Visibility = Visibility.Collapsed;
        MarkProps.Visibility = Visibility.Collapsed;
        TileProps.Visibility = Visibility.Collapsed;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        _activeText = null;
        _activeTextBox = null;
        _activeTextHost = null;
        _editingText = false;
        _textEditSnapshot = null;
        _activeWatermark = null;
        HideDraft();
        SyncTextAngleFromDocument();
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;
    }

    // ================= 画布布局 / 底图 =================

    private double CanvasW
    {
        get
        {
            var v = ImageBox.Width;
            return double.IsFinite(v) && v > 0 ? v : ImageBox.ActualWidth;
        }
    }

    private double CanvasH
    {
        get
        {
            var v = ImageBox.Height;
            return double.IsFinite(v) && v > 0 ? v : ImageBox.ActualHeight;
        }
    }

    private double DisplayScale =>
        _doc == null || _doc.VisibleWidth <= 0 ? 1 : CanvasW / _doc.VisibleWidth;

    private readonly record struct GeomSig(
        int X, int Y, int W, int H, double Straighten, int Rotation, bool Flip);

    private static GeomSig SignatureOf(EditorDocument doc) => new(
        doc.Crop.X, doc.Crop.Y, doc.Crop.Width, doc.Crop.Height,
        Math.Round(doc.StraightenAngle, 3),
        ((doc.Rotation % 360) + 360) % 360, doc.FlipHorizontal);

    private bool HasColorEdits =>
        _doc != null && (_doc.Filter != EditorFilter.None || !_doc.Adjustments.IsIdentity);

    /// <summary>
    /// 打码取样用的底图：有调色时用调色后的预览位图，保证预览与导出的马赛克颜色一致；
    /// 比例算法按画布宽度计算，所以和预览位图的实际尺寸无关。
    /// </summary>
    private BitmapSource? PrivacySampleSource =>
        _doc == null
            ? null
            : HasColorEdits && _previewBitmap != null
                ? _previewBitmap
                : _doc.GetBaseBitmap();

    private void ApplyBase()
    {
        if (_doc == null || _suppressBaseApply) return;

        var sig = SignatureOf(_doc);
        var colorEdits = HasColorEdits;

        // 几何变了，旧的调色预览与打码取色都作废
        if (_lastGeometry != sig || !colorEdits) _previewBitmap = null;

        // 几何没变且已有调色时，不重置底图，避免「松手后颜色闪一下再回来」
        if (_lastGeometry != sig || !colorEdits)
            PreviewImage.Source = _doc.GetBaseBitmap();

        _lastGeometry = sig;

        SyncAdjustmentUi();
        SyncOutputSizeUi();
        RefreshFilterButtons();
        LayoutCanvas();
        RebuildOverlays();

        if (colorEdits) SchedulePreview();
        else
        {
            _previewToken++;
            _previewTimer.Stop();
        }
    }

    /// <summary>按当前底图比例计算画布尺寸（适应区域，不放大超过 100%）。</summary>
    private void LayoutCanvas()
    {
        if (_doc == null) return;

        var vw = Math.Max(1, _doc.VisibleWidth);
        var vh = Math.Max(1, _doc.VisibleHeight);
        var availW = Math.Max(50, ImageArea.ActualWidth - 24);
        var availH = Math.Max(50, ImageArea.ActualHeight - 24);

        var scale = Math.Min(Math.Min(availW / vw, availH / vh), 1.0);
        scale = Math.Max(0.01, scale);

        _suppressOverlayRebuild = true;
        ImageBox.Width = Math.Max(24, vw * scale);
        ImageBox.Height = Math.Max(24, vh * scale);
        _suppressOverlayRebuild = false;

        if (_mode == ToolMode.Crop) LayoutCrop();
    }

    // ================= 后台调色预览 =================

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _ = RenderPreviewAsync();
    }

    /// <summary>
    /// 后台线程用 ImageSharp 降采样图算颜色，不阻塞 UI；
    /// 结果回来时校验令牌与状态，过期结果直接丢弃（防止翻页/连续拖动时错位）。
    /// </summary>
    private async Task RenderPreviewAsync()
    {
        var doc = _doc;
        if (doc == null) return;

        var token = ++_previewToken;
        var sig = SignatureOf(doc);
        var adjustments = doc.Adjustments;
        var filter = doc.Filter;

        BitmapSource? result = null;
        try
        {
            result = await Task.Run(() => _previewService.RenderPreview(doc)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "生成调色预览失败");
            return;
        }

        if (result == null) return;
        if (token != _previewToken || !ReferenceEquals(doc, _doc)) return;
        if (_doc == null || SignatureOf(_doc) != sig) return;
        if (doc.Filter != filter || doc.Adjustments != adjustments) return;

        _previewBitmap = result;
        PreviewImage.Source = result;

        // 打码取样跟随调色后的颜色，避免「预览灰块、导出暖色块」
        if (_doc!.Mosaics.Count > 0 || _doc.Privacies.Count > 0)
            RebuildMosaics(CanvasW, CanvasH);
    }

    // ================= 叠加层渲染 =================

    /// <summary>
    /// 重建画布叠加层。文字编辑中默认不重建，避免把正在输入的 TextBox 销毁导致焦点丢失。
    /// </summary>
    private void RebuildOverlays(bool forceText = false)
    {
        if (_doc == null) return;

        EnsureSelectionsValid();

        if (IsTextEditing && !forceText) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        RebuildMosaics(w, h);
        RebuildWatermarks(w, h);
        RebuildTexts(w, h);
        RebuildShapes(w, h);
        RebuildInk(w, h);
        RebuildTileWatermark(w, h);
        UpdateHistoryButtons();
    }

    /// <summary>撤销/重做后对象会被整体替换，旧的选中引用必须失效。</summary>
    private void EnsureSelectionsValid()
    {
        if (_doc == null) return;

        if (_activeText != null && !_doc.Texts.Contains(_activeText))
        {
            _activeText = null;
            _activeTextBox = null;
            _activeTextHost = null;
            _editingText = false;
            _textEditSnapshot = null;
            _textDirty = false;
            TextProps.Visibility = Visibility.Collapsed;
            TextTool.IsChecked = false;
            SyncTextAngleFromDocument();
        }

        if (_activeWatermark != null && !_doc.Watermarks.Contains(_activeWatermark))
        {
            _activeWatermark = null;
            MarkProps.Visibility = Visibility.Collapsed;
        }
    }

    private void RebuildMosaics(double w, double h)
    {
        if (_doc == null) return;

        MosaicLayer.Children.Clear();
        _draftElements.Clear();

        var baseBmp = PrivacySampleSource;
        if (baseBmp == null) return;

        foreach (var mosaic in _doc.Mosaics)
        {
            var img = EditorOverlayFactory.BuildMosaicRect(mosaic, baseBmp, w, h);
            if (img != null) MosaicLayer.Children.Add(img);
        }

        foreach (var mode in new[] { PrivacyMode.Mosaic, PrivacyMode.Blur })
        {
            var layer = EditorOverlayFactory.BuildPrivacyLayer(_doc.Privacies, mode, baseBmp, w, h);
            if (layer != null) MosaicLayer.Children.Add(layer);
        }

        ApplyOverlayZOrder();
    }

    /// <summary>
    /// 打码与涂鸦的上下关系跟随「最近一次用的是哪个工具」：
    /// 最后用的是打码 → 打码盖在涂鸦上；最后用的是画笔 → 画笔回到打码之上。
    /// 55 = 在涂鸦(50) 之上、平铺水印(60) 之下；10 = 原来的位置（所有标注之下）。
    /// </summary>
    private void ApplyOverlayZOrder()
    {
        if (_doc == null) return;

        var z = _doc.PrivacyAboveInk ? 55 : 10;
        if (Panel.GetZIndex(MosaicLayer) != z) Panel.SetZIndex(MosaicLayer, z);
    }

    private void RebuildWatermarks(double w, double h)
    {
        if (_doc == null) return;

        WatermarkLayer.Children.Clear();
        _wmImageElement = null;
        _wmBorderElement = null;
        _wmHandleElement = null;
        _wmHitElement = null;

        foreach (var item in _doc.Watermarks)
        {
            var img = EditorOverlayFactory.BuildWatermark(item, w, h);
            WatermarkLayer.Children.Add(img);

            var hit = new Border
            {
                Width = img.Width,
                Height = img.Height,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Tag = item,
            };
            Canvas.SetLeft(hit, Canvas.GetLeft(img));
            Canvas.SetTop(hit, Canvas.GetTop(img));
            hit.MouseLeftButtonDown += (s, ev) =>
            {
                SelectWatermark(item);
                BeginWatermarkDrag(item, resize: false, ev, (FrameworkElement)s);
            };
            WatermarkLayer.Children.Add(hit);

            if (!ReferenceEquals(item, _activeWatermark)) continue;

            var border = new Border
            {
                Width = img.Width,
                Height = img.Height,
                BorderBrush = ParseBrush("#4A90D9"),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, Canvas.GetLeft(img));
            Canvas.SetTop(border, Canvas.GetTop(img));
            WatermarkLayer.Children.Add(border);

            var handle = new Thumb
            {
                Width = 14,
                Height = 14,
                Cursor = Cursors.SizeNWSE,
                Style = (Style)FindResource("CropHandle"),
            };
            Canvas.SetLeft(handle, Canvas.GetLeft(img) + img.Width - 7);
            Canvas.SetTop(handle, Canvas.GetTop(img) + img.Height - 7);
            handle.DragDelta += (s, ev) => ResizeWatermark(ev.HorizontalChange);
            WatermarkLayer.Children.Add(handle);

            _wmImageElement = img;
            _wmBorderElement = border;
            _wmHandleElement = handle;
            _wmHitElement = hit;
        }
    }

    /// <summary>拖动/缩放水印时直接改位置，不整体重建（否则会把正在拖的元素换掉）。</summary>
    private void LayoutWatermarkElement(ImageWatermarkItem item)
    {
        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        var left = item.X * w;
        var top = item.Y * h;
        var width = item.W * w;
        var height = item.H * h;

        if (_wmImageElement != null)
        {
            _wmImageElement.Width = width;
            _wmImageElement.Height = height;
            Canvas.SetLeft(_wmImageElement, left);
            Canvas.SetTop(_wmImageElement, top);
        }
        if (_wmBorderElement != null)
        {
            _wmBorderElement.Width = width;
            _wmBorderElement.Height = height;
            Canvas.SetLeft(_wmBorderElement, left);
            Canvas.SetTop(_wmBorderElement, top);
        }
        if (_wmHitElement != null)
        {
            _wmHitElement.Width = width;
            _wmHitElement.Height = height;
            Canvas.SetLeft(_wmHitElement, left);
            Canvas.SetTop(_wmHitElement, top);
        }
        if (_wmHandleElement != null)
        {
            Canvas.SetLeft(_wmHandleElement, left + width - 7);
            Canvas.SetTop(_wmHandleElement, top + height - 7);
        }
    }

    private void RebuildTexts(double w, double h)
    {
        if (_doc == null) return;

        TextLayer.Children.Clear();
        var scale = DisplayScale;

        foreach (var item in _doc.Texts)
        {
            if (ReferenceEquals(item, _activeText) && IsTextEditing)
            {
                TextLayer.Children.Add(CreateTextEditor(item, w, h, scale));
                continue;
            }

            if (!OverlayTextRules.HasContent(item.Text)) continue;

            var element = EditorOverlayFactory.BuildText(item, w, h, scale);
            element.IsHitTestVisible = true;
            element.Cursor = Cursors.Hand;
            element.Tag = item;
            element.MouseLeftButtonDown += (s, ev) =>
            {
                SelectText(item);
                ev.Handled = true;
            };
            TextLayer.Children.Add(element);
        }
    }

    private Border CreateTextEditor(TextOverlayItem item, double w, double h, double scale)
    {
        var box = new TextBox
        {
            Text = item.Text,
            Background = string.IsNullOrWhiteSpace(item.BackgroundHex)
                ? Brushes.Transparent
                : ParseBrush(item.BackgroundHex),
            BorderThickness = new Thickness(0),
            Foreground = ParseBrush(item.ColorHex),
            Opacity = item.Opacity,
            CaretBrush = Brushes.White,
            FontFamily = new FontFamily(item.FontFamily),
            FontSize = Math.Max(6, item.FontSize * scale),
            FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
            MinWidth = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(2),
            MaxWidth = Math.Max(120, w - item.X * w),
        };

        var handle = new Border
        {
            Height = 16,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x90, 0xD9)),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock
            {
                Text = "✥ 拖动",
                FontSize = 10,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var host = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            Child = new StackPanel { Children = { handle, box } },
            Tag = item,
        };

        Canvas.SetLeft(host, item.X * w);
        Canvas.SetTop(host, item.Y * h);

        box.TextChanged += (_, _) =>
        {
            if (_activeTextBox == null || _activeText == null) return;
            _activeText.Text = box.Text;
            _textDirty = true;
            MeasureText(_activeText, box);
        };

        // 点右侧属性面板会让文本框失焦，所以不在这里结束编辑：
        // 改为「点画布空白处」或 Esc 结束（见 OnCanvasMouseDown / OnKeyDown）。
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                ev.Handled = true;
                EndTextEditing();
            }
        };

        Point start = default;
        bool dragging = false;
        double startLeft = 0, startTop = 0;

        handle.MouseLeftButtonDown += (_, ev) =>
        {
            dragging = true;
            start = ev.GetPosition(TextLayer);
            startLeft = Canvas.GetLeft(host);
            startTop = Canvas.GetTop(host);
            handle.CaptureMouse();
            ev.Handled = true;
        };
        handle.MouseMove += (_, ev) =>
        {
            if (!dragging) return;
            var p = ev.GetPosition(TextLayer);
            var left = Math.Clamp(startLeft + (p.X - start.X), 0, Math.Max(0, w - 40));
            var top = Math.Clamp(startTop + (p.Y - start.Y), 0, Math.Max(0, h - 24));
            Canvas.SetLeft(host, left);
            Canvas.SetTop(host, top);
            item.X = left / w;
            item.Y = top / h;
            _textDirty = true;
        };
        handle.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            handle.ReleaseMouseCapture();
        };

        _activeTextBox = box;
        _activeTextHost = host;
        return host;
    }

    /// <summary>判断元素是否位于指定父级的可视子树内。</summary>
    private static bool IsInTree(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;

            node = node switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                FrameworkContentElement fce => fce.Parent,
                _ => null,
            };
        }
        return false;
    }

    private void MeasureText(TextOverlayItem item, TextBox box)
    {
        var w = CanvasW;
        var h = CanvasH;
        if (w <= 0 || h <= 0) return;

        box.Measure(new Size(Math.Max(20, w), Math.Max(20, h)));
        item.W = box.DesiredSize.Width / w;
        item.H = box.DesiredSize.Height / h;
    }

    private void RebuildShapes(double w, double h)
    {
        if (_doc == null) return;

        // 拖动中的形状由 RefreshShapeDraft 单独维护，这里只重画已提交的
        if (_shapeDraftElement != null)
        {
            ShapeLayer.Children.Remove(_shapeDraftElement);
            _shapeDraftElement = null;
        }

        ShapeLayer.Children.Clear();
        foreach (var item in _doc.Shapes)
        {
            var element = EditorOverlayFactory.BuildShape(item, w, h);
            if (element != null) ShapeLayer.Children.Add(element);
        }

        if (_shapeDraft != null) RefreshShapeDraft();
    }

    private void RebuildInk(double w, double h)
    {
        if (_doc == null) return;

        InkLayer.Strokes.Clear();
        foreach (var model in _doc.Strokes)
            InkLayer.Strokes.Add(CreateInkStroke(model, w, h));

        InkLayer.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = ParseColor(_inkColor),
            Width = _inkWidthDisplay,
            Height = _inkWidthDisplay,
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
        };
    }

    private void RebuildTileWatermark(double w, double h)
    {
        if (_doc == null) return;

        TileLayer.Children.Clear();
        if (_doc.TileWatermark == null)
        {
            TileProps.Visibility = Visibility.Collapsed;
            return;
        }

        var element = EditorOverlayFactory.BuildTileWatermark(_doc.TileWatermark, w, h);
        if (element != null) TileLayer.Children.Add(element);

        // 撤销/重做后把面板同步回模型；正在输入水印文字时别打断光标
        if (!TileTextBox.IsKeyboardFocused) UpdateTilePanelFromItem();
    }

    private static Stroke CreateInkStroke(StrokeOverlayItem model, double w, double h)
    {
        var points = new StylusPointCollection();
        foreach (var p in model.Points)
            points.Add(new StylusPoint(p.X * w, p.Y * h));

        var attrs = new DrawingAttributes
        {
            Color = ParseColor(model.ColorHex),
            Width = Math.Max(1, model.Thickness * w),
            Height = Math.Max(1, model.Thickness * w),
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
        };
        return new Stroke(points, attrs);
    }

    // ================= 顶部：撤销 / 重做 / 对比 / 保存 =================

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _doc?.History.CanUndo == true;
        RedoButton.IsEnabled = _doc?.History.CanRedo == true;
        UndoButton.ToolTip = _doc?.History.UndoName is string u ? $"撤销「{u}」（Ctrl+Z）" : "没有可撤销的操作";
        RedoButton.ToolTip = _doc?.History.RedoName is string r ? $"重做「{r}」（Ctrl+Y）" : "没有可重做的操作";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_doc?.History.CanUndo != true) return;
        EndTextEditing();
        CommitContinuous();
        var name = _doc.History.Undo();
        if (name != null) ShowToast($"已撤销：{name}");
    }

    private void Redo()
    {
        if (_doc?.History.CanRedo != true) return;
        EndTextEditing();
        CommitContinuous();
        var name = _doc.History.Redo();
        if (name != null) ShowToast($"已重做：{name}");
    }

    private void OnCompareDown(object sender, MouseButtonEventArgs e) => BeginCompare();

    private void OnCompareUp(object sender, MouseButtonEventArgs e) => EndCompare();

    private void BeginCompare()
    {
        if (_doc == null || _comparing) return;
        _comparing = true;
        EndTextEditing();
        // 用「几何已应用、颜色为原始色」的底图对比，否则裁剪/旋转过的图一按 \ 就变形
        PreviewImage.Source = _doc.GetBaseBitmap();
        MosaicLayer.Visibility = Visibility.Collapsed;
        WatermarkLayer.Visibility = Visibility.Collapsed;
        ShapeLayer.Visibility = Visibility.Collapsed;
        TextLayer.Visibility = Visibility.Collapsed;
        InkLayer.Visibility = Visibility.Collapsed;
        TileLayer.Visibility = Visibility.Collapsed;
        ToolHint.Text = "正在显示原图（松开恢复）";
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Alt+Tab / 点开别的窗口时收不到 KeyUp，兜底恢复原图显示
        if (_comparing) EndCompare();
    }

    private void EndCompare()
    {
        if (!_comparing) return;
        _comparing = false;
        MosaicLayer.Visibility = Visibility.Visible;
        WatermarkLayer.Visibility = Visibility.Visible;
        ShapeLayer.Visibility = Visibility.Visible;
        TextLayer.Visibility = Visibility.Visible;
        InkLayer.Visibility = Visibility.Visible;
        TileLayer.Visibility = Visibility.Visible;
        _lastGeometry = null;
        ApplyBase();
        ToolHint.Text = HintText;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            CustomMessageBox.Warn("当前没有可保存的图片。", "保存副本");
            return;
        }

        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;

        try
        {
            EndTextEditing();
            CommitContinuous();

            var item = _viewModel.CurrentItem;
            if (item == null)
            {
                CustomMessageBox.Warn("当前没有可保存的图片。", "保存副本");
                return;
            }

            HideSaveResult();
            ToolHint.Text = EditorRenderer.IsEditedCopy(item.Path) ? "正在更新副本…" : "正在保存副本…";

            var result = await EditorRenderer.SaveAsync(_doc);
            _doc.SavedUndoCount = _doc.History.UndoCount;

            ShowSaveResult(result);
            CopySaved?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "保存编辑副本失败");
            ToolHint.Text = "保存失败（文件未改动）";
            HideSaveResult();
            CustomMessageBox.Error(
                StorageErrors.WithAction(ex, "保存编辑副本")
                + "\n\n正在编辑的文件没有被改动。请检查磁盘剩余空间、目标文件夹权限，或换一个位置后再试。",
                "保存失败");
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 保存按钮文案跟随「当前这张是不是副本」：
    /// 原图 → 「保存副本」（会新建 _edited，原图不动）；副本 → 「更新副本」（直接改它自己）。
    /// </summary>
    private void UpdateSaveButtonLabel()
    {
        if (SaveButton == null) return;

        var isCopy = EditorRenderer.IsEditedCopy(_viewModel.CurrentItem?.Path);
        SaveButton.Content = isCopy ? "💾 更新副本" : "💾 保存副本";
        SaveButton.ToolTip = isCopy
            ? "当前这张就是编辑副本，保存会直接覆盖它"
            : "把所有修改保存为 _edited 副本，原图不变";
    }

    private void ShowSaveResult(SaveResult result)
    {
        _lastSavedPath = result.Path;

        SaveResultTitle.Text = result.OverwroteSource
            ? "已更新这个副本"
            : "已保存为新文件（原图未修改）";
        SaveResultName.Text = result.FileName;
        var tag = result.OverwroteSource ? "原文件已被覆盖" : "原图未修改";
        SaveResultDetail.Text =
            $"{result.Width} × {result.Height} 像素 · {FormatFileSize(result.SizeBytes)} · {tag}";
        SaveResultBar.Visibility = Visibility.Visible;
        ToolHint.Text = result.OverwroteSource
            ? $"✅ 已更新副本：{result.FileName}"
            : $"✅ 已保存为新文件：{result.FileName}（原图未修改）";

        ToastBar.Visibility = Visibility.Collapsed;
        _toastTimer?.Stop();

        _saveResultTimer?.Stop();
        _saveResultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _saveResultTimer.Tick += (_, _) => HideSaveResult();
        _saveResultTimer.Start();
    }

    private void HideSaveResult()
    {
        _saveResultTimer?.Stop();
        SaveResultBar.Visibility = Visibility.Collapsed;
    }

    private void OnSaveResultDismiss(object sender, RoutedEventArgs e) => HideSaveResult();

    private void OnSaveResultOpenFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSavedPath)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_lastSavedPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "打开副本所在文件夹失败");
            CustomMessageBox.Warn(
                StorageErrors.WithAction(ex, "打开文件夹") + $"\n\n文件位置：\n{_lastSavedPath}",
                "打开文件夹");
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    // ================= 复制到剪贴板 / 另存为 =================

    /// <summary>把当前编辑效果生成图片放进剪贴板，可直接粘贴到微信 / QQ。</summary>
    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            CustomMessageBox.Warn("当前没有可复制的图片。", "复制到剪贴板");
            return;
        }

        if (_saving) return;
        _saving = true;
        CopyButton.IsEnabled = false;

        try
        {
            EndTextEditing();
            CommitContinuous();

            ToolHint.Text = "正在生成图片…";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var bitmap = EditorRenderer.Render(_doc);

            // 剪贴板偶尔被其它程序占用，失败时稍等再试一次
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetImage(bitmap);
                    break;
                }
                catch (COMException) when (attempt == 0)
                {
                    await Task.Delay(120);
                }
            }

            ToolHint.Text = "✅ 已复制当前效果到剪贴板";
            ShowToast("已复制到剪贴板，可直接粘贴到微信 / QQ");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "复制图片到剪贴板失败");
            ToolHint.Text = "复制失败";
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "复制图片到剪贴板"), "复制失败");
        }
        finally
        {
            _saving = false;
            CopyButton.IsEnabled = true;
        }
    }

    /// <summary>另存为：自选路径、格式与 JPEG / WebP 质量。</summary>
    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            CustomMessageBox.Warn("当前没有可另存的图片。", "另存为");
            return;
        }

        if (_saving) return;

        var sourcePath = _doc.SourcePath;
        var initialFormat = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SaveImageFormat.Jpeg,
            ".webp" => SaveImageFormat.Webp,
            ".bmp" => SaveImageFormat.Bmp,
            _ => SaveImageFormat.Png,
        };

        EndTextEditing();
        CommitContinuous();

        var suggested = BuildSaveAsName(sourcePath, initialFormat);
        var dialog = new SaveAsWindow(sourcePath, suggested, initialFormat) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _saving = true;
        SaveAsButton.IsEnabled = false;

        try
        {
            ToolHint.Text = "正在另存为…";
            HideSaveResult();

            var result = await EditorRenderer.SaveAsAsync(_doc, dialog.OutputPath, dialog.Format, dialog.Quality);

            _lastSavedPath = result.Path;
            ToolHint.Text = $"✅ 已另存为：{result.FileName}";
            ShowToast($"已另存为：{result.FileName}（{result.Width} × {result.Height}）");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "另存为失败");
            ToolHint.Text = "另存为失败";
            CustomMessageBox.Error(
                StorageErrors.WithAction(ex, "另存为")
                + "\n\n正在编辑的文件没有被改动。请检查目标文件夹权限、磁盘剩余空间，或换一个位置后再试。",
                "另存为失败");
        }
        finally
        {
            _saving = false;
            SaveAsButton.IsEnabled = true;
        }
    }

    /// <summary>另存为的默认文件名：原名 + _edited + 目标格式扩展名（本身就是副本时加 _2，避免和原文件同名）。</summary>
    private static string BuildSaveAsName(string sourcePath, SaveImageFormat format)
    {
        var ext = EditorRenderer.ExtensionOf(format);
        var name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        while (name.EndsWith(EditedFileNaming.Suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^EditedFileNaming.Suffix.Length];
        if (name.Length == 0) name = "image";

        var suggested = name + EditedFileNaming.Suffix + ext;
        if (string.Equals(suggested, System.IO.Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase))
            suggested = name + EditedFileNaming.Suffix + "_2" + ext;
        return suggested;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>修改底图（裁剪/旋转/翻转）时合并通知，避免一次操作刷新两遍。</summary>
    private void MutateBase(string name, Action change)
    {
        if (_doc == null) return;

        _suppressBaseApply = true;
        try
        {
            _doc.BeginEdit();
            change();
            _doc.InvalidateBase();
        }
        finally
        {
            _suppressBaseApply = false;
        }

        _doc.Commit(name);
    }

    // ================= 基础：旋转 / 翻转 =================

    private void OnRotateLeft(object sender, RoutedEventArgs e) => RotateBy(-90);

    private void OnRotateRight(object sender, RoutedEventArgs e) => RotateBy(90);

    private void RotateBy(int degrees)
    {
        if (_doc == null) return;

        EndTextEditing();
        if (_mode == ToolMode.Crop) ExitCropMode();

        MutateBase(degrees > 0 ? "向右旋转 90°" : "向左旋转 90°", () => _doc.Rotate(degrees));
        ShowToast(degrees > 0 ? "已向右旋转 90°" : "已向左旋转 90°");
    }

    private void OnFlipHorizontal(object sender, RoutedEventArgs e) => FlipImage(true);

    private void OnFlipVertical(object sender, RoutedEventArgs e) => FlipImage(false);

    private void FlipImage(bool horizontal)
    {
        if (_doc == null) return;

        EndTextEditing();
        if (_mode == ToolMode.Crop) ExitCropMode();

        // 只镜像画面与标注位置，文字内容本身不镜像（否则会变成反字）
        MutateBase(horizontal ? "水平翻转" : "垂直翻转", () => _doc.Flip(horizontal));
        ShowToast(horizontal ? "已水平翻转" : "已垂直翻转");
    }

    // ================= 基础：裁剪 =================

    private void OnCropToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (CropTool.IsChecked == true) EnterCropMode();
        else ExitCropMode();
    }

    private void OnCropRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CropRatioCombo?.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString() ?? "0";
        _cropAspect = double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0
            ? a
            : null;

        if (_mode != ToolMode.Crop) return;

        var w = CanvasW;
        var h = CanvasH;
        _cropBox = CropMath.FitInside(_cropBox, _cropAspect, w, h);
        LayoutCrop();
    }

    private void EnterCropMode()
    {
        if (_doc == null) return;

        EndTextEditing();
        EndOtherModes(except: ToolMode.Crop);
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;

        _mode = ToolMode.Crop;
        CropTool.IsChecked = true;

        var w = CanvasW;
        var h = CanvasH;
        _cropBox = new CropBox(w * 0.1, h * 0.1, w * 0.8, h * 0.8);
        _cropBox = CropMath.FitInside(_cropBox, _cropAspect, w, h);

        CropLayer.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Visible;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        ToolHint.Text = "裁剪：拖动 8 个白点调整范围，拖动框内移动位置，然后点「确认裁剪」";

        LayoutCrop();
    }

    private void ExitCropMode()
    {
        _mode = ToolMode.None;
        CropLayer.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        CropTool.IsChecked = false;
        ToolHint.Text = HintText;
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ExitCropMode();

    private void OnConfirmCrop(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 0 || h <= 0) return;

        var box = _cropBox.Normalized();
        var norm = new Rect(
            Math.Clamp(box.X / w, 0, 1),
            Math.Clamp(box.Y / h, 0, 1),
            Math.Clamp(box.Width / w, 0, 1),
            Math.Clamp(box.Height / h, 0, 1));

        if (norm.Width < 0.02 || norm.Height < 0.02)
        {
            ShowToast("裁剪区域太小，请重新框选");
            return;
        }

        ExitCropMode();
        MutateBase("裁剪", () => _doc.ApplyCropNormalized(norm));
        ShowToast("已裁剪（可撤销）");
    }

    private void LayoutCrop()
    {
        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        var r = CropMath.Clamp(_cropBox, w, h);
        _cropBox = r;

        SetRect(MaskTop, 0, 0, w, r.Top);
        SetRect(MaskBottom, 0, r.Bottom, w, h - r.Bottom);
        SetRect(MaskLeft, 0, r.Top, r.Left, r.Height);
        SetRect(MaskRight, r.Right, r.Top, w - r.Right, r.Height);

        Canvas.SetLeft(CropBorder, r.Left);
        Canvas.SetTop(CropBorder, r.Top);
        CropBorder.Width = r.Width;
        CropBorder.Height = r.Height;

        SetLine(GridLineV1, r.Left + r.Width / 3, r.Top, r.Left + r.Width / 3, r.Bottom);
        SetLine(GridLineV2, r.Left + r.Width * 2 / 3, r.Top, r.Left + r.Width * 2 / 3, r.Bottom);
        SetLine(GridLineH1, r.Left, r.Top + r.Height / 3, r.Right, r.Top + r.Height / 3);
        SetLine(GridLineH2, r.Left, r.Top + r.Height * 2 / 3, r.Right, r.Top + r.Height * 2 / 3);

        SetThumb(CropNW, r.Left, r.Top);
        SetThumb(CropN, r.Left + r.Width / 2, r.Top);
        SetThumb(CropNE, r.Right, r.Top);
        SetThumb(CropE, r.Right, r.Top + r.Height / 2);
        SetThumb(CropSE, r.Right, r.Bottom);
        SetThumb(CropS, r.Left + r.Width / 2, r.Bottom);
        SetThumb(CropSW, r.Left, r.Bottom);
        SetThumb(CropW, r.Left, r.Top + r.Height / 2);

        Canvas.SetLeft(CropMoveThumb, r.Left);
        Canvas.SetTop(CropMoveThumb, r.Top);
        CropMoveThumb.Width = r.Width;
        CropMoveThumb.Height = r.Height;

        if (_doc != null)
            CropSizeText.Text =
                $"{_doc.VisibleWidth * r.Width / w:0} × {_doc.VisibleHeight * r.Height / h:0} 像素";
    }

    private static void SetRect(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, w);
        element.Height = Math.Max(0, h);
    }

    private static void SetThumb(Thumb thumb, double x, double y)
    {
        Canvas.SetLeft(thumb, x - thumb.Width / 2);
        Canvas.SetTop(thumb, y - thumb.Height / 2);
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
    }

    private void OnCropMoveDelta(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.Move, e);

    private void OnCropNW(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.NW, e);
    private void OnCropN(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.N, e);
    private void OnCropNE(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.NE, e);
    private void OnCropE(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.E, e);
    private void OnCropSE(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.SE, e);
    private void OnCropS(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.S, e);
    private void OnCropSW(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.SW, e);
    private void OnCropW(object sender, DragDeltaEventArgs e) => DragCrop(CropHandle.W, e);

    private void DragCrop(CropHandle handle, DragDeltaEventArgs e)
    {
        var w = CanvasW;
        var h = CanvasH;
        if (w <= 0 || h <= 0) return;

        _cropBox = CropMath.Drag(_cropBox, handle, e.HorizontalChange, e.VerticalChange, _cropAspect, w, h);
        LayoutCrop();
    }

    // ================= 基础：输出尺寸 =================

    private void OnSizeOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeCombo?.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString() ?? "0";

        if (tag == "custom")
        {
            CustomSizePanel.Visibility = Visibility.Visible;
            if (_doc != null)
            {
                CustomWidthBox.Text = _doc.VisibleWidth.ToString();
                CustomHeightBox.Text = _doc.VisibleHeight.ToString();
            }
            UpdateOutputSizeText();
            return;
        }

        CustomSizePanel.Visibility = Visibility.Collapsed;
        if (_doc == null || _suppressSizeUi) return;

        int? maxEdge = int.TryParse(tag, out var v) && v > 0 ? v : null;
        if (_doc.OutputMaxEdge == maxEdge && _doc.OutputWidth == null && _doc.OutputHeight == null)
        {
            UpdateOutputSizeText();
            return;
        }

        _doc.Mutate("输出尺寸", () =>
        {
            _doc.OutputMaxEdge = maxEdge;
            _doc.OutputWidth = null;
            _doc.OutputHeight = null;
        });
    }

    private void OnApplyCustomSize(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (!int.TryParse(CustomWidthBox.Text, out var w) || w <= 0 ||
            !int.TryParse(CustomHeightBox.Text, out var h) || h <= 0)
        {
            ShowToast("请输入有效的宽度和高度（像素）");
            return;
        }

        w = Math.Clamp(w, 1, 20000);
        h = Math.Clamp(h, 1, 20000);
        CustomWidthBox.Text = w.ToString();
        CustomHeightBox.Text = h.ToString();

        _doc.Mutate("自定义尺寸", () =>
        {
            _doc.OutputWidth = w;
            _doc.OutputHeight = h;
            _doc.OutputMaxEdge = null;
        });

        var (ow, oh) = EditorRenderer.ResolveOutputSize(_doc);
        var aspectDiffers = Math.Abs((double)w / h - (double)_doc.VisibleWidth / _doc.VisibleHeight) > 0.01;
        ShowToast(ow == w && oh == h
            ? $"输出尺寸：{ow} × {oh}"
            : aspectDiffers
                ? $"实际输出 {ow} × {oh}（宽高比不同，按等比缩放，不拉伸）"
                : $"输出尺寸已限制为 {ow} × {oh}（超出上限）");
    }

    /// <summary>让下拉框跟随当前文档状态（撤销/重做后也要一致）。</summary>
    private void SyncOutputSizeUi()
    {
        if (_doc == null || SizeCombo == null) return;

        _suppressSizeUi = true;
        try
        {
            if (_doc.OutputWidth is int cw && cw > 0 && _doc.OutputHeight is int ch && ch > 0)
            {
                SelectByTag(SizeCombo, "custom");
                CustomSizePanel.Visibility = Visibility.Visible;
                CustomWidthBox.Text = cw.ToString();
                CustomHeightBox.Text = ch.ToString();
            }
            else
            {
                SelectByTag(SizeCombo, _doc.OutputMaxEdge?.ToString() ?? "0");
                CustomSizePanel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _suppressSizeUi = false;
        }

        UpdateOutputSizeText();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = ci;
                return;
            }
        }
    }

    private void UpdateOutputSizeText()
    {
        if (OutputSizeText == null) return;

        if (_doc == null)
        {
            OutputSizeText.Text = string.Empty;
            return;
        }

        var (ow, oh) = EditorRenderer.ResolveOutputSize(_doc);
        var text = $"输出：{ow} × {oh} 像素（可见区域 {_doc.VisibleWidth} × {_doc.VisibleHeight}）";

        // 自定义宽高比与原图不一致时说明是等比适配，避免用户以为没按输入尺寸输出
        if (_doc.OutputWidth is int cw and > 0 && _doc.OutputHeight is int ch and > 0 && _doc.VisibleHeight > 0)
        {
            var target = (double)cw / ch;
            var source = (double)_doc.VisibleWidth / _doc.VisibleHeight;
            if (Math.Abs(target - source) / source > 0.01)
                text += $"  ·  自定义 {cw} × {ch} 宽高比不同，已等比缩放（不拉伸）";
        }

        OutputSizeText.Text = text;
    }

    // ================= 调色 =================

    private void BuildFilterButtons()
    {
        var options = new (string Name, EditorFilter Filter)[]
        {
            ("原图", EditorFilter.None),
            ("黑白", EditorFilter.Gray),
            ("暖色", EditorFilter.Warm),
            ("复古", EditorFilter.Retro),
            ("日系", EditorFilter.Japanese),
        };

        var style = (Style)FindResource("ChipButton");

        foreach (var (name, filter) in options)
        {
            var btn = new Button
            {
                Content = name,
                Style = style,
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = filter,
            };
            btn.Click += (_, _) => ApplyFilter(filter, name);
            FilterButtons.Children.Add(btn);
        }

        RefreshFilterButtons();
    }

    private void ApplyFilter(EditorFilter filter, string name)
    {
        if (_doc == null) return;

        EndTextEditing();
        if (_doc.Filter == filter) return;

        MutateBase($"滤镜：{name}", () => _doc.Filter = filter);
        ShowToast($"滤镜：{name}");
    }

    private void RefreshFilterButtons()
    {
        if (FilterButtons == null) return;

        var active = _doc?.Filter ?? EditorFilter.None;
        foreach (var child in FilterButtons.Children)
        {
            if (child is not Button btn) continue;

            var isActive = btn.Tag is EditorFilter f && f == active;
            if (isActive)
            {
                btn.SetResourceReference(BackgroundProperty, "PrimaryBrush");
                btn.SetResourceReference(BorderBrushProperty, "PrimaryBrush");
                btn.Foreground = Brushes.White;
            }
            else
            {
                btn.ClearValue(BackgroundProperty);
                btn.ClearValue(BorderBrushProperty);
                btn.ClearValue(ForegroundProperty);
            }
        }
    }

    private void WireSliders()
    {
        foreach (var slider in new[] { BrightnessSlider, ContrastSlider, SaturationSlider })
        {
            slider.PreviewMouseLeftButtonDown += (_, _) => TouchContinuous("调色");
            slider.PreviewMouseLeftButtonUp += (_, _) => CommitContinuous();
            slider.LostMouseCapture += (_, _) => CommitContinuous();
        }

        foreach (var slider in new[] { StraightenSlider, FilterStrengthSlider })
        {
            slider.PreviewMouseLeftButtonDown += (_, _) => TouchContinuous(
                ReferenceEquals(slider, StraightenSlider) ? "拉直" : "滤镜强度");
            slider.PreviewMouseLeftButtonUp += (_, _) => CommitContinuous();
            slider.LostMouseCapture += (_, _) => CommitContinuous();
        }

        MarkOpacitySlider.PreviewMouseLeftButtonDown += (_, _) => TouchContinuous("水印不透明度");
        MarkOpacitySlider.PreviewMouseLeftButtonUp += (_, _) => CommitContinuous();
        MarkOpacitySlider.LostMouseCapture += (_, _) => CommitContinuous();
    }

    // ================= 基础：拉直 =================

    private void OnStraightenChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StraightenText != null) StraightenText.Text = $"{e.NewValue:F1}°";
        if (_suppressAdjustUi || _doc == null) return;

        TouchContinuous("拉直");
        _doc.SetStraighten(e.NewValue);
        _doc.InvalidateBase();
    }

    private void OnResetStraighten(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        EndTextEditing();
        CommitContinuous();
        if (Math.Abs(_doc.StraightenAngle) < 0.01) return;

        MutateBase("重置拉直", () => _doc.SetStraighten(0));
        ShowToast("已重置拉直");
    }

    // ================= 调色：滤镜强度 =================

    private void OnFilterStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FilterStrengthText != null)
            FilterStrengthText.Text = $"{(int)Math.Round(e.NewValue)}%";

        if (_suppressAdjustUi || _doc == null) return;

        TouchContinuous("滤镜强度");
        _doc.FilterStrength = (int)Math.Round(e.NewValue);
        SchedulePreview();
    }

    private void OnAdjustmentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BrightnessText.Text = ((int)Math.Round(BrightnessSlider.Value)).ToString();
        ContrastText.Text = ((int)Math.Round(ContrastSlider.Value)).ToString();
        SaturationText.Text = ((int)Math.Round(SaturationSlider.Value)).ToString();

        if (_suppressAdjustUi || _doc == null) return;

        TouchContinuous("调色");
        _doc.Adjustments = new ColorAdjustments(
            Math.Round(BrightnessSlider.Value),
            Math.Round(ContrastSlider.Value),
            Math.Round(SaturationSlider.Value));

        // 拖动时只重排颜色，不做几何处理
        SchedulePreview();
    }

    private void OnResetAdjustments(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        EndTextEditing();
        CommitContinuous();

        _doc.Mutate("重置调色", () => _doc.Adjustments = ColorAdjustments.None);
        ShowToast("已重置亮度 / 对比度 / 饱和度");
    }

    private void SyncAdjustmentUi()
    {
        if (_doc == null || BrightnessSlider == null) return;
        if (_doc.IsEditing) return;   // 拖动中不覆盖用户输入

        _suppressAdjustUi = true;
        try
        {
            BrightnessSlider.Value = Math.Clamp(_doc.Adjustments.Brightness, -100, 100);
            ContrastSlider.Value = Math.Clamp(_doc.Adjustments.Contrast, -100, 100);
            SaturationSlider.Value = Math.Clamp(_doc.Adjustments.Saturation, -100, 100);
            FilterStrengthSlider.Value = Math.Clamp(_doc.FilterStrength, 0, 100);
            StraightenSlider.Value = Math.Clamp(_doc.StraightenAngle, -15, 15);

            BrightnessText.Text = ((int)Math.Round(BrightnessSlider.Value)).ToString();
            ContrastText.Text = ((int)Math.Round(ContrastSlider.Value)).ToString();
            SaturationText.Text = ((int)Math.Round(SaturationSlider.Value)).ToString();
            FilterStrengthText.Text = $"{(int)Math.Round(FilterStrengthSlider.Value)}%";
            StraightenText.Text = $"{StraightenSlider.Value:F1}°";
        }
        finally
        {
            _suppressAdjustUi = false;
        }
    }

    /// <summary>开始一段连续调整（滑块拖动），整段只记一次撤销。</summary>
    private void TouchContinuous(string name)
    {
        if (_doc == null) return;

        if (!_doc.IsEditing)
        {
            _doc.BeginEdit();
            _continuousName = name;
        }

        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void CommitContinuous()
    {
        _commitTimer.Stop();

        if (_doc == null || !_doc.IsEditing)
        {
            _continuousName = null;
            return;
        }

        var name = _continuousName ?? "调整";
        _continuousName = null;
        _doc.Commit(name);   // Commit → 通知界面刷新（含调色预览）
    }

    // ================= 调色：我的预设 =================

    private void InitializePresets()
    {
        try
        {
            var config = App.Services.GetRequiredService<IAppConfigService>();
            _presetStore = new EditorPresetStore(config.DataDirectory);

            _presets.Clear();
            _presets.AddRange(_presetStore.Load().Where(p => !string.IsNullOrWhiteSpace(p.Name)));
            ReloadPresetCombo();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "初始化编辑预设失败");
        }
    }

    private void ReloadPresetCombo(string? selectName = null)
    {
        if (PresetCombo == null) return;

        PresetCombo.ItemsSource = null;
        PresetCombo.ItemsSource = _presets;

        if (!string.IsNullOrEmpty(selectName))
        {
            var target = _presets.FirstOrDefault(p => p.Name == selectName);
            if (target != null) PresetCombo.SelectedItem = target;
        }
    }

    private void PersistPresets()
    {
        if (_presetStore == null) return;

        try
        {
            _presetStore.Save(_presets);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存编辑预设失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "保存编辑预设"), "保存预设失败");
        }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        var name = CustomPrompt.Show(
            "把当前调色参数与文字水印存成预设，之后可以一键套到其它图片。",
            "保存我的预设",
            $"我的预设 {_presets.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        if (_presets.Any(p => p.Name == name) &&
            !CustomMessageBox.Confirm($"已存在名为「{name}」的预设，要覆盖它吗？", "保存预设", "覆盖"))
        {
            return;
        }

        EndTextEditing();

        // 最后一段文字视为水印样式：位置与字号都存成相对值，换尺寸的图也能复用
        var source = _doc.Texts.LastOrDefault();
        var preset = new EditorPreset
        {
            Name = name,
            Brightness = _doc.Adjustments.Brightness,
            Contrast = _doc.Adjustments.Contrast,
            Saturation = _doc.Adjustments.Saturation,
            Filter = _doc.Filter,
            FilterStrength = Math.Clamp(_doc.FilterStrength, 0, 100),
            Watermark = source == null ? null : new PresetWatermark
            {
                Text = source.Text,
                FontFamily = source.FontFamily,
                FontSizeRatio = Math.Clamp(source.FontSize / Math.Max(1, _doc.VisibleHeight), 0.005, 0.4),
                ColorHex = source.ColorHex,
                Opacity = source.Opacity,
                OutlineHex = source.OutlineHex,
                OutlineWidth = source.OutlineWidth,
                Bold = source.Bold,
                Italic = source.Italic,
                X = source.X,
                Y = source.Y,
                Angle = source.Angle,
            },
        };

        _presets.RemoveAll(p => p.Name == name);
        _presets.Add(preset);
        PersistPresets();
        ReloadPresetCombo(name);
        ShowToast($"已保存预设「{name}」");
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (PresetCombo.SelectedItem is not EditorPreset preset)
        {
            ShowToast("请先在列表中选择一个预设");
            return;
        }

        EndTextEditing();
        CommitContinuous();

        var vh = Math.Max(1, _doc.VisibleHeight);
        var wm = preset.Watermark;

        _doc.Mutate("套用预设", () =>
        {
            _doc.Adjustments = preset.Adjustments.Clamped();
            _doc.Filter = preset.Filter;
            _doc.FilterStrength = Math.Clamp(preset.FilterStrength, 0, 100);

            if (wm == null || string.IsNullOrWhiteSpace(wm.Text)) return;

            // 同内容的水印只更新样式，避免一键套到多张时重复叠加
            var existing = _doc.Texts.FirstOrDefault(t => t.Text.Trim() == wm.Text.Trim());
            if (existing != null)
            {
                ApplyPresetWatermark(existing, wm, vh);
                return;
            }

            var item = new TextOverlayItem();
            ApplyPresetWatermark(item, wm, vh);
            _doc.Texts.Add(item);
        });

        ShowToast($"已套用预设「{preset.Name}」");
    }

    private static void ApplyPresetWatermark(TextOverlayItem item, PresetWatermark wm, double visibleHeight)
    {
        item.Text = wm.Text;
        item.FontFamily = string.IsNullOrWhiteSpace(wm.FontFamily) ? "Microsoft YaHei" : wm.FontFamily;
        item.FontSize = Math.Max(12, visibleHeight * Math.Clamp(wm.FontSizeRatio, 0.005, 0.4));
        item.ColorHex = wm.ColorHex;
        item.Opacity = Math.Clamp(wm.Opacity, 0, 1);
        item.OutlineHex = wm.OutlineHex;
        item.OutlineWidth = Math.Clamp(wm.OutlineWidth, 0, 0.5);
        item.Bold = wm.Bold;
        item.Italic = wm.Italic;
        item.X = Math.Clamp(wm.X, 0, 1);
        item.Y = Math.Clamp(wm.Y, 0, 1);
        item.Angle = Math.Clamp(wm.Angle, -180, 180);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not EditorPreset preset)
        {
            ShowToast("请先在列表中选择一个预设");
            return;
        }

        if (!CustomMessageBox.ConfirmDanger($"删除预设「{preset.Name}」？", "删除预设", "删除")) return;

        _presets.Remove(preset);
        PersistPresets();
        ReloadPresetCombo();
        ShowToast($"已删除预设「{preset.Name}」");
    }

    // ================= 局部：文字 =================

    private void OnTextToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (IsTextEditing)
        {
            TextTool.IsChecked = true;
            FocusActiveTextEditor();
            ShowToast("已有正在编辑的文字，直接输入即可；点「✓ 完成」后可以再加一段");
            return;
        }

        if (TextTool.IsChecked == true) AddTextItem(isWatermark: false);
        else EndTextEditing();
    }

    private void OnFinishTextClick(object sender, RoutedEventArgs e)
    {
        EndTextEditing();
        ShowToast("已完成这段文字，可以再点「文字」添加新的一段");
    }

    private void AddTextItem(bool isWatermark)
    {
        if (_doc == null) return;

        if (IsTextEditing)
        {
            FocusActiveTextEditor();
            return;
        }

        EndTextEditing(rebuild: false);
        EndOtherModes(except: ToolMode.None);

        var fontPx = Math.Max(16, _doc.VisibleHeight * 0.055);
        var (x, y) = TextOverlayPlacement.Next(_doc.Texts.Count, isWatermark);

        var item = new TextOverlayItem
        {
            Text = isWatermark ? "水印文字" : "输入文字",
            FontSize = fontPx,
            ColorHex = "#FFFFFF",
            Opacity = isWatermark ? 0.55 : 1.0,
            X = x,
            Y = y,
            Bold = true,
            OutlineHex = isWatermark ? null : "#FF000000",
        };

        _doc.Mutate(isWatermark ? "添加水印" : "添加文字", () => _doc.Texts.Add(item));
        SelectText(item, selectAll: true);
        ShowToast(isWatermark ? "输入水印文字，拖「✥ 拖动」调整位置" : "直接输入文字，拖「✥ 拖动」调整位置");
    }

    private void SelectText(TextOverlayItem item, bool selectAll = false)
    {
        if (_doc == null) return;

        if (IsTextEditing && ReferenceEquals(_activeText, item))
        {
            FocusActiveTextEditor();
            return;
        }

        EndTextEditing(rebuild: false);

        _activeText = item;
        _editingText = true;
        _textDirty = false;
        _textEditSnapshot = EditorSnapshot.Capture(_doc);

        TextTool.IsChecked = true;
        if (CatText.IsChecked != true) CatText.IsChecked = true;
        TextProps.Visibility = Visibility.Visible;
        TextAngleHost.Visibility = Visibility.Visible;

        UpdateTextPanelFromItem(item);
        RebuildOverlays(forceText: true);
        FocusActiveTextEditor(selectAll);
    }

    private void FocusActiveTextEditor(bool selectAll = false)
    {
        if (_doc == null || _activeText == null) return;

        if (_activeTextBox == null) RebuildOverlays(forceText: true);
        if (_activeTextBox == null) return;

        _activeTextBox.Focus();
        Keyboard.Focus(_activeTextBox);

        if (selectAll) _activeTextBox.SelectAll();
        else _activeTextBox.CaretIndex = _activeTextBox.Text.Length;
    }

    /// <summary>
    /// 结束文字编辑并写入撤销栈（一次编辑会话 = 一条可撤销记录）。
    /// 内容被清空的文字项直接丢弃，避免留下看不见却会被导出的文本框。
    /// </summary>
    private void EndTextEditing(bool rebuild = true)
    {
        if (!_editingText && _activeText == null) return;

        var item = _activeText;
        _editingText = false;

        if (_doc != null && item != null && !OverlayTextRules.HasContent(item.Text))
        {
            if (_doc.Texts.Remove(item)) _textDirty = true;
        }

        if (_doc != null && _textEditSnapshot != null && _textDirty)
        {
            var after = EditorSnapshot.Capture(_doc);
            _doc.History.Push(new SnapshotCommand(_doc, _textEditSnapshot, after, "编辑文字"));
        }

        _textEditSnapshot = null;
        _textDirty = false;

        _activeText = null;
        _activeTextBox = null;
        _activeTextHost = null;
        TextTool.IsChecked = false;
        TextProps.Visibility = Visibility.Collapsed;
        SyncTextAngleFromDocument();

        if (rebuild) RebuildOverlays();
        else UpdateHistoryButtons();
    }

    private void UpdateTextPanelFromItem(TextOverlayItem item)
    {
        _suppressTextPanel = true;
        try
        {
            foreach (ComboBoxItem ci in FontCombo.Items)
            {
                if (string.Equals(ci.Tag?.ToString(), item.FontFamily, StringComparison.OrdinalIgnoreCase))
                {
                    FontCombo.SelectedItem = ci;
                    break;
                }
            }

            var scale = DisplayScale <= 0 ? 1 : DisplayScale;
            FontSizeSlider.Value = Math.Clamp(item.FontSize * scale, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
            FontSizeText.Text = ((int)Math.Round(FontSizeSlider.Value)).ToString();
            TextAngleSlider.Value = Math.Clamp(item.Angle, TextAngleSlider.Minimum, TextAngleSlider.Maximum);
            TextAngleText.Text = $"{(int)Math.Round(TextAngleSlider.Value)}°";
            SyncTextAngleInput();
            SyncTextScaleInput();
            BoldToggle.IsChecked = item.Bold;
            ItalicToggle.IsChecked = item.Italic;

            TextBgToggle.IsChecked = !string.IsNullOrWhiteSpace(item.BackgroundHex);
            if (TextBgToggle.IsChecked == true)
                SelectByTag(TextBgColorCombo, item.BackgroundHex ?? "#CC000000");

            TextOutlineToggle.IsChecked = !string.IsNullOrWhiteSpace(item.OutlineHex);
            if (TextOutlineToggle.IsChecked == true)
                SelectByTag(TextOutlineColorCombo, item.OutlineHex ?? "#FF000000");

            OutlineWidthSlider.Value = Math.Clamp(item.OutlineWidth, 0.03, 0.2);
        }
        finally
        {
            _suppressTextPanel = false;
        }
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTextPanel || _activeTextBox == null || _activeText == null) return;
        if (FontCombo.SelectedItem is not ComboBoxItem item) return;

        _activeText.FontFamily = item.Tag?.ToString() ?? "Microsoft YaHei";
        _activeTextBox.FontFamily = new FontFamily(_activeText.FontFamily);
        _textDirty = true;
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTextPanel || _activeTextBox == null || _activeText == null) return;

        var scale = DisplayScale <= 0 ? 1 : DisplayScale;
        _activeText.FontSize = FontSizeSlider.Value / scale;
        _activeTextBox.FontSize = FontSizeSlider.Value;
        FontSizeText.Text = ((int)Math.Round(FontSizeSlider.Value)).ToString();
        _textDirty = true;
        MeasureText(_activeText, _activeTextBox);
    }

    private void OnTextAngleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextAngleText != null) TextAngleText.Text = $"{(int)Math.Round(e.NewValue)}°";
        if (_suppressTextPanel || _doc == null) return;

        // 没有正在编辑的文字时，角度作用于最后一段（可以先把文字摆好再统一斜放）
        var item = _activeText ?? _doc.Texts.LastOrDefault();
        if (item == null) return;

        if (_activeText == null) TouchContinuous("文字角度");

        item.Angle = e.NewValue;
        if (ReferenceEquals(item, _activeText)) _textDirty = true;

        // 编辑中的文本框也跟着转，否则拖滑块时看不到效果
        if (_activeTextHost != null)
        {
            _activeTextHost.RenderTransformOrigin = new Point(0.5, 0.5);
            _activeTextHost.RenderTransform = new RotateTransform(e.NewValue);
        }

        if (_activeText == null) RebuildOverlays();
    }

    /// <summary>缩放比例 100% 对应的字号基准。</summary>
    private const double BaseTextFontSize = 48;

    private void OnTextAngleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTextAngleInput();
        e.Handled = true;
    }

    private void OnTextAngleInputCommitted(object sender, RoutedEventArgs e) => CommitTextAngleInput();

    /// <summary>把数字框里的 0-360 角度转成内部使用的 -180~180，并驱动滑块。</summary>
    private void CommitTextAngleInput()
    {
        if (TextAngleInput == null || TextAngleSlider == null) return;

        if (!double.TryParse(TextAngleInput.Text.Trim().TrimEnd('°', ' '), out var value))
        {
            SyncTextAngleInput();
            return;
        }

        value = ((value % 360) + 360) % 360;
        var angle = value > 180 ? value - 360 : value;
        TextAngleSlider.Value = angle;      // 触发 OnTextAngleChanged，应用到当前文字
        TextAngleInput.Text = ((int)Math.Round(value)).ToString();
    }

    private void SyncTextAngleInput()
    {
        if (TextAngleInput == null || TextAngleSlider == null) return;
        var angle = TextAngleSlider.Value;
        TextAngleInput.Text = ((int)Math.Round(((angle % 360) + 360) % 360)).ToString();
    }

    private void OnTextScaleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTextScaleInput();
        e.Handled = true;
    }

    private void OnTextScaleInputCommitted(object sender, RoutedEventArgs e) => CommitTextScaleInput();

    /// <summary>缩放比例：相对默认字号 48px 的百分比，换算后驱动字号滑块。</summary>
    private void CommitTextScaleInput()
    {
        if (TextScaleInput == null) return;

        if (!double.TryParse(TextScaleInput.Text.Trim().TrimEnd('%', ' '), out var percent))
        {
            SyncTextScaleInput();
            return;
        }

        percent = Math.Clamp(percent, 20, 300);
        TextScaleInput.Text = ((int)Math.Round(percent)).ToString();

        var item = _activeText ?? _doc?.Texts.LastOrDefault();
        if (item == null) return;

        var targetPx = BaseTextFontSize * percent / 100.0;
        if (_activeTextBox != null)
        {
            var scale = DisplayScale <= 0 ? 1 : DisplayScale;
            FontSizeSlider.Value = Math.Clamp(targetPx * scale, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        }
        else
        {
            item.FontSize = targetPx;
            RebuildOverlays();
        }
    }

    private void SyncTextScaleInput()
    {
        if (TextScaleInput == null) return;
        var item = _activeText ?? _doc?.Texts.LastOrDefault();
        var percent = item == null ? 100 : item.FontSize / BaseTextFontSize * 100;
        TextScaleInput.Text = ((int)Math.Round(percent)).ToString();
    }

    private void OnTextStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressTextPanel || _activeTextBox == null || _activeText == null) return;

        _activeText.Bold = BoldToggle.IsChecked == true;
        _activeText.Italic = ItalicToggle.IsChecked == true;
        _activeTextBox.FontWeight = _activeText.Bold ? FontWeights.Bold : FontWeights.Normal;
        _activeTextBox.FontStyle = _activeText.Italic ? FontStyles.Italic : FontStyles.Normal;
        _textDirty = true;
        MeasureText(_activeText, _activeTextBox);
    }

    private void OnTextBgToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressTextPanel || _activeText == null) return;

        if (TextBgToggle.IsChecked == true)
        {
            var hex = SelectedTag(TextBgColorCombo) ?? "#CC000000";
            _activeText.BackgroundHex = hex;
            if (_activeTextBox != null) _activeTextBox.Background = ParseBrush(hex);
        }
        else
        {
            _activeText.BackgroundHex = null;
            if (_activeTextBox != null) _activeTextBox.Background = Brushes.Transparent;
        }

        _textDirty = true;
    }

    private void OnTextBgColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTextPanel || _activeText == null) return;
        if (TextBgToggle.IsChecked != true) return;

        var hex = SelectedTag(TextBgColorCombo);
        if (string.IsNullOrWhiteSpace(hex)) return;

        _activeText.BackgroundHex = hex;
        if (_activeTextBox != null) _activeTextBox.Background = ParseBrush(hex);
        _textDirty = true;
    }

    private void OnTextOutlineToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressTextPanel || _activeText == null) return;

        _activeText.OutlineHex = TextOutlineToggle.IsChecked == true
            ? SelectedTag(TextOutlineColorCombo) ?? "#FF000000"
            : null;
        _textDirty = true;
    }

    private void OnTextOutlineColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTextPanel || _activeText == null) return;
        if (TextOutlineToggle.IsChecked != true) return;

        var hex = SelectedTag(TextOutlineColorCombo);
        if (string.IsNullOrWhiteSpace(hex)) return;

        _activeText.OutlineHex = hex;
        _textDirty = true;
    }

    private void OnOutlineWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTextPanel || _activeText == null) return;

        _activeText.OutlineWidth = Math.Clamp(e.NewValue, 0.03, 0.2);
        _textDirty = true;
    }

    private void OnDeleteTextClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _activeText == null) return;

        var target = _activeText;
        EndTextEditing(rebuild: false);

        if (_doc.Texts.Contains(target))
            _doc.Mutate("删除文字", () => _doc.Texts.Remove(target));

        RebuildOverlays();
    }

    private void OnTextColorClick(object sender, RoutedEventArgs e)
    {
        if (_activeText == null || _activeTextBox == null) return;
        if (sender is not FrameworkElement { Tag: string hex }) return;

        _activeText.ColorHex = hex;
        _activeTextBox.Foreground = ParseBrush(hex);
        _textDirty = true;
    }

    // ================= 局部：涂鸦 =================

    private void OnInkToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (InkTool.IsChecked == true) EnterInkMode();
        else ExitInkMode();
    }

    private void EnterInkMode()
    {
        EndTextEditing();
        EndOtherModes(except: ToolMode.Ink);
        // 刻意不复位缩放/平移：放大到 300% 精修时点一下工具就被缩回去，是最容易被当成「bug」的操作
        _mode = ToolMode.Ink;
        InkTool.IsChecked = true;
        InkLayer.IsHitTestVisible = true;
        InkLayer.EditingMode = InkCanvasEditingMode.Ink;
        ToolHint.Text = "涂鸦：按住鼠标画线；可换颜色/粗细，Ctrl+Z 撤销";
        RebuildInk(CanvasW, CanvasH);
    }

    private void ExitInkMode()
    {
        _mode = ToolMode.None;
        InkTool.IsChecked = false;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        ToolHint.Text = HintText;
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_doc == null || _mode != ToolMode.Ink) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        var points = e.Stroke.StylusPoints
            .Select(p => new Point(
                Math.Clamp(p.X / w, 0, 1),
                Math.Clamp(p.Y / h, 0, 1)))
            .ToList();
        if (points.Count < 2) return;

        var model = new StrokeOverlayItem
        {
            Points = points,
            ColorHex = _inkColor,
            Thickness = Math.Max(0.0005, _inkWidthDisplay / w),
        };

        _suppressOverlayRebuild = true;
        // 画笔是最近一次操作：涂鸦回到打码之上
        _doc.Mutate("涂鸦", () =>
        {
            _doc.Strokes.Add(model);
            _doc.PrivacyAboveInk = false;
        });
        _suppressOverlayRebuild = false;
        RebuildInk(w, h);
        ApplyOverlayZOrder();   // 画笔是最近一次操作 → 涂鸦层回到打码之上
        UpdateHistoryButtons();
    }

    private void OnInkWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _inkWidthDisplay = e.NewValue;
        if (InkWidthText != null) InkWidthText.Text = ((int)Math.Round(e.NewValue)).ToString();

        if (InkLayer != null)
        {
            InkLayer.DefaultDrawingAttributes.Width = e.NewValue;
            InkLayer.DefaultDrawingAttributes.Height = e.NewValue;
        }
    }

    private void OnInkColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;

        _inkColor = hex;
        if (InkLayer != null) InkLayer.DefaultDrawingAttributes.Color = ParseColor(hex);
    }

    // ================= 局部：打码（马赛克 / 模糊） =================

    private void OnMosaicToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (MosaicTool.IsChecked == true) EnterPrivacyMode();
        else ExitPrivacyMode();
    }

    private void OnPrivacyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrivacyModeCombo?.SelectedItem is not ComboBoxItem item) return;
        _privacyMode = string.Equals(item.Tag?.ToString(), "blur", StringComparison.Ordinal)
            ? PrivacyMode.Blur
            : PrivacyMode.Mosaic;
    }

    private void OnPrivacyToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrivacyToolCombo?.SelectedItem is not ComboBoxItem item) return;
        _privacyRectTool = string.Equals(item.Tag?.ToString(), "rect", StringComparison.Ordinal);
    }

    private void OnPrivacyWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _privacyWidthDisplay = e.NewValue;
        if (PrivacyWidthText != null) PrivacyWidthText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void OnPrivacyBlockChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _privacyBlockPermille = e.NewValue;
        if (PrivacyBlockText != null) PrivacyBlockText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void EnterPrivacyMode()
    {
        if (_doc == null) return;

        EndTextEditing();
        EndOtherModes(except: ToolMode.Privacy);
        // 不复位缩放/平移：切工具不该改变用户当前的观察位置

        _mode = ToolMode.Privacy;
        MosaicTool.IsChecked = true;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        ToolHint.Text = _privacyRectTool
            ? "打码：在画面上拖出一个矩形，框内区域会被打码（可撤销）"
            : "打码：在画面上按住拖动涂抹，涂抹区域会被打码（可撤销）";
    }

    private void ExitPrivacyMode()
    {
        _mode = ToolMode.None;
        MosaicTool.IsChecked = false;
        CancelPrivacyDraft();
        ToolHint.Text = HintText;
    }

    private void CancelPrivacyDraft()
    {
        if (_doc != null && _doc.IsEditing && _continuousName == "打码") _doc.CancelEdit();
        HideDraft();
        _privacyDraft = null;
    }

    private void HideDraft()
    {
        foreach (var element in _draftElements)
            MosaicLayer.Children.Remove(element);
        _draftElements.Clear();
    }

    /// <summary>涂抹过程中实时显示 draft 的马赛克/模糊效果。</summary>
    private void RefreshPrivacyDraft()
    {
        if (_doc == null || _privacyDraft == null) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        HideDraft();
        var sample = PrivacySampleSource;
        if (sample == null) return;

        var layer = EditorOverlayFactory.BuildPrivacyLayer(
            new[] { _privacyDraft }, _privacyDraft.Mode, sample, w, h);
        if (layer == null) return;

        MosaicLayer.Children.Add(layer);
        _draftElements.Add(layer);
    }

    private Point? _lastPrivacyPoint;

    /// <summary>自上次重建 draft 几何以来新增的点数（用于节流）。</summary>
    private int _draftSinceRefresh;

    private void BeginPrivacyStroke(Point p)
    {
        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        _privacyDraft = new PrivacyOverlayItem
        {
            Mode = _privacyMode,
            Thickness = Math.Max(0.002, _privacyWidthDisplay / w),
            BlockRatio = _privacyBlockPermille / 1000.0,
        };

        var norm = new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
        _privacyDraft.Points.Add(norm);
        _lastPrivacyPoint = p;

        _continuousName = "打码";
        if (_doc != null && !_doc.IsEditing) _doc.BeginEdit();

        _draftSinceRefresh = 0;
        RefreshPrivacyDraft();
    }

    private void ExtendPrivacyStroke(Point p)
    {
        if (_privacyDraft == null) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        if (_lastPrivacyPoint is Point last)
        {
            var dx = p.X - last.X;
            var dy = p.Y - last.Y;
            if (dx * dx + dy * dy < 4) return;   // 至少 2px 才采点
        }

        _privacyDraft.Points.Add(new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1)));
        _lastPrivacyPoint = p;

        // 每 3 个点才重建一次几何：逐点重建是 O(n²)，拖快了会明显掉帧
        if (++_draftSinceRefresh >= 3)
        {
            _draftSinceRefresh = 0;
            RefreshPrivacyDraft();
        }
    }

    private void FinishPrivacyStroke()
    {
        if (_doc == null || _privacyDraft == null) return;

        var draft = _privacyDraft;
        _privacyDraft = null;
        _lastPrivacyPoint = null;
        HideDraft();

        if (draft.Points.Count == 0)
        {
            if (_continuousName == "打码") _doc.CancelEdit();
            _continuousName = null;
            return;
        }

        _doc.Privacies.Add(draft);
        // 打码是最近一次操作：盖到已有涂鸦之上
        _doc.PrivacyAboveInk = true;
        _continuousName = null;
        _doc.Commit("打码");
        ShowToast(_privacyMode == PrivacyMode.Mosaic ? "已添加马赛克（可撤销）" : "已添加模糊（可撤销）");
    }

    /// <summary>把矩形拖拽转换成一条来回覆盖的轨迹，复用同一套打码渲染。</summary>
    private PrivacyOverlayItem? BuildRectPrivacy(Rect rect, double w, double h)
    {
        if (rect.Width < 4 || rect.Height < 4) return null;

        var pen = Math.Max(2, _privacyWidthDisplay);
        var step = Math.Max(2, pen * 0.45);
        var points = new List<Point>();
        var leftToRight = true;

        var yStart = rect.Top + pen / 2;
        var yEnd = rect.Bottom - pen / 2;
        if (yEnd < yStart)
        {
            yStart = yEnd = rect.Top + rect.Height / 2;
        }

        for (var y = yStart; y <= yEnd + 0.01; y += step)
        {
            var x1 = rect.Left + pen / 2;
            var x2 = rect.Right - pen / 2;
            if (x2 < x1) x1 = x2 = rect.Left + rect.Width / 2;

            if (leftToRight)
            {
                points.Add(new Point(Math.Clamp(x1 / w, 0, 1), Math.Clamp(y / h, 0, 1)));
                points.Add(new Point(Math.Clamp(x2 / w, 0, 1), Math.Clamp(y / h, 0, 1)));
            }
            else
            {
                points.Add(new Point(Math.Clamp(x2 / w, 0, 1), Math.Clamp(y / h, 0, 1)));
                points.Add(new Point(Math.Clamp(x1 / w, 0, 1), Math.Clamp(y / h, 0, 1)));
            }
            leftToRight = !leftToRight;
        }

        if (points.Count == 0) return null;

        return new PrivacyOverlayItem
        {
            Mode = _privacyMode,
            Thickness = Math.Max(0.002, pen / w),
            BlockRatio = _privacyBlockPermille / 1000.0,
            Points = points,
        };
    }

    // ================= 局部：形状标注 =================

    private void OnShapeToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            ResetShapeToggles();
            return;
        }

        if (sender is ToggleButton { IsChecked: true } button)
        {
            _shapeKind = ReferenceEquals(button, ShapeArrowTool) ? ShapeKind.Arrow
                : ReferenceEquals(button, ShapeEllipseTool) ? ShapeKind.Ellipse
                : ShapeKind.Rect;

            if (_mode == ToolMode.Shape)
            {
                SyncShapeToggles();
                return;
            }

            EnterShapeMode();
        }
        else
        {
            ExitShapeMode();
        }
    }

    private void EnterShapeMode()
    {
        EndTextEditing();
        EndOtherModes(except: ToolMode.Shape);
        // 不复位缩放/平移：切工具不该改变用户当前的观察位置

        _mode = ToolMode.Shape;
        SyncShapeToggles();
        ToolHint.Text = "形状标注：在画面上按住拖动绘制（矩形 / 箭头 / 椭圆），可撤销";
    }

    private void ExitShapeMode()
    {
        if (_mode == ToolMode.Shape) _mode = ToolMode.None;
        ResetShapeToggles();
        CancelShapeDraft();
        ToolHint.Text = HintText;
    }

    private void SyncShapeToggles()
    {
        ShapeRectTool.IsChecked = _shapeKind == ShapeKind.Rect;
        ShapeArrowTool.IsChecked = _shapeKind == ShapeKind.Arrow;
        ShapeEllipseTool.IsChecked = _shapeKind == ShapeKind.Ellipse;
    }

    private void ResetShapeToggles()
    {
        ShapeRectTool.IsChecked = false;
        ShapeArrowTool.IsChecked = false;
        ShapeEllipseTool.IsChecked = false;
    }

    private void CancelShapeDraft()
    {
        _shapeDraft = null;
        if (_shapeDraftElement != null)
        {
            ShapeLayer.Children.Remove(_shapeDraftElement);
            _shapeDraftElement = null;
        }
    }

    /// <summary>拖动过程中只替换草稿元素，不整体重建叠加层（否则元素会被换掉、鼠标捕获也受影响）。</summary>
    private void RefreshShapeDraft()
    {
        if (_shapeDraftElement != null)
        {
            ShapeLayer.Children.Remove(_shapeDraftElement);
            _shapeDraftElement = null;
        }

        if (_shapeDraft == null) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        var element = EditorOverlayFactory.BuildShape(_shapeDraft, w, h);
        if (element == null) return;

        ShapeLayer.Children.Add(element);
        _shapeDraftElement = element;
    }

    private void CommitShapeDraft()
    {
        var draft = _shapeDraft;
        _shapeDraft = null;
        if (_shapeDraftElement != null)
        {
            ShapeLayer.Children.Remove(_shapeDraftElement);
            _shapeDraftElement = null;
        }

        if (_doc == null || draft == null) return;

        var w = Math.Max(1, CanvasW);
        var h = Math.Max(1, CanvasH);
        var pixelW = Math.Abs(draft.X2 - draft.X1) * w;
        var pixelH = Math.Abs(draft.Y2 - draft.Y1) * h;
        if (Math.Max(pixelW, pixelH) < 6)
        {
            ShowToast("形状太小，请拖得长一点");
            return;
        }

        _doc.Mutate("形状标注", () => _doc.Shapes.Add(draft));
        ShowToast("已添加形状标注（可撤销）");
    }

    // ================= 水印：文字水印 / 图片水印 =================

    private void OnWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (IsTextEditing)
        {
            if (CatText.IsChecked != true) CatText.IsChecked = true;
            TextTool.IsChecked = true;
            FocusActiveTextEditor();
            ShowToast("请先点「✓ 完成」结束当前文字编辑，再添加水印");
            return;
        }

        AddTextItem(isWatermark: true);
    }

    private void OnPickImageWatermark(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择水印图片（推荐透明 PNG）",
            Filter = "PNG 图片|*.png|图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        BitmapImage bitmap;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(dialog.FileName);
            using var ms = new System.IO.MemoryStream(bytes, writable: false);
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取水印图片失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "读取水印图片"), "添加图片水印失败");
            return;
        }

        var vw = Math.Max(1, _doc.VisibleWidth);
        var vh = Math.Max(1, _doc.VisibleHeight);
        var aspect = bitmap.PixelHeight > 0 ? (double)bitmap.PixelWidth / bitmap.PixelHeight : 1;

        var wm = new ImageWatermarkItem
        {
            Image = bitmap,
            Name = System.IO.Path.GetFileName(dialog.FileName),
            X = 0.55,
            Y = 0.75,
            W = 0.28,
            Opacity = 0.8,
        };
        wm.H = Math.Clamp(wm.W * vw / (aspect * vh), 0.02, 0.9);

        EndTextEditing();
        _doc.Mutate("添加图片水印", () => _doc.Watermarks.Add(wm));
        SelectWatermark(wm);
        ShowToast("拖动水印调整位置，拖右下角改大小");
    }

    private void SelectWatermark(ImageWatermarkItem item)
    {
        if (_doc == null) return;

        EndTextEditing();
        _activeWatermark = item;

        MarkProps.Visibility = Visibility.Visible;
        MarkName.Text = item.Name;

        _suppressMarkOpacity = true;
        MarkOpacitySlider.Value = Math.Round(item.Opacity * 100);
        _suppressMarkOpacity = false;
        MarkOpacityText.Text = $"{(int)Math.Round(item.Opacity * 100)}%";

        RebuildOverlays(forceText: true);
    }

    private void BeginWatermarkDrag(
        ImageWatermarkItem item, bool resize, MouseButtonEventArgs e, FrameworkElement source)
    {
        if (_doc == null) return;

        if (!resize)
        {
            _wmDragItem = item;
            _wmDragOrigin = e.GetPosition(WatermarkLayer);
            _wmStartX = item.X;
            _wmStartY = item.Y;
            _wmDragSource = source;
            source.CaptureMouse();

            if (!_doc.IsEditing) _doc.BeginEdit();
            e.Handled = true;
        }
    }

    private void ResizeWatermark(double horizontalChange)
    {
        if (_doc == null || _activeWatermark == null) return;

        var w = CanvasW;
        var h = CanvasH;
        if (w <= 1 || h <= 1) return;

        if (!_doc.IsEditing) _doc.BeginEdit();

        var item = _activeWatermark;
        var aspect = item.Aspect <= 0 ? 1 : item.Aspect;
        var newWidthPx = Math.Max(16, item.W * w + horizontalChange);
        var newW = Math.Clamp(newWidthPx / w, 0.03, 1.0);
        var newH = Math.Clamp(newW * w / (aspect * h), 0.02, 1.0);

        // 右下角缩放：左上角固定，不允许越出画布
        if (item.X + newW > 1) newW = Math.Max(0.03, 1 - item.X);
        if (item.Y + newH > 1) newH = Math.Max(0.02, 1 - item.Y);

        item.W = newW;
        item.H = newH;
        LayoutWatermarkElement(item);
    }

    private void OnWatermarkOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarkOpacityText != null) MarkOpacityText.Text = $"{(int)Math.Round(e.NewValue)}%";

        if (_suppressMarkOpacity || _doc == null || _activeWatermark == null) return;

        TouchContinuous("水印不透明度");
        _activeWatermark.Opacity = Math.Clamp(e.NewValue / 100.0, 0.05, 1);
        if (_wmImageElement != null) _wmImageElement.Opacity = _activeWatermark.Opacity;
    }

    private void OnDeleteWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _activeWatermark == null) return;

        var target = _activeWatermark;
        CommitContinuous();
        _activeWatermark = null;
        MarkProps.Visibility = Visibility.Collapsed;

        if (_doc.Watermarks.Contains(target))
            _doc.Mutate("删除图片水印", () => _doc.Watermarks.Remove(target));

        RebuildOverlays(forceText: true);
    }

    // ================= 水印：平铺文字 =================

    private void OnAddTileWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (_doc.TileWatermark == null)
            _doc.Mutate("添加平铺水印", () => _doc.TileWatermark = new TileWatermarkItem());

        UpdateTilePanelFromItem();
        RefreshTileWatermark();
        ShowToast("已铺满整张图；可调文字、字号、角度、疏密与不透明度");
    }

    private void UpdateTilePanelFromItem()
    {
        if (_doc?.TileWatermark is not { } item) return;

        TileProps.Visibility = Visibility.Visible;

        _suppressTilePanel = true;
        TileTextBox.Text = item.Text;
        TileSizeSlider.Value = Math.Clamp(item.FontSizeRatio * 100, TileSizeSlider.Minimum, TileSizeSlider.Maximum);
        TileSizeText.Text = $"{TileSizeSlider.Value:0.#}%";
        TileAngleSlider.Value = Math.Clamp(item.Angle, TileAngleSlider.Minimum, TileAngleSlider.Maximum);
        TileAngleText.Text = $"{(int)Math.Round(TileAngleSlider.Value)}°";
        TileGapSlider.Value = Math.Clamp(item.GapRatio, TileGapSlider.Minimum, TileGapSlider.Maximum);
        TileGapText.Text = $"{TileGapSlider.Value:0.0}×";
        TileOpacitySlider.Value = Math.Clamp(item.Opacity * 100, TileOpacitySlider.Minimum, TileOpacitySlider.Maximum);
        TileOpacityText.Text = $"{(int)Math.Round(TileOpacitySlider.Value)}%";

        foreach (ComboBoxItem ci in TileColorCombo.Items)
        {
            if (string.Equals(ci.Tag?.ToString(), item.ColorHex, StringComparison.OrdinalIgnoreCase))
            {
                TileColorCombo.SelectedItem = ci;
                break;
            }
        }
        _suppressTilePanel = false;
    }

    private void RefreshTileWatermark()
    {
        RebuildTileWatermark(CanvasW, CanvasH);
    }

    private void OnTileTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;

        TouchContinuous("平铺水印文字");
        item.Text = TileTextBox.Text;
        RefreshTileWatermark();
    }

    private void OnTileSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TileSizeText != null) TileSizeText.Text = $"{e.NewValue:0.#}%";
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;

        TouchContinuous("平铺水印字号");
        item.FontSizeRatio = e.NewValue / 100.0;
        RefreshTileWatermark();
    }

    private void OnTileAngleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TileAngleText != null) TileAngleText.Text = $"{(int)Math.Round(e.NewValue)}°";
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;

        TouchContinuous("平铺水印角度");
        item.Angle = e.NewValue;
        RefreshTileWatermark();
    }

    private void OnTileGapChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TileGapText != null) TileGapText.Text = $"{e.NewValue:0.0}×";
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;

        TouchContinuous("平铺水印间距");
        item.GapRatio = e.NewValue;
        RefreshTileWatermark();
    }

    private void OnTileOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TileOpacityText != null) TileOpacityText.Text = $"{(int)Math.Round(e.NewValue)}%";
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;

        TouchContinuous("平铺水印不透明度");
        item.Opacity = Math.Clamp(e.NewValue / 100.0, 0.04, 0.6);
        RefreshTileWatermark();
    }

    private void OnTileColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTilePanel || _doc?.TileWatermark is not { } item) return;
        if (TileColorCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string hex) return;

        _doc.Mutate("平铺水印颜色", () => item.ColorHex = hex);
        RefreshTileWatermark();
    }

    private void OnDeleteTileWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_doc?.TileWatermark == null) return;

        _doc.Mutate("删除平铺水印", () => _doc.TileWatermark = null);
        TileProps.Visibility = Visibility.Collapsed;
        RefreshTileWatermark();
    }

    // ================= 分类切换 =================

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        ShowCategory(tag);
    }

    /// <summary>切换分类面板：同一时刻只显示一个，淡入过渡避免「啪」地跳出来。</summary>
    private void ShowCategory(string tag)
    {
        if (BasicPanel == null || CropPanel == null || ColorPanel == null || TextPanel == null
            || DrawPanel == null || PrivacyPanel == null || MarkPanel == null) return;

        var panels = new (string Key, StackPanel Panel)[]
        {
            ("basic", BasicPanel),
            ("crop", CropPanel),
            ("color", ColorPanel),
            ("text", TextPanel),
            ("draw", DrawPanel),
            ("privacy", PrivacyPanel),
            ("mark", MarkPanel),
        };

        foreach (var (key, panel) in panels)
        {
            var visible = string.Equals(key, tag, StringComparison.Ordinal);
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            if (visible)
            {
                panel.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(130)));
            }
            else
            {
                panel.BeginAnimation(OpacityProperty, null);
            }
        }

        if (tag != "text") EndTextEditing();
        if (tag == "text" && !IsTextEditing) SyncTextAngleFromDocument();
    }

    /// <summary>没有选中文字时，把角度滑块同步到「最后一段文字」，避免滑块显示 0° 与实际不符。</summary>
    private void SyncTextAngleFromDocument()
    {
        if (_doc == null) return;

        var item = _activeText ?? _doc.Texts.LastOrDefault();

        // 角度滑块常驻在「文字」面板顶部：没有文字时也看得见，只是不可用
        _suppressTextPanel = true;
        TextAngleSlider.Value = item == null ? 0 : Math.Clamp(item.Angle, -180, 180);
        TextAngleSlider.IsEnabled = item != null;
        if (TextAngleInput != null) TextAngleInput.IsEnabled = item != null;
        _suppressTextPanel = false;
        TextAngleText.Text = $"{(int)Math.Round(TextAngleSlider.Value)}°";
        SyncTextAngleInput();
        SyncTextScaleInput();
    }

    /// <summary>整栏收起/展开：保留分类图标轨，只把右侧属性面板藏起来。</summary>
    private void OnPanelCollapseToggle(object sender, RoutedEventArgs e)
    {
        var collapsed = PanelCollapseToggle.IsChecked == true;
        PanelHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        PanelColumn.Width = collapsed ? new GridLength(0) : new GridLength(300);
        PanelCollapseToggle.Content = collapsed ? "⟩" : "⟨";
        PanelCollapseToggle.ToolTip = collapsed ? "展开属性面板" : "收起属性面板";
    }

    // ================= 模式互斥 =================

    /// <summary>退出其它模式（除 except 指定的模式），保证同一时间只有一种工具生效。</summary>
    private void EndOtherModes(ToolMode except)
    {
        if (TextTool.IsChecked == true) TextTool.IsChecked = false;

        if (except != ToolMode.Crop && _mode == ToolMode.Crop) ExitCropMode();
        if (except != ToolMode.Ink && _mode == ToolMode.Ink) ExitInkMode();
        if (except != ToolMode.Privacy && _mode == ToolMode.Privacy) ExitPrivacyMode();
        if (except != ToolMode.Shape && _mode == ToolMode.Shape) ExitShapeMode();
    }

    // ================= 鼠标 / 缩放 / 平移 =================

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode is ToolMode.Crop or ToolMode.Ink or ToolMode.Privacy or ToolMode.Shape) return;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        ApplyZoom(_viewModel.Zoom * factor);
    }

    private const double MinZoom = 0.15;

    /// <summary>
    /// 相对「适应窗口」的最大放大倍数。固定值不够用：4000px 宽的图适应窗口后
    /// DisplayScale 可能只有 0.2，×4 才 80%，根本到不了 1:1 看细节。
    /// 这里保证至少能放大到「实际像素 400%」。
    /// </summary>
    private double MaxZoom
    {
        get
        {
            var fit = DisplayScale;
            if (fit <= 0.001) return 4.0;
            return Math.Clamp(4.0 / fit, 4.0, 32.0);
        }
    }

    /// <summary>图片在屏幕上的真实缩放比（1.0 = 一个图片像素对一个屏幕 DIP）。</summary>
    private double AbsoluteScale => DisplayScale * _viewModel.Zoom;

    private void ApplyZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _viewModel.Zoom = zoom;
        ZoomScale.ScaleX = zoom;
        ZoomScale.ScaleY = zoom;

        if (ZoomText != null)
        {
            if (Math.Abs(zoom - 1.0) < 0.001)
            {
                // 「适应窗口」时顺便告诉用户真实比例，否则 100% 到底代表什么没人知道
                var fitPct = DisplayScale * 100;
                ZoomText.Text = fitPct is > 0 and < 99.5 ? $"适应窗口（{fitPct:F0}%）" : "适应窗口";
            }
            else
            {
                // 显示的是「相对原图」的真实比例，而不是相对适应窗口的倍数：
                // 之前 zoom*100% 在 4000px 的图上会把「实际 100%」显示成 300%，永远对不上。
                ZoomText.Text = $"{AbsoluteScale * 100:F0}%";
            }
        }

        ClampPan();
    }

    /// <summary>
    /// 把平移量限制在「图片边缘不离开窗口」的范围内。
    /// 适应窗口 / 缩小状态下可移动量为 0，等于禁止平移 —— 这正好挡住
    /// 「没东西可看时手一抖把图拖歪」的问题。
    /// </summary>
    private void ClampPan()
    {
        if (ImageArea == null) return;   // 构造期间（XAML 字段还没赋值）安全跳过

        var viewW = ImageArea.ActualWidth;
        var viewH = ImageArea.ActualHeight;
        if (viewW <= 1 || viewH <= 1) return;

        var contentW = CanvasW * _viewModel.Zoom;
        var contentH = CanvasH * _viewModel.Zoom;

        var maxX = Math.Max(0, (contentW - viewW) / 2);
        var maxY = Math.Max(0, (contentH - viewH) / 2);

        PanTranslate.X = Math.Clamp(PanTranslate.X, -maxX, maxX);
        PanTranslate.Y = Math.Clamp(PanTranslate.Y, -maxY, maxY);
    }

    /// <summary>按屏幕位移平移，并夹在合法范围内。</summary>
    private void PanBy(double dx, double dy)
    {
        PanTranslate.X += dx;
        PanTranslate.Y += dy;
        ClampPan();
    }

    /// <summary>缩放到 1:1（一个图片像素对一个屏幕像素）。</summary>
    private void ZoomToActualSize()
    {
        var fit = DisplayScale;
        if (fit <= 0.0001) return;
        ApplyZoom(1.0 / fit);
    }

    /// <summary>翻页按钮的「热区」宽度：鼠标进入距左右边缘这么近的范围才淡入。</summary>
    private const double NavHotZone = 110;

    private void UpdateNavArrows()
    {
        if (ImageArea == null || PrevButton == null || NextButton == null) return;

        Point p;
        try { p = Mouse.GetPosition(ImageArea); }
        catch { return; }

        var w = ImageArea.ActualWidth;
        var h = ImageArea.ActualHeight;
        var inside = p.X >= 0 && p.Y >= 0 && p.X <= w && p.Y <= h;

        FadeArrow(PrevButton, inside && p.X <= NavHotZone);
        FadeArrow(NextButton, inside && p.X >= w - NavHotZone);
    }

    private void OnImageAreaMouseLeave(object sender, MouseEventArgs e)
    {
        FadeArrow(PrevButton, false);
        FadeArrow(NextButton, false);
    }

    private static void FadeArrow(UIElement? element, bool show)
    {
        if (element == null) return;
        var target = show ? 1.0 : 0.0;
        if (Math.Abs(element.Opacity - target) < 0.02) return;
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(show ? 110 : 180)));
    }

    private void ResetNavArrows()
    {
        PrevButton?.BeginAnimation(OpacityProperty, null);
        NextButton?.BeginAnimation(OpacityProperty, null);
        if (PrevButton != null) PrevButton.Opacity = 0;
        if (NextButton != null) NextButton.Opacity = 0;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsTextEditing)
        {
            var src = e.OriginalSource as DependencyObject;
            if (!IsInTree(src, TextLayer))
            {
                EndTextEditing();
                return;
            }
        }

        if (_mode == ToolMode.Crop) return;      // 交给裁剪手柄
        if (_mode == ToolMode.Ink) return;       // InkCanvas 自己处理

        if (_mode == ToolMode.Shape)
        {
            var p = e.GetPosition(ImageBox);
            var w = Math.Max(1, CanvasW);
            var h = Math.Max(1, CanvasH);

            ImageArea.CaptureMouse();
            e.Handled = true;

            CancelShapeDraft();
            _shapeDraft = new ShapeOverlayItem
            {
                Kind = _shapeKind,
                X1 = Math.Clamp(p.X / w, 0, 1),
                Y1 = Math.Clamp(p.Y / h, 0, 1),
                X2 = Math.Clamp(p.X / w, 0, 1),
                Y2 = Math.Clamp(p.Y / h, 0, 1),
                ColorHex = _inkColor,
                Thickness = Math.Clamp(_inkWidthDisplay / w, 0.001, 0.05),
            };
            RefreshShapeDraft();
            return;
        }

        if (_mode == ToolMode.Privacy)
        {
            var p = e.GetPosition(ImageBox);
            ImageArea.CaptureMouse();
            e.Handled = true;

            if (_privacyRectTool)
            {
                _privacyRectStart = p;
                HideDraft();

                var preview = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x90, 0xD9)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(preview, p.X);
                Canvas.SetTop(preview, p.Y);
                MosaicLayer.Children.Add(preview);
                _draftElements.Add(preview);
            }
            else
            {
                BeginPrivacyStroke(p);
            }
            return;
        }

        // 不直接进入拖动：先「武装」，等鼠标真的移出死区才平移。
        // 之前左键一按下就 _isDragging = true，单击时的轻微手抖会把图挪歪几个像素。
        _isDragging = false;
        _dragArmed = true;
        _dragStartPosition = e.GetPosition(this);
        _lastPosition = _dragStartPosition;
        ImageArea.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        UpdateNavArrows();

        if (_wmDragItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(WatermarkLayer);
            var w = CanvasW;
            var h = CanvasH;

            var nx = _wmStartX + (p.X - _wmDragOrigin.X) / Math.Max(1, w);
            var ny = _wmStartY + (p.Y - _wmDragOrigin.Y) / Math.Max(1, h);

            _wmDragItem.X = Math.Clamp(nx, 0, Math.Max(0, 1 - _wmDragItem.W));
            _wmDragItem.Y = Math.Clamp(ny, 0, Math.Max(0, 1 - _wmDragItem.H));
            LayoutWatermarkElement(_wmDragItem);
            return;
        }

        if (_mode == ToolMode.Shape && e.LeftButton == MouseButtonState.Pressed && _shapeDraft != null)
        {
            var p = e.GetPosition(ImageBox);
            _shapeDraft.X2 = Math.Clamp(p.X / Math.Max(1, CanvasW), 0, 1);
            _shapeDraft.Y2 = Math.Clamp(p.Y / Math.Max(1, CanvasH), 0, 1);
            RefreshShapeDraft();
            return;
        }

        if (_mode == ToolMode.Privacy && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(ImageBox);

            if (_privacyRectTool)
            {
                if (_draftElements.Count > 0 && _draftElements[0] is Rectangle preview)
                {
                    var x = Math.Min(p.X, _privacyRectStart.X);
                    var y = Math.Min(p.Y, _privacyRectStart.Y);
                    Canvas.SetLeft(preview, x);
                    Canvas.SetTop(preview, y);
                    preview.Width = Math.Abs(p.X - _privacyRectStart.X);
                    preview.Height = Math.Abs(p.Y - _privacyRectStart.Y);
                }
            }
            else
            {
                ExtendPrivacyStroke(p);
            }
            return;
        }

        if (!_dragArmed) return;

        // 鼠标抬起事件可能丢失（Alt+Tab、捕获被抢）：没按住左键时绝不能继续平移
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed = false;
            _isDragging = false;
            return;
        }

        var pos = e.GetPosition(this);

        if (!_isDragging)
        {
            // 死区：移动量不足就不算拖动（单击、双击、轻微手抖都不动画面）
            if (Math.Abs(pos.X - _dragStartPosition.X) < DragDeadZone &&
                Math.Abs(pos.Y - _dragStartPosition.Y) < DragDeadZone)
                return;

            _isDragging = true;
            _lastPosition = pos;
        }

        PanBy(pos.X - _lastPosition.X, pos.Y - _lastPosition.Y);
        _lastPosition = pos;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_wmDragItem != null)
        {
            _wmDragItem = null;
            _wmDragSource?.ReleaseMouseCapture();
            _wmDragSource = null;

            if (_doc != null && _doc.IsEditing) _doc.Commit("移动水印");
            return;
        }

        if (_mode == ToolMode.Shape && e.LeftButton == MouseButtonState.Released)
        {
            ImageArea.ReleaseMouseCapture();
            if (_shapeDraft != null) CommitShapeDraft();
            return;
        }

        if (_mode == ToolMode.Privacy && e.LeftButton == MouseButtonState.Released)
        {
            ImageArea.ReleaseMouseCapture();

            if (_privacyRectTool)
            {
                if (_doc == null || _draftElements.Count == 0 || _draftElements[0] is not Rectangle preview) return;

                var rect = new Rect(
                    Canvas.GetLeft(preview), Canvas.GetTop(preview),
                    preview.Width, preview.Height);
                HideDraft();

                if (rect.Width < 6 || rect.Height < 6)
                {
                    ShowToast("框选区域太小");
                    return;
                }

                var item = BuildRectPrivacy(rect, CanvasW, CanvasH);
                if (item == null) return;

                _doc.Mutate("打码", () =>
                {
                    _doc.Privacies.Add(item);
                    _doc.PrivacyAboveInk = true;   // 最新一次是打码 → 盖在涂鸦之上
                });
                ShowToast(_privacyMode == PrivacyMode.Mosaic ? "已添加马赛克（可撤销）" : "已添加模糊（可撤销）");
            }
            else if (_privacyDraft != null)
            {
                FinishPrivacyStroke();
            }
            return;
        }

        _isDragging = false;
        _dragArmed = false;
        ImageArea.ReleaseMouseCapture();
    }

    // ================= 键盘 =================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Enter)
        {
            OpenCurrentLocation();
            e.Handled = true;
            return;
        }
        // Ctrl+0 / Ctrl+小键盘0：回到适应窗口（与工具栏按钮一致，不在画面上做双击手势）
        // 文字编辑中不响应：否则正在输入的文本框会跟着缩放移动
        if (!IsTextEditing && ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            FitToWindow();
            e.Handled = true;
            return;
        }
        // Ctrl+1 / Ctrl+小键盘1：按实际像素显示（1:1）
        if (!IsTextEditing && ctrl && (e.Key == Key.D1 || e.Key == Key.NumPad1))
        {
            ZoomToActualSize();
            e.Handled = true;
            return;
        }
        if (!IsTextEditing && (e.Key == Key.OemBackslash || e.Key == Key.Oem5))
        {
            BeginCompare();
            e.Handled = true;
            return;
        }
        // R = Random：备注/文字编辑框获得焦点时不能抢字母输入
        if (!ctrl && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.R && !IsTextInputFocused())
        {
            _ = JumpRandomAsync();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (IsTextEditing) { EndTextEditing(); e.Handled = true; return; }
                if (_mode == ToolMode.Crop) { ExitCropMode(); e.Handled = true; return; }
                if (_mode == ToolMode.Ink) { ExitInkMode(); e.Handled = true; return; }
                if (_mode == ToolMode.Privacy) { ExitPrivacyMode(); e.Handled = true; return; }
                if (_mode == ToolMode.Shape) { ExitShapeMode(); e.Handled = true; return; }
                if (_isFullScreen) OnFullScreen(sender, e);
                else Close();
                e.Handled = true;
                break;
            case Key.Left:
                if (!IsTextEditing) GoPrevious();
                break;
            case Key.Right:
                if (!IsTextEditing) GoNext();
                break;
            case Key.F11:
                OnFullScreen(sender, e);
                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemBackslash || e.Key == Key.Oem5)
        {
            EndCompare();
            e.Handled = true;
        }
    }

    // ================= 沉浸式随机跳转 =================

    private readonly List<(long Id, string Name)> _randomCategories = new();
    private readonly List<(long Id, string Name)> _randomTags = new();

    private async void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        await LoadRandomSettingsAsync();
    }

    private async Task LoadRandomSettingsAsync()
    {
        if (_randomSettingsLoaded) return;
        _randomSettingsLoaded = true;

        try
        {
            var repo = _viewModel.Repository;
            var scopeRaw = await repo.GetSettingAsync(RandomScopeSettingKey);
            if (Enum.TryParse(scopeRaw, true, out RandomScope scope)) _randomScope = scope;

            var idRaw = await repo.GetSettingAsync(RandomScopeIdSettingKey);
            if (long.TryParse(idRaw, out var id)) _randomScopeId = id;
            _randomScopeName = await repo.GetSettingAsync(RandomScopeNameSettingKey);

            await BuildSpecifiedMenusAsync();

            // 分类/标签被删掉后，别让随机范围指向一个空目标
            if (IsSpecifiedScope(_randomScope) && !SpecifiedScopeExists())
                SetRandomScope(RandomScope.List, null, null, persist: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取随机跳转范围设置失败");
        }
        finally
        {
            UpdateRandomMenu();
        }
    }

    private async Task BuildSpecifiedMenusAsync()
    {
        var repo = _viewModel.Repository;

        _randomCategories.Clear();
        foreach (var c in await repo.GetCategoriesAsync()) _randomCategories.Add((c.Id, c.Name));
        _randomTags.Clear();
        foreach (var t in await repo.GetTagsAsync()) _randomTags.Add((t.Id, t.Name));

        FillScopeMenu(ScopeCategoryItem, _randomCategories, "category", "（还没有分类）");
        FillScopeMenu(ScopeTagItem, _randomTags, "tag", "（还没有标签）");
    }

    private void FillScopeMenu(MenuItem host, List<(long Id, string Name)> entries, string kind, string emptyText)
    {
        host.Items.Clear();
        if (entries.Count == 0)
        {
            host.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false });
            return;
        }

        foreach (var (id, name) in entries)
        {
            var mi = new MenuItem
            {
                Header = name,
                Tag = $"{kind}:{id}",
                IsCheckable = true,
            };
            mi.Click += OnRandomSpecifiedScopeClick;
            host.Items.Add(mi);
        }
    }

    private static bool IsSpecifiedScope(RandomScope scope)
        => scope is RandomScope.Category or RandomScope.Tag;

    private bool SpecifiedScopeExists()
    {
        if (!_randomScopeId.HasValue) return false;
        var list = _randomScope switch
        {
            RandomScope.Category => _randomCategories,
            RandomScope.Tag => _randomTags,
            _ => null,
        };
        return list?.Any(x => x.Id == _randomScopeId.Value) == true;
    }

    private void OnRandomScopeClick(object sender, RoutedEventArgs e)
    {
        UpdateRandomMenu();
        RandomMenu.PlacementTarget = RandomScopeButton;
        RandomMenu.Placement = PlacementMode.Bottom;
        RandomMenu.IsOpen = true;
    }

    private void OnRandomScopeItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;

        var scope = tag switch
        {
            "folder" => RandomScope.Folder,
            "all" => RandomScope.All,
            "favorite" => RandomScope.Favorite,
            _ => RandomScope.List,
        };
        SetRandomScope(scope, null, null);
    }

    private void OnRandomSpecifiedScopeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;

        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], out var id)) return;

        var name = mi.Header?.ToString();
        var scope = parts[0] switch
        {
            "category" => RandomScope.Category,
            "tag" => RandomScope.Tag,
            _ => RandomScope.List,
        };
        SetRandomScope(scope, id, name);
    }

    private void SetRandomScope(RandomScope scope, long? id, string? name, bool persist = true)
    {
        _randomScope = scope;
        _randomScopeId = id;
        _randomScopeName = name;
        UpdateRandomMenu();
        if (persist) _ = PersistRandomScopeAsync();
    }

    private async Task PersistRandomScopeAsync()
    {
        try
        {
            var repo = _viewModel.Repository;
            await repo.SetSettingAsync(RandomScopeSettingKey, _randomScope.ToString());
            await repo.SetSettingAsync(RandomScopeIdSettingKey, _randomScopeId?.ToString() ?? string.Empty);
            await repo.SetSettingAsync(RandomScopeNameSettingKey, _randomScopeName ?? string.Empty);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存随机跳转范围设置失败");
        }
    }

    private void UpdateRandomMenu()
    {
        if (ScopeListItem == null) return;

        ScopeListItem.Header = $"当前列表结果（{_viewModel.Items.Count} 项）";
        ScopeFolderItem.Header = _viewModel.CurrentItem == null
            ? "当前文件夹"
            : "当前文件夹（这张图所在根目录）";
        ScopeSpecifiedItem.Header = IsSpecifiedScope(_randomScope) && !string.IsNullOrWhiteSpace(_randomScopeName)
            ? $"指定：{_randomScopeName}"
            : "指定分类 / 标签";

        ScopeListItem.IsChecked = _randomScope == RandomScope.List;
        ScopeFolderItem.IsChecked = _randomScope == RandomScope.Folder;
        ScopeAllItem.IsChecked = _randomScope == RandomScope.All;
        ScopeFavoriteItem.IsChecked = _randomScope == RandomScope.Favorite;
        ScopeSpecifiedItem.IsChecked = IsSpecifiedScope(_randomScope);

        SetSpecifiedChecks(ScopeCategoryItem, RandomScope.Category, "category");
        SetSpecifiedChecks(ScopeTagItem, RandomScope.Tag, "tag");
    }

    private void SetSpecifiedChecks(MenuItem host, RandomScope scope, string kind)
    {
        foreach (var obj in host.Items)
        {
            if (obj is not MenuItem mi) continue;
            var selected = _randomScope == scope && _randomScopeId.HasValue
                && string.Equals(mi.Tag?.ToString(), $"{kind}:{_randomScopeId.Value}", StringComparison.Ordinal);
            mi.IsChecked = selected;
        }
    }

    private void OnRandomJumpClick(object sender, RoutedEventArgs e)
    {
        _ = JumpRandomAsync();
    }

    private async Task JumpRandomAsync()
    {
        if (_randomJumping || _viewModel.CurrentItem == null) return;
        if (!ConfirmDiscardChanges()) return;

        _randomJumping = true;
        RandomButton.IsEnabled = false;
        try
        {
            var (candidates, replaceList) = await LoadRandomCandidatesAsync();
            var images = candidates.Where(i => !i.IsVideo).ToList();
            var current = _viewModel.CurrentItem;

            if (images.Count <= 1)
            {
                ShowToast("当前范围内的媒体太少啦，无法随机");
                return;
            }

            var pool = images
                .Where(i => current == null || (i.Id != current.Id && !string.Equals(i.Path, current.Path, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (pool.Count == 0)
            {
                ShowToast("当前范围内的媒体太少啦，无法随机");
                return;
            }

            var picked = pool[Random.Shared.Next(pool.Count)];
            ResetToolState();

            await FadePreviewAsync(1, 0, 70);

            if (replaceList)
            {
                var index = images.FindIndex(i => ReferenceEquals(i, picked));
                if (index < 0) index = 0;
                _viewModel.Initialize(images, index);
            }
            else
            {
                var index = IndexOfItem(_viewModel.Items, picked);
                if (index >= 0) _viewModel.JumpTo(index);
            }

            PreviewImage.Opacity = 0;
            await FadePreviewAsync(0, 1, 80);
            PreviewImage.BeginAnimation(OpacityProperty, null);
            PreviewImage.Opacity = 1;

            ShowToast($"🎲 {picked.FileName}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "随机跳转失败");
            ShowToast("随机跳转失败：" + StorageErrors.FriendlyMessage(ex));
        }
        finally
        {
            _randomJumping = false;
            RandomButton.IsEnabled = true;
        }
    }

    private static int IndexOfItem(IReadOnlyList<ThumbnailItemViewModel> items, ThumbnailItemViewModel target)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target)) return i;
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id != 0 && items[i].Id == target.Id) return i;
        }
        return -1;
    }

    /// <summary>
    /// 按当前范围取候选图片。当前列表直接复用内存列表（不查库，速度和列表一致）；
    /// 其他范围查库后替换查看器列表，翻页/撤销上下文也跟着范围走。
    /// </summary>
    private async Task<(List<ThumbnailItemViewModel> Items, bool ReplaceList)> LoadRandomCandidatesAsync()
    {
        if (_randomScope == RandomScope.List)
            return (_viewModel.Items.Where(i => !i.IsVideo).ToList(), false);

        var current = _viewModel.CurrentItem;
        var filter = new MediaFilter
        {
            MediaType = MediaType.Image,
            Limit = 100000,
        };

        switch (_randomScope)
        {
            case RandomScope.Folder:
                if (current == null || current.Media.RootFolderId <= 0) return (new(), true);
                filter.RootFolderId = current.Media.RootFolderId;
                break;
            case RandomScope.Favorite:
                filter.Favorite = true;
                break;
            case RandomScope.Category:
                if (!_randomScopeId.HasValue) return (new(), true);
                filter.CategoryId = _randomScopeId.Value;
                break;
            case RandomScope.Tag:
                if (!_randomScopeId.HasValue) return (new(), true);
                filter.TagId = _randomScopeId.Value;
                break;
        }

        var media = await _viewModel.Repository.QueryAsync(filter);
        var items = media
            .Where(m => m.MediaType == MediaType.Image && !m.IsMissing)
            .Select(m => new ThumbnailItemViewModel(m)
            {
                ThumbnailPath = m.ThumbnailPath,
                IsFavorite = m.Favorite,
                Rating = m.Rating,
                Note = m.Note,
            })
            .ToList();
        return (items, true);
    }

    private Task FadePreviewAsync(double from, double to, int milliseconds)
    {
        var tcs = new TaskCompletionSource();
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => tcs.TrySetResult();
        PreviewImage.BeginAnimation(OpacityProperty, animation);
        return tcs.Task;
    }

    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox;

    // ================= 导航 / 缩放按钮 =================

    private void OnPrevious(object sender, RoutedEventArgs e) => GoPrevious();

    private void OnNext(object sender, RoutedEventArgs e) => GoNext();

    private void GoPrevious()
    {
        if (!_viewModel.HasPrevious) return;
        if (!ConfirmDiscardChanges()) return;
        ResetToolState();
        _viewModel.PreviousCommand.Execute(null);
    }

    private void GoNext()
    {
        if (!_viewModel.HasNext) return;
        if (!ConfirmDiscardChanges()) return;
        ResetToolState();
        _viewModel.NextCommand.Execute(null);
    }

    /// <summary>有未保存的编辑时，翻页/关闭前确认。</summary>
    private bool ConfirmDiscardChanges()
    {
        EndTextEditing(rebuild: false);
        CommitContinuous();
        if (_doc?.IsDirty != true) return true;

        return CustomMessageBox.Confirm(
            "当前图片有未保存的修改，继续将丢弃这些修改。\n\n可以先点「💾 保存副本」再离开。",
            "放弃修改？", confirmText: "放弃修改", danger: true);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }

        _toastTimer?.Stop();
        _previewTimer.Stop();
        _commitTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DetachDocument();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom * 1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom / 1.25);

    private void OnFit(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnZoomActual(object sender, RoutedEventArgs e) => ZoomToActualSize();

    private void FitToWindow()
    {
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;
    }

    private void OnFullScreen(object sender, RoutedEventArgs e)
    {
        if (!_isFullScreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
        }
        else
        {
            WindowStyle = _prevStyle;
            WindowState = _prevState;
            _isFullScreen = false;
        }
    }

    private void OpenCurrentLocation()
    {
        var item = _viewModel.CurrentItem;
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;

        var fullPath = item.Path;
        if (!System.IO.File.Exists(fullPath))
        {
            CustomMessageBox.Info($"文件已不在原位置：\n{fullPath}", "打开所在位置");
            return;
        }

        var target = fullPath.Length >= 260 && !fullPath.StartsWith(@"\\?\")
            ? @"\\?\" + fullPath
            : fullPath;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "打开所在位置"), "打开所在位置失败");
        }
    }

    // ================= 小工具 =================

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBar.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            ToastBar.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    private void BuildColorPalettes()
    {
        var colors = new (string Hex, string Name)[]
        {
            ("#FFFFFF", "白色"), ("#000000", "黑色"), ("#FF3B3B", "红色"),
            ("#FF9F0A", "橙色"), ("#FFD60A", "黄色"), ("#30D158", "绿色"),
            ("#0A84FF", "蓝色"), ("#BF5AF2", "紫色"), ("#FF375F", "玫红"),
            ("#8E8E93", "灰色"), ("#00C7BE", "青色"), ("#A2845E", "棕色"),
        };

        foreach (var (hex, name) in colors)
            TextColorPanel.Children.Add(CreateSwatch(hex, name, 20, OnTextColorClick));

        foreach (var (hex, name) in colors.Take(6))
            InkColorPanel.Children.Add(CreateSwatch(hex, name, 22, OnInkColorClick));
    }

    private static Border CreateSwatch(string hex, string name, double size, RoutedEventHandler onClick)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();

        var swatch = new Border
        {
            Width = size,
            Height = size,
            Margin = new Thickness(2),
            Background = brush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            ToolTip = name,
            Tag = hex,
        };
        swatch.MouseLeftButtonDown += (s, e) =>
        {
            onClick(s, e);
            e.Handled = true;
        };
        return swatch;
    }

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.White; }
    }

    private static Brush ParseBrush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }
}
