using MediaManager.Core.Editing;

namespace MediaManager.Tests;

/// <summary>
/// 导出坐标 / 尺寸的绝对安全检查。
/// 目标：无论编辑状态里出现 NaN、越界、负宽高还是超大字号，
/// 都不能生成越界元素，更不能在导出图上留下整块黑底或白底。
/// </summary>
public class OverlayGeometryTests
{
    [Theory]
    [InlineData(0.0, 0.0, 0.5, 0.5, 0, 0, 100, 100)]
    [InlineData(0.5, 0.5, 0.5, 0.5, 100, 100, 100, 100)]
    public void NormalRect_MapsToPixels(
        double x, double y, double w, double h, int ex, int ey, int ew, int eh)
    {
        Assert.True(OverlayGeometry.TryToPixelRect(x, y, w, h, 200, 200, out var px, out var py, out var pw, out var ph));
        Assert.Equal(ex, px);
        Assert.Equal(ey, py);
        Assert.Equal(ew, pw);
        Assert.Equal(eh, ph);
    }

    [Fact]
    public void OutOfBoundsRect_IsClampedIntoCanvas()
    {
        // 右下角越界：必须被裁到画布内，而不是溢出
        Assert.True(OverlayGeometry.TryToPixelRect(0.8, 0.8, 0.5, 0.5, 100, 100, out var px, out var py, out var pw, out var ph));

        Assert.Equal(80, px);
        Assert.Equal(80, py);
        Assert.Equal(20, pw);
        Assert.Equal(20, ph);
        Assert.True(px + pw <= 100);
        Assert.True(py + ph <= 100);
    }

    [Fact]
    public void NegativeRect_IsNormalized()
    {
        // 反向拖拽产生负宽高时，应按左上 → 右下归一化
        Assert.True(OverlayGeometry.TryToPixelRect(0.6, 0.6, -0.4, -0.4, 100, 100, out var px, out var py, out var pw, out var ph));
        Assert.Equal(20, px);
        Assert.Equal(20, py);
        Assert.Equal(40, pw);
        Assert.Equal(40, ph);
    }

    [Theory]
    [InlineData(1.2, 0.5, 0.2, 0.2)]   // 完全在右侧画布外
    [InlineData(0.5, -0.5, 0.2, 0.2)]  // 完全在上方画布外
    [InlineData(1.0, 0.5, 0.2, 0.2)]   // 起点正好在右边界
    public void FullyOutside_ReturnsFalse(double x, double y, double w, double h)
    {
        Assert.False(OverlayGeometry.TryToPixelRect(x, y, w, h, 100, 100, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValues_AreRejected(double bad)
    {
        Assert.False(OverlayGeometry.TryToPixelRect(bad, 0.1, 0.2, 0.2, 100, 100, out _, out _, out _, out _));
        Assert.Equal(0, OverlayGeometry.ClampUnit(bad));
        Assert.Equal(1, OverlayGeometry.ClampOpacity(double.NaN));
        Assert.Equal(6, OverlayGeometry.ClampFontSize(bad, 1000, 1000));
    }

    [Fact]
    public void ZeroCanvasSize_ReturnsFalse()
    {
        Assert.False(OverlayGeometry.TryToPixelRect(0, 0, 1, 1, 0, 0, out _, out _, out _, out _));
    }

    [Fact]
    public void HugeFont_IsCappedToHalfOfShortEdge()
    {
        // 字号 99999 在 1000×500 画布上最多 250，否则会把图糊成一整块纯色
        Assert.Equal(250, OverlayGeometry.ClampFontSize(99999, 1000, 500));
        Assert.Equal(6, OverlayGeometry.ClampFontSize(0.1, 1000, 500));
        Assert.Equal(48, OverlayGeometry.ClampFontSize(48, 1000, 500));
    }

    [Fact]
    public void Thickness_IsClamped()
    {
        // 非法线宽按画布宽度的 0.2% 兜底（1000px 宽 → 2px），不是 0
        Assert.Equal(2, OverlayGeometry.ClampThickness(0, 1000));
        Assert.Equal(250, OverlayGeometry.ClampThickness(99999, 1000));
        Assert.Equal(4, OverlayGeometry.ClampThickness(4, 1000));
    }

    [Fact]
    public void OutputSize_IsClamped()
    {
        Assert.Equal((1, 1), OverlayGeometry.ClampOutputSize(0, -5));

        var (w, h) = OverlayGeometry.ClampOutputSize(40000, 20000, 20000);
        Assert.Equal(20000, w);
        Assert.Equal(10000, h);

        var (w2, h2) = OverlayGeometry.ClampOutputSize(1200, 800);
        Assert.Equal(1200, w2);
        Assert.Equal(800, h2);
    }

    [Fact]
    public void Opacity_IsClamped()
    {
        Assert.Equal(0, OverlayGeometry.ClampOpacity(-1));
        Assert.Equal(1, OverlayGeometry.ClampOpacity(3));
        Assert.Equal(0.55, OverlayGeometry.ClampOpacity(0.55));
    }
}
