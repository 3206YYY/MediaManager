using System.IO;
using System.Windows.Media.Imaging;
using MediaManager.Core.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MediaManager.App.Editing;

/// <summary>
/// 后台调色预览：把原图按几何变换（裁剪/旋转/翻转）降采样到长边 ~1400px 缓存，
/// 之后每次调参只在这张小图上做颜色运算，几十毫秒内出结果，不阻塞 UI 线程。
/// 结果只能通过 <see cref="RenderPreview"/> 在后台线程计算，由调用方切回 UI 线程使用。
/// </summary>
public sealed class EditorPreviewService : IDisposable
{
    private readonly object _gate = new();
    private Image<Rgba32>? _working;
    private GeometryKey _key;
    private bool _disposed;

    private readonly record struct GeometryKey(
        string Path, long Length, DateTime WriteTime,
        int CropX, int CropY, int CropWidth, int CropHeight,
        double Straighten, int Rotation, bool Flip);

    /// <summary>在线程池上计算预览位图；文件不可用或解码失败时返回 null（由界面回退到普通底图）。</summary>
    public BitmapSource? RenderPreview(EditorDocument doc, int maxLongEdge = 1400)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            try
            {
                var key = KeyOf(doc);
                if (_working == null || !key.Equals(_key))
                {
                    _working?.Dispose();
                    _working = BuildWorkingCopy(doc, maxLongEdge);
                    _key = key;
                }

                if (_working == null) return null;

                using var output = _working.Clone();
                EditorRenderer.ApplyColorGrading(
                    output, doc.Adjustments, doc.Filter, doc.FilterStrength);
                return EditorRenderer.ToBitmapSource(output);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "生成调色预览失败");
                return null;
            }
        }
    }

    private static GeometryKey KeyOf(EditorDocument doc)
    {
        long length = 0;
        var stamp = DateTime.MinValue;
        try
        {
            var info = new FileInfo(doc.SourcePath);
            if (info.Exists)
            {
                length = info.Length;
                stamp = info.LastWriteTimeUtc;
            }
        }
        catch
        {
            // 文件不可访问时用空信息作为键，后面加载会失败并回退
        }

        return new GeometryKey(
            doc.SourcePath, length, stamp,
            doc.Crop.X, doc.Crop.Y, doc.Crop.Width, doc.Crop.Height,
            Math.Round(doc.StraightenAngle, 3),
            ((doc.Rotation % 360) + 360) % 360, doc.FlipHorizontal);
    }

    private static Image<Rgba32>? BuildWorkingCopy(EditorDocument doc, int maxLongEdge)
    {
        if (string.IsNullOrWhiteSpace(doc.SourcePath) || !File.Exists(doc.SourcePath))
            return null;

        using var source = Image.Load<Rgba32>(doc.SourcePath);
        source.Mutate(x => x.AutoOrient());

        // 拉直在「源画面」层生效（与导出同一套系数），裁剪矩形就在拉直后的坐标系里
        var angle = StraightenMath.ClampAngle(doc.StraightenAngle);
        if (Math.Abs(angle) > 0.01)
        {
            var (cw, ch) = StraightenMath.StraightenedSize(source.Width, source.Height, angle);
            source.Mutate(x => x.Rotate((float)angle, KnownResamplers.Bicubic));
            cw = Math.Min(cw, source.Width);
            ch = Math.Min(ch, source.Height);
            source.Mutate(x => x.Crop(new Rectangle(
                Math.Max(0, (source.Width - cw) / 2),
                Math.Max(0, (source.Height - ch) / 2),
                cw, ch)));
        }

        var crop = doc.NormalizedCrop;
        var rect = new Rectangle(
            (int)Math.Floor(crop.X * source.Width),
            (int)Math.Floor(crop.Y * source.Height),
            Math.Max(1, (int)Math.Round(crop.Width * source.Width)),
            Math.Max(1, (int)Math.Round(crop.Height * source.Height)));

        rect = new Rectangle(
            Math.Clamp(rect.X, 0, Math.Max(0, source.Width - 1)),
            Math.Clamp(rect.Y, 0, Math.Max(0, source.Height - 1)),
            Math.Clamp(rect.Width, 1, source.Width - Math.Clamp(rect.X, 0, Math.Max(0, source.Width - 1))),
            Math.Clamp(rect.Height, 1, source.Height - Math.Clamp(rect.Y, 0, Math.Max(0, source.Height - 1))));

        using var cropped = source.Clone(x => x.Crop(rect));

        switch (((doc.Rotation % 360) + 360) % 360)
        {
            case 90:
                cropped.Mutate(x => x.Rotate(RotateMode.Rotate90));
                break;
            case 180:
                cropped.Mutate(x => x.Rotate(RotateMode.Rotate180));
                break;
            case 270:
                cropped.Mutate(x => x.Rotate(RotateMode.Rotate270));
                break;
        }

        if (doc.FlipHorizontal)
            cropped.Mutate(x => x.Flip(FlipMode.Horizontal));

        var (w, h) = OutputSize.Resolve(cropped.Width, cropped.Height, null, null, maxLongEdge);
        if (cropped.Width != w || cropped.Height != h)
        {
            cropped.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(w, h),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
            }));
        }

        return cropped.Clone();
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _working?.Dispose();
            _working = null;
            _key = default;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _working?.Dispose();
            _working = null;
        }
    }
}
