using MediaManager.App.Services;
using MediaManager.Core.Enums;

namespace MediaManager.App.Tests;

/// <summary>
/// 非图片/视频文件的彩色类型图标映射。
/// 网格里每个非视觉文件都靠它认脸 —— 标签错了（.flac 显示成 MP3）用户会以为软件认错了文件。
/// </summary>
public class FileTypeIconTests
{
    [Theory]
    [InlineData("pdf", "PDF", "PDF 文档")]
    [InlineData("docx", "DOC", "Word 文档")]
    [InlineData("csv", "CSV", "CSV 表格")]
    [InlineData("flac", "FLAC", "无损音频")]   // 回归：不能回落成大类 MP3
    [InlineData("cs", "C#", "C# 源码")]
    public void For_登记过的扩展名返回对应图标(string ext, string label, string desc)
    {
        var icon = FileTypeIcon.For(ext, MediaType.Text);
        Assert.Equal(label, icon.Label);
        Assert.Equal(desc, icon.Description);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".pdf")]
    [InlineData("PDF")]
    [InlineData(" PDF ")]
    public void For_扩展名大小写和点号都不影响匹配(string ext)
    {
        Assert.Equal("PDF", FileTypeIcon.For(ext, MediaType.Text).Label);
    }

    [Fact]
    public void For_没登记的短扩展名用大类配色加扩展名当标签()
    {
        // kdbx（KeePass）不在字典里：Archive 大类 + "KDBX" 标签，而不是糊成一个 ARCH
        var icon = FileTypeIcon.For("kdbx", MediaType.Archive);
        Assert.Equal("KDBX", icon.Label);
        Assert.Equal("压缩包", icon.Description);
    }

    [Fact]
    public void For_长扩展名回落到大类默认图标()
    {
        // 5 个字符以上的未登记扩展名，标签太长显示不下，回落大类
        var icon = FileTypeIcon.For("torrent", MediaType.Archive);
        Assert.Equal("ARCH", icon.Label);
        Assert.Equal("压缩包", icon.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    public void For_没有扩展名老实显示FILE不猜大类(string? ext)
    {
        // 猜成 TXT 会误导用户以为软件认出了文件类型（README、Makefile 这类）
        var icon = FileTypeIcon.For(ext, MediaType.Text);
        Assert.Equal("FILE", icon.Label);
        Assert.Equal("文件", icon.Description);
    }

    [Fact]
    public void For_图片类型的未登记短扩展名按文件处理()
    {
        // Image 不在 fallback switch 里：不炸、走通用兜底
        var icon = FileTypeIcon.For("raw", MediaType.Image);
        Assert.Equal("RAW", icon.Label);
        Assert.Equal("文件", icon.Description);
    }

    [Fact]
    public void For_音频大类兜底()
    {
        var icon = FileTypeIcon.For("opus", MediaType.Audio);
        Assert.Equal("OPUS", icon.Label);
        Assert.Equal("音频", icon.Description);
    }
}
