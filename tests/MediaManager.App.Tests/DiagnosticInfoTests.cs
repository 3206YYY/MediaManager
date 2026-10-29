using MediaManager.App.Services;

namespace MediaManager.App.Tests;

/// <summary>
/// 诊断信息是「用户会原样贴到公开论坛」的东西，所以抹用户名不是锦上添花，是硬要求。
/// 这里钉住两件事：该抹的抹掉、该留的留着（盘符和目录结构排查要用）。
/// </summary>
public class DiagnosticInfoTests
{
    private static DiagnosticInfo.Snapshot Sample() => new(
        AppVersion: "1.4.2",
        OsDescription: "Microsoft Windows 10.0.19045",
        RuntimeDescription: ".NET 8.0.21",
        Architecture: "X64",
        DataMode: "安装（数据在 %LocalAppData%\\MediaManager）",
        DataDirectory: @"C:\Users\upmen\AppData\Local\MediaManager",
        LogDirectory: @"C:\Users\upmen\AppData\Local\MediaManager\logs",
        MediaItemCount: 83,
        UserName: "upmen");

    [Fact]
    public void RedactPath_用户名被替换掉()
    {
        var result = DiagnosticInfo.RedactPath(@"C:\Users\upmen\AppData\Local\MediaManager", "upmen");

        Assert.Equal(@"C:\Users\<用户>\AppData\Local\MediaManager", result);
        Assert.DoesNotContain("upmen", result);
    }

    [Fact]
    public void RedactPath_保留盘符和目录结构()
    {
        // 抹太多会让排查失去意义：还得知道用户把数据放在 C 盘还是移动硬盘
        var result = DiagnosticInfo.RedactPath(@"D:\backup\upmen\MediaManager", "upmen");

        Assert.StartsWith(@"D:\backup\", result);
        Assert.EndsWith(@"\MediaManager", result);
    }

    [Fact]
    public void RedactPath_大小写不敏感()
    {
        var result = DiagnosticInfo.RedactPath(@"C:\Users\UpMeN\Pictures", "upmen");

        Assert.DoesNotContain("UpMeN", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactPath_用户名未知时原样返回()
    {
        var result = DiagnosticInfo.RedactPath(@"C:\Users\someone\x", "");

        Assert.Equal(@"C:\Users\someone\x", result);
    }

    [Fact]
    public void RedactPath_空路径返回空串而不是崩溃()
    {
        Assert.Equal(string.Empty, DiagnosticInfo.RedactPath(null, "upmen"));
        Assert.Equal(string.Empty, DiagnosticInfo.RedactPath("   ", "upmen"));
    }

    [Fact]
    public void Compose_关键字段齐全且不含用户名()
    {
        var text = DiagnosticInfo.Compose(Sample(), new DateTime(2026, 10, 29, 17, 52, 0));

        Assert.Contains("1.4.2", text);
        Assert.Contains("Microsoft Windows 10.0.19045", text);
        Assert.Contains(".NET 8.0.21", text);
        Assert.Contains("X64", text);
        Assert.Contains("83", text);
        Assert.Contains("2026-10-29 17:52:00", text);
        Assert.DoesNotContain("upmen", text);
    }

    [Fact]
    public void Compose_空字段写成未知而不是空白()
    {
        var snapshot = Sample() with { AppVersion = "", OsDescription = "   " };

        var text = DiagnosticInfo.Compose(snapshot, DateTime.Now);

        Assert.Contains("（未知）", text);
    }

    [Fact]
    public void Compose_零条目也算正常值()
    {
        var text = DiagnosticInfo.Compose(Sample() with { MediaItemCount = 0 }, DateTime.Now);

        Assert.Contains("库内媒体条目：0", text);
    }
}
