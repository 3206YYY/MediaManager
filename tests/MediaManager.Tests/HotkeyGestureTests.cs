using MediaManager.Core.Input;
using Xunit;

namespace MediaManager.Tests;

/// <summary>
/// 全局快捷键的解析 / 规范化用例。
/// 这里守的是「存库字符串 ↔ 界面显示」的一致性：一旦解析变宽松，
/// 用户手改数据库或旧版本留下的值就会变成一条按不出来的死快捷键。
/// </summary>
public class HotkeyGestureTests
{
    // ---------- 解析 ----------

    [Fact]
    public void Parse_CtrlAltM_ShouldKeepModifiersAndKey()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+M", out var g));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, g.Modifiers);
        Assert.Equal("M", g.Key);
        Assert.Equal("Ctrl+Alt+M", g.ToStorageValue());
    }

    [Fact]
    public void Parse_ShouldBeCaseInsensitiveAndAcceptAliases()
    {
        Assert.True(HotkeyGesture.TryParse("control+alt+m", out var lower));
        Assert.Equal("Ctrl+Alt+M", lower.ToStorageValue());

        Assert.True(HotkeyGesture.TryParse("Meta+Shift+K", out var meta));
        Assert.True(meta.Modifiers.HasFlag(HotkeyModifiers.Win));
        Assert.True(meta.Modifiers.HasFlag(HotkeyModifiers.Shift));
    }

    [Fact]
    public void Parse_ShouldNormalizeModifierOrder()
    {
        Assert.True(HotkeyGesture.TryParse("shift+win+alt+ctrl+k", out var g));
        Assert.Equal("Ctrl+Alt+Shift+Win+K", g.ToStorageValue());
    }

    [Fact]
    public void Parse_ShouldAllowBareFunctionKey()
    {
        Assert.True(HotkeyGesture.TryParse("F1", out var f1));
        Assert.Equal(HotkeyModifiers.None, f1.Modifiers);
        Assert.Equal("F1", f1.ToStorageValue());

        Assert.True(HotkeyGesture.TryParse("f24", out var f24));
        Assert.Equal("F24", f24.Key);
    }

    [Fact]
    public void Parse_ShouldRejectBareLetterOrDigit()
    {
        // 单按字母会成为系统级劫持，连正常打字都受影响，必须拒绝
        Assert.False(HotkeyGesture.TryParse("M", out _));
        Assert.False(HotkeyGesture.TryParse("5", out _));
    }

    [Fact]
    public void Parse_ShouldRejectOutOfRangeFunctionKey()
    {
        Assert.False(HotkeyGesture.TryParse("F0", out _));
        Assert.False(HotkeyGesture.TryParse("F25", out _));
    }

    [Fact]
    public void Parse_ShouldRejectUnknownKey()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+Bogus", out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+", out _));
    }

    [Fact]
    public void Parse_ShouldRejectUnknownModifier()
    {
        // 拼错的修饰键整条作废，绝不「猜一个最像的」
        Assert.False(HotkeyGesture.TryParse("Hyper+M", out _));
        Assert.False(HotkeyGesture.TryParse("Ctr+M", out _));
    }

    [Fact]
    public void Parse_ShouldRejectEmptyInput()
    {
        Assert.False(HotkeyGesture.TryParse(null, out _));
        Assert.False(HotkeyGesture.TryParse("", out _));
        Assert.False(HotkeyGesture.TryParse("   ", out _));
    }

    [Fact]
    public void Parse_ShouldTolerateSpacesAroundTokens()
    {
        Assert.True(HotkeyGesture.TryParse(" Ctrl +  Alt + M ", out var g));
        Assert.Equal("Ctrl+Alt+M", g.ToStorageValue());
    }

    [Fact]
    public void Parse_DigitAliasShouldMapToWpfDKey()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+1", out var g));
        Assert.Equal("D1", g.Key);              // WPF 里主键盘数字是 D1
        Assert.Equal("Ctrl+D1", g.ToStorageValue());
    }

    // ---------- 显示 ----------

    [Fact]
    public void Display_Unset_ShouldReadAsChineseLabel()
    {
        Assert.True(HotkeyGesture.None.IsEmpty);
        Assert.Equal("未设置", HotkeyGesture.None.ToDisplayString());
        Assert.Equal(string.Empty, HotkeyGesture.None.ToStorageValue());
    }

    [Fact]
    public void Display_ShouldUseSymbolsForOemKeys()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+OemTilde", out var tilde));
        Assert.Equal("Ctrl + `", tilde.ToDisplayString());

        Assert.True(HotkeyGesture.TryParse("Ctrl+OemPlus", out var plus));
        Assert.Equal("Ctrl + =", plus.ToDisplayString());
    }

    [Fact]
    public void Display_ShouldShortenDigitsAndNumpad()
    {
        Assert.Equal("1", HotkeyGesture.DisplayKey("D1"));
        Assert.Equal("Num5", HotkeyGesture.DisplayKey("NumPad5"));
        Assert.Equal("PgUp", HotkeyGesture.DisplayKey("PageUp"));
        Assert.Equal("Backspace", HotkeyGesture.DisplayKey("Back"));
        Assert.Equal("↑", HotkeyGesture.DisplayKey("Up"));
    }

    [Fact]
    public void Display_ShouldJoinModifiersWithPlus()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+F5", out var g));
        Assert.Equal("Ctrl + Shift + F5", g.ToDisplayString());
    }

    // ---------- 一致性 ----------

    [Fact]
    public void Storage_ShouldRoundTrip()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+F12", out var first));
        Assert.True(HotkeyGesture.TryParse(first.ToStorageValue(), out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Equality_ShouldCompareByValue()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+M", out var a));
        Assert.True(HotkeyGesture.TryParse("alt+ctrl+m", out var b));
        Assert.Equal(a, b);   // record struct 值相等 → 设置页去重靠这个
    }

    [Fact]
    public void DisplayString_ShouldBeParsableAsInput()
    {
        // 显示串（带空格）也能被 TryParse 吃回去，方便用户手动粘贴
        Assert.True(HotkeyGesture.TryParse("Ctrl + Alt + M", out var g));
        Assert.Equal("Ctrl+Alt+M", g.ToStorageValue());
    }

    [Fact]
    public void NormalizeKeyName_ShouldReturnNullForUnknown()
    {
        Assert.Null(HotkeyGesture.NormalizeKeyName("NotAKey"));
        Assert.Equal("OemComma", HotkeyGesture.NormalizeKeyName("oemcomma"));
    }
}
