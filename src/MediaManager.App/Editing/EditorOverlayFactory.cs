using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaManager.Core.Editing;

namespace MediaManager.App.Editing;

/// <summary>
/// 叠加层元素工厂：预览（ImageBox 上的画布层）和导出（RenderTargetBitmap）共用，
/// 保证「画面上看到什么」和「保存出来什么」完全一致。
/// 所有尺寸参数 w/h 都是当前渲染画布的像素尺寸，scale 是画布相对「可见图片」的缩放。
/// </summary>
public static class EditorOverlayFactory
{
    // ---------- 文字 ----------

    /// <summary>构建一段文字（含底色 / 描边），返回可放到 Canvas 上的元素。</summary>
    public static FrameworkElement BuildText(TextOverlayItem item, double w, double h, double scale)
    {
        var x = Math.Min(OverlayGeometry.ClampUnit(item.X), 0.999);
        var y = Math.Min(OverlayGeometry.ClampUnit(item.Y), 0.999);
        var fontPx = OverlayGeometry.ClampFontSize(item.FontSize * scale, (int)Math.Max(1, w), (int)Math.Max(1, h));
        var maxWidth = Math.Max(20, w - x * w);
        var family = new FontFamily(string.IsNullOrWhiteSpace(item.FontFamily) ? "Microsoft YaHei" : item.FontFamily);
        var brush = ParseBrush(item.ColorHex);
        var weight = item.Bold ? FontWeights.Bold : FontWeights.Normal;
        var style = item.Italic ? FontStyles.Italic : FontStyles.Normal;

        // 统一用 FormattedText 转几何来画：有描边 / 没描边都走同一条路径，
        // 这样开关描边时文字不会「跳一下」，换行点也完全一致。
        var ft = new FormattedText(
            item.Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, style, weight, FontStretches.Normal),
            fontPx,
            brush,
            1.0)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = Math.Max(20, h - y * h),
        };

        var hasOutline = !string.IsNullOrWhiteSpace(item.OutlineHex);
        var geometry = ft.BuildGeometry(new Point(0, 0));

        FrameworkElement content = new Path
        {
            Data = geometry,
            Fill = brush,
            Stroke = hasOutline ? ParseBrush(item.OutlineHex ?? "#FF000000") : null,
            StrokeThickness = hasOutline
                ? Math.Max(1, fontPx * Math.Clamp(item.OutlineWidth, 0.02, 0.3))
                : 0,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        FrameworkElement visual = content;
        if (!string.IsNullOrWhiteSpace(item.BackgroundHex))
        {
            visual = new Border
            {
                Background = ParseBrush(item.BackgroundHex),
                CornerRadius = new CornerRadius(fontPx * 0.12),
                Padding = new Thickness(fontPx * 0.18, fontPx * 0.06, fontPx * 0.18, fontPx * 0.06),
                Child = content,
                IsHitTestVisible = false,
            };
        }

        visual.Opacity = OverlayGeometry.ClampOpacity(item.Opacity);
        Canvas.SetLeft(visual, x * w);
        Canvas.SetTop(visual, y * h);

        // 水印斜放：绕元素中心旋转，坐标（左上角）保持不变
        if (double.IsFinite(item.Angle) && Math.Abs(item.Angle) > 0.01)
        {
            visual.RenderTransformOrigin = new Point(0.5, 0.5);
            visual.RenderTransform = new RotateTransform(item.Angle);
        }

        return visual;
    }

    // ---------- 涂鸦 ----------

    public static Polyline? BuildStroke(StrokeOverlayItem stroke, double w, double h)
    {
        if (stroke.Points.Count == 0) return null;

        var points = new PointCollection(stroke.Points.Count);
        foreach (var p in stroke.Points)
        {
            if (!OverlayGeometry.IsFinitePoint(p.X, p.Y)) continue;
            points.Add(new Point(OverlayGeometry.ClampUnit(p.X) * w, OverlayGeometry.ClampUnit(p.Y) * h));
        }

        if (points.Count == 0) return null;

        return new Polyline
        {
            Points = points,
            Stroke = ParseBrush(stroke.ColorHex),
            StrokeThickness = OverlayGeometry.ClampThickness(stroke.Thickness * w, (int)Math.Max(1, w)),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
    }

    // ---------- 形状标注 ----------

    /// <summary>矩形框 / 椭圆 / 箭头：两个对角点决定位置与大小。</summary>
    public static FrameworkElement? BuildShape(ShapeOverlayItem item, double w, double h)
    {
        if (!OverlayGeometry.IsFinitePoint(item.X1, item.Y1) ||
            !OverlayGeometry.IsFinitePoint(item.X2, item.Y2))
            return null;

        var x1 = OverlayGeometry.ClampUnit(item.X1) * w;
        var y1 = OverlayGeometry.ClampUnit(item.Y1) * h;
        var x2 = OverlayGeometry.ClampUnit(item.X2) * w;
        var y2 = OverlayGeometry.ClampUnit(item.Y2) * h;

        var thickness = OverlayGeometry.ClampThickness(item.Thickness * w, (int)Math.Max(1, w));
        var brush = ParseBrush(item.ColorHex);
        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Abs(x2 - x1);
        var height = Math.Abs(y2 - y1);

        switch (item.Kind)
        {
            case ShapeKind.Rect:
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(1, width),
                    Height = Math.Max(1, height),
                    Stroke = brush,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Miter,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
                return rect;
            }

            case ShapeKind.Ellipse:
            {
                var ellipse = new Ellipse
                {
                    Width = Math.Max(1, width),
                    Height = Math.Max(1, height),
                    Stroke = brush,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(ellipse, left);
                Canvas.SetTop(ellipse, top);
                return ellipse;
            }

            default:
                return BuildArrow(x1, y1, x2, y2, thickness, brush);
        }
    }

    private static Path? BuildArrow(double x1, double y1, double x2, double y2, double thickness, Brush brush)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 1) return null;

        var ux = dx / length;
        var uy = dy / length;

        // 箭头头部长宽随线宽走，但不超过箭杆本身
        var head = Math.Min(length, Math.Max(thickness * 3.5, Math.Min(length * 0.3, thickness * 9)));
        var half = head * 0.45;
        var baseX = x2 - ux * head;
        var baseY = y2 - uy * head;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x1, y1), false, false);
            ctx.LineTo(new Point(baseX, baseY), true, false);

            ctx.BeginFigure(new Point(x2, y2), true, true);
            ctx.LineTo(new Point(baseX - uy * half, baseY + ux * half), true, false);
            ctx.LineTo(new Point(baseX + uy * half, baseY - ux * half), true, false);
        }
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Stroke = brush,
            Fill = brush,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
    }

    // ---------- 矩形马赛克 ----------

    public static Image? BuildMosaicRect(
        MosaicOverlayItem mosaic, BitmapSource baseBitmap, double w, double h)
    {
        if (!OverlayGeometry.TryToPixelRect(mosaic.X, mosaic.Y, mosaic.W, mosaic.H,
                (int)Math.Max(1, w), (int)Math.Max(1, h), out var dx, out var dy, out var dw, out var dh))
            return null;

        int bw = baseBitmap.PixelWidth;
        int bh = baseBitmap.PixelHeight;
        if (bw <= 0 || bh <= 0) return null;

        var left = Math.Clamp((double)dx / w, 0, 1);
        var top = Math.Clamp((double)dy / h, 0, 1);
        var right = Math.Clamp((double)(dx + dw) / w, 0, 1);
        var bottom = Math.Clamp((double)(dy + dh) / h, 0, 1);

        var sx = Math.Clamp((int)Math.Floor(left * bw), 0, Math.Max(0, bw - 1));
        var sy = Math.Clamp((int)Math.Floor(top * bh), 0, Math.Max(0, bh - 1));
        var sw = Math.Clamp((int)Math.Ceiling((right - left) * bw), 1, bw - sx);
        var sh = Math.Clamp((int)Math.Ceiling((bottom - top) * bh), 1, bh - sy);
        if (sw <= 0 || sh <= 0) return null;

        try
        {
            var cropped = new CroppedBitmap(baseBitmap, new Int32Rect(sx, sy, sw, sh));
            // 颗粒大小由 BlockRatio 决定（占画布宽度的比例），画布越大方块越大
            var factor = Math.Max(2.0, w * NormalizeBlockRatio(mosaic.BlockRatio));
            var tiny = new TransformedBitmap(cropped, new ScaleTransform(1.0 / factor, 1.0 / factor));
            tiny.Freeze();

            var img = new Image
            {
                Source = tiny,
                Width = dw,
                Height = dh,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, dx);
            Canvas.SetTop(img, dy);
            return img;
        }
        catch
        {
            // 单块失败不影响整张图：跳过继续渲染其它图层
            return null;
        }
    }

    // ---------- 画笔式打码（马赛克 / 模糊） ----------

    /// <summary>把同一模式的涂抹轨迹合并成一个集合区域（一次绘制，避免逐笔堆元素）。</summary>
    public static Geometry? BuildPrivacyGeometry(
        IEnumerable<PrivacyOverlayItem> items, PrivacyMode mode, double w, double h)
    {
        GeometryGroup? group = null;
        foreach (var item in items)
        {
            if (item.Mode != mode || item.Points.Count == 0) continue;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var first = true;
                foreach (var p in item.Points)
                {
                    if (!OverlayGeometry.IsFinitePoint(p.X, p.Y)) continue;
                    var point = new Point(OverlayGeometry.ClampUnit(p.X) * w, OverlayGeometry.ClampUnit(p.Y) * h);
                    if (first) { ctx.BeginFigure(point, false, false); first = false; }
                    else ctx.LineTo(point, true, false);
                }
            }
            geometry.Freeze();

            var width = Math.Clamp(item.Thickness * w, 2, Math.Max(2, w / 2));
            var pen = new Pen(Brushes.Black, width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();

            var widened = geometry.GetWidenedPathGeometry(pen);
            if (widened.Bounds.IsEmpty) continue;

            group ??= new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(widened);
        }

        return group;
    }

    /// <summary>生成打码图层：用底图的马赛克/模糊版本 + 轨迹裁剪。</summary>
    /// <remarks>
    /// 每条笔迹可以有自己的颗粒大小，按颗粒分组各画一层：
    /// 同一层内共用一张马赛克底图，既保证颗粒可调，又不会退化成「一笔一个元素」。
    /// </remarks>
    public static FrameworkElement? BuildPrivacyLayer(
        IReadOnlyList<PrivacyOverlayItem> items, PrivacyMode mode,
        BitmapSource baseBitmap, double w, double h)
    {
        var groups = items
            .Where(i => i.Mode == mode && i.Points.Count > 0)
            .GroupBy(i => Math.Round(NormalizeBlockRatio(i.BlockRatio), 4))
            .ToList();
        if (groups.Count == 0) return null;

        var layers = new List<FrameworkElement>(groups.Count);
        foreach (var group in groups)
        {
            var geometry = BuildPrivacyGeometry(group, mode, w, h);
            if (geometry == null) continue;

            var blockRatio = group.Key;
            // 预览阶段（画布尺寸 = 屏幕尺寸）才预模糊并缓存；导出大图直接交给渲染管线，
            // 否则一张 4000×3000 的模糊位图就是 48MB，得不偿失。
            var useCachedBlur = mode == PrivacyMode.Blur && (long)w * (long)h <= 4_000_000;

            var img = new Image
            {
                Source = mode switch
                {
                    PrivacyMode.Mosaic => BuildMosaicSource(baseBitmap, w, blockRatio),
                    _ when useCachedBlur => BuildBlurSource(baseBitmap, w, h, blockRatio),
                    _ => baseBitmap,
                },
                Width = w,
                Height = h,
                Stretch = Stretch.Fill,
                Clip = geometry,
                IsHitTestVisible = false,
            };

            if (mode == PrivacyMode.Mosaic)
            {
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            }
            else if (!useCachedBlur)
            {
                img.Effect = new BlurEffect
                {
                    Radius = Math.Clamp(w * blockRatio, 3, 120),
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance,
                };
            }

            layers.Add(img);
        }

        if (layers.Count == 0) return null;
        if (layers.Count == 1) return layers[0];

        var canvas = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        foreach (var layer in layers) canvas.Children.Add(layer);
        return canvas;
    }

    /// <summary>颗粒比例的合法范围（相对画布宽度）。</summary>
    public static double NormalizeBlockRatio(double ratio)
        => double.IsFinite(ratio) ? Math.Clamp(ratio, 1.0 / 500, 1.0 / 8) : 1.0 / 60;

    /// <summary>
    /// 打码取样图缓存。键 = 「底图实例 + 画布宽度 + 颗粒比例」：
    /// 涂抹时每移动几个像素就会重建一次叠加层，不缓存的话每次都要把整张大图重新降采样一遍，
    /// 大图上必然掉帧。<see cref="ConditionalWeakTable{TKey,TValue}"/> 以底图实例为键，
    /// 底图一换（调色/裁剪/翻页）旧缓存自动失效，不会泄漏。
    /// </summary>
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<(int W, int Ratio), BitmapSource>>
        MosaicCache = new();

    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<(int W, int H, int Radius), BitmapSource>>
        BlurCache = new();

    /// <summary>单个底图最多缓存几张取样图（超过就整表清空，防止调参数时无限增长）。</summary>
    private const int MaxCachedSources = 6;

    private static BitmapSource BuildMosaicSource(
        BitmapSource baseBitmap, double canvasWidth, double blockRatio)
    {
        var key = ((int)Math.Round(canvasWidth), (int)Math.Round(NormalizeBlockRatio(blockRatio) * 10000));
        var cache = MosaicCache.GetValue(baseBitmap, _ => new Dictionary<(int W, int Ratio), BitmapSource>());

        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        // 颗粒 = 画布宽度 × BlockRatio，画面越大方块越大，保证「视觉上粗细一致」
        var factor = Math.Max(2.0, canvasWidth * NormalizeBlockRatio(blockRatio));
        BitmapSource result;
        try
        {
            var tiny = new TransformedBitmap(baseBitmap,
                new ScaleTransform(1.0 / factor, 1.0 / factor));
            tiny.Freeze();
            // 立刻解成一张真正的小位图：TransformedBitmap 是惰性的，留着它等于每帧都重新采样大图
            result = Materialize(tiny);
        }
        catch
        {
            result = baseBitmap;
        }

        lock (cache)
        {
            if (cache.Count >= MaxCachedSources) cache.Clear();
            cache[key] = result;
        }
        return result;
    }

    /// <summary>
    /// 模糊取样图：整张画布预模糊一次并缓存。
    /// 原来是给图层挂 <see cref="BlurEffect"/>，半径最大 120 —— 每一帧都要对整块画布做高斯模糊，
    /// 拖涂抹时直接掉到个位数帧率。缓存后一条笔迹只模糊一次，后续只是贴图。
    /// </summary>
    private static BitmapSource BuildBlurSource(
        BitmapSource baseBitmap, double w, double h, double blockRatio)
    {
        if (w < 1 || h < 1) return baseBitmap;

        var radius = Math.Clamp(w * blockRatio, 3, 120);
        var key = ((int)Math.Round(w), (int)Math.Round(h), (int)Math.Round(radius));
        var cache = BlurCache.GetValue(baseBitmap, _ => new Dictionary<(int, int, int), BitmapSource>());

        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        BitmapSource result;
        try
        {
            var visual = new DrawingVisual
            {
                Effect = new BlurEffect
                {
                    Radius = radius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance,
                },
            };

            using (var dc = visual.RenderOpen())
                dc.DrawImage(baseBitmap, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(
                Math.Max(1, key.Item1), Math.Max(1, key.Item2), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            result = rtb;
        }
        catch
        {
            result = baseBitmap;
        }

        lock (cache)
        {
            if (cache.Count >= 4) cache.Clear();   // 每张都是整块画布大小，缓存要收紧
            cache[key] = result;
        }
        return result;
    }

    /// <summary>把惰性位图真正光栅化成独立小位图并冻结，之后重绘只是贴图。</summary>
    private static BitmapSource Materialize(BitmapSource source)
    {
        var pw = source.PixelWidth;
        var ph = source.PixelHeight;
        if (pw <= 0 || ph <= 0) return source;
        // 大到位图本身（说明没降采样成功）就不折腾了
        if ((long)pw * ph > 32_000_000) return source;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, pw, ph));

        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ---------- 平铺文字水印 ----------

    /// <summary>
    /// 平铺文字水印：用一块「文字 + 间距」的图案平铺成整块画布，再整体旋转。
    /// 用 DrawingBrush 平铺而不是逐个摆放文字，图案再密也只有 1 个元素。
    /// </summary>
    public static FrameworkElement? BuildTileWatermark(TileWatermarkItem item, double w, double h)
    {
        if (string.IsNullOrWhiteSpace(item.Text) || w <= 1 || h <= 1) return null;

        var fontPx = Math.Clamp(item.FontSizeRatio * w, 6, w * 0.4);
        var family = new FontFamily(string.IsNullOrWhiteSpace(item.FontFamily) ? "Microsoft YaHei" : item.FontFamily);
        var brush = ParseBrush(item.ColorHex);
        var weight = item.Bold ? FontWeights.Bold : FontWeights.Normal;

        var ft = new FormattedText(
            item.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            fontPx,
            brush,
            1.0);

        var textW = Math.Max(1, ft.Width);
        var textH = Math.Max(1, ft.Height);
        var gap = Math.Clamp(item.GapRatio, 0.2, 6);
        var stepX = textW + fontPx * gap;
        var stepY = textH + fontPx * gap;

        var geometry = ft.BuildGeometry(new Point((stepX - textW) / 2, (stepY - textH) / 2));
        geometry.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(brush, null, geometry));
        drawing.Freeze();

        var tileBrush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, stepX, stepY),
            Viewbox = new Rect(0, 0, stepX, stepY),
        };
        tileBrush.Freeze();

        // 旋转后的正方形只要边长 = 画布对角线，就能盖住整张画布
        var diag = Math.Sqrt(w * w + h * h);
        var fill = new Rectangle
        {
            Width = diag,
            Height = diag,
            Fill = tileBrush,
            IsHitTestVisible = false,
            Opacity = OverlayGeometry.ClampOpacity(item.Opacity),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(double.IsFinite(item.Angle) ? item.Angle : 0),
        };
        Canvas.SetLeft(fill, (w - diag) / 2);
        Canvas.SetTop(fill, (h - diag) / 2);

        var host = new Canvas
        {
            Width = w,
            Height = h,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };
        host.Children.Add(fill);
        return host;
    }

    // ---------- 图片水印 ----------

    public static Image BuildWatermark(ImageWatermarkItem item, double w, double h)
    {
        var width = Math.Clamp(OverlayGeometry.ClampUnit(item.W), 0.01, 1) * w;
        var height = Math.Clamp(OverlayGeometry.ClampUnit(item.H), 0.01, 1) * h;

        var img = new Image
        {
            Source = item.Image,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Opacity = OverlayGeometry.ClampOpacity(item.Opacity),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(img, Math.Clamp(item.X, 0, 1) * w);
        Canvas.SetTop(img, Math.Clamp(item.Y, 0, 1) * h);
        return img;
    }

    // ---------- 小工具 ----------

    public static Brush ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.White;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
