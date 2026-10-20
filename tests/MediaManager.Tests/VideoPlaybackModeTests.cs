using MediaManager.Core.Media;

namespace MediaManager.Tests;

public class VideoPlaybackModeTests
{
    [Fact]
    public void StorageValue_RoundTrips()
    {
        foreach (var mode in new[]
                 {
                     VideoPlaybackMode.Sequential,
                     VideoPlaybackMode.PauseAtEnd,
                     VideoPlaybackMode.Random,
                 })
        {
            var raw = mode.ToStorageValue();
            Assert.Equal(mode, VideoPlaybackModes.Parse(raw));
        }
    }

    /// <summary>
    /// 从未设置（null / 空字符串）或无法识别的值 → 回落到默认的「播完暂停」。
    /// 默认值必须是「播完暂停」：不替用户决定要不要接着放下一个。
    /// </summary>
    [Theory]
    [InlineData(null, VideoPlaybackMode.PauseAtEnd)]
    [InlineData("", VideoPlaybackMode.PauseAtEnd)]
    [InlineData("垃圾值", VideoPlaybackMode.PauseAtEnd)]
    [InlineData("sequential", VideoPlaybackMode.Sequential)]
    [InlineData("  PAUSE ", VideoPlaybackMode.PauseAtEnd)]
    [InlineData("shuffle", VideoPlaybackMode.Random)]
    public void Parse_FallsBackToPauseAtEnd(string? raw, VideoPlaybackMode expected)
        => Assert.Equal(expected, VideoPlaybackModes.Parse(raw));

    [Fact]
    public void Default_IsPauseAtEnd()
        => Assert.Equal(VideoPlaybackMode.PauseAtEnd, VideoPlaybackModes.Default);

    [Fact]
    public void Display_IsNotEmpty()
    {
        foreach (var mode in Enum.GetValues<VideoPlaybackMode>())
            Assert.False(string.IsNullOrWhiteSpace(mode.ToDisplay()));
    }
}

public class PlaybackQueueTests
{
    [Fact]
    public void EmptyList_HasNoNext()
        => Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.Sequential, 0, 0, 0));

    [Fact]
    public void SingleVideo_HasNoNext()
    {
        // 只有一个视频时「下一个」还是自己，会导致播完立刻从头再来 → 判定为结束
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.Sequential, 0, 1, 7));
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.Random, 0, 1, 7));
    }

    [Fact]
    public void PauseAtEnd_AlwaysStops()
    {
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.PauseAtEnd, 0, 10, 3));
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.PauseAtEnd, 5, 10, 3));
    }

    [Fact]
    public void Sequential_AdvancesAndLoops()
    {
        Assert.Equal(4, PlaybackQueue.Next(VideoPlaybackMode.Sequential, 3, 10, 0));
        Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.Sequential, 9, 10, 0));
    }

    [Fact]
    public void Sequential_FromUnknownIndex_StartsAtFirst()
        => Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.Sequential, -1, 5, 0));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_SkipsCurrent(int current)
    {
        var next = PlaybackQueue.Next(VideoPlaybackMode.Random, current, 4, 0);
        Assert.NotEqual(current, next);
        Assert.InRange(next, 0, 3);
    }

    [Fact]
    public void Random_NeverReturnsCurrent_ForAllSeeds()
    {
        for (var count = 2; count <= 8; count++)
        for (var current = 0; current < count; current++)
        for (var seed = 0; seed < 200; seed++)
        {
            var next = PlaybackQueue.Next(VideoPlaybackMode.Random, current, count, seed);
            Assert.InRange(next, 0, count - 1);
            Assert.NotEqual(current, next);
        }
    }

    [Fact]
    public void Random_IsDeterministicForSameSeed()
    {
        var a = PlaybackQueue.Next(VideoPlaybackMode.Random, 2, 6, 12345);
        var b = PlaybackQueue.Next(VideoPlaybackMode.Random, 2, 6, 12345);
        Assert.Equal(a, b);
    }
}
