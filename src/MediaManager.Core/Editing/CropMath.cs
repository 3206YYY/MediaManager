namespace MediaManager.Core.Editing;

/// <summary>裁剪框（画布像素坐标）。Core 项目不引用 WPF，所以自带一个轻量矩形。</summary>
public readonly record struct CropBox(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static CropBox FromLTRB(double left, double top, double right, double bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public CropBox Normalized() => new(
        Math.Min(X, X + Width),
        Math.Min(Y, Y + Height),
        Math.Abs(Width),
        Math.Abs(Height));
}

/// <summary>裁剪框的 8 个手柄 + 整体移动。</summary>
public enum CropHandle
{
    None,
    Move,
    NW,
    N,
    NE,
    E,
    SE,
    S,
    SW,
    W,
}

/// <summary>
/// 裁剪框的几何约束（纯函数，方便单测）：
/// 自由拖拽、固定比例（1:1 / 4:3 / 16:9）、边界钳制。
/// 所有坐标都是「画布像素」，与图片像素的换算在调用方完成。
/// </summary>
public static class CropMath
{
    public const double MinSize = 24;

    /// <summary>把矩形钳制到画布内（先保证宽高不越界，再平移回画布）。</summary>
    public static CropBox Clamp(CropBox rect, double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return rect;

        var r = rect.Normalized();
        var w = Math.Clamp(r.Width, 0, canvasWidth);
        var h = Math.Clamp(r.Height, 0, canvasHeight);
        var x = Math.Clamp(r.X, 0, canvasWidth - w);
        var y = Math.Clamp(r.Y, 0, canvasHeight - h);
        return new CropBox(x, y, w, h);
    }

    /// <summary>按固定比例把矩形收进画布（保持中心尽量不变），aspect 为 null 时仅做边界钳制。</summary>
    public static CropBox FitInside(CropBox rect, double? aspect, double canvasWidth, double canvasHeight)
    {
        if (aspect is not > 0) return Clamp(rect, canvasWidth, canvasHeight);

        var w = rect.Width;
        var h = w / aspect.Value;
        if (h > canvasHeight)
        {
            h = canvasHeight;
            w = h * aspect.Value;
        }
        if (w > canvasWidth)
        {
            w = canvasWidth;
            h = w / aspect.Value;
        }

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var x = Math.Clamp(cx - w / 2, 0, Math.Max(0, canvasWidth - w));
        var y = Math.Clamp(cy - h / 2, 0, Math.Max(0, canvasHeight - h));
        return new CropBox(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// 拖动手柄后的新矩形。aspect 有值时锁定比例：
    /// 角手柄 / 左右手柄以宽度为准，上下手柄以高度为准。
    /// </summary>
    public static CropBox Drag(
        CropBox rect, CropHandle handle, double dx, double dy,
        double? aspect, double canvasWidth, double canvasHeight, double minSize = MinSize)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return rect;

        var r = Clamp(rect, canvasWidth, canvasHeight);
        if (handle == CropHandle.None) return r;

        if (handle == CropHandle.Move)
        {
            var mx = Math.Clamp(r.X + dx, 0, Math.Max(0, canvasWidth - r.Width));
            var my = Math.Clamp(r.Y + dy, 0, Math.Max(0, canvasHeight - r.Height));
            return new CropBox(mx, my, r.Width, r.Height);
        }

        var left = handle is CropHandle.NW or CropHandle.W or CropHandle.SW;
        var right = handle is CropHandle.NE or CropHandle.E or CropHandle.SE;
        var top = handle is CropHandle.NW or CropHandle.N or CropHandle.NE;
        var bottom = handle is CropHandle.SW or CropHandle.S or CropHandle.SE;

        double x1 = r.Left, y1 = r.Top, x2 = r.Right, y2 = r.Bottom;
        if (left) x1 = Math.Clamp(x1 + dx, 0, x2 - minSize);
        if (right) x2 = Math.Clamp(x2 + dx, x1 + minSize, canvasWidth);
        if (top) y1 = Math.Clamp(y1 + dy, 0, y2 - minSize);
        if (bottom) y2 = Math.Clamp(y2 + dy, y1 + minSize, canvasHeight);

        var raw = CropBox.FromLTRB(x1, y1, x2, y2);
        if (aspect is not > 0) return Clamp(raw, canvasWidth, canvasHeight);

        return ConstrainAspect(raw, handle, aspect.Value, canvasWidth, canvasHeight, minSize);
    }

    private static CropBox ConstrainAspect(
        CropBox raw, CropHandle handle, double aspect, double canvasWidth, double canvasHeight, double minSize)
    {
        double w, h;
        if (handle is CropHandle.N or CropHandle.S)
        {
            h = raw.Height;
            w = h * aspect;
        }
        else
        {
            w = raw.Width;
            h = w / aspect;
        }

        // 先受画布限制
        if (w > canvasWidth)
        {
            w = canvasWidth;
            h = w / aspect;
        }
        if (h > canvasHeight)
        {
            h = canvasHeight;
            w = h * aspect;
        }
        if (w < minSize)
        {
            w = Math.Min(minSize, canvasWidth);
            h = w / aspect;
        }
        if (h < minSize)
        {
            h = Math.Min(minSize, canvasHeight);
            w = h * aspect;
        }

        // 锚定：角手柄固定对角；左右手柄固定对边并保持垂直居中；上下手柄固定对边并保持水平居中。
        double x, y;
        switch (handle)
        {
            case CropHandle.NW:
                x = raw.Right - w;
                y = raw.Bottom - h;
                break;
            case CropHandle.NE:
                x = raw.Left;
                y = raw.Bottom - h;
                break;
            case CropHandle.SE:
                x = raw.Left;
                y = raw.Top;
                break;
            case CropHandle.SW:
                x = raw.Right - w;
                y = raw.Top;
                break;
            case CropHandle.E:
                x = raw.Left;
                y = raw.Top + raw.Height / 2 - h / 2;
                break;
            case CropHandle.W:
                x = raw.Right - w;
                y = raw.Top + raw.Height / 2 - h / 2;
                break;
            case CropHandle.S:
                y = raw.Top;
                x = raw.Left + raw.Width / 2 - w / 2;
                break;
            default: // N
                y = raw.Bottom - h;
                x = raw.Left + raw.Width / 2 - w / 2;
                break;
        }

        x = Math.Clamp(x, 0, Math.Max(0, canvasWidth - w));
        y = Math.Clamp(y, 0, Math.Max(0, canvasHeight - h));
        return new CropBox(x, y, w, h);
    }
}

/// <summary>输出尺寸计算（纯函数）：自定义宽高优先，其次长边压缩，最后受内存上限约束。</summary>
public static class OutputSize
{
    public const int MaxEdge = 20000;
    public const long MaxPixels = 40_000_000;

    public static (int Width, int Height) Resolve(
        int visibleWidth, int visibleHeight,
        int? customWidth, int? customHeight, int? maxLongEdge)
    {
        if (visibleWidth <= 0 || visibleHeight <= 0) return (1, 1);

        double w = visibleWidth;
        double h = visibleHeight;

        var hasCustomW = customWidth is int cw && cw > 0;
        var hasCustomH = customHeight is int ch && ch > 0;

        if (hasCustomW && hasCustomH)
        {
            // 同时指定宽高时按「等比适配」处理：把可见区域等比缩进目标框，
            // 不改变宽高比（直接拉伸会把 4:3 的照片塞进 16:9，人脸被压扁）。
            // 想要精确的 1920×1080，可以先把裁剪比例设为 16:9 再自定义尺寸。
            var scale = Math.Min(
                customWidth!.Value / (double)visibleWidth,
                customHeight!.Value / (double)visibleHeight);
            w = visibleWidth * scale;
            h = visibleHeight * scale;
        }
        else if (hasCustomW)
        {
            w = customWidth!.Value;
            h = w * visibleHeight / visibleWidth;
        }
        else if (hasCustomH)
        {
            h = customHeight!.Value;
            w = h * visibleWidth / visibleHeight;
        }
        else if (maxLongEdge is int max && max > 0)
        {
            var longEdge = Math.Max(visibleWidth, visibleHeight);
            if (longEdge > max)
            {
                var scale = (double)max / longEdge;
                w = visibleWidth * scale;
                h = visibleHeight * scale;
            }
        }

        // 内存保护：先压长边，再压总像素
        var edge = Math.Max(w, h);
        if (edge > MaxEdge)
        {
            var scale = MaxEdge / edge;
            w *= scale;
            h *= scale;
        }

        if (w * h > MaxPixels)
        {
            var scale = Math.Sqrt(MaxPixels / (w * h));
            w *= scale;
            h *= scale;
        }

        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }
}
