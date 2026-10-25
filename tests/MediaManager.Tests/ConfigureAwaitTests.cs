using System.Text.RegularExpressions;

namespace MediaManager.Tests;

/// <summary>
/// 守住「仓储层每个 await 都写 ConfigureAwait(false)」这条约定。
///
/// 背景：MediaRepository 曾经 85 个 await、一个 ConfigureAwait(false) 都没有。
/// 一旦某个 await 真的异步化，UI 线程上任何同步等待
/// （关闭窗口写设置、备份目录属性…）就会变成死锁 —— 这类问题只在用户关机时才炸，
/// 单测抓不到，所以直接对源码做静态检查。
/// </summary>
public class ConfigureAwaitTests
{
    [Fact]
    public void MediaRepository_AllAwaitsUseConfigureAwaitFalse()
    {
        var path = FindRepositoryFile("src", "MediaManager.Infrastructure", "Database", "MediaRepository.cs");

        var code = File.ReadAllLines(path)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("*", StringComparison.Ordinal))
            .Aggregate(new System.Text.StringBuilder(), (sb, line) => sb.AppendLine(line))
            .ToString();

        var awaits = Regex.Matches(code, @"\bawait\s").Count;
        var configured = Regex.Matches(code, @"\.ConfigureAwait\(false\)").Count;

        Assert.True(awaits > 50, $"没读到预期的 await 数量（{awaits}），检查文件路径是否对：{path}");
        Assert.Equal(awaits, configured);
    }

    /// <summary>从测试输出目录往上找解决方案根目录（找不到就一路走到盘符）。</summary>
    private static string FindRepositoryFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MediaManager.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        var path = Path.Combine(new[] { dir!.FullName }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return path;
    }
}
