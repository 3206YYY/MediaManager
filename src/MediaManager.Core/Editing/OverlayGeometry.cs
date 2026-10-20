namespace MediaManager.Core.Editing;

/// <summary>
/// 叠加层坐标 / 尺寸的绝对安全检查（纯函数，方便单测）。
///
/// 导出时所有叠加层坐标都是「归一化 0~1」，一旦出现 NaN、Infinity、
/// 负数或大于 1 的越界值，WPF 布局会把元素摆到画布外，甚至在
/// RenderTargetBitmap 上留下整块黑/白底色。这里统一做钳制，
/// 并且把「完全落在画布外」的元素单独识别出来。
/// </summary>
public static class OverlayGeometry
{
    /// <summary>把任意值钳制到 [0, 1]；NaN / Infinity 一律视为 0。</summary>
    public static double ClampUnit(double value)
    {
        if (!double.IsFinite(value)) return 0;
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    /// <summary>把不透明度钳制到 [0, 1]；非法值按 1（完全可见）处理。</summary>
    public static double ClampOpacity(double value)
    {
        if (!double.IsFinite(value)) return 1;
        return Math.Clamp(value, 0, 1);
    }

    /// <summary>颜色分量是否合法（用于过滤损坏的 ColorHex）。</summary>
    public static bool IsFinitePoint(double x, double y)
        => double.IsFinite(x) && double.IsFinite(y);

    /// <summary>
    /// 归一化矩形 → 画布像素矩形。会先钳制到画布内，再返回实际可绘制区域。
    /// 完全落在画布外（或钳制后宽高为 0）时返回 false。
    /// </summary>
    public static bool TryToPixelRect(
        double x, double y, double w, double h,
        int canvasWidth, int canvasHeight,
        out int px, out int py, out int pw, out int ph)
    {
        px = py = pw = ph = 0;
        if (canvasWidth <= 0 || canvasHeight <= 0) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(w) || !double.IsFinite(h)) return false;

        // 允许负宽高（用户反向拖拽），先归一化
        var left = Math.Min(x, x + w);
        var top = Math.Min(y, y + h);
        var right = Math.Max(x, x + w);
        var bottom = Math.Max(y, y + h);

        // 完全在画布外
        if (right <= 0 || bottom <= 0 || left >= 1 || top >= 1) return false;

        var clampedLeft = Math.Clamp(left, 0, 1);
        var clampedTop = Math.Clamp(top, 0, 1);
        var clampedRight = Math.Clamp(right, 0, 1);
        var clampedBottom = Math.Clamp(bottom, 0, 1);

        px = (int)Math.Round(clampedLeft * canvasWidth);
        py = (int)Math.Round(clampedTop * canvasHeight);
        pw = (int)Math.Round((clampedRight - clampedLeft) * canvasWidth);
        ph = (int)Math.Round((clampedBottom - clampedTop) * canvasHeight);

        // 贴边取整后可能变成 0，至少给 1 像素，避免空绘制
        if (pw <= 0) pw = 1;
        if (ph <= 0) ph = 1;

        // 再夹一次，保证不越出画布
        px = Math.Clamp(px, 0, canvasWidth - 1);
        py = Math.Clamp(py, 0, canvasHeight - 1);
        pw = Math.Min(pw, canvasWidth - px);
        ph = Math.Min(ph, canvasHeight - py);

        return pw > 0 && ph > 0;
    }

    /// <summary>
    /// 字号钳制：过小会变成 1px 噪点，过大会撑爆画布。
    /// 上限取画布短边的 1/2（再大必然溢出，且会把画面糊成一片纯色）。
    /// </summary>
    public static double ClampFontSize(double fontPx, int canvasWidth, int canvasHeight)
    {
        if (!double.IsFinite(fontPx) || fontPx <= 0) return 6;
        var max = Math.Max(12.0, Math.Min(canvasWidth, canvasHeight) / 2.0);
        return Math.Clamp(fontPx, 6, max);
    }

    /// <summary>线宽钳制（按画布宽度比例），避免 0 宽或粗到糊满整张图。</summary>
    public static double ClampThickness(double thickness, int canvasWidth)
    {
        if (!double.IsFinite(thickness) || thickness <= 0) return Math.Max(1, canvasWidth / 500.0);
        var max = Math.Max(2.0, canvasWidth / 4.0);
        return Math.Clamp(thickness, 1, max);
    }

    /// <summary>输出尺寸钳制：避免 0 尺寸或超大图把内存打爆。</summary>
    public static (int Width, int Height) ClampOutputSize(int width, int height, int maxEdge = 20000)
    {
        if (width <= 0 || height <= 0) return (1, 1);
        var scale = 1.0;
        if (maxEdge > 0 && Math.Max(width, height) > maxEdge)
            scale = (double)maxEdge / Math.Max(width, height);

        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }
}
