using MediaManager.Core.Services;
using Xunit;

namespace MediaManager.Tests;

/// <summary>
/// 更新提示的判定逻辑。
/// 这些判断错了会有两种后果：该提示的不提示（用户永远停在旧版），
/// 或者不该提示的天天提示（用户直接关掉开关，等于永久失去更新通道）。
/// </summary>
public class UpdatePolicyTests
{
    private static UpdateCheckResult NewVersion(
        string latest, bool succeeded = true, bool hasUpdate = true, string? notes = null)
        => new()
        {
            Succeeded = succeeded,
            HasUpdate = hasUpdate,
            CurrentVersion = "1.4.5",
            LatestVersion = latest,
            ReleaseNotes = notes,
        };

    [Theory]
    [InlineData("1.10.0", "1.9.9", true)]    // 数字段比较，不能按字符串比
    [InlineData("1.4.5", "1.4.5", false)]
    [InlineData("1.4.5.1", "1.4.5", true)]
    [InlineData("1.4.4", "1.4.5", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.4.5.0", "1.4.5", false)]  // 末尾补零不算新版本
    public void IsNewer_按数字段比较(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdatePolicy.IsNewer(latest, current));

    [Theory]
    [InlineData("v1.2.5", "1.2.5")]
    [InlineData("V1.2.5", "1.2.5")]
    [InlineData("1.2.5-beta.1", "1.2.5")]
    [InlineData("1.2.5+build7", "1.2.5")]
    [InlineData("  1.2.5  ", "1.2.5")]
    [InlineData("v", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeVersion_洗掉前缀与预发布后缀(string? tag, string? expected)
        => Assert.Equal(expected, UpdatePolicy.NormalizeVersion(tag));

    [Theory]
    [InlineData(null, true, true)]        // 没存过 → 用默认值（开启）
    [InlineData("", true, true)]
    [InlineData("1", false, true)]
    [InlineData("0", true, false)]
    [InlineData("true", false, true)]
    [InlineData("False", true, false)]
    [InlineData("随便写的", true, true)]   // 脏数据不该把功能关死
    public void ParseBool_脏数据回退到默认值(string? raw, bool fallback, bool expected)
        => Assert.Equal(expected, UpdatePolicy.ParseBool(raw, fallback));

    [Fact]
    public void 启动检查默认开启_但绝不自动下载()
    {
        Assert.True(UpdatePolicy.DefaultCheckOnStartup);
    }

    [Theory]
    [InlineData("1.4.6", "1.4.6", true)]
    [InlineData("v1.4.6", "1.4.6", true)]   // 存的时候可能带 v
    [InlineData("1.4.6", "v1.4.6", true)]
    [InlineData("1.4.6", "1.4.7", false)]
    [InlineData("1.4.6", null, false)]
    [InlineData(null, "1.4.6", false)]
    public void IsSkipped_只认同一个版本(string? latest, string? skipped, bool expected)
        => Assert.Equal(expected, UpdatePolicy.IsSkipped(latest, skipped));

    [Fact]
    public void ShouldNotify_有新版且没跳过才提示()
        => Assert.True(UpdatePolicy.ShouldNotify(NewVersion("1.4.6"), null));

    [Fact]
    public void ShouldNotify_跳过过就不再提示()
        => Assert.False(UpdatePolicy.ShouldNotify(NewVersion("1.4.6"), "1.4.6"));

    [Fact]
    public void ShouldNotify_跳过后出了更新的版本仍然提示()
        => Assert.True(UpdatePolicy.ShouldNotify(NewVersion("1.4.7"), "1.4.6"));

    [Fact]
    public void ShouldNotify_检查失败不提示()
        => Assert.False(UpdatePolicy.ShouldNotify(NewVersion("1.4.6", succeeded: false), null));

    [Fact]
    public void ShouldNotify_没有新版不提示()
        => Assert.False(UpdatePolicy.ShouldNotify(NewVersion("1.4.5", hasUpdate: false), null));

    [Fact]
    public void ShouldNotify_结果为空不提示()
        => Assert.False(UpdatePolicy.ShouldNotify(null, null));

    [Fact]
    public void BuildDigest_没有说明时是空的()
    {
        foreach (var empty in new[] { null, "", "   \n\n  " })
        {
            var d = UpdatePolicy.BuildDigest(empty);
            Assert.Null(d.Summary);
            Assert.Empty(d.Bullets);
            Assert.Equal(0, d.TotalBullets);
            Assert.False(d.HasMore);
        }
    }

    [Fact]
    public void BuildDigest_扒开头那段概括加要点()
    {
        var d = UpdatePolicy.BuildDigest(
            "这一版主要修两件事：文件夹明明有货却显示 0 项，批量删除入口看不见。\n\n"
            + "## 细节\n- 嵌套根目录自动合并\n- 顶部加了批量删除按钮\n- 帮助页新增快捷键");

        Assert.Equal("这一版主要修两件事：文件夹明明有货却显示 0 项，批量删除入口看不见。", d.Summary);
        Assert.Equal(3, d.Bullets.Count);
        Assert.Equal("嵌套根目录自动合并", d.Bullets[0]);
        Assert.False(d.HasMore);
    }

    [Fact]
    public void BuildDigest_要点最多五条_其余靠完整说明()
    {
        var body = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"- 第 {i} 条改动"));
        var d = UpdatePolicy.BuildDigest(body);

        Assert.Equal(UpdatePolicy.MaxBullets, d.Bullets.Count);
        Assert.Equal(12, d.TotalBullets);
        Assert.True(d.HasMore);
        Assert.Equal("第 5 条改动", d.Bullets[^1]);
    }

    [Fact]
    public void BuildDigest_小节标题不进摘要()
    {
        var d = UpdatePolicy.BuildDigest("## 这一版做了什么\n- 修了 bug");
        Assert.Null(d.Summary);
        Assert.Single(d.Bullets);
    }

    [Fact]
    public void BuildDigest_清洗Markdown内联标记()
    {
        var d = UpdatePolicy.BuildDigest(
            "- 修复了 **严重** 问题，见 [更新说明](https://example.com/x)");

        Assert.DoesNotContain("**", d.Bullets[0]);
        Assert.DoesNotContain("http", d.Bullets[0]);
        Assert.Contains("严重", d.Bullets[0]);
        Assert.Contains("更新说明", d.Bullets[0]);
    }

    [Fact]
    public void BuildDigest_概括太长会截断()
    {
        var longLine = new string('改', 300);
        var d = UpdatePolicy.BuildDigest(longLine);

        Assert.NotNull(d.Summary);
        Assert.True(d.Summary!.Length <= UpdatePolicy.MaxSummaryChars + 1,
            "截断后长度 " + d.Summary.Length);
        Assert.EndsWith("…", d.Summary);
    }

    [Fact]
    public void BuildDigest_概括只取开头一段()
    {
        // 空行就是段落的边界：后面那些是细节，不该混进「一句概括」里
        var d = UpdatePolicy.BuildDigest(
            "概括第一句。\n概括第二句。\n\n## 细节\n- 要点一\n- 要点二");

        Assert.Equal("概括第一句。概括第二句。", d.Summary);
        Assert.Equal(2, d.Bullets.Count);
    }

    [Fact]
    public void BuildDigest_编号列表也算要点()
    {
        var d = UpdatePolicy.BuildDigest("1. 第一项改动\n2) 第二项改动");

        Assert.Null(d.Summary);
        Assert.Equal(2, d.Bullets.Count);
        Assert.Equal("第一项改动", d.Bullets[0]);
        Assert.Equal("第二项改动", d.Bullets[1]);
    }

    [Fact]
    public void BuildDigest_跳过注释与代码围栏()
    {
        var d = UpdatePolicy.BuildDigest("<!-- 内部备注 -->\n---\n- 真正的条目");
        Assert.Null(d.Summary);
        Assert.Single(d.Bullets);
        Assert.Equal("真正的条目", d.Bullets[0]);
    }

    [Fact]
    public void BuildDigest_过长的要点压到两行以内()
    {
        var padded = new string('长', 100) + "。后面还有一大段细节不该出现在提示框里";
        var d = UpdatePolicy.BuildDigest("- " + padded);

        Assert.Single(d.Bullets);
        Assert.True(d.Bullets[0].Length <= UpdatePolicy.MaxBulletChars,
            "长度 " + d.Bullets[0].Length);
    }

    [Fact]
    public void BuildDigest_能切在句号后面就不硬截()
    {
        var d = UpdatePolicy.BuildDigest(
            "- 这一条的前半句已经把意思说完了。后面这段是补充细节，"
            + "写得非常非常长，长到必须截断，否则整个提示框都会被它撑开，"
            + "而这一段恰恰是最不该出现在这里的内容");
        Assert.EndsWith("。", d.Bullets[0]);
        Assert.DoesNotContain("后半句", d.Bullets[0]);
    }

    [Fact]
    public void BuildDigest_去掉渲染不了的垃圾桶符号()
    {
        var d = UpdatePolicy.BuildDigest("- 顶部新增「\U0001F5D1 批量删除」按钮");
        Assert.DoesNotContain("\U0001F5D1", d.Bullets[0]);
        Assert.Contains("批量删除", d.Bullets[0]);
    }
}
