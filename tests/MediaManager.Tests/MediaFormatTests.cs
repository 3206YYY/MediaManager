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
}
