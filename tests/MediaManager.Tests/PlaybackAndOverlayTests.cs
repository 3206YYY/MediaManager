using MediaManager.Core.Editing;
using MediaManager.Core.Media;

namespace MediaManager.Tests;

/// <summary>
/// 视频 Seek 合并器测试：模拟「快速连点 / 连续拖动进度条」产生的请求风暴，
/// 确保底层 Seek 始终串行、且总是落到最后一次的目标上（这正是卡死的根因）。
/// </summary>
public class SeekRequestCoalescerTests
{
    [Fact]
    public void FirstRequest_ShouldBeginImmediately()
    {
        var c = new SeekRequestCoalescer();

        Assert.True(c.TryBegin(1000, out var target));
        Assert.Equal(1000, target);
        Assert.True(c.IsRunning);
    }

    [Fact]
    public void WhileRunning_NewRequests_AreMerged_NotQueued()
    {
        var c = new SeekRequestCoalescer();
        Assert.True(c.TryBegin(1000, out _));

        // 播放器还在执行第一个 Seek，此时用户又狂点了 5 下
        for (var i = 2; i <= 6; i++)
            Assert.False(c.TryBegin(i * 1000, out _));

        // 只有最新那个会被保留
        Assert.True(c.TryContinue(out var next));
        Assert.Equal(6000, next);

        // 之后没有新请求 → 结束
        Assert.False(c.TryContinue(out _));
        Assert.False(c.IsRunning);
    }

    [Fact]
    public void BurstDuringExecution_ShouldCollapseToLatestTarget()
    {
        var c = new SeekRequestCoalescer();
        var executed = new List<long>();

        // 第一个 Seek 已经下发（底层正在跑）
        Assert.True(c.TryBegin(100, out var current));

        // 执行期间用户又狂点/狂拖了 199 次
        for (var i = 2; i <= 200; i++)
            c.TryBegin(i * 10, out _);

        // 串行补跑：只应该补一次，且落在最后一个目标上
        while (true)
        {
            executed.Add(current);
            if (!c.TryContinue(out current)) break;
        }

        Assert.Equal(new long[] { 100, 2000 }, executed);
        Assert.Equal(199, c.DroppedCount);
        Assert.False(c.IsRunning);
    }

    [Fact]
    public void ConcurrentBurst_ShouldNeverRunSeeksInParallel()
    {
        var c = new SeekRequestCoalescer();
        var executed = new List<long>();
        var running = 0;
        var maxParallel = 0;
        var gate = new object();

        void Worker(long first)
        {
            var target = first;
            while (true)
            {
                lock (gate)
                {
                    running++;
                    maxParallel = Math.Max(maxParallel, running);
                    executed.Add(target);
                }

                Thread.Sleep(5); // 模拟底层 Seek 的耗时

                lock (gate) running--;

                if (!c.TryContinue(out target)) break;
            }
        }

        // 每 1ms 提交一个，底层每次要 5ms → 必然出现「执行期间又来了新请求」
        for (var i = 1; i <= 100; i++)
        {
            if (c.TryBegin(i * 10, out var first))
                _ = Task.Run(() => Worker(first));

            Thread.Sleep(1);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (c.IsRunning && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.False(c.IsRunning);
        Assert.Equal(1, maxParallel);              // 永远不会并发执行
        Assert.Equal(1000, executed[^1]);          // 最终一定落到最后一个目标
        Assert.True(executed.Count < 100);         // 中间请求被合并
    }

    [Fact]
    public void Reset_ShouldClearPendingState()
    {
        var c = new SeekRequestCoalescer();
        Assert.True(c.TryBegin(500, out _));
        c.TryBegin(900, out _);

        c.Reset();

        Assert.False(c.IsRunning);
        Assert.True(c.TryBegin(300, out var target)); // 重置后可以重新开始
        Assert.Equal(300, target);
    }
}

/// <summary>
/// 文字叠加层落点测试：连续添加多段文字时不能全部堆在同一个坐标上。
/// </summary>
public class TextOverlayPlacementTests
{
    [Fact]
    public void ConsecutiveTexts_ShouldNotOverlapExactly()
    {
        var points = Enumerable.Range(0, TextOverlayPlacement.CascadeSlots)
            .Select(i => TextOverlayPlacement.Next(i, isWatermark: false))
            .ToList();

        Assert.Equal(points.Count, points.Distinct().Count());
    }

    [Fact]
    public void Placement_ShouldStayInsideCanvas()
    {
        for (var i = 0; i < 50; i++)
        {
            var (x, y) = TextOverlayPlacement.Next(i, isWatermark: false);
            Assert.InRange(x, 0, 1);
            Assert.InRange(y, 0, 1);

            var (wx, wy) = TextOverlayPlacement.Next(i, isWatermark: true);
            Assert.InRange(wx, 0, 1);
            Assert.InRange(wy, 0, 1);
        }
    }

    [Fact]
    public void Watermark_ShouldSitBelowRegularText()
    {
        var (_, textY) = TextOverlayPlacement.Next(0, isWatermark: false);
        var (_, markY) = TextOverlayPlacement.Next(0, isWatermark: true);

        Assert.True(markY > textY);
    }

    [Fact]
    public void NegativeCount_ShouldBeTreatedAsZero()
    {
        Assert.Equal(TextOverlayPlacement.Next(0, false), TextOverlayPlacement.Next(-5, false));
    }
}

/// <summary>
/// 文字有效性测试：空内容 / 越界坐标不能进入导出结果。
/// </summary>
public class OverlayTextRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void EmptyContent_IsNotRenderable(string? text)
    {
        Assert.False(OverlayTextRules.HasContent(text));
        Assert.False(OverlayTextRules.IsRenderable(text, 0.2, 0.2));
    }

    [Fact]
    public void NormalContent_IsRenderable()
    {
        Assert.True(OverlayTextRules.IsRenderable("你好", 0.2, 0.3));
        Assert.True(OverlayTextRules.IsRenderable("a", 0, 0));
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(1.0, 0.5)]
    [InlineData(0.5, -1)]
    [InlineData(0.5, 2)]
    [InlineData(double.NaN, 0.5)]
    public void OutOfRangePosition_IsNotRenderable(double x, double y)
    {
        Assert.False(OverlayTextRules.HasValidPosition(x, y));
        Assert.False(OverlayTextRules.IsRenderable("有效文字", x, y));
    }
}
