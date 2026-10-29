using MediaManager.Core.Plugins;
using Xunit;

namespace MediaManager.Tests;

/// <summary>
/// 插件动作熔断计数器。这是「插件崩了不能拖累主程序 / 用户」这条承诺的开关，
/// 语义必须钉死：只在正好达到阈值的那一次触发，之后不再重复。
/// </summary>
public class PluginFaultTrackerTests
{
    [Fact]
    public void 达到阈值的那一次才触发()
    {
        var t = new PluginFaultTracker(threshold: 3);
        Assert.False(t.Record("A"));
        Assert.False(t.Record("A"));
        Assert.True(t.Record("A"));
    }

    [Fact]
    public void 触发之后继续失败不再重复触发()
    {
        // 熔断后动作已被撤下，理论上不会再有失败上报；
        // 万一有（竞态），重复弹窗/重复撤菜单比不熔断更糟
        var t = new PluginFaultTracker(threshold: 2);
        Assert.False(t.Record("A"));
        Assert.True(t.Record("A"));
        Assert.False(t.Record("A"));
        Assert.False(t.Record("A"));
    }

    [Fact]
    public void 不同插件分开计数()
    {
        var t = new PluginFaultTracker(threshold: 3);
        t.Record("A");
        t.Record("A");
        t.Record("B");
        Assert.True(t.Record("A"));  // A 到 3 次
        Assert.False(t.Record("B")); // B 才第 2 次
    }

    [Fact]
    public void 插件名不区分大小写()
    {
        // 插件名的来源（plugin.json / 插件自报）大小写不稳定，归组必须稳
        var t = new PluginFaultTracker(threshold: 2);
        t.Record("MyPlugin");
        Assert.True(t.Record("myplugin"));
    }

    [Fact]
    public void Reset清零后重新计数()
    {
        var t = new PluginFaultTracker(threshold: 2);
        t.Record("A");
        t.Reset("A");
        Assert.Equal(0, t.Count("A"));
        Assert.False(t.Record("A"));
    }

    [Fact]
    public void Count返回累计次数()
    {
        var t = new PluginFaultTracker(threshold: 5);
        t.Record("A");
        t.Record("A");
        Assert.Equal(2, t.Count("A"));
        Assert.Equal(0, t.Count("B"));
    }

    [Fact]
    public void 阈值小于1直接拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginFaultTracker(0));
    }

    [Fact]
    public void null插件名按空串归组不抛异常()
    {
        var t = new PluginFaultTracker(threshold: 1);
        Assert.True(t.Record(null!));
        Assert.Equal(1, t.Count(null!));
        t.Reset(null!);
        Assert.Equal(0, t.Count(null!));
    }
}
