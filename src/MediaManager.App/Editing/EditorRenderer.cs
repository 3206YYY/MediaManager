using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaManager.Core.Editing;
using IOPath = System.IO.Path;

namespace MediaManager.App.Editing;

/// <summary>一次保存的结果（供界面做明确反馈）。</summary>
public sealed record SaveResult(string Path, string FileName, int Width, int Height, long SizeBytes);

/// <summary>
/// 把编辑文档渲染成最终图片：
/// 预览用同一套状态实时拼装，保存时用 RenderTargetBitmap 一次性导出，
/// 再交给 ImageSharp 写文件（保持与原图相同的格式）。
/// </summary>
public static class EditorRenderer
{
    /// <summary>按编辑状态渲染出位图（可选限制长边，用于「压缩」）。</summary>
    public static BitmapSource Render(EditorDocument doc, int? maxLongEdge = null)
    {
        int visibleW = doc.VisibleWidth;
        int visibleH = doc.VisibleHeight;

        // 裁剪区域异常（宽高 <= 0）时绝不能继续：否则会渲染出一张 1px 图，
        // 用户看到的像是「保存出一块纯黑/纯白」。
        if (visibleW <= 0 || visibleH <= 0)
            throw new InvalidOperationException("图片可视区域尺寸无效（裁剪区域可能过小），无法生成副本。");

        double scale = 1.0;
        if (maxLongEdge is int max && max > 0 && Math.Max(visibleW, visibleH) > max)
            scale = (double)max / Math.Max(visibleW, visibleH);

        var (outW, outH) = OverlayGeometry.ClampOutputSize(
            Math.Max(1, (int)Math.Round(visibleW * scale)),
            Math.Max(1, (int)Math.Round(visibleH * scale)));

        var root = new Grid
        {
            Width = outW,
            Height = outH,
            Background = Brushes.Transparent,
            ClipToBounds = true,
        };

        // 1) 底图（已裁剪 + 旋转 + 黑白）
        root.Children.Add(new Image
        {
            Source = doc.GetBaseBitmap(),
            Stretch = Stretch.Fill,
            Width = outW,
            Height = outH,
        });

        // 2) 暖色 / 复古滤镜叠加
        if (doc.GetTint() is var (tintColor, tintOpacity))
        {
            root.Children.Add(new Rectangle
            {
                Fill = new SolidColorBrush(tintColor) { Opacity = OverlayGeometry.ClampOpacity(tintOpacity) },
                Width = outW,
                Height = outH,
            });
        }

        // 3) 马赛克
        foreach (var mosaic in doc.Mosaics)
        {
            var img = BuildMosaicImage(doc, mosaic, outW, outH);
            if (img != null) root.Children.Add(img);
        }

        // 4) 涂鸦 + 文字
        var overlay = new Canvas { Width = outW, Height = outH };
        root.Children.Add(overlay);

        foreach (var stroke in doc.Strokes)
        {
            var line = BuildStroke(stroke, outW, outH);
            if (line != null) overlay.Children.Add(line);
        }

        foreach (var text in doc.Texts)
        {
            // 只绘制「有效」文字：空内容 / 坐标非法的一律跳过。
            // 文本会话被反复叠加时会在文档里留下若干空壳文字块，它们没有内容，
            // 却仍会参与布局；一旦坐标被裁剪/旋转重映射过，就会在导出图上留下错位残影。
            if (!OverlayTextRules.IsRenderable(text.Text, text.X, text.Y))
                continue;

            overlay.Children.Add(BuildText(text, outW, outH));
        }

        root.Measure(new Size(outW, outH));
        root.Arrange(new Rect(0, 0, outW, outH));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>渲染并写入编辑副本（原名_edited.扩展名），返回保存结果。</summary>
    /// <remarks>
    /// 写盘策略：先写同目录的临时文件，校验大小无误后再原子替换目标文件。
    /// 这样磁盘满 / 无权限 / 中途失败时既不会留下半张图，也不会破坏上一次的副本。
    /// 任何异常都会向上抛出，由界面明确告知用户，绝不静默失败。
    /// </remarks>
    public static async Task<SaveResult> SaveAsync(EditorDocument doc, int? maxLongEdge = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        // 渲染必须在 UI 线程完成（WPF 可视化对象有线程亲和性），耗时在几十毫秒级
        var bitmap = Render(doc, maxLongEdge);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("生成的图片尺寸为 0，无法保存副本。");

        var output = BuildOutputPath(doc.SourcePath);
        var dir = IOPath.GetDirectoryName(output);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"目标文件夹不存在或已断开：{dir}");

        // 登记一下：这是我们自己写出去的文件，别让文件监控当成"用户拷进来的新文件"弹提示
        MediaManager.Infrastructure.FileSystem.SelfWriteTracker.Register(output);

        // JPEG / BMP 不支持透明通道，先铺白底再编码，避免出现黑块
        if (!SupportsAlpha(output))
            bitmap = Flatten(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        var pngBytes = memory.ToArray();

        await Task.Run(() =>
        {
            var temp = IOPath.Combine(dir, $".{IOPath.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new MemoryStream(pngBytes, writable: false))
                using (var image = SixLabors.ImageSharp.Image.Load(stream))
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    image.Save(file, EncoderFor(output));
                    file.Flush(true);   // 落盘后再做原子替换，防止断电留下空壳
                }

                var tempInfo = new FileInfo(temp);
                if (!tempInfo.Exists || tempInfo.Length == 0)
                    throw new IOException("写入文件失败：生成的文件为空（可能磁盘空间不足）。");

                File.Move(temp, output, overwrite: true);

                var finalInfo = new FileInfo(output);
                if (!finalInfo.Exists || finalInfo.Length == 0)
                    throw new IOException("写入文件失败：目标文件为空（可能磁盘空间不足）。");
            }
            finally
            {
                TryDelete(temp);
            }
        });

        var info = new FileInfo(output);
        return new SaveResult(
            output,
            IOPath.GetFileName(output),
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            info.Exists ? info.Length : 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不影响保存结果，忽略
        }
    }

    private static bool SupportsAlpha(string path)
    {
        var ext = IOPath.GetExtension(path).ToLowerInvariant();
        return ext is not (".jpg" or ".jpeg" or ".bmp");
    }

    private static BitmapSource Flatten(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var rtb = new RenderTargetBitmap(
            source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static SixLabors.ImageSharp.Formats.IImageEncoder EncoderFor(string path)
    {
        return IOPath.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 },
            ".webp" => new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = 92 },
            ".bmp" => new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(),
            ".gif" => new SixLabors.ImageSharp.Formats.Gif.GifEncoder(),
            ".tif" or ".tiff" => new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder(),
            _ => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
        };
    }

    /// <summary>生成「原名_edited.扩展名」路径；已带后缀时先去掉，避免无限套娃。</summary>
    public static string BuildOutputPath(string input)
    {
        var dir = IOPath.GetDirectoryName(input) ?? ".";
        var name = IOPath.GetFileNameWithoutExtension(input);
        const string suffix = "_edited";

        while (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^suffix.Length];

        return IOPath.Combine(dir, $"{name}{suffix}{IOPath.GetExtension(input)}");
    }

    // ---------- 元素构建 ----------

    private static Image? BuildMosaicImage(EditorDocument doc, MosaicOverlayItem mosaic, int outW, int outH)
    {
        // 目标区先钳制到画布内（越界部分直接裁掉，而不是让 CroppedBitmap 抛异常后整块马赛克消失）
        if (!OverlayGeometry.TryToPixelRect(mosaic.X, mosaic.Y, mosaic.W, mosaic.H,
                outW, outH, out var dx, out var dy, out var dw, out var dh))
            return null;

        var baseBmp = doc.GetBaseBitmap();
        int bw = baseBmp.PixelWidth;
        int bh = baseBmp.PixelHeight;
        if (bw <= 0 || bh <= 0) return null;

        // 目标像素区 → 归一化 → 底图像素区（导出缩放时两者尺寸可能不同）
        var left = Math.Clamp((double)dx / outW, 0, 1);
        var top = Math.Clamp((double)dy / outH, 0, 1);
        var right = Math.Clamp((double)(dx + dw) / outW, 0, 1);
        var bottom = Math.Clamp((double)(dy + dh) / outH, 0, 1);

        var sx = Math.Clamp((int)Math.Floor(left * bw), 0, Math.Max(0, bw - 1));
        var sy = Math.Clamp((int)Math.Floor(top * bh), 0, Math.Max(0, bh - 1));
        var sw = Math.Clamp((int)Math.Ceiling((right - left) * bw), 1, bw - sx);
        var sh = Math.Clamp((int)Math.Ceiling((bottom - top) * bh), 1, bh - sy);
        if (sw <= 0 || sh <= 0) return null;

        var srcRect = new Int32Rect(sx, sy, sw, sh);

        try
        {
            var cropped = new CroppedBitmap(baseBmp, srcRect);

            // 先缩到很小，再用「最近邻」放大 → 形成马赛克方块
            var factor = Math.Max(2.0, dw / 16.0);
            var tiny = new TransformedBitmap(cropped, new ScaleTransform(1.0 / factor, 1.0 / factor));
            tiny.Freeze();

            var img = new Image
            {
                Source = tiny,
                Width = dw,
                Height = dh,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, dx);
            Canvas.SetTop(img, dy);
            return img;
        }
        catch
        {
            // 单块马赛克失败不应该让整次保存失败：跳过它继续渲染其它层
            return null;
        }
    }

    private static Polyline? BuildStroke(StrokeOverlayItem stroke, int outW, int outH)
    {
        if (stroke.Points.Count == 0) return null;

        var points = new PointCollection(stroke.Points.Count);
        foreach (var p in stroke.Points)
        {
            if (!OverlayGeometry.IsFinitePoint(p.X, p.Y)) continue;
            points.Add(new Point(OverlayGeometry.ClampUnit(p.X) * outW, OverlayGeometry.ClampUnit(p.Y) * outH));
        }

        if (points.Count == 0) return null;

        var brush = ParseBrush(stroke.ColorHex);
        return new Polyline
        {
            Points = points,
            Stroke = brush,
            // Thickness 是「占画布宽度的比例」，先换算成像素再钳制
            StrokeThickness = OverlayGeometry.ClampThickness(stroke.Thickness * outW, outW),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
    }

    private static TextBlock BuildText(TextOverlayItem text, int outW, int outH)
    {
        // 坐标再钳一次：绘制端也要绝对安全，不能只依赖上游校验
        var x = Math.Min(OverlayGeometry.ClampUnit(text.X), 0.999);
        var y = Math.Min(OverlayGeometry.ClampUnit(text.Y), 0.999);
        var fontPx = OverlayGeometry.ClampFontSize(text.FontSize, outW, outH);

        var block = new TextBlock
        {
            Text = text.Text,
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(text.FontFamily) ? "Microsoft YaHei" : text.FontFamily),
            FontSize = fontPx,
            Foreground = ParseBrush(text.ColorHex),
            Opacity = OverlayGeometry.ClampOpacity(text.Opacity),
            FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(10, outW - x * outW),
        };

        block.Measure(new Size(block.MaxWidth, outH));
        Canvas.SetLeft(block, x * outW);
        Canvas.SetTop(block, y * outH);
        return block;
    }

    private static Brush ParseBrush(string hex)
    {
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
