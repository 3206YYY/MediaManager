using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaManager.App.Editing;

namespace OverlayCheck;

/// <summary>
/// 打码渲染冒烟检查：确认改造后（取样图缓存 + 预模糊）画面仍然正常，
/// 并顺带量一下同一条笔迹第二次重建的耗时（缓存是否真的生效）。
/// </summary>
internal static class Program
{
    private const int W = 800;
    private const int H = 600;

    [STAThread]
    private static void Main()
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);

        var baseBmp = MakeBaseBitmap();
        SaveBitmap(outDir, "base.png", baseBmp);

        var mosaic = MakeStroke(PrivacyMode.Mosaic, 1.0 / 30);
        var blur = MakeStroke(PrivacyMode.Blur, 1.0 / 40);

        // 第一次（冷缓存）
        var sw = Stopwatch.StartNew();
        var mosaicLayer1 = EditorOverlayFactory.BuildPrivacyLayer(new[] { mosaic }, PrivacyMode.Mosaic, baseBmp, W, H);
        Save(outDir, "mosaic1.png", baseBmp, mosaicLayer1);
        var cold = sw.Elapsed.TotalMilliseconds;

        // 第二次（命中缓存）
        sw.Restart();
        var mosaicLayer2 = EditorOverlayFactory.BuildPrivacyLayer(new[] { mosaic }, PrivacyMode.Mosaic, baseBmp, W, H);
        Save(outDir, "mosaic2.png", baseBmp, mosaicLayer2);
        var warm = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var blurLayer = EditorOverlayFactory.BuildPrivacyLayer(new[] { blur }, PrivacyMode.Blur, baseBmp, W, H);
        Save(outDir, "blur1.png", baseBmp, blurLayer);
        var blurCold = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var blurLayer2 = EditorOverlayFactory.BuildPrivacyLayer(new[] { blur }, PrivacyMode.Blur, baseBmp, W, H);
        Save(outDir, "blur2.png", baseBmp, blurLayer2);
        var blurWarm = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"mosaic 冷 {cold:F1}ms / 热 {warm:F1}ms");
        Console.WriteLine($"blur   冷 {blurCold:F1}ms / 热 {blurWarm:F1}ms");

        // 与「改动前」的实现逐像素对照：确认缓存/预模糊没改变画面
        Save(outDir, "blur_legacy.png", baseBmp, BuildLegacyBlurLayer(baseBmp, blur));
        Console.WriteLine($"blur 与旧实现平均像素差 {MeanDiff(outDir, "blur1.png", "blur_legacy.png"):F2}");

        Save(outDir, "mosaic_legacy.png", baseBmp, BuildLegacyMosaicLayer(baseBmp, mosaic));
        Console.WriteLine($"mosaic 与旧实现平均像素差 {MeanDiff(outDir, "mosaic1.png", "mosaic_legacy.png"):F2}");

        // 预览描边只许出现在界面层：导出路径（EditorRenderer.Render）必须不带
        var outlineLayer = EditorOverlayFactory.BuildPrivacyLayer(
            new[] { mosaic }, PrivacyMode.Mosaic, baseBmp, W, H, showOutline: true);
        Save(outDir, "mosaic_outline.png", baseBmp, outlineLayer);
        var outlineDiff = MeanDiff(outDir, "mosaic_outline.png", "mosaic1.png");

        var doc = new EditorDocument(baseBmp, Path.Combine(outDir, "base.png"));
        doc.Privacies.Add(mosaic);
        SaveBitmap(outDir, "mosaic_exported.png", EditorRenderer.Render(doc));
        var exportDiff = MeanDiff(outDir, "mosaic_exported.png", "mosaic1.png");

        Console.WriteLine($"预览描边 vs 无描边 平均像素差 {outlineDiff:F3}（应 > 0）");
        Console.WriteLine($"导出     vs 无描边 平均像素差 {exportDiff:F3}（应 ≈ 0）");
        Console.WriteLine(outlineDiff > 0.5 ? "预览描边 PASS" : "预览描边 FAIL");
        Console.WriteLine(exportDiff < 1.0 ? "导出无描边 PASS" : "导出无描边 FAIL");
        // 给守卫脚本读的 ASCII 行（中文字面量在不同代码页下容易乱码）
        Console.WriteLine($"OUTLINE_DIFF={outlineDiff:F3}");
        Console.WriteLine($"EXPORT_DIFF={exportDiff:F3}");
        Console.WriteLine("EXPORT_NO_OUTLINE=" + (exportDiff < 1.0 ? "PASS" : "FAIL"));

        Console.WriteLine($"输出目录 {outDir}");
    }

    /// <summary>改动前的模糊实现：直接把 BlurEffect 挂在图层上。</summary>
    private static FrameworkElement? BuildLegacyBlurLayer(BitmapSource baseBmp, PrivacyOverlayItem item)
    {
        var geometry = EditorOverlayFactory.BuildPrivacyGeometry(new[] { item }, item.Mode, W, H);
        if (geometry == null) return null;

        return new Image
        {
            Source = baseBmp,
            Width = W,
            Height = H,
            Stretch = Stretch.Fill,
            Clip = geometry,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = Math.Clamp(W * EditorOverlayFactory.NormalizeBlockRatio(item.BlockRatio), 3, 120),
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
        };
    }

    /// <summary>改动前的马赛克实现：用惰性的 TransformedBitmap 当取样源。</summary>
    private static FrameworkElement? BuildLegacyMosaicLayer(BitmapSource baseBmp, PrivacyOverlayItem item)
    {
        var geometry = EditorOverlayFactory.BuildPrivacyGeometry(new[] { item }, item.Mode, W, H);
        if (geometry == null) return null;

        var factor = Math.Max(2.0, W * EditorOverlayFactory.NormalizeBlockRatio(item.BlockRatio));
        var tiny = new TransformedBitmap(baseBmp, new ScaleTransform(1.0 / factor, 1.0 / factor));
        tiny.Freeze();

        var img = new Image
        {
            Source = tiny,
            Width = W,
            Height = H,
            Stretch = Stretch.Fill,
            Clip = geometry,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }

    private static double MeanDiff(string dir, string a, string b)
    {
        var pa = LoadPixels(Path.Combine(dir, a));
        var pb = LoadPixels(Path.Combine(dir, b));
        if (pa.Length != pb.Length) return double.NaN;

        long sum = 0;
        for (var i = 0; i < pa.Length; i++) sum += Math.Abs(pa[i] - pb[i]);
        return (double)sum / pa.Length;
    }

    private static byte[] LoadPixels(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static PrivacyOverlayItem MakeStroke(PrivacyMode mode, double ratio)
    {
        var item = new PrivacyOverlayItem { Mode = mode, BlockRatio = ratio, Thickness = 0.06 };
        for (var i = 0; i <= 80; i++)
        {
            var t = i / 80.0;
            item.Points.Add(new Point(0.1 + t * 0.8, 0.5 + Math.Sin(t * Math.PI * 3) * 0.18));
        }
        return item;
    }

    private static BitmapSource MakeBaseBitmap()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));

            // 细格子 + 小字：马赛克/模糊前后一眼能看出效果
            var thin = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x99)), 1);
            for (var x = 0; x < W; x += 12) dc.DrawLine(thin, new Point(x, 0), new Point(x, H));
            for (var y = 0; y < H; y += 12) dc.DrawLine(thin, new Point(0, y), new Point(W, y));

            var text = new FormattedText(
                "PRIVATE 12345678 ABCDEFGH 机密内容",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"),
                54,
                Brushes.Black,
                1.0);
            dc.DrawText(text, new Point(24, 250));
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(string dir, string name, BitmapSource baseBmp, FrameworkElement? layer)
    {
        var canvas = new Canvas { Width = W, Height = H, Background = Brushes.Black };
        var img = new Image { Source = baseBmp, Width = W, Height = H, Stretch = Stretch.Fill };
        canvas.Children.Add(img);
        if (layer != null) canvas.Children.Add(layer);

        canvas.Measure(new Size(W, H));
        canvas.Arrange(new Rect(0, 0, W, H));
        canvas.UpdateLayout();

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
        rtb.Freeze();

        SaveBitmap(dir, name, rtb);
    }

    private static void SaveBitmap(string dir, string name, BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, name));
        encoder.Save(fs);
    }
}
