using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaManager.Core.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IOPath = System.IO.Path;
using RgbaImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using SharpSize = SixLabors.ImageSharp.Size;
using SharpResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;

namespace MediaManager.App.Editing;

/// <summary>一次保存的结果（供界面做明确反馈）。</summary>
/// <param name="OverwroteSource">true = 直接覆盖了正在编辑的副本；false = 生成了新副本（原图未动）。</param>
public sealed record SaveResult(
    string Path, string FileName, int Width, int Height, long SizeBytes, bool OverwroteSource);

/// <summary>「另存为」支持的输出格式。</summary>
public enum SaveImageFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp,
}

/// <summary>
/// 把编辑文档渲染成最终图片：
/// 1) ImageSharp 从原图按原尺寸烘焙（裁剪/旋转/翻转/滤镜/调色，非破坏性）；
/// 2) WPF 在烘焙结果上合成文字、涂鸦、马赛克、图片水印；
/// 3) 再用 ImageSharp 编码写盘（目录内临时文件 + 原子替换）。
/// </summary>
public static class EditorRenderer
{
    /// <summary>按编辑状态计算最终输出尺寸。</summary>
    public static (int Width, int Height) ResolveOutputSize(EditorDocument doc, int? maxLongEdgeOverride = null)
        => OutputSize.Resolve(
            doc.VisibleWidth, doc.VisibleHeight,
            doc.OutputWidth, doc.OutputHeight,
            maxLongEdgeOverride ?? doc.OutputMaxEdge);

    /// <summary>
    /// 用 ImageSharp 在原图分辨率上烘焙底图（不写文件，纯内存）。
    /// 失败时抛异常，由调用方决定是提示还是回退。
    /// </summary>
    public static BitmapSource BakeBase(EditorDocument doc, int outWidth, int outHeight)
    {
        ArgumentNullException.ThrowIfNull(doc);

        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(doc.SourcePath);
        image.Mutate(x => x.AutoOrient());

        // 拉直在「源画面」层生效：整图旋转后取居中的内接框（与预览同一套系数）
        ApplyStraighten(image, doc.StraightenAngle);

        // 归一化裁剪 → 原图像素裁剪（原图可能比预览解码的更大，这里才是真正的无损尺寸）
        var crop = doc.NormalizedCrop;
        var rect = new Rectangle(
            (int)Math.Floor(crop.X * image.Width),
            (int)Math.Floor(crop.Y * image.Height),
            Math.Max(1, (int)Math.Round(crop.Width * image.Width)),
            Math.Max(1, (int)Math.Round(crop.Height * image.Height)));

        rect = ClampRectangle(rect, image.Width, image.Height);
        if (rect.X != 0 || rect.Y != 0 || rect.Width != image.Width || rect.Height != image.Height)
            image.Mutate(x => x.Crop(rect));

        switch (((doc.Rotation % 360) + 360) % 360)
        {
            case 90:
                image.Mutate(x => x.Rotate(RotateMode.Rotate90));
                break;
            case 180:
                image.Mutate(x => x.Rotate(RotateMode.Rotate180));
                break;
            case 270:
                image.Mutate(x => x.Rotate(RotateMode.Rotate270));
                break;
        }

        if (doc.FlipHorizontal)
            image.Mutate(x => x.Flip(FlipMode.Horizontal));

        if (image.Width != outWidth || image.Height != outHeight)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SharpSize(outWidth, outHeight),
                Mode = SharpResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
                Compand = true,
            }));
        }

        ApplyColorGrading(image, doc.Adjustments, doc.Filter, doc.FilterStrength);
        return ToBitmapSource(image);
    }

    /// <summary>按拉直角度旋转整图，再取居中的内接框（去掉旋转后露出的四角）。</summary>
    private static void ApplyStraighten(RgbaImage image, double angle)
    {
        if (Math.Abs(angle) <= 0.01) return;

        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0) return;

        // Rgba32 旋转后四角以透明填充，接着居中裁掉这些透明区域
        image.Mutate(x => x.Rotate((float)angle, KnownResamplers.Bicubic));

        var (cw, ch) = StraightenMath.StraightenedSize(width, height, angle);
        cw = Math.Min(cw, image.Width);
        ch = Math.Min(ch, image.Height);

        image.Mutate(x => x.Crop(new Rectangle(
            Math.Max(0, (image.Width - cw) / 2),
            Math.Max(0, (image.Height - ch) / 2),
            cw, ch)));
    }

    /// <summary>按编辑状态渲染出最终合成位图（必须在 UI 线程调用）。</summary>
    public static BitmapSource Render(EditorDocument doc, int? maxLongEdgeOverride = null)
    {
        var (outW, outH) = ResolveOutputSize(doc, maxLongEdgeOverride);

        if (outW <= 0 || outH <= 0)
            throw new InvalidOperationException("图片可视区域尺寸无效（裁剪区域可能过小），无法生成副本。");

        var baseBmp = BakeBase(doc, outW, outH);
        var scale = outW / (double)Math.Max(1, doc.VisibleWidth);

        var root = new Grid
        {
            Width = outW,
            Height = outH,
            Background = Brushes.Transparent,
            ClipToBounds = true,
        };

        // 1) 烘焙后的底图（已含裁剪/旋转/翻转/滤镜/调色）
        root.Children.Add(new System.Windows.Controls.Image
        {
            Source = baseBmp,
            Stretch = Stretch.Fill,
            Width = outW,
            Height = outH,
        });

        // 2) 矩形马赛克
        foreach (var mosaic in doc.Mosaics)
        {
            var img = EditorOverlayFactory.BuildMosaicRect(mosaic, baseBmp, outW, outH);
            if (img != null) root.Children.Add(img);
        }

        // 3) 画笔打码：马赛克 / 模糊分开合成（各自一次绘制）
        foreach (var mode in new[] { PrivacyMode.Mosaic, PrivacyMode.Blur })
        {
            var layer = EditorOverlayFactory.BuildPrivacyLayer(doc.Privacies, mode, baseBmp, outW, outH);
            if (layer != null) root.Children.Add(layer);
        }

        // 4) 图片水印 + 涂鸦 + 文字
        var overlay = new Canvas { Width = outW, Height = outH };
        root.Children.Add(overlay);

        foreach (var watermark in doc.Watermarks)
            overlay.Children.Add(EditorOverlayFactory.BuildWatermark(watermark, outW, outH));

        foreach (var stroke in doc.Strokes)
        {
            var line = EditorOverlayFactory.BuildStroke(stroke, outW, outH);
            if (line != null) overlay.Children.Add(line);
        }

        foreach (var shape in doc.Shapes)
        {
            var element = EditorOverlayFactory.BuildShape(shape, outW, outH);
            if (element != null) overlay.Children.Add(element);
        }

        foreach (var text in doc.Texts)
        {
            // 只绘制「有效」文字：空内容 / 坐标非法的一律跳过。
            if (!OverlayTextRules.IsRenderable(text.Text, text.X, text.Y))
                continue;

            overlay.Children.Add(EditorOverlayFactory.BuildText(text, outW, outH, scale));
        }

        // 5) 平铺文字水印盖在最上层（导出与预览共用同一套绘制）
        if (doc.TileWatermark != null)
        {
            var tile = EditorOverlayFactory.BuildTileWatermark(doc.TileWatermark, outW, outH);
            if (tile != null) overlay.Children.Add(tile);
        }

        root.Measure(new System.Windows.Size(outW, outH));
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
    ///
    /// 元数据策略（**有意为之，不要"顺手"加回来**）：
    /// 副本不携带任何 EXIF —— 不搬拍摄时间、不搬机型、不搬 GPS。
    /// 原因是编辑副本大概率会被分享出去，GPS / 设备序列号属于隐私元数据，
    /// 一旦随副本扩散就无法收回。像素已经在 <see cref="BakeBase"/> 里按
    /// EXIF 方向烘焙成正立，所以只写一个 Orientation=1，避免看图软件重复旋转。
    /// 产品里也没有依赖「拍摄时间」的字段（<c>MediaItem</c> 只有文件系统时间），
    /// 因此不搬元数据不会丢任何功能。
    /// </remarks>
    public static async Task<SaveResult> SaveAsync(EditorDocument doc, int? maxLongEdgeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        // 渲染必须在 UI 线程完成（WPF 可视化对象有线程亲和性）
        var bitmap = Render(doc, maxLongEdgeOverride);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("生成的图片尺寸为 0，无法保存副本。");

        var output = BuildOutputPath(doc.SourcePath);
        var overwrote = string.Equals(
            IOPath.GetFullPath(output), IOPath.GetFullPath(doc.SourcePath), StringComparison.OrdinalIgnoreCase);

        await WriteBitmapAsync(bitmap, output, EncoderFor(output));

        var info = new FileInfo(output);
        return new SaveResult(
            output,
            IOPath.GetFileName(output),
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            info.Exists ? info.Length : 0,
            overwrote);
    }

    /// <summary>
    /// 「另存为…」：把当前编辑结果写到指定路径，格式与 JPEG 质量可选。
    /// 不覆盖原图（路径由调用方选择），走同一套临时文件 + 原子替换策略。
    /// </summary>
    public static async Task<SaveResult> SaveAsAsync(
        EditorDocument doc, string outputPath, SaveImageFormat format, int jpegQuality = 92)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var bitmap = Render(doc);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("生成的图片尺寸为 0，无法保存副本。");

        var full = IOPath.GetFullPath(outputPath);
        // 绝不覆盖正在编辑的原图：那会破坏「只存副本、不毁原图」的承诺
        if (string.Equals(full, IOPath.GetFullPath(doc.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能把编辑结果保存回原图，请选择一个新文件名。");

        await WriteBitmapAsync(bitmap, full, EncoderFor(format, jpegQuality));

        var info = new FileInfo(full);
        return new SaveResult(
            full,
            IOPath.GetFileName(full),
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            info.Exists ? info.Length : 0,
            false);
    }

    /// <summary>渲染结果写盘：临时文件 → 校验非空 → 原子替换目标。</summary>
    private static async Task WriteBitmapAsync(
        BitmapSource bitmap, string output, SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        var dir = IOPath.GetDirectoryName(output);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"目标文件夹不存在或已断开：{dir}");

        // 登记一下：这是我们自己写出去的文件，别让文件监控当成"用户拷进来的新文件"弹提示
        MediaManager.Infrastructure.FileSystem.SelfWriteTracker.Register(output);

        // JPEG / BMP 不支持透明通道，先铺白底再编码，避免出现黑块
        if (!SupportsAlpha(output))
            bitmap = Flatten(bitmap);

        // 直接把像素交给 ImageSharp 编码：省掉「整图编码成 PNG → 再解码」的往返，
        // 大图（40MP）能少几百 MB 的峰值内存和数秒耗时。
        var pixelSource = bitmap.Format == PixelFormats.Pbgra32
            ? (BitmapSource)bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);

        var width = pixelSource.PixelWidth;
        var height = pixelSource.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        pixelSource.CopyPixels(pixels, stride, 0);
        UnpremultiplyAlpha(pixels);

        await Task.Run(() =>
        {
            var temp = IOPath.Combine(dir, $".{IOPath.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, width, height))
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WriteOrientationMetadata(image, output);
                    image.Save(file, encoder);
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
    }

    // ---------- ImageSharp 处理 ----------

    private static Rectangle ClampRectangle(Rectangle rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(rect.Width, 1, width - x);
        var h = Math.Clamp(rect.Height, 1, height - y);
        return new Rectangle(x, y, w, h);
    }

    internal static void ApplyColorGrading(
        RgbaImage image, ColorAdjustments adjustments, EditorFilter filter, int filterStrength = 100)
    {
        var adj = adjustments.Clamped();
        if (adj.IsIdentity && (filter == EditorFilter.None || filterStrength <= 0)) return;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var (r, g, b) = ColorGrading.Apply(
                        pixel.R, pixel.G, pixel.B, adj, filter, filterStrength);
                    pixel.R = r;
                    pixel.G = g;
                    pixel.B = b;
                }
            }
        });
    }

    /// <summary>ImageSharp 像素 → WPF 位图（BGRA，跨线程可冻结）。</summary>
    public static BitmapSource ToBitmapSource(RgbaImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[width * height * 4];
        image.CopyPixelDataTo(pixels);

        // RGBA → BGRA
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------- 文件 ----------

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

    /// <summary>
    /// 给 JPEG / TIFF 副本写一个干净的 EXIF：方向标记固定为 1（像素在解码时已按原图方向摆正，
    /// 再保留原方向值会让看图软件重复旋转一次），并写入实际像素尺寸。
    /// 只写这两项，不搬运源图的拍摄时间 / GPS 等信息。
    /// </summary>
    private static void WriteOrientationMetadata(SixLabors.ImageSharp.Image image, string outputPath)
    {
        var ext = IOPath.GetExtension(outputPath).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff")) return;

        var exif = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)1);
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.PixelXDimension, (uint)image.Width);
        exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.PixelYDimension, (uint)image.Height);
        image.Metadata.ExifProfile = exif;
    }

    /// <summary>Pbgra32 → 直通 alpha（ImageSharp 的 Bgra32 不认预乘）。</summary>
    private static void UnpremultiplyAlpha(byte[] pixels)
    {
        Parallel.For(0, pixels.Length / 4, i =>
        {
            var offset = i * 4;
            var alpha = pixels[offset + 3];
            if (alpha == 255) return;
            if (alpha == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                return;
            }

            pixels[offset] = (byte)Math.Min(255, pixels[offset] * 255 / alpha);
            pixels[offset + 1] = (byte)Math.Min(255, pixels[offset + 1] * 255 / alpha);
            pixels[offset + 2] = (byte)Math.Min(255, pixels[offset + 2] * 255 / alpha);
        });
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

    private static SixLabors.ImageSharp.Formats.IImageEncoder EncoderFor(
        SaveImageFormat format, int jpegQuality)
    {
        var quality = Math.Clamp(jpegQuality, 1, 100);
        return format switch
        {
            SaveImageFormat.Jpeg => new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality },
            SaveImageFormat.Webp => new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = quality },
            SaveImageFormat.Bmp => new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(),
            _ => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
        };
    }

    /// <summary>格式对应的默认扩展名（另存为窗口用）。</summary>
    public static string ExtensionOf(SaveImageFormat format) => format switch
    {
        SaveImageFormat.Jpeg => ".jpg",
        SaveImageFormat.Webp => ".webp",
        SaveImageFormat.Bmp => ".bmp",
        _ => ".png",
    };

    /// <summary>
    /// 决定保存目标路径（V1.2.5 起的规则）：
    /// 「编辑原图 → 新建副本；编辑副本 → 直接改副本」。
    /// 具体规则见 <see cref="EditedFileNaming"/>（放在 Core 便于单测覆盖）。
    /// </summary>
    public static string BuildOutputPath(string input) => EditedFileNaming.BuildOutputPath(input);

    /// <summary>
    /// 判断一个路径是不是「编辑副本」（文件名主体以 <c>_edited</c> 结尾）。
    /// 界面用它决定按钮文案与保存结果提示语。
    /// </summary>
    public static bool IsEditedCopy(string? path) => EditedFileNaming.IsEditedCopy(path);
}
