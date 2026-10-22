namespace MediaManager.Core.Editing;

/// <summary>
/// 拉直（±15° 微调）的几何计算：旋转后自动裁掉四角，避免出现黑边。
/// 预览（WPF）与导出（ImageSharp）共用这里的系数，保证所见即所得。
/// </summary>
public static class StraightenMath
{
    /// <summary>允许的拉直角度范围。</summary>
    public const double MaxAngle = 15;

    public static double ClampAngle(double angle)
        => double.IsFinite(angle) ? Math.Clamp(angle, -MaxAngle, MaxAngle) : 0;

    /// <summary>
    /// 旋转后能保留的最大「同比例内接框」缩放系数 k（0~1）。
    /// 旋转 θ 后，中心的 w*k × h*k 矩形四个角刚好落在原矩形边界内。
    /// </summary>
    public static double FrameScale(int width, int height, double angle)
    {
        if (width <= 0 || height <= 0) return 1;

        var a = ClampAngle(angle);
        if (Math.Abs(a) < 0.01) return 1;

        var rad = a * Math.PI / 180.0;
        var c = Math.Abs(Math.Cos(rad));
        var s = Math.Abs(Math.Sin(rad));

        var k = Math.Min(
            width / (width * c + height * s),
            height / (width * s + height * c));

        return Math.Clamp(k, 0.01, 1.0);
    }

    /// <summary>拉直并自动裁边后的像素尺寸。</summary>
    public static (int Width, int Height) StraightenedSize(int width, int height, double angle)
    {
        if (width <= 0 || height <= 0) return (Math.Max(1, width), Math.Max(1, height));

        var k = FrameScale(width, height, angle);
        return (
            Math.Max(1, (int)Math.Round(width * k)),
            Math.Max(1, (int)Math.Round(height * k)));
    }

    /// <summary>旋转后画布的展开尺寸（用于 WPF 预览渲染）。</summary>
    public static (int Width, int Height) ExpandedSize(int width, int height, double angle)
    {
        var a = ClampAngle(angle);
        if (Math.Abs(a) < 0.01) return (Math.Max(1, width), Math.Max(1, height));

        var rad = a * Math.PI / 180.0;
        var c = Math.Abs(Math.Cos(rad));
        var s = Math.Abs(Math.Sin(rad));

        return (
            Math.Max(1, (int)Math.Ceiling(width * c + height * s)),
            Math.Max(1, (int)Math.Ceiling(width * s + height * c)));
    }
}
