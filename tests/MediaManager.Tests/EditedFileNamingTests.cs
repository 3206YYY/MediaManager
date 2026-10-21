using MediaManager.Core.Editing;

namespace MediaManager.Tests;

/// <summary>
/// 编辑保存的命名规则：「编辑原图 → 新建 _edited 副本；编辑副本 → 直接改副本」。
/// 这组用例盯的是三件事：原图绝不被覆盖、副本不会套娃、界面判断副本的判断与保存落点一致。
/// </summary>
public class EditedFileNamingTests
{
    [Theory]
    [InlineData(@"C:\pics\cat.jpg")]
    [InlineData(@"C:\pics\cat.png")]
    [InlineData("/home/u/cat.jpeg")]
    [InlineData(@"C:\pics\cat_without_suffix.bmp")]
    public void Original_GetsEditedSuffix(string input)
    {
        var actual = EditedFileNaming.BuildOutputPath(input);

        Assert.EndsWith("_edited" + System.IO.Path.GetExtension(input), actual);
        Assert.NotEqual(input, actual);   // 关键：原图路径绝不能等于输出路径
        Assert.False(EditedFileNaming.IsEditedCopy(input));
    }

    [Theory]
    [InlineData(@"C:\pics\cat_edited.jpg")]
    [InlineData(@"C:\pics\cat_EDITED.jpg")]
    [InlineData("/home/u/cat_edited.png")]
    public void EditedCopy_IsOverwrittenInPlace(string input)
    {
        var actual = EditedFileNaming.BuildOutputPath(input);

        Assert.Equal(input, actual);      // 原地覆盖，不再生成新文件
        Assert.True(EditedFileNaming.IsEditedCopy(input));
    }

    [Fact]
    public void MultipleSuffixes_DoNotNest()
    {
        // 历史遗留的套娃文件名：仍按「已经是副本」处理，原地覆盖，不再继续加后缀
        var actual = EditedFileNaming.BuildOutputPath(@"C:\pics\cat_edited_edited.jpg");

        Assert.Equal(@"C:\pics\cat_edited_edited.jpg", actual);
        Assert.DoesNotContain("_edited_edited_edited", actual);
    }

    [Fact]
    public void RepeatedSave_StaysOnSamePath()
    {
        var first = EditedFileNaming.BuildOutputPath(@"C:\pics\cat.jpg");
        var second = EditedFileNaming.BuildOutputPath(first);
        var third = EditedFileNaming.BuildOutputPath(second);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(@"C:\pics\cat_edited.jpg", first);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEditedCopy_NullOrBlank_IsFalse(string? path)
    {
        Assert.False(EditedFileNaming.IsEditedCopy(path));
    }

    [Fact]
    public void IsEditedCopy_OnlyMatchesTrailingSuffix()
    {
        Assert.False(EditedFileNaming.IsEditedCopy(@"C:\pics\edited_cat.jpg"));
        Assert.False(EditedFileNaming.IsEditedCopy(@"C:\pics\cat_edited_final.jpg"));
        Assert.True(EditedFileNaming.IsEditedCopy(@"C:\pics\cat_edited_final_edited.jpg"));
    }

    [Fact]
    public void BuildOutputPath_KeepsDirectory()
    {
        var actual = EditedFileNaming.BuildOutputPath(@"D:\media\2024\trip.jpg");

        Assert.Equal(@"D:\media\2024", System.IO.Path.GetDirectoryName(actual));
    }
}
