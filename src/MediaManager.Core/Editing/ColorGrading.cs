namespace MediaManager.Core.Editing;

/// <summary>滤镜预设（保存时由 ImageSharp 一次性烘焙，预览与导出共用同一套算法）。</summary>
public enum EditorFilter
{
    None,
    Gray,
    Warm,
    Retro,
    Japanese,
}

/// <summary>调色参数：亮度 / 对比度 / 饱和度，范围 -100 ~ +100（0 = 不调整）。</summary>
public readonly record struct ColorAdjustments(double Brightness, double Contrast, double Saturation)
{
    public static ColorAdjustments None { get; } = new(0, 0, 0);

    public bool IsIdentity =>
        Math.Abs(Brightness) < 0.01 && Math.Abs(Contrast) < 0.01 && Math.Abs(Saturation) < 0.01;

    /// <summary>把三个参数钳制到合法范围；NaN / Infinity 视为 0。</summary>
    public ColorAdjustments Clamped() => new(
        Clamp(Brightness), Clamp(Contrast), Clamp(Saturation));

    private static double Clamp(double value)
        => double.IsFinite(value) ? Math.Clamp(value, -100, 100) : 0;
}

/// <summary>
/// 色彩处理（纯函数，方便单测）：滤镜预设 → 饱和度 → 对比度 → 亮度。
/// 预览（降采样）与最终保存（原图尺寸）都调用这里，保证所见即所得。
/// </summary>
public static class ColorGrading
{
    /// <summary>
    /// 换算一个像素：滤镜（按 <paramref name="filterStrength"/> 与原图混合）→ 饱和度 → 对比度 → 亮度。
    /// filterStrength 0 = 不套滤镜，100 = 完整滤镜效果。
    /// </summary>
    public static (byte R, byte G, byte B) Apply(
        byte r, byte g, byte b, ColorAdjustments adjustments, EditorFilter filter, int filterStrength = 100)
    {
        var adj = adjustments.Clamped();

        var (fr, fg, fb) = ApplyFilter(r, g, b, filter);
        var strength = Math.Clamp(filterStrength, 0, 100) / 100.0;
        if (strength < 0.999)
        {
            // 滤镜强度：在原图与滤镜结果之间线性插值，日系/复古这类重滤镜可以按口味减半
            fr = r + (fr - r) * strength;
            fg = g + (fg - g) * strength;
            fb = b + (fb - b) * strength;
        }

        // 1) 饱和度：以亮度为基准在「灰度」与「原色」之间插值
        if (Math.Abs(adj.Saturation) > 0.01)
        {
            var luminance = 0.299 * fr + 0.587 * fg + 0.114 * fb;
            var factor = 1 + adj.Saturation / 100.0;
            fr = luminance + (fr - luminance) * factor;
            fg = luminance + (fg - luminance) * factor;
            fb = luminance + (fb - luminance) * factor;
        }

        // 2) 对比度：-100 → 0.5 倍，+100 → 2 倍（以 128 为中心）
        if (Math.Abs(adj.Contrast) > 0.01)
        {
            var factor = Math.Pow(2, adj.Contrast / 100.0);
            fr = (fr - 128) * factor + 128;
            fg = (fg - 128) * factor + 128;
            fb = (fb - 128) * factor + 128;
        }

        // 3) 亮度：-100 → -60，+100 → +60
        if (Math.Abs(adj.Brightness) > 0.01)
        {
            var shift = adj.Brightness / 100.0 * 60;
            fr += shift;
            fg += shift;
            fb += shift;
        }

        return (ToByte(fr), ToByte(fg), ToByte(fb));
    }

    /// <summary>滤镜预设本身（不含用户调色参数）。</summary>
    public static (double R, double G, double B) ApplyFilter(
        double r, double g, double b, EditorFilter filter)
    {
        switch (filter)
        {
            case EditorFilter.Gray:
            {
                var y = 0.299 * r + 0.587 * g + 0.114 * b;
                return (y, y, y);
            }

            case EditorFilter.Warm:
                // 偏暖：红黄抬升、蓝压暗，轻微提亮肤色
                return (r * 1.10 + 8, g * 1.03 + 3, b * 0.90);

            case EditorFilter.Retro:
            {
                // 棕褐（sepia）与少量原色混合，保留一点层次
                var sr = 0.393 * r + 0.769 * g + 0.189 * b;
                var sg = 0.349 * r + 0.686 * g + 0.168 * b;
                var sb = 0.272 * r + 0.534 * g + 0.131 * b;
                return (r * 0.15 + sr * 0.85,
                        g * 0.15 + sg * 0.85,
                        b * 0.15 + sb * 0.85);
            }

            case EditorFilter.Japanese:
                // 日系：整体提亮、轻微降饱和、阴影偏青蓝
                return (r * 0.92 + 20, g * 0.96 + 18, b * 1.02 + 16);

            default:
                return (r, g, b);
        }
    }

    private static byte ToByte(double value)
    {
        if (!double.IsFinite(value)) return 0;
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }
}
