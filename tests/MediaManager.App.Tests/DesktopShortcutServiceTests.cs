using System.IO;
using MediaManager.App.Services;

namespace MediaManager.App.Tests;

/// <summary>
/// 桌面快捷方式。要钉住的是「自愈不会乱动别人的东西」：
/// 桌面上的 .lnk 千千万，程序只允许改指向自己的那些。
/// <para>
/// 这里全部在临时目录里跑（<see cref="DesktopShortcutService"/> 的方法都收 desktopDir 参数），
/// 绝不碰真实桌面。
/// </para>
/// </summary>
public class DesktopShortcutServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm_lnk_" + Guid.NewGuid().ToString("N"));

    public DesktopShortcutServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string OtherExe()
    {
        // 造一个「像程序」的文件，用来代表另一个副本 / 已经搬走的旧版本
        var path = Path.Combine(_dir, "MediaManager.App.exe");
        File.WriteAllText(path, "stub");
        return path;
    }

    [Fact]
    public void 桌面没有图标时状态是未创建()
    {
        var info = DesktopShortcutService.Inspect(_dir);

        Assert.Equal(ShortcutState.Missing, info.State);
        Assert.Null(info.Path);
    }

    [Fact]
    public void 创建后能读回目标且判定为当前()
    {
        var lnk = DesktopShortcutService.Create(_dir);

        Assert.True(File.Exists(lnk));
        Assert.Equal(DesktopShortcutService.FileName, Path.GetFileName(lnk));

        // COM 读回来的路径可能被系统规范化（大小写/短名），按文件身份比
        var target = DesktopShortcutService.ReadTarget(lnk);
        Assert.NotNull(target);
        Assert.Equal(Path.GetFileName(DesktopShortcutService.ExecutablePath),
                     Path.GetFileName(target));

        Assert.Equal(ShortcutState.Current, DesktopShortcutService.Inspect(_dir).State);
    }

    [Fact]
    public void 重复创建只覆盖同一个文件不会冒出第二个()
    {
        var first = DesktopShortcutService.Create(_dir);
        var second = DesktopShortcutService.Create(_dir);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_dir, "*.lnk"));
    }

    [Fact]
    public void 指向已不存在程序的图标会被自愈()
    {
        // 先手动造一个指向「旧路径」的图标：模拟 publish 把旧目录移走之后的状态
        var missing = Path.Combine(_dir, "old", "MediaManager.App.exe");
        var lnk = Path.Combine(_dir, DesktopShortcutService.FileName);
        WriteRaw(lnk, missing);

        Assert.Equal(ShortcutState.Stale, DesktopShortcutService.Inspect(_dir).State);

        var fixedAny = DesktopShortcutService.EnsureUpToDate(_dir);

        Assert.True(fixedAny);
        Assert.Equal(ShortcutState.Current, DesktopShortcutService.Inspect(_dir).State);
    }

    [Fact]
    public void 自愈不改别人的快捷方式()
    {
        var other = Path.Combine(_dir, "notepad.exe");
        File.WriteAllText(other, "stub");
        var otherLnk = Path.Combine(_dir, "记事本.lnk");
        WriteRaw(otherLnk, other);

        var before = DesktopShortcutService.ReadTarget(otherLnk);
        var fixedAny = DesktopShortcutService.EnsureUpToDate(_dir);
        var after = DesktopShortcutService.ReadTarget(otherLnk);

        Assert.False(fixedAny);                    // 没本程序的图标，什么都不该发生
        Assert.Equal(before, after);               // 别人的快捷方式一个字节都没动
        Assert.Equal(other, after);
    }

    [Fact]
    public void 改名过的本程序图标也会被认出来()
    {
        // 用户可能把图标改成「看图」这类名字，自愈仍要认得（按目标 exe 判断，不看 .lnk 名）
        var lnk = Path.Combine(_dir, "看图.lnk");
        WriteRaw(lnk, Path.Combine(_dir, "old", "MediaManager.App.exe"));

        var info = DesktopShortcutService.Inspect(_dir);
        Assert.Equal(ShortcutState.Stale, info.State);
        Assert.Equal(lnk, info.Path);
    }

    /// <summary>用同一个服务写一个「指向别处」的 .lnk（内部借助 Create 的 COM 写入，再改目标不方便，所以另写一份最小的）。</summary>
    private static void WriteRaw(string lnkPath, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(type);
        dynamic shell = Activator.CreateInstance(type!)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        shortcut.Save();
    }
}
