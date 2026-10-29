using MediaManager.App.Services;

namespace MediaManager.App.Tests;

/// <summary>
/// 全局快捷键动作清单的完整性。设置页、注册服务、帮助文档读同一份清单，
/// 这里守的是「三处不会各写一遍还对不上」的前提。
/// </summary>
public class HotkeyActionsTests
{
    [Fact]
    public void SettingKey统一前缀()
    {
        Assert.Equal("Hotkey.ShowWindow", HotkeyActions.SettingKey(HotkeyActions.ShowWindow));
        Assert.StartsWith("Hotkey.", HotkeyActions.SettingKey("Anything"));
    }

    [Fact]
    public void 所有动作Id唯一()
    {
        var ids = HotkeyActions.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void 常量与清单一致()
    {
        // 常量被代码里到处引用，清单被界面遍历：两边对不上就是「设置页有这项但热键不生效」
        Assert.Contains(HotkeyActions.All, d => d.Id == HotkeyActions.ShowWindow);
        Assert.Contains(HotkeyActions.All, d => d.Id == HotkeyActions.Discover);
        Assert.Contains(HotkeyActions.All, d => d.Id == HotkeyActions.RandomMedia);
        Assert.Contains(HotkeyActions.All, d => d.Id == HotkeyActions.Settings);
        Assert.Equal(4, HotkeyActions.All.Count);
    }

    [Fact]
    public void 只有显示主窗口有默认组合键()
    {
        // 全局热键是系统级抢占，不该在用户没同意时占一堆 —— 这是写进注释里的约定
        Assert.Equal("Ctrl+Alt+Shift+M", HotkeyActions.All.Single(d => d.Id == HotkeyActions.ShowWindow).Default);
        Assert.All(HotkeyActions.All.Where(d => d.Id != HotkeyActions.ShowWindow),
            d => Assert.Equal(string.Empty, d.Default));
    }

    [Fact]
    public void 每个动作都有界面文案()
    {
        Assert.All(HotkeyActions.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.Hint));
        });
    }
}
