using MediaManager.Core.Editing;

namespace MediaManager.Tests;

/// <summary>V1.3.0 编辑器新增算法的回归测试（纯函数，无 WPF 依赖）。</summary>
public class EditorV13Tests
{
    // ---------- 调色 / 滤镜 ----------

    [Fact]
    public void Identity_LeavesPixelsUnchanged()
    {
        var (r, g, b) = ColorGrading.Apply(30, 120, 200, ColorAdjustments.None, EditorFilter.None);
        Assert.Equal(30, r);
        Assert.Equal(120, g);
        Assert.Equal(200, b);
    }

    [Fact]
    public void Brightness_MovesAllChannelsBySameAmount()
    {
        var darker = ColorGrading.Apply(100, 100, 100, new ColorAdjustments(-50, 0, 0), EditorFilter.None);
        var brighter = ColorGrading.Apply(100, 100, 100, new ColorAdjustments(50, 0, 0), EditorFilter.None);

        Assert.Equal(70, darker.R);
        Assert.Equal(130, brighter.R);
        Assert.Equal(darker.R, darker.G);
        Assert.Equal(70, darker.B);
    }

    [Fact]
    public void Contrast_PushesAwayFromMidGray()
    {
        var high = ColorGrading.Apply(180, 180, 180, new ColorAdjustments(0, 100, 0), EditorFilter.None);
        var low = ColorGrading.Apply(180, 180, 180, new ColorAdjustments(0, -100, 0), EditorFilter.None);

        Assert.True(high.R > 180);
        Assert.True(low.R < 180);
        // 中点保持不变
        var mid = ColorGrading.Apply(128, 128, 128, new ColorAdjustments(0, 100, 0), EditorFilter.None);
        Assert.InRange(mid.R, 126, 130);
    }

    [Fact]
    public void Saturation_Zero_ProducesGray()
    {
        var (r, g, b) = ColorGrading.Apply(200, 100, 50, new ColorAdjustments(0, 0, -100), EditorFilter.None);
        Assert.InRange(Math.Abs(r - g), 0, 1);
        Assert.InRange(Math.Abs(g - b), 0, 1);
    }

    [Fact]
    public void GrayFilter_ProducesEqualChannels()
    {
        var (r, g, b) = ColorGrading.Apply(10, 200, 60, ColorAdjustments.None, EditorFilter.Gray);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
    }

    [Fact]
    public void Filters_AreDistinctFromOriginal()
    {
        var original = ColorGrading.Apply(120, 110, 100, ColorAdjustments.None, EditorFilter.None);
        foreach (var filter in new[] { EditorFilter.Warm, EditorFilter.Retro, EditorFilter.Japanese })
        {
            var result = ColorGrading.Apply(120, 110, 100, ColorAdjustments.None, filter);
            Assert.NotEqual(original, result);
        }
    }

    [Fact]
    public void Adjustments_ClampAndRejectNonFinite()
    {
        var adj = new ColorAdjustments(1000, -1000, double.NaN).Clamped();
        Assert.Equal(100, adj.Brightness);
        Assert.Equal(-100, adj.Contrast);
        Assert.Equal(0, adj.Saturation);

        var (r, g, b) = ColorGrading.Apply(200, 200, 200, new ColorAdjustments(100, 100, 100), EditorFilter.Japanese);
        Assert.InRange(r, 0, (byte)255);
        Assert.InRange(g, 0, (byte)255);
        Assert.InRange(b, 0, (byte)255);
    }

    // ---------- 裁剪比例 ----------

    [Fact]
    public void Drag_SquareAspect_KeepsRatio_OnCorner()
    {
        var r = CropMath.Drag(new CropBox(10, 10, 100, 100), CropHandle.SE, 60, 0,
            aspect: 1.0, canvasWidth: 400, canvasHeight: 400);

        Assert.Equal(160, r.Width, 1);
        Assert.Equal(160, r.Height, 1);
        Assert.Equal(10, r.X, 1);
        Assert.Equal(10, r.Y, 1);
    }

    [Fact]
    public void Drag_WideAspect_OnEdge_KeepsRatio()
    {
        var r = CropMath.Drag(new CropBox(0, 0, 160, 90), CropHandle.E, 40, 0,
            aspect: 16.0 / 9.0, canvasWidth: 400, canvasHeight: 400);

        Assert.Equal(200, r.Width, 1);
        Assert.Equal(200 * 9.0 / 16.0, r.Height, 1);
        Assert.Equal(0, r.X, 1);
    }

    [Fact]
    public void Drag_NeverLeavesCanvas()
    {
        var r = CropMath.Drag(new CropBox(300, 300, 90, 90), CropHandle.SE, 500, 500,
            aspect: null, canvasWidth: 400, canvasHeight: 400);

        Assert.True(r.Right <= 400.001);
        Assert.True(r.Bottom <= 400.001);
        Assert.True(r.Left >= -0.001);
        Assert.True(r.Top >= -0.001);
    }

    [Fact]
    public void Drag_Move_ClampsInsideCanvas()
    {
        var r = CropMath.Drag(new CropBox(50, 50, 100, 100), CropHandle.Move, -200, 400,
            aspect: null, canvasWidth: 400, canvasHeight: 300);

        Assert.Equal(0, r.X);
        Assert.Equal(200, r.Y);
        Assert.Equal(100, r.Width);
        Assert.Equal(100, r.Height);
    }

    [Fact]
    public void Drag_RespectsMinimumSize()
    {
        var r = CropMath.Drag(new CropBox(10, 10, 100, 100), CropHandle.SE, -500, -500,
            aspect: null, canvasWidth: 400, canvasHeight: 400);

        Assert.True(r.Width >= CropMath.MinSize - 0.001);
        Assert.True(r.Height >= CropMath.MinSize - 0.001);
    }

    [Fact]
    public void FitInside_ProducesExactRatio_AndFitsCanvas()
    {
        var r = CropMath.FitInside(new CropBox(0, 0, 400, 100), 1.0, 400, 300);

        Assert.Equal(r.Width, r.Height, 1);
        Assert.True(r.Width <= 300.001);
        Assert.True(r.Left >= -0.001 && r.Top >= -0.001);
    }

    // ---------- 输出尺寸 ----------

    [Fact]
    public void OutputSize_CustomWidth_DerivesHeightFromAspect()
    {
        var (w, h) = OutputSize.Resolve(4000, 3000, 1920, null, null);
        Assert.Equal(1920, w);
        Assert.Equal(1440, h);
    }

    [Fact]
    public void OutputSize_CustomWidthAndHeight_FitsInsideWithoutDistortion()
    {
        // 4:3 的图放进 1000×1000 的框：等比缩到 1000×750，而不是拉成正方形
        var (w, h) = OutputSize.Resolve(4000, 3000, 1000, 1000, 1280);
        Assert.Equal(1000, w);
        Assert.Equal(750, h);
    }

    [Fact]
    public void OutputSize_CustomBoxWithDifferentAspect_KeepsSourceAspect()
    {
        // 4:3 的图放进 1920×1080（16:9）的框：高度受限，输出 1440×1080
        var (w, h) = OutputSize.Resolve(4000, 3000, 1920, 1080, null);
        Assert.Equal(1440, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void OutputSize_MaxLongEdge_ScalesProportionally()
    {
        var (w, h) = OutputSize.Resolve(4000, 3000, null, null, 1920);
        Assert.Equal(1920, w);
        Assert.Equal(1440, h);
    }

    [Fact]
    public void OutputSize_ClampsAbsurdValues()
    {
        var (w, h) = OutputSize.Resolve(4000, 3000, 40000, 30000, null);
        Assert.True(Math.Max(w, h) <= OutputSize.MaxEdge);
        Assert.True((long)w * h <= OutputSize.MaxPixels);

        var (ew, eh) = OutputSize.Resolve(0, 0, null, null, null);
        Assert.Equal(1, ew);
        Assert.Equal(1, eh);
    }
}
