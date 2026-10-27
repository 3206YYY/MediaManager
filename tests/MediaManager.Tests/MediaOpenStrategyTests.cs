using MediaManager.Core.Enums;
using MediaManager.Core.Media;

namespace MediaManager.Tests;

/// <summary>
/// 「哪些类型进内置查看器、哪些交给系统默认程序」的守门测试。
/// 这条分流是产品决策（图片视频必须内置，其余外放），不是实现细节 ——
/// 以后加新格式时如果顺手改了默认值，这里会先炸。
/// </summary>
public class MediaOpenStrategyTests
{
    [Theory]
    [InlineData(MediaType.Image)]
    [InlineData(MediaType.Video)]
    public void ImageAndVideo_UseBuiltInViewer(MediaType type)
    {
        Assert.False(MediaOpenStrategy.UseExternalApp(type));
        Assert.True(MediaOpenStrategy.UseBuiltInViewer(type));
    }

    [Theory]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.Text)]
    [InlineData(MediaType.Archive)]
    public void AudioTextArchive_GoToSystemDefaultApp(MediaType type)
    {
        Assert.True(MediaOpenStrategy.UseExternalApp(type));
        Assert.False(MediaOpenStrategy.UseBuiltInViewer(type));
    }

    /// <summary>两个判断必须永远互补，不能出现「两个都 false」让双击变成哑巴。</summary>
    [Fact]
    public void Strategy_IsAlwaysMutuallyExclusive()
    {
        foreach (var type in Enum.GetValues<MediaType>())
        {
            var external = MediaOpenStrategy.UseExternalApp(type);
            var builtIn = MediaOpenStrategy.UseBuiltInViewer(type);

            Assert.True(external ^ builtIn, $"{type} 的两个判断不是互补关系");
        }
    }

    /// <summary>枚举里每个值都要有明确归宿（新增类型时必须来这张表登记）。</summary>
    [Fact]
    public void EveryMediaType_IsCovered()
    {
        var expected = new Dictionary<MediaType, bool>
        {
            [MediaType.Image] = false,     // 内置
            [MediaType.Video] = false,     // 内置
            [MediaType.Audio] = true,      // 外部
            [MediaType.Text] = true,       // 外部
            [MediaType.Archive] = true,    // 外部
        };

        Assert.Equal(
            expected.Keys.OrderBy(k => k),
            Enum.GetValues<MediaType>().OrderBy(k => k));

        foreach (var (type, goExternal) in expected)
            Assert.Equal(goExternal, MediaOpenStrategy.UseExternalApp(type));
    }
}
