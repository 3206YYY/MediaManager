using MediaManager.Core.Services;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MediaManager.Infrastructure.Media;

/// <summary>图片基础编辑服务（旋转、裁剪、缩放、格式转换、亮度对比度）。默认另存为新文件。</summary>
public interface IImageEditService
{
    Task<string> RotateAsync(string input, int degrees);
    Task<string> CropAsync(string input, Rectangle cropRect);
    Task<string> CropCenterSquareAsync(string input);
    Task<string> ResizeAsync(string input, int maxLongEdge);
    Task<string> ConvertFormatAsync(string input, string format);
    Task<string> AdjustAsync(string input, float brightness, float contrast);
    Task<string> AddTextWatermarkAsync(string input, string text);
    Task<string> AddTextAsync(string input, string text, string position = "bottom", string colorHex = "#FFFFFF");
    Task<string> DrawTextAtAsync(string input, string text, float x, float y, float fontSize, string colorHex, float opacity = 1.0f);
    Task<string> DrawStrokesAsync(string input, IReadOnlyList<IReadOnlyList<PointF>> strokes, float strokeWidth, string colorHex);
}

public class ImageEditService : IImageEditService
{
    private const string EditSuffix = "_edited";

    /// <summary>生成编辑副本路径：统一为「原名_edited.扩展名」。重复编辑覆盖同一副本，避免套娃。</summary>
    private static string GetOutputPath(string input, string? newExtension = null)
    {
        var dir = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        // 输入已是编辑副本时，去掉旧后缀，防止 _edited_edited 套娃
        if (name.EndsWith(EditSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^EditSuffix.Length];
        var ext = newExtension ?? Path.GetExtension(input);
        return Path.Combine(dir, $"{name}{EditSuffix}{ext}");
    }

    public Task<string> RotateAsync(string input, int degrees)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            var mode = degrees switch
            {
                90 => RotateMode.Rotate90,
                180 => RotateMode.Rotate180,
                270 => RotateMode.Rotate270,
                _ => RotateMode.Rotate90,
            };
            image.Mutate(x => x.Rotate(mode));
            image.Save(output);
            return output;
        });
    }

    public Task<string> CropAsync(string input, Rectangle cropRect)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            image.Mutate(x => x.Crop(new SixLabors.ImageSharp.Rectangle(
                cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height)));
            image.Save(output);
            return output;
        });
    }

    /// <summary>居中裁剪为正方形（以短边为边长，居中裁剪）。</summary>
    public Task<string> CropCenterSquareAsync(string input)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            var side = Math.Min(image.Width, image.Height);
            var x = (image.Width - side) / 2;
            var y = (image.Height - side) / 2;
            image.Mutate(i => i.Crop(new SixLabors.ImageSharp.Rectangle(x, y, side, side)));
            image.Save(output);
            return output;
        });
    }

    public Task<string> ResizeAsync(string input, int maxLongEdge)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            var ratio = (double)maxLongEdge / Math.Max(image.Width, image.Height);
            var w = Math.Max(1, (int)(image.Width * ratio));
            var h = Math.Max(1, (int)(image.Height * ratio));
            image.Mutate(x => x.Resize(w, h));
            image.Save(output);
            return output;
        });
    }

    public Task<string> ConvertFormatAsync(string input, string format)
    {
        var ext = format.StartsWith(".") ? format : "." + format;
        var output = GetOutputPath(input, ext);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            image.Save(output);
            return output;
        });
    }

    public Task<string> AdjustAsync(string input, float brightness, float contrast)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            image.Mutate(x => x
                .Brightness(brightness)
                .Contrast(contrast));
            image.Save(output);
            return output;
        });
    }

    /// <summary>在右下角添加半透明白色文字水印。</summary>
    public Task<string> AddTextWatermarkAsync(string input, string text)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);

            // 字号按图片宽度自适应（约 4%，最小 16）
            var fontSize = Math.Max(16f, image.Width * 0.04f);
            var font = GetFont(fontSize);

            // 右下角（粗略按字数估算宽度，右侧留 2% 边距）
            var margin = image.Width * 0.02f;
            var textWidth = fontSize * text.Length * 0.8f;
            var x = image.Width - margin - textWidth;
            var y = image.Height - fontSize * 2.2f;

            image.Mutate(i => i.DrawText(text, font, Color.White.WithAlpha(0.75f), new PointF(x, y)));
            image.Save(output);
            return output;
        });
    }

    /// <summary>在图片上添加清晰文字（带黑色阴影）。position 支持 9 宫格：topleft/topcenter/topright/centerleft/center/centerright/bottomleft/bottomcenter/bottomright。</summary>
    public Task<string> AddTextAsync(string input, string text, string position = "bottomcenter", string colorHex = "#FFFFFF")
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);

            var fontSize = Math.Max(20f, image.Width * 0.05f);
            var font = GetFont(fontSize);
            var color = Color.ParseHex(colorHex);

            var margin = image.Width * 0.03f;
            var textWidth = fontSize * text.Length * 0.9f;

            var x = position switch
            {
                "topleft" or "centerleft" or "bottomleft" => margin,
                "topright" or "centerright" or "bottomright" => image.Width - margin - textWidth,
                _ => (image.Width - textWidth) / 2f,
            };
            var y = position switch
            {
                "topleft" or "topcenter" or "topright" => margin,
                "centerleft" or "center" or "centerright" => (image.Height - fontSize) / 2f,
                _ => image.Height - margin - fontSize,
            };

            // 黑色阴影 + 主体文字
            var shadow = Math.Max(1f, fontSize * 0.06f);
            image.Mutate(i => i.DrawText(text, font, Color.Black.WithAlpha(0.7f), new PointF(x + shadow, y + shadow)));
            image.Mutate(i => i.DrawText(text, font, color, new PointF(x, y)));
            image.Save(output);
            return output;
        });
    }

    /// <summary>在图片指定像素坐标绘制文字（带对比描边）。x/y 为文字左上角像素坐标，fontSize 为像素字号，opacity 为不透明度。</summary>
    public Task<string> DrawTextAtAsync(string input, string text, float x, float y, float fontSize, string colorHex, float opacity = 1.0f)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            var font = GetFont(Math.Max(8f, fontSize));
            var color = Color.ParseHex(colorHex).WithAlpha(Math.Clamp(opacity, 0.1f, 1.0f));

            // 根据文字明暗选择对比描边：深色文字用浅色描边，浅色文字用深色描边，
            // 保证浅色/深色背景上都清晰可见（默认黑色文字在深色背景也能看清）
            var rgba = color.ToPixel<Rgba32>();
            var luminance = 0.299f * rgba.R + 0.587f * rgba.G + 0.114f * rgba.B;
            var outline = luminance < 128
                ? Color.White.WithAlpha(0.85f * opacity)
                : Color.Black.WithAlpha(0.6f * opacity);

            var shadow = Math.Max(1f, fontSize * 0.06f);
            image.Mutate(i => i.DrawText(text, font, outline, new PointF(x + shadow, y + shadow)));
            image.Mutate(i => i.DrawText(text, font, color, new PointF(x, y)));
            image.Save(output);
            return output;
        });
    }

    /// <summary>在图片上绘制涂鸦笔画。strokes 为归一化坐标（0~1，相对图片宽高），strokeWidth 为归一化线宽（相对图片宽度）。</summary>
    public Task<string> DrawStrokesAsync(string input, IReadOnlyList<IReadOnlyList<PointF>> strokes, float strokeWidth, string colorHex)
    {
        var output = GetOutputPath(input);
        return Task.Run(() =>
        {
            using var image = Image.Load(input);
            var color = Color.ParseHex(colorHex);
            var px = Math.Max(1f, strokeWidth * image.Width);
            image.Mutate(x =>
            {
                foreach (var stroke in strokes)
                {
                    if (stroke.Count < 2) continue;
                    var pts = stroke.Select(p => new PointF(p.X * image.Width, p.Y * image.Height)).ToArray();
                    x.DrawLine(color, px, pts);
                }
            });
            image.Save(output);
            return output;
        });
    }

    /// <summary>获取加粗字体（优先微软雅黑，回退系统首个字体）。</summary>
    private static Font GetFont(float fontSize)
    {
        var family = SystemFonts.Families.FirstOrDefault(f => f.Name.Contains("YaHei", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(family.Name))
            family = SystemFonts.Families.FirstOrDefault();
        if (string.IsNullOrEmpty(family.Name))
            throw new InvalidOperationException("系统中未找到可用字体");
        return family.CreateFont(fontSize, FontStyle.Bold);
    }
}
