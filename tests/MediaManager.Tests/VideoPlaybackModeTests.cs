using MediaManager.Core.Media;

namespace MediaManager.Tests;

public class VideoPlaybackModeTests
{
    [Fact]
    public void StorageValue_RoundTrips()
    {
        foreach (var mode in Enum.GetValues<VideoPlaybackMode>())
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
    [InlineData("  PAUSE ", VideoPlaybackMode.PauseAtEnd)]
    [InlineData("list", VideoPlaybackMode.LoopList)]
    [InlineData("single", VideoPlaybackMode.LoopSingle)]
    [InlineData("shuffle", VideoPlaybackMode.Random)]
    public void Parse_FallsBackToPauseAtEnd(string? raw, VideoPlaybackMode expected)
        => Assert.Equal(expected, VideoPlaybackModes.Parse(raw));

    /// <summary>V1.2.4 存的是 "sequential"（旧的「连续播放」）→ 迁移成「列表循环」。</summary>
    [Theory]
    [InlineData("sequential")]
    [InlineData("next")]
    [InlineData("auto")]
    public void Parse_MigratesLegacySequentialToListLoop(string raw)
        => Assert.Equal(VideoPlaybackMode.LoopList, VideoPlaybackModes.Parse(raw));

    [Fact]
    public void Default_IsPauseAtEnd()
        => Assert.Equal(VideoPlaybackMode.PauseAtEnd, VideoPlaybackModes.Default);

    [Fact]
    public void Display_IsNotEmpty()
    {
        foreach (var mode in Enum.GetValues<VideoPlaybackMode>())
        {
            Assert.False(string.IsNullOrWhiteSpace(mode.ToDisplay()));
            Assert.False(string.IsNullOrWhiteSpace(mode.ToHint()));
        }
    }

    /// <summary>四种模式的名字必须互不相同，否则用户还是分不清。</summary>
    [Fact]
    public void Display_AllDistinct()
    {
        var names = Enum.GetValues<VideoPlaybackMode>().Select(m => m.ToDisplay()).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}

public class PlaybackQueueTests
{
    [Fact]
    public void EmptyList_HasNoNext()
        => Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.LoopList, 0, 0, 0));

    [Fact]
    public void PauseAtEnd_AlwaysStops()
    {
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.PauseAtEnd, 0, 10, 3));
        Assert.Equal(-1, PlaybackQueue.Next(VideoPlaybackMode.PauseAtEnd, 5, 10, 3));
    }

    /// <summary>
    /// 单个视频 + 列表循环 → 重播自己。
    /// 这是 V1.2.4 的 bug：选了「连续播放」仍停在结束画面，因为旧实现把 count==1 直接判定为结束。
    /// </summary>
    [Fact]
    public void LoopList_SingleVideo_ReplaysItself()
        => Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.LoopList, 0, 1, 7));

    [Fact]
    public void LoopSingle_AlwaysReplaysCurrent()
    {
        Assert.Equal(3, PlaybackQueue.Next(VideoPlaybackMode.LoopSingle, 3, 10, 0));
        Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.LoopSingle, 0, 1, 0));
    }

    [Fact]
    public void LoopSingle_FromUnknownIndex_StartsAtFirst()
        => Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.LoopSingle, -1, 5, 0));

    [Fact]
    public void LoopList_AdvancesAndWraps()
    {
        Assert.Equal(4, PlaybackQueue.Next(VideoPlaybackMode.LoopList, 3, 10, 0));
        Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.LoopList, 9, 10, 0));
    }

    [Fact]
    public void LoopList_FromUnknownIndex_StartsAtFirst()
        => Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.LoopList, -1, 5, 0));

    [Fact]
    public void Random_SingleVideo_ReplaysItself()
        => Assert.Equal(0, PlaybackQueue.Next(VideoPlaybackMode.Random, 0, 1, 7));

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
