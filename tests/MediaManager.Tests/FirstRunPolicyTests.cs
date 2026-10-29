using MediaManager.Core.Services;

namespace MediaManager.Tests;

/// <summary>
/// 「首次启动该不该问一句」的守门测试。
///
/// 判定本身只有一行，但错一次的代价不对称：
/// - 该问没问 → 新用户继续面对 9 个入口（老问题，但不致命）
/// - 不该问却问了 → 老用户升级后看到选择框，第一反应是「我的库是不是被清了」
///
/// 所以"老库不打扰"这条要单独钉住。
/// </summary>
public class FirstRunPolicyTests
{
    [Fact]
    public void 全新库_要问()
    {
        Assert.True(FirstRunPolicy.ShouldAsk(alreadyDone: false, rootFolderCount: 0, mediaCount: 0));
        Assert.False(FirstRunPolicy.ShouldSilentlyMarkDone(false, 0, 0));
    }

    [Fact]
    public void 问过之后_不再问也不重复补标记()
    {
        Assert.False(FirstRunPolicy.ShouldAsk(alreadyDone: true, rootFolderCount: 0, mediaCount: 0));
        Assert.False(FirstRunPolicy.ShouldSilentlyMarkDone(true, 0, 0));
    }

    [Theory]
    [InlineData(1, 0L)]     // 加了文件夹但还没扫出东西
    [InlineData(0, 1L)]     // 只有媒体没有根目录（导入/恢复出来的库）
    [InlineData(3, 1200L)]  // 正常在用的库
    public void 老库升级_不弹窗只补标记(int roots, long media)
    {
        Assert.False(FirstRunPolicy.ShouldAsk(alreadyDone: false, roots, media));
        Assert.True(FirstRunPolicy.ShouldSilentlyMarkDone(false, roots, media));
    }

    [Fact]
    public void 计数为负数_当成空库不倒向老用户()
    {
        // 计数来自 SQL COUNT(*)，理论上不会是负数；真出现说明查询出了问题。
        // 这种情况下宁可问一句，也别把新用户误判成老用户、让他继续面对满屏入口。
        Assert.True(FirstRunPolicy.ShouldAsk(false, -1, -1));
    }

    [Fact]
    public void 设置键名固定_改名会让所有人重回首启()
    {
        Assert.Equal("Ui.FirstRunDone", FirstRunPolicy.DoneSettingKey);
    }
}
