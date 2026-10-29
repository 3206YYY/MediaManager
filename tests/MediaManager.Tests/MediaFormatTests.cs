using MediaManager.Core.Enums;
using MediaManager.Infrastructure.FileSystem;

namespace MediaManager.Tests;

/// <summary>扩展格式（音频/文档/压缩包）识别与开关测试。</summary>
public class MediaFormatTests
{
    [Theory]
    [InlineData("a.jpg", MediaType.Image)]
    [InlineData("a.JPEG", MediaType.Image)]
    [InlineData("a.png", MediaType.Image)]
    [InlineData("a.jfif", MediaType.Image)]
    [InlineData("a.ico", MediaType.Image)]
    [InlineData("a.mp4", MediaType.Video)]
    [InlineData("a.MKV", MediaType.Video)]
    [InlineData("a.mpeg", MediaType.Video)]
    [InlineData("a.mp3", MediaType.Audio)]
    [InlineData("a.wav", MediaType.Audio)]
    [InlineData("a.flac", MediaType.Audio)]
    [InlineData("a.aiff", MediaType.Audio)]
    [InlineData("a.txt", MediaType.Text)]
    [InlineData("a.md", MediaType.Text)]
    [InlineData("a.json", MediaType.Text)]
    [InlineData("a.html", MediaType.Text)]
    [InlineData("a.py", MediaType.Text)]
    [InlineData("a.zip", MediaType.Archive)]
    [InlineData("a.rar", MediaType.Archive)]
    [InlineData("a.7z", MediaType.Archive)]
    [InlineData("a.bz2", MediaType.Archive)]
    public void GetMediaType_RecognizesCategoryAndIsCaseInsensitive(string path, MediaType expected)
    {
        Assert.Equal(expected, MediaFormat.GetMediaType(path));
    }

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a.blend")]
    [InlineData("noext")]
    public void GetMediaType_ReturnsNullForUnsupported(string path)
    {
        Assert.Null(MediaFormat.GetMediaType(path));
        Assert.False(MediaFormat.IsSupported(path));
    }

    [Fact]
    public void ExtendedFormatsDisabled_HidesExtendedButKeepsImageVideo()
    {
        try
        {
            MediaFormat.SetExtendedFormatsEnabled(false);

            Assert.Null(MediaFormat.GetMediaType("a.mp3"));
            Assert.Null(MediaFormat.GetMediaType("a.txt"));
            Assert.Null(MediaFormat.GetMediaType("a.zip"));
            Assert.Equal(MediaType.Image, MediaFormat.GetMediaType("a.jpg"));
            Assert.Equal(MediaType.Video, MediaFormat.GetMediaType("a.mp4"));
        }
        finally
        {
            MediaFormat.SetExtendedFormatsEnabled(true);
        }
    }

    [Fact]
    public void GetMediaTypeForOpen_StillRecognizesExtendedWhenScanDisabled()
    {
        try
        {
            MediaFormat.SetExtendedFormatsEnabled(false);

            // 自动扫描关掉后，手动打开仍应认出这些类型
            Assert.Equal(MediaType.Audio, MediaFormat.GetMediaTypeForOpen("a.mp3"));
            Assert.Equal(MediaType.Text, MediaFormat.GetMediaTypeForOpen("a.txt"));
            Assert.Equal(MediaType.Archive, MediaFormat.GetMediaTypeForOpen("a.zip"));
            Assert.Equal(MediaType.Image, MediaFormat.GetMediaTypeForOpen("a.jpg"));
        }
        finally
        {
            MediaFormat.SetExtendedFormatsEnabled(true);
        }
    }

    [Theory]
    [InlineData(MediaType.Audio, true)]
    [InlineData(MediaType.Text, true)]
    [InlineData(MediaType.Archive, true)]
    [InlineData(MediaType.Image, false)]
    [InlineData(MediaType.Video, false)]
    public void IsExtendedType_MatchesCategory(MediaType type, bool expected)
    {
        Assert.Equal(expected, MediaFormat.IsExtendedType(type));
    }

    /// <summary>
    /// 「是否受扩展格式扫描开关控制」和「是否外放给系统程序」现在是同一批类型，
    /// 但它们是**两个独立概念**，将来完全可能分叉（比如哪天内置了 PDF 阅读，
    /// 它仍是扩展格式却不再外放）。所以这里不强行合并实现，只用测试把当下的
    /// 一致性钉住：一旦有人只改一边，这条会红，逼他确认那是不是有意为之。
    /// </summary>
    [Fact]
    public void ExtendedTypeAndExternalOpen_CurrentlyAgree()
    {
        foreach (var type in Enum.GetValues<MediaType>())
        {
            Assert.Equal(
                MediaManager.Core.Media.MediaOpenStrategy.UseExternalApp(type),
                MediaFormat.IsExtendedType(type));
        }
    }

    /// <summary>
    /// Windows 每个目录都塞一个 desktop.ini。它曾被当成「文本文档」扫进图库，
    /// 于是图库里全是文档图标 —— 这条守住不再收录 ini。
    /// </summary>
    [Fact]
    public void IniIsNotIndexedAsText()
    {
        Assert.Null(MediaFormat.GetMediaType("desktop.ini"));
        Assert.Null(MediaFormat.GetMediaType("config.ini"));
        Assert.Equal(MediaType.Text, MediaFormat.GetMediaType("a.txt"));
    }

    [Theory]
    [InlineData("desktop.ini", FileAttributes.Normal, true)]
    [InlineData("Desktop.INI", FileAttributes.Normal, true)]
    [InlineData("Thumbs.db", FileAttributes.Normal, true)]
    [InlineData(".DS_Store", FileAttributes.Normal, true)]
    [InlineData(".picasa.ini", FileAttributes.Normal, true)]
    [InlineData(@"D:\图片\a.jpg", FileAttributes.Normal, false)]
    // 只有「隐藏 + 系统」双属性才算系统垃圾，单纯隐藏的用户文件要保留
    [InlineData(@"D:\图片\a.jpg", FileAttributes.Hidden, false)]
    [InlineData(@"D:\图片\a.jpg", FileAttributes.System | FileAttributes.Hidden, true)]
    public void IsSystemJunk_FiltersOsFilesOnly(string path, FileAttributes attributes, bool expected)
    {
        Assert.Equal(expected, MediaFormat.IsSystemJunk(path, attributes));
    }
}
