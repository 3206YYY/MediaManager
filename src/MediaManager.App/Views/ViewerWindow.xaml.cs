using System.ComponentModel;
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

namespace MediaManager.App.Views;

/// <summary>
/// 查看器 / 图片编辑器。
/// 编辑采用「实时预览 + 保存时一次性导出」：
/// 所有操作只改编辑文档（含撤销栈），界面用同一份状态渲染，保存时 RenderTargetBitmap 出图。
/// </summary>
public partial class ViewerWindow : Window
{
    private enum ToolMode { None, Crop, Ink, Mosaic }

    private readonly ViewerViewModel _viewModel;

    // 视图状态
    private bool _isDragging;
    private Point _lastPosition;
    private bool _isFullScreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;

    // 编辑文档
    private EditorDocument? _doc;
    private ToolMode _mode = ToolMode.None;
    private bool _comparing;
    private int? _outputMaxEdge;

    // 文字（同一时刻只允许一个编辑会话，见 SelectText / EndTextEditing）
    private TextOverlayItem? _activeText;
    private TextBox? _activeTextBox;
    private Border? _activeTextHost;
    private bool _editingText;
    private EditorSnapshot? _textEditSnapshot;
    private bool _textDirty;
    private bool _suppressTextPanel;
    private bool _suppressOverlayRebuild;

    /// <summary>
    /// 是否有「正在编辑」的文本框。
    /// 统一由会话标志 + 当前文字项推导，避免出现「标志位说在编辑，实际却没有文本框」的漂移状态
    /// ——那正是之前点击一次就叠出一个新文本框的根因。
    /// </summary>
    private bool IsTextEditing => _editingText && _activeText != null;

    // 涂鸦
    private string _inkColor = "#FF3B3B";
    private double _inkWidthDisplay = 4;

    // 马赛克拖拽
    private Point _mosaicStart;
    private Rectangle? _mosaicPreview;

    // 裁剪
    private Rect _cropRect;

    // 浮动面板拖动
    private FrameworkElement? _dragPanel;
    private Point _dragOrigin;

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

        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ImageArea.SizeChanged += (_, _) =>
        {
            LayoutCanvas();
            // 窗口变小后，之前拖到右边的面板可能已经跑到画布外，重新压回可视区
            ClampPanelOffsets();
        };
        ImageBox.SizeChanged += (_, _) =>
        {
            if (!_suppressOverlayRebuild) RebuildOverlays();
        };

        BuildColorPalettes();
        BuildFilterButtons();
        UpdateHistoryButtons();
        Closed += (_, _) => DetachDocument();
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
    }

    private void LoadDocument()
    {
        DetachDocument();
        UpdateSaveButtonLabel();   // 换图先同步按钮文案（原图=保存副本 / 副本=更新副本）

        if (_viewModel.ImageSource is not BitmapSource bitmap)
        {
            _doc = null;
            PreviewImage.Source = null;
            ToolHint.Text = _viewModel.LoadStatusText;
            return;
        }

        var item = _viewModel.CurrentItem;
        if (item == null) return;

        _doc = new EditorDocument(bitmap, item.Path);
        _doc.BaseChanged += OnDocBaseChanged;
        _doc.OverlaysChanged += OnDocOverlaysChanged;
        _doc.History.Changed += OnHistoryChanged;

        ResetToolState();
        ApplyBase();
        ResetNavArrows();
        UpdateHistoryButtons();
        ToolHint.Text = "提示：右侧选择工具，Ctrl+Z 撤销，按住 \\ 看原图";
    }

    /// <summary>解绑旧文档事件，避免切换图片后旧文档/位图被事件引用导致内存泄漏。</summary>
    private void DetachDocument()
    {
        if (_doc == null) return;
        _doc.BaseChanged -= OnDocBaseChanged;
        _doc.OverlaysChanged -= OnDocOverlaysChanged;
        _doc.History.Changed -= OnHistoryChanged;
        _doc = null;

        MosaicLayer.Children.Clear();
        TextLayer.Children.Clear();
        InkLayer.Strokes.Clear();
        _activeText = null;
        _activeTextBox = null;
        _activeTextHost = null;
        _editingText = false;
        _textEditSnapshot = null;
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
        ShowPanel(null);
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        _activeText = null;
        _editingText = false;
        _textEditSnapshot = null;
        HideMosaicPreview();
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;
    }

    // ================= 画布布局 =================

    private void ApplyBase()
    {
        if (_doc == null) return;

        PreviewImage.Source = _doc.GetBaseBitmap();

        var tint = _doc.GetTint();
        if (tint is var (color, opacity))
        {
            TintLayer.Fill = new SolidColorBrush(color) { Opacity = opacity };
            TintLayer.Visibility = Visibility.Visible;
        }
        else
        {
            TintLayer.Visibility = Visibility.Collapsed;
        }

        LayoutCanvas();
        RebuildOverlays();
    }

    /// <summary>按当前底图比例计算画布尺寸（适应区域，不放大超过 100%）。</summary>
    private void LayoutCanvas()
    {
        if (_doc == null) return;

        var bmp = _doc.GetBaseBitmap();
        var availW = Math.Max(50, ImageArea.ActualWidth - 24);
        var availH = Math.Max(50, ImageArea.ActualHeight - 24);

        var scale = Math.Min(availW / bmp.PixelWidth, availH / bmp.PixelHeight);
        scale = Math.Min(scale, 1.0);

        _suppressOverlayRebuild = true;
        ImageBox.Width = Math.Max(24, bmp.PixelWidth * scale);
        ImageBox.Height = Math.Max(24, bmp.PixelHeight * scale);
        _suppressOverlayRebuild = false;

        if (_mode == ToolMode.Crop) LayoutCrop();
    }

    private double DisplayScale => _doc == null || _doc.VisibleWidth <= 0
        ? 1
        : ImageBox.ActualWidth / _doc.VisibleWidth;

    // ================= 叠加层渲染 =================

    /// <summary>
    /// 重建画布叠加层。
    /// forceText：文字编辑中也需要重建文字层（首次进入编辑、改颜色/字号时）。
    /// 平时（例如打字触发的重绘）不重建，避免把正在编辑的 TextBox 销毁导致焦点丢失。
    /// </summary>
    private void RebuildOverlays(bool forceText = false)
    {
        if (_doc == null) return;
        if (IsTextEditing && !forceText) return;

        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        if (w <= 1 || h <= 1) return;

        RebuildMosaics(w, h);
        RebuildTexts(w, h);
        RebuildInk(w, h);
        UpdateHistoryButtons();
    }

    private void RebuildMosaics(double w, double h)
    {
        if (_doc == null) return;
        MosaicLayer.Children.Clear();

        foreach (var mosaic in _doc.Mosaics)
        {
            var img = BuildMosaicPreview(mosaic, w, h);
            if (img != null) MosaicLayer.Children.Add(img);
        }
    }

    private Image? BuildMosaicPreview(MosaicOverlayItem mosaic, double w, double h)
    {
        if (_doc == null) return null;

        var x = mosaic.X * w;
        var y = mosaic.Y * h;
        var mw = mosaic.W * w;
        var mh = mosaic.H * h;
        if (mw < 2 || mh < 2) return null;

        var baseBmp = _doc.GetBaseBitmap();
        int bw = baseBmp.PixelWidth, bh = baseBmp.PixelHeight;

        var srcRect = new Int32Rect(
            Math.Clamp((int)Math.Round(mosaic.X * bw), 0, bw - 1),
            Math.Clamp((int)Math.Round(mosaic.Y * bh), 0, bh - 1),
            Math.Clamp((int)Math.Round(mosaic.W * bw), 1, bw),
            Math.Clamp((int)Math.Round(mosaic.H * bh), 1, bh));

        try
        {
            var cropped = new CroppedBitmap(baseBmp, srcRect);
            var factor = Math.Max(2.0, mw / 14.0);
            var tiny = new TransformedBitmap(cropped, new ScaleTransform(1.0 / factor, 1.0 / factor));
            tiny.Freeze();

            var img = new Image
            {
                Source = tiny,
                Width = mw,
                Height = mh,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);
            return img;
        }
        catch
        {
            return null;
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
                var host = CreateTextEditor(item, w, h, scale);
                TextLayer.Children.Add(host);
                continue;
            }

            var block = new TextBlock
            {
                Text = item.Text,
                FontFamily = new FontFamily(item.FontFamily),
                FontSize = Math.Max(6, item.FontSize * scale),
                Foreground = ParseBrush(item.ColorHex),
                Opacity = item.Opacity,
                FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(20, w - item.X * w),
                Cursor = Cursors.Hand,
                Tag = item,
            };

            block.MouseLeftButtonDown += (_, ev) =>
            {
                SelectText(item);
                ev.Handled = true;
            };

            Canvas.SetLeft(block, item.X * w);
            Canvas.SetTop(block, item.Y * h);
            TextLayer.Children.Add(block);
        }
    }

    private Border CreateTextEditor(TextOverlayItem item, double w, double h, double scale)
    {
        var box = new TextBox
        {
            Text = item.Text,
            Background = Brushes.Transparent,
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
            if (_activeTextBox == null) return;
            _activeText!.Text = box.Text;
            _textDirty = true;
            MeasureText(item, box);
        };

        // 注意：不能在这里挂 LostKeyboardFocus → EndTextEditing。
        // 点右侧属性面板（颜色/字号/字体）会让文本框失焦，那样面板一点就退出编辑，改不了样式。
        // 改为「点画布空白处结束编辑」（见 OnCanvasMouseDown）+ Esc。
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                ev.Handled = true;
                EndTextEditing();
            }
        };

        // 拖动标题条移动整块文字
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
    {        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        box.Measure(new Size(Math.Max(20, w), Math.Max(20, h)));
        item.W = box.DesiredSize.Width / w;
        item.H = box.DesiredSize.Height / h;
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

    // ================= 顶部按钮：撤销 / 重做 / 对比 / 保存 =================

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
        var name = _doc.History.Undo();
        if (name != null) ShowToast($"已撤销：{name}");
    }

    private void Redo()
    {
        if (_doc?.History.CanRedo != true) return;
        EndTextEditing();
        var name = _doc.History.Redo();
        if (name != null) ShowToast($"已重做：{name}");
    }

    private void OnCompareDown(object sender, MouseButtonEventArgs e) => BeginCompare();

    private void OnCompareUp(object sender, MouseButtonEventArgs e) => EndCompare();

    private void BeginCompare()
    {
        if (_doc == null || _comparing) return;
        _comparing = true;
        PreviewImage.Source = _doc.Bitmap;
        TintLayer.Visibility = Visibility.Collapsed;
        MosaicLayer.Visibility = Visibility.Collapsed;
        TextLayer.Visibility = Visibility.Collapsed;
        InkLayer.Visibility = Visibility.Collapsed;
        ToolHint.Text = "正在显示原图（松开恢复）";
    }

    private void EndCompare()
    {
        if (!_comparing) return;
        _comparing = false;
        MosaicLayer.Visibility = Visibility.Visible;
        TextLayer.Visibility = Visibility.Visible;
        InkLayer.Visibility = Visibility.Visible;
        ApplyBase();
        ToolHint.Text = "提示：右侧选择工具，Ctrl+Z 撤销，按住 \\ 看原图";
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null)
        {
            CustomMessageBox.Warn("当前没有可保存的图片。", "保存副本");
            return;
        }

        // 防止连点：一次保存没结束时重复触发会写同一路径，看起来像「没反应」
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;

        try
        {
            EndTextEditing();

            var item = _viewModel.CurrentItem;
            if (item == null)
            {
                CustomMessageBox.Warn("当前没有可保存的图片。", "保存副本");
                return;
            }

            HideSaveResult();
            ToolHint.Text = EditorRenderer.IsEditedCopy(item.Path) ? "正在更新副本…" : "正在保存副本…";

            var result = await EditorRenderer.SaveAsync(_doc, _outputMaxEdge);
            _doc.SavedUndoCount = _doc.History.UndoCount;

            // 强反馈：文件名 + 尺寸 + 是否动了原图，10 秒内一直可见（可手动关闭）
            ShowSaveResult(result);

            // 通知主界面：刷新网格并选中这张图（原地更新时同时重建缩略图）
            CopySaved?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            // 绝不静默失败：硬盘满 / 无权限 / 设备断开 都要明确告诉用户
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

    /// <summary>保存成功的醒目反馈（画布底部中央的绿色卡片）。</summary>
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

        // 同时收起普通 toast，避免两条提示叠在一起
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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>修改底图（裁剪/旋转/滤镜）时，先让缓存失效再提交，保证预览立即正确。</summary>
    private void MutateBase(string name, Action change)
    {
        if (_doc == null) return;
        _doc.BeginEdit();
        change();
        _doc.InvalidateBase();
        _doc.Commit(name);
    }

    // ================= 工具：旋转 / 滤镜 / 压缩 =================

    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        EndTextEditing();
        MutateBase("旋转 90°", () =>
        {
            _doc.RemapOverlaysForRotation(90);
            _doc.Rotation = (_doc.Rotation + 90) % 360;
        });
        ShowToast("已旋转 90°");
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        // 打开滤镜面板前先退出裁剪/涂鸦/马赛克等模式，避免工具高亮与实际状态不一致
        EndTextEditing();   // 切工具 = 结束文字编辑会话（否则面板收起了、文本框还在偷偷接收输入）
        EndOtherModes(except: ToolMode.None);
        TogglePanel(FilterPanel);
    }

    private void BuildFilterButtons()
    {
        var options = new (string Name, EditorFilter Filter)[]
        {
            ("原图", EditorFilter.None),
            ("黑白", EditorFilter.Gray),
            ("暖色", EditorFilter.Warm),
            ("复古", EditorFilter.Retro),
        };

        foreach (var (name, filter) in options)
        {
            var btn = new Button
            {
                Content = name,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = filter,
            };
            btn.Click += (_, _) =>
            {
                if (_doc == null) return;
                EndTextEditing();
                if (_doc.Filter == filter) return;
                MutateBase($"滤镜：{name}", () => _doc.Filter = filter);
                ShowToast($"滤镜：{name}");
            };
            FilterButtons.Children.Add(btn);
        }
    }

    private void OnResizeClick(object sender, RoutedEventArgs e)
    {
        EndTextEditing();   // 同上：切工具就结束文字编辑会话
        EndOtherModes(except: ToolMode.None);
        TogglePanel(ResizePanel);
    }

    private void OnResizeOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResizeCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString();
        _outputMaxEdge = int.TryParse(tag, out var v) && v > 0 ? v : null;
    }

    // ================= 浮动工具面板：互斥 / 拖动 / 折叠 =================

    /// <summary>所有可显示的浮动面板（按同一套规则管理，新增面板记得加进来）。</summary>
    private FrameworkElement[] AllPanels() => new FrameworkElement[] { TextPanel, InkPanel, FilterPanel, ResizePanel };

    private FrameworkElement? PanelByKey(object? key) => key is string id
        ? id switch
        {
            "TextPanel" => TextPanel,
            "InkPanel" => InkPanel,
            "FilterPanel" => FilterPanel,
            "ResizePanel" => ResizePanel,
            _ => null,
        }
        : null;

    private FrameworkElement? PanelBodyByKey(object? key) => key is string id
        ? id switch
        {
            "TextPanel" => TextPanelBody,
            "InkPanel" => InkPanelBody,
            "FilterPanel" => FilterPanelBody,
            "ResizePanel" => ResizePanelBody,
            _ => null,
        }
        : null;

    /// <summary>
    /// 同一时刻只允许一个面板可见。传 null = 全部收起。
    /// 「不保存就能同时出现好几个工具面板」原本是 bug：旧代码里 TogglePanel 只切自己，
    /// 而每个入口又各写了几行 Visibility.Collapsed（有的漏了），
    /// 于是裁剪/马赛克进来时一个都不关，能叠 3-4 个面板。现在统一收敛到这里。
    /// </summary>
    private void ShowPanel(FrameworkElement? target)
    {
        foreach (var panel in AllPanels())
            if (!ReferenceEquals(panel, target))
                panel.Visibility = Visibility.Collapsed;

        if (target != null) target.Visibility = Visibility.Visible;
    }

    /// <summary>再点一次工具按钮 = 收起（已经是唯一可见的那个就收起，否则换成它）。</summary>
    private void TogglePanel(FrameworkElement panel) =>
        ShowPanel(panel.Visibility == Visibility.Visible ? null : panel);

    /// <summary>折叠（只留标题栏）按钮：用户嫌面板挡视野时可以先折起来，随时再展开。</summary>
    private void OnPanelCollapseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var body = PanelBodyByKey(btn.Tag);
        if (body == null) return;

        var collapsed = body.Visibility == Visibility.Visible;
        body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        btn.Content = collapsed ? "▸" : "▾";
        btn.ToolTip = collapsed ? "展开面板" : "折叠面板（只留标题栏）";
    }

    private TranslateTransform? OffsetOf(FrameworkElement panel) => panel.RenderTransform as TranslateTransform;

    /// <summary>标题栏按下 = 开始拖动面板（按钮/Slider 会自己吃掉事件，不会误触发）。</summary>
    private void OnPanelHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement header) return;
        if (header.Tag is null) return;
        if (Mouse.Captured != null) return;

        var panel = PanelByKey(header.Tag);
        if (panel == null) return;

        _dragPanel = panel;
        _dragOrigin = e.GetPosition(this);
        header.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelHeaderMove(object sender, MouseEventArgs e)
    {
        if (_dragPanel == null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPanelDrag();
            return;
        }

        var t = OffsetOf(_dragPanel);
        if (t == null) return;
        if (_dragPanel.Parent is not FrameworkElement host) return;

        var hostW = host.ActualWidth;
        var hostH = host.ActualHeight;
        var pw = _dragPanel.ActualWidth;
        var ph = _dragPanel.ActualHeight;
        if (hostW <= 0 || hostH <= 0 || pw <= 0 || ph <= 0) return;

        // 面板未平移时的原始左上角：TranslatePoint 会带上 RenderTransform，先减掉当前偏移
        var visual = _dragPanel.TranslatePoint(new Point(0, 0), host);
        var baseX = visual.X - t.X;
        var baseY = visual.Y - t.Y;

        var p = e.GetPosition(this);
        var targetX = baseX + (p.X - _dragOrigin.X);
        var targetY = baseY + (p.Y - _dragOrigin.Y);

        // 钳制在画布区内：绝不允许把面板拖到看不见的地方（否则用户找不回来）
        targetX = Math.Clamp(targetX, 6, Math.Max(6, hostW - pw - 6));
        targetY = Math.Clamp(targetY, 6, Math.Max(6, hostH - ph - 6));

        t.X = targetX - baseX;
        t.Y = targetY - baseY;
    }

    private void OnPanelHeaderUp(object sender, MouseButtonEventArgs e) => EndPanelDrag();

    private void EndPanelDrag()
    {
        if (_dragPanel == null) return;
        _dragPanel = null;
        if (Mouse.Captured != null) Mouse.Capture(null);
    }

    /// <summary>窗口尺寸变化后把已拖动的面板压回可视区，避免它跑到画布外再也点不到。</summary>
    private void ClampPanelOffsets()
    {
        foreach (var panel in AllPanels())
        {
            if (panel.Visibility != Visibility.Visible) continue;
            var t = OffsetOf(panel);
            if (t == null || (Math.Abs(t.X) < 0.5 && Math.Abs(t.Y) < 0.5)) continue;
            if (panel.Parent is not FrameworkElement host) continue;

            var hostW = host.ActualWidth;
            var hostH = host.ActualHeight;
            var pw = panel.ActualWidth;
            var ph = panel.ActualHeight;
            if (hostW <= 0 || hostH <= 0 || pw <= 0 || ph <= 0) continue;

            var visual = panel.TranslatePoint(new Point(0, 0), host);
            var baseX = visual.X - t.X;
            var baseY = visual.Y - t.Y;

            var targetX = Math.Clamp(baseX + t.X, 6, Math.Max(6, hostW - pw - 6));
            var targetY = Math.Clamp(baseY + t.Y, 6, Math.Max(6, hostH - ph - 6));

            t.X = targetX - baseX;
            t.Y = targetY - baseY;
        }
    }

    // ================= 工具：裁剪 =================

    private void OnCropToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (CropTool.IsChecked == true) EnterCropMode();
        else ExitCropMode();
    }

    private void EnterCropMode()
    {
        EndTextEditing();
        EndOtherModes(except: ToolMode.Crop);
        ShowPanel(null);   // 裁剪没有属性面板，全部收起，别和面板叠在一起挡画面

        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;

        _mode = ToolMode.Crop;
        CropTool.IsChecked = true;

        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        _cropRect = new Rect(w * 0.1, h * 0.1, w * 0.8, h * 0.8);

        CropLayer.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Visible;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        ToolHint.Text = "裁剪：拖动 8 个白点调整边框，拖动框内移动位置，然后点「确认裁剪」";

        LayoutCrop();
    }

    private void ExitCropMode()
    {
        _mode = ToolMode.None;
        CropLayer.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        CropTool.IsChecked = false;
        ToolHint.Text = "提示：右侧选择工具，Ctrl+Z 撤销，按住 \\ 看原图";
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e) => ExitCropMode();

    private void OnConfirmCrop(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var norm = new Rect(
            Math.Clamp(_cropRect.X / w, 0, 1),
            Math.Clamp(_cropRect.Y / h, 0, 1),
            Math.Clamp(_cropRect.Width / w, 0, 1),
            Math.Clamp(_cropRect.Height / h, 0, 1));

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
        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 保证裁剪框在画面内
        var r = _cropRect;
        r = new Rect(
            Math.Clamp(r.X, 0, w - 24),
            Math.Clamp(r.Y, 0, h - 24),
            Math.Clamp(r.Width, 24, w),
            Math.Clamp(r.Height, 24, h));
        if (r.Right > w) r.Width = w - r.X;
        if (r.Bottom > h) r.Height = h - r.Y;
        _cropRect = r;

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

        CropSizeText.Text = $"{_doc?.VisibleWidth * r.Width / w:0} × {_doc?.VisibleHeight * r.Height / h:0} 像素";
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

    private void OnCropMoveDelta(object sender, DragDeltaEventArgs e)
    {
        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;

        var x = Math.Clamp(_cropRect.X + e.HorizontalChange, 0, Math.Max(0, w - _cropRect.Width));
        var y = Math.Clamp(_cropRect.Y + e.VerticalChange, 0, Math.Max(0, h - _cropRect.Height));

        _cropRect = new Rect(x, y, _cropRect.Width, _cropRect.Height);
        LayoutCrop();
    }

    private void OnCropNW(object sender, DragDeltaEventArgs e) => ResizeCrop(e, left: true, top: true, right: false, bottom: false);
    private void OnCropN(object sender, DragDeltaEventArgs e) => ResizeCrop(e, false, true, false, false);
    private void OnCropNE(object sender, DragDeltaEventArgs e) => ResizeCrop(e, false, true, true, false);
    private void OnCropE(object sender, DragDeltaEventArgs e) => ResizeCrop(e, false, false, true, false);
    private void OnCropSE(object sender, DragDeltaEventArgs e) => ResizeCrop(e, false, false, true, true);
    private void OnCropS(object sender, DragDeltaEventArgs e) => ResizeCrop(e, false, false, false, true);
    private void OnCropSW(object sender, DragDeltaEventArgs e) => ResizeCrop(e, true, false, false, true);
    private void OnCropW(object sender, DragDeltaEventArgs e) => ResizeCrop(e, true, false, false, false);

    private void ResizeCrop(DragDeltaEventArgs e, bool left, bool top, bool right, bool bottom)
    {
        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
        const double min = 24;

        var x1 = _cropRect.Left;
        var y1 = _cropRect.Top;
        var x2 = _cropRect.Right;
        var y2 = _cropRect.Bottom;

        if (left) x1 = Math.Clamp(x1 + e.HorizontalChange, 0, x2 - min);
        if (top) y1 = Math.Clamp(y1 + e.VerticalChange, 0, y2 - min);
        if (right) x2 = Math.Clamp(x2 + e.HorizontalChange, x1 + min, w);
        if (bottom) y2 = Math.Clamp(y2 + e.VerticalChange, y1 + min, h);

        _cropRect = new Rect(x1, y1, x2 - x1, y2 - y1);
        LayoutCrop();
    }

    // ================= 工具：文字 / 水印 =================
    //
    // 这里只有「一个」文字编辑会话：同一时刻画布上最多存在一个可编辑的文本框。
    // 之前「添加文字」是无条件 new 一个 TextOverlayItem 并塞进文档，而且所有新框都用
    // 同一个固定坐标，于是连续点几下就会在同一个位置叠出一堆文本框，
    // 保存时全部参与绘制，坐标互相重叠、后续很难再单独选中其中一段。
    // 现在：
    //   1) 有正在编辑的框 → 只把焦点交还给它（单例），绝不再新建；
    //   2) 想再加一段 → 先「完成」当前编辑（面板上的按钮 / Esc / 点画布空白）；
    //   3) 新框按序号阶梯错位，不会再完全重合。

    private void OnTextToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (IsTextEditing)
        {
            // 单例：已经有正在编辑的文本框，再点「文字」只是把焦点要回来
            TextTool.IsChecked = true;
            FocusActiveTextEditor();
            ShowToast("已有正在编辑的文字，直接输入即可；点「✓ 完成」后可以再加一段");
            return;
        }

        if (TextTool.IsChecked == true) AddTextItem(isWatermark: false);
        else
        {
            // 取消高亮 = 结束会话（此时已没有编辑中的框，只是收起面板）
            EndTextEditing();
            ShowPanel(null);
        }
    }

    private void OnWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        if (IsTextEditing)
        {
            // 水印也是文字，同样受单例约束：不新建、不覆盖，只把焦点交回现有文本框
            TextTool.IsChecked = true;
            FocusActiveTextEditor();
            ShowToast("请先点「✓ 完成」结束当前文字编辑，再添加水印");
            return;
        }

        AddTextItem(isWatermark: true);
    }

    /// <summary>「✓ 完成」按钮：结束当前文字编辑，之后才允许新增下一段。</summary>
    private void OnFinishTextClick(object sender, RoutedEventArgs e)
    {
        EndTextEditing();
        ShowPanel(null);
        ShowToast("已完成这段文字，可以再点「文字」添加新的一段");
    }

    private void AddTextItem(bool isWatermark)
    {
        if (_doc == null) return;

        // 双保险：任何路径进到这里，只要还有编辑中的框就绝不新建
        if (IsTextEditing)
        {
            FocusActiveTextEditor();
            return;
        }

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
        };

        _doc.Mutate(isWatermark ? "添加水印" : "添加文字", () => _doc.Texts.Add(item));
        RebuildOverlays();
        SelectText(item, selectAll: true);
        ShowToast(isWatermark ? "输入水印文字，拖「✥ 拖动」调整位置" : "直接输入文字，拖「✥ 拖动」调整位置");
    }

    private void SelectText(TextOverlayItem item, bool selectAll = false)
    {
        if (_doc == null) return;

        // 同一个文字项重复「选中」→ 只把焦点交回去，绝不重新进入编辑会话
        if (IsTextEditing && ReferenceEquals(_activeText, item))
        {
            FocusActiveTextEditor();
            return;
        }

        // 切换前先结束上一段文字的编辑（会把修改写进撤销栈、清掉空文本）
        EndTextEditing(rebuild: false);

        _activeText = item;
        _editingText = true;
        _textDirty = false;
        _textEditSnapshot = EditorSnapshot.Capture(_doc);

        ShowPanel(TextPanel);

        // 文字工具按钮的高亮 = 「正在编辑文字」，统一在这里点亮，避免两处各改一半导致状态不一致
        TextTool.IsChecked = true;

        UpdateTextPanelFromItem(item);
        // 必须强制重建文字层：否则编辑框（TextBox）永远不会被创建
        RebuildOverlays(forceText: true);

        FocusActiveTextEditor(selectAll);
    }

    /// <summary>把键盘焦点交还给正在编辑的文本框（单例会话的「再次点击」行为）。</summary>
    private void FocusActiveTextEditor(bool selectAll = false)
    {
        if (_doc == null || _activeText == null) return;

        ShowPanel(TextPanel);

        // 编辑框可能被强制重建掉了（改样式/改颜色），补建一个
        if (_activeTextBox == null) RebuildOverlays(forceText: true);
        if (_activeTextBox == null) return;

        _activeTextBox.Focus();
        Keyboard.Focus(_activeTextBox);

        if (selectAll) _activeTextBox.SelectAll();
        else _activeTextBox.CaretIndex = _activeTextBox.Text.Length;
    }

    /// <summary>
    /// 结束文字编辑并写入撤销栈（一次编辑会话 = 一条可撤销记录）。
    /// 同时负责「收尾清理」：内容被清空的文字项直接丢弃，
    /// 否则画布上会留下看不见的文本框，保存时也会被当成有效文字参与绘制。
    /// </summary>
    private void EndTextEditing(bool rebuild = true)
    {
        if (!_editingText && _activeText == null) return;

        var item = _activeText;
        _editingText = false;

        // 内容为空 → 这个文字块没有存在意义，直接移除（此时仍在会话快照内，撤销可回退）
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

        // ★ 关键：会话结束时必须把引用全部清干净。
        // 之前只清了 TextBox，_activeText 一直指向上一次的文字块，
        // 下一次点「添加文字」时状态判断就会错乱，继而在同一坐标叠出新的文本框。
        _activeText = null;
        _activeTextBox = null;
        _activeTextHost = null;
        TextTool.IsChecked = false;

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
            FontSizeText.Text = ((int)FontSizeSlider.Value).ToString();
            BoldToggle.IsChecked = item.Bold;
            ItalicToggle.IsChecked = item.Italic;
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
        FontSizeText.Text = ((int)FontSizeSlider.Value).ToString();
        _textDirty = true;
        MeasureText(_activeText, _activeTextBox);
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

    private void OnDeleteTextClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _activeText == null) return;

        var target = _activeText;

        // 先正常结束会话：把本次改动写进撤销栈，并把内容为空的文字块清掉
        EndTextEditing(rebuild: false);

        if (_doc.Texts.Contains(target))
            _doc.Mutate("删除文字", () => _doc.Texts.Remove(target));

        ShowPanel(null);
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

    // ================= 工具：涂鸦 =================

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
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;

        _mode = ToolMode.Ink;
        InkTool.IsChecked = true;
        InkLayer.IsHitTestVisible = true;
        InkLayer.EditingMode = InkCanvasEditingMode.Ink;
        ShowPanel(InkPanel);
        ToolHint.Text = "涂鸦：按住鼠标画线；可换颜色/粗细，Ctrl+Z 撤销";
        RebuildInk(ImageBox.ActualWidth, ImageBox.ActualHeight);
    }

    private void ExitInkMode()
    {
        _mode = ToolMode.None;
        InkTool.IsChecked = false;
        InkLayer.IsHitTestVisible = false;
        InkLayer.EditingMode = InkCanvasEditingMode.None;
        ShowPanel(null);
        ToolHint.Text = "提示：右侧选择工具，Ctrl+Z 撤销，按住 \\ 看原图";
    }

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_doc == null || _mode != ToolMode.Ink) return;

        var w = ImageBox.ActualWidth;
        var h = ImageBox.ActualHeight;
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
        _doc.Mutate("涂鸦", () => _doc.Strokes.Add(model));
        _suppressOverlayRebuild = false;
        RebuildInk(w, h);
        UpdateHistoryButtons();
    }

    private void OnUndoStrokeClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (_doc.Strokes.Count == 0)
        {
            ShowToast("没有可撤销的笔画");
            return;
        }

        _doc.Mutate("撤销一笔", () => _doc.Strokes.RemoveAt(_doc.Strokes.Count - 1));
        ShowToast("已撤销上一笔");
    }

    private void OnInkWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _inkWidthDisplay = InkWidthSlider.Value;
        if (InkWidthText != null) InkWidthText.Text = ((int)e.NewValue).ToString();

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
        InkLayer.DefaultDrawingAttributes.Color = ParseColor(hex);
    }

    // ================= 工具：马赛克 =================

    private void OnMosaicToolClick(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (MosaicTool.IsChecked == true) EnterMosaicMode();
        else ExitMosaicMode();
    }

    private void EnterMosaicMode()
    {
        EndTextEditing();
        EndOtherModes(except: ToolMode.Mosaic);
        ShowPanel(null);   // 马赛克没有属性面板，全部收起
        ApplyZoom(1.0);
        PanTranslate.X = 0;
        PanTranslate.Y = 0;

        _mode = ToolMode.Mosaic;
        MosaicTool.IsChecked = true;
        ToolHint.Text = "马赛克：在画面上拖出一个矩形即可打码（可撤销）";
    }

    private void ExitMosaicMode()
    {
        _mode = ToolMode.None;
        MosaicTool.IsChecked = false;
        HideMosaicPreview();
        ToolHint.Text = "提示：右侧选择工具，Ctrl+Z 撤销，按住 \\ 看原图";
    }

    private void HideMosaicPreview()
    {
        if (_mosaicPreview != null)
        {
            MosaicLayer.Children.Remove(_mosaicPreview);
            _mosaicPreview = null;
        }
    }

    /// <summary>退出其它模式（除 except 指定的模式），保证同一时间只有一种工具生效。</summary>
    private void EndOtherModes(ToolMode except)
    {
        // 文字不是常驻模式（点一次加一个文字块），切换任何工具都要取消它的高亮
        if (TextTool.IsChecked == true) TextTool.IsChecked = false;

        if (except != ToolMode.Crop && _mode == ToolMode.Crop) ExitCropMode();
        if (except != ToolMode.Ink && _mode == ToolMode.Ink) ExitInkMode();
        if (except != ToolMode.Mosaic && _mode == ToolMode.Mosaic) ExitMosaicMode();
    }

    // ================= 鼠标 / 缩放 / 平移 =================

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode is ToolMode.Crop or ToolMode.Ink or ToolMode.Mosaic) return;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        ApplyZoom(_viewModel.Zoom * factor);
    }

    /// <summary>
    /// 缩放基于「适应窗口」的倍数：1.0 = 正好铺满可视区。
    /// 下限放到 0.15：用户明确要求能缩到比适应窗口更小（对比细节、看整张排版）。
    /// 上限 4.0 保持不变。缩到 1.0 以下时图像仍居中，不会偏到角落。
    /// </summary>
    private const double MinZoom = 0.15;
    private const double MaxZoom = 4.0;

    private void ApplyZoom(double zoom)
    {
        var previous = _viewModel.Zoom;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _viewModel.Zoom = zoom;
        ZoomScale.ScaleX = zoom;
        ZoomScale.ScaleY = zoom;

        if (ZoomText != null)
            ZoomText.Text = zoom <= 1.001 && zoom >= 0.999 ? "适应窗口" : $"{zoom * 100:F0}%";

        // 回到「适应窗口」以上时把平移归零，避免「放大再缩回来，图却歪在一边」
        if (zoom >= 0.999 && previous < 0.999)
        {
            PanTranslate.X = 0;
            PanTranslate.Y = 0;
        }

        // 缩小到 1.0 以下时同样归零平移：否则会看到图片偏在一侧，像加载错了
        if (zoom < 1.0)
        {
            PanTranslate.X = 0;
            PanTranslate.Y = 0;
        }
    }

    /// <summary>翻页按钮的「热区」宽度：鼠标进入距左右边缘这么近的范围才淡入。</summary>
    private const double NavHotZone = 110;

    /// <summary>
    /// 翻页按钮按需显隐：常驻会挡住画面（尤其是窄图两侧留白处）。
    /// 只做透明度动画（纯合成，不触发重排），120ms 淡入淡出。
    /// </summary>
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

    /// <summary>应用启动/切换图片后重置翻页按钮，避免上一张的可见状态带过来。</summary>
    private void ResetNavArrows()
    {
        PrevButton?.BeginAnimation(OpacityProperty, null);
        NextButton?.BeginAnimation(OpacityProperty, null);
        if (PrevButton != null) PrevButton.Opacity = 0;
        if (NextButton != null) NextButton.Opacity = 0;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击画面 = 回到「适应窗口」。用户缩放跑偏之后最想要的就是这个手势，
        // 之前只能去底部状态栏找小按钮，很多人根本找不到。
        if (e.ClickCount == 2 && _mode == ToolMode.None)
        {
            FitToWindow();
            return;
        }

        // 文字编辑中点到画布空白处 → 结束编辑（点到文字本身则继续编辑）
        if (IsTextEditing)
        {
            var src = e.OriginalSource as DependencyObject;
            if (!IsInTree(src, TextLayer))
            {
                EndTextEditing();
                return;
            }
        }

        if (_mode == ToolMode.Crop) return;   // 交给裁剪手柄
        if (_mode == ToolMode.Mosaic)
        {
            _mosaicStart = e.GetPosition(ImageBox);
            _mosaicPreview = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x90, 0xD9)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_mosaicPreview, _mosaicStart.X);
            Canvas.SetTop(_mosaicPreview, _mosaicStart.Y);
            _mosaicPreview.Width = 0;
            _mosaicPreview.Height = 0;
            MosaicLayer.Children.Add(_mosaicPreview);
            ImageArea.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_mode == ToolMode.Ink) return;    // InkCanvas 自己处理

        _isDragging = true;
        _lastPosition = e.GetPosition(this);
        ImageArea.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        UpdateNavArrows();
        if (_mode == ToolMode.Mosaic && _mosaicPreview != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(ImageBox);
            var x = Math.Min(p.X, _mosaicStart.X);
            var y = Math.Min(p.Y, _mosaicStart.Y);
            Canvas.SetLeft(_mosaicPreview, x);
            Canvas.SetTop(_mosaicPreview, y);
            _mosaicPreview.Width = Math.Abs(p.X - _mosaicStart.X);
            _mosaicPreview.Height = Math.Abs(p.Y - _mosaicStart.Y);
            return;
        }

        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        PanTranslate.X += pos.X - _lastPosition.X;
        PanTranslate.Y += pos.Y - _lastPosition.Y;
        _lastPosition = pos;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == ToolMode.Mosaic && _mosaicPreview != null)
        {
            var w = ImageBox.ActualWidth;
            var h = ImageBox.ActualHeight;
            var rect = new Rect(
                Canvas.GetLeft(_mosaicPreview), Canvas.GetTop(_mosaicPreview),
                _mosaicPreview.Width, _mosaicPreview.Height);
            HideMosaicPreview();
            ImageArea.ReleaseMouseCapture();

            if (rect.Width > 6 && rect.Height > 6 && w > 0 && h > 0 && _doc != null)
            {
                var model = new MosaicOverlayItem
                {
                    X = rect.X / w,
                    Y = rect.Y / h,
                    W = rect.Width / w,
                    H = rect.Height / h,
                };
                _doc.Mutate("马赛克", () => _doc.Mosaics.Add(model));
                ShowToast("已添加马赛克（可撤销）");
            }
            return;
        }

        _isDragging = false;
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
        if (!IsTextEditing && (e.Key == Key.OemBackslash || e.Key == Key.Oem5))
        {
            BeginCompare();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (IsTextEditing) { EndTextEditing(); e.Handled = true; return; }
                if (_mode == ToolMode.Crop) { ExitCropMode(); e.Handled = true; return; }
                if (_mode == ToolMode.Ink) { ExitInkMode(); e.Handled = true; return; }
                if (_mode == ToolMode.Mosaic) { ExitMosaicMode(); e.Handled = true; return; }
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DetachDocument();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom * 1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(_viewModel.Zoom / 1.25);

    private void OnFit(object sender, RoutedEventArgs e) => FitToWindow();

    /// <summary>恢复「适应窗口」：缩放 1.0 + 平移归零。</summary>
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
        {
            TextColorPanel.Children.Add(CreateSwatch(hex, name, 20, OnTextColorClick));
        }

        foreach (var (hex, name) in colors.Take(6))
        {
            InkColorPanel.Children.Add(CreateSwatch(hex, name, 22, OnInkColorClick));
        }
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
