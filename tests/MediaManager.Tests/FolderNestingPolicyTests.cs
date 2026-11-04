using MediaManager.Core.Services;

namespace MediaManager.Tests;

/// <summary>
/// 添加文件夹时的嵌套判定。
///
/// 守的是 v1.4.5 修的 P0：把子文件夹当独立根目录加进库，它名下一条记录都没有，
/// 用户点进去「共 0 项」，刷新扫描都救不回来。
/// </summary>
public class FolderNestingPolicyTests
{
    [Fact]
    public void 子目录已包含在父级里时拒绝单独添加()
    {
        var verdict = FolderNestingPolicy.Evaluate(
            @"C:\测试\实拍", new[] { @"C:\测试" }, out var covering, out _);

        Assert.Equal(FolderAddVerdict.CoveredByExisting, verdict);
        Assert.Equal(@"C:\测试", covering);
    }

    [Fact]
    public void 深层子目录也算被覆盖()
    {
        var verdict = FolderNestingPolicy.Evaluate(
            @"C:\测试\实拍\2024\国庆", new[] { @"C:\测试" }, out var covering, out _);

        Assert.Equal(FolderAddVerdict.CoveredByExisting, verdict);
        Assert.Equal(@"C:\测试", covering);
    }

    [Theory]
    [InlineData(@"C:\测试", @"C:\测试")]                 // 完全相同
    [InlineData(@"C:/测试", @"C:\测试")]                 // 正反斜杠
    [InlineData(@"C:\测试\", @"C:\测试")]                // 尾斜杠
    [InlineData(@"c:\测试", @"C:\测试")]                 // 大小写
    public void 同一个文件夹判为重复而不是新根目录(string added, string existing)
    {
        var verdict = FolderNestingPolicy.Evaluate(added, new[] { existing }, out _, out _);
        Assert.Equal(FolderAddVerdict.Duplicate, verdict);
    }

    [Fact]
    public void 相邻前缀不算包含()
    {
        // C:\测试2 不在 C:\测试 里面 —— 纯 StartsWith 会误判，这里必须靠分隔符
        var verdict = FolderNestingPolicy.Evaluate(
            @"C:\测试2", new[] { @"C:\测试" }, out var covering, out _);

        Assert.Equal(FolderAddVerdict.Ok, verdict);
        Assert.Null(covering);

        Assert.False(FolderNestingPolicy.IsUnder(@"C:\测试2\a.jpg", @"C:\测试"));
        Assert.True(FolderNestingPolicy.IsUnder(@"C:\测试\a.jpg", @"C:\测试"));
    }

    [Fact]
    public void 新加父目录时列出可合并的子目录()
    {
        var verdict = FolderNestingPolicy.Evaluate(
            @"C:\测试",
            new[] { @"C:\测试\实拍", @"C:\测试\截图", @"D:\其他" },
            out var covering,
            out var contained);

        Assert.Equal(FolderAddVerdict.ContainsExisting, verdict);
        Assert.Null(covering);
        Assert.Equal(2, contained.Count);
        Assert.Contains(@"C:\测试\实拍", contained);
        Assert.DoesNotContain(@"D:\其他", contained);
    }

    [Fact]
    public void 互不相干的文件夹可以正常添加()
    {
        var verdict = FolderNestingPolicy.Evaluate(
            @"D:\照片", new[] { @"C:\测试" }, out var covering, out var contained);

        Assert.Equal(FolderAddVerdict.Ok, verdict);
        Assert.Null(covering);
        Assert.Empty(contained);
    }

    [Fact]
    public void 盘根路径规范化不会退化成C冒号()
    {
        Assert.Equal(@"C:\", FolderNestingPolicy.Normalize(@"C:\"));
        Assert.Equal(@"C:\", FolderNestingPolicy.Normalize("C:"));
        // C:\ 是所有 C 盘目录的上级
        Assert.True(FolderNestingPolicy.IsUnder(@"C:\测试", @"C:\"));
    }

    [Fact]
    public void 空路径不崩()
    {
        var verdict = FolderNestingPolicy.Evaluate("", new[] { @"C:\测试" }, out _, out var contained);
        Assert.Equal(FolderAddVerdict.Ok, verdict);
        Assert.Empty(contained);
        Assert.False(FolderNestingPolicy.IsUnder("", @"C:\测试"));
    }

    // ---------- 老库自愈：把嵌套根目录并进父目录 ----------

    [Fact]
    public void 嵌套根目录按最顶层祖先分组()
    {
        // A、A\B、A\B\C 三层都当过根目录：一次全并进 A，别一层层往上爬
        var groups = FolderNestingPolicy.GroupNestedRoots(new (long, string?)[]
        {
            (1, @"C:\测试"),
            (2, @"C:\测试\实拍"),
            (3, @"C:\测试\实拍\2024"),
        });

        Assert.Single(groups);
        Assert.True(groups.ContainsKey(1));
        Assert.Equal(new long[] { 2, 3 }, groups[1].OrderBy(x => x));
    }

    [Fact]
    public void 互不相干的根目录不合并()
    {
        var groups = FolderNestingPolicy.GroupNestedRoots(new (long, string?)[]
        {
            (1, @"C:\测试"),
            (2, @"C:\测试2"),
            (3, @"D:\照片"),
        });

        Assert.Empty(groups);
    }

    [Fact]
    public void 保险箱锚点目录不参与合并()
    {
        // 锚点也挂在 RootFolder 表里，但它不是用户添加的文件夹，绝不能被动
        var groups = FolderNestingPolicy.GroupNestedRoots(
            new (long, string?)[] { (1, @"C:\APPDATA\MediaManager"), (2, @"C:\APPDATA\MediaManager\Vault") },
            excludePath: @"C:\APPDATA\MediaManager\Vault");

        Assert.Empty(groups);
    }

    [Fact]
    public void 只有一个根目录时不做任何事()
    {
        Assert.Empty(FolderNestingPolicy.GroupNestedRoots(new (long, string?)[] { (1, @"C:\测试") }));
        Assert.Empty(FolderNestingPolicy.GroupNestedRoots(Array.Empty<(long, string?)>()));
    }
}
