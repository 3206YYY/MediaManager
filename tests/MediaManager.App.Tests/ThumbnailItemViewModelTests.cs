using MediaManager.App.ViewModels;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;

namespace MediaManager.App.Tests;

/// <summary>
/// 缩略图网格项的展示逻辑。这里断言的都是「状态栏/网格上用户看得见」的字符串，
/// 错一个就是界面上的错字或「未知」，属于最便宜的回归防线。
/// </summary>
public class ThumbnailItemViewModelTests
{
    private static MediaItem Media(MediaType type, string path, long size = 1024) => new()
    {
        Id = 1,
        Path = path,
        FileName = System.IO.Path.GetFileName(path),
        Extension = System.IO.Path.GetExtension(path),
        MediaType = type,
        SizeBytes = size,
    };

    // ---------- FormatSize：四个量级的边界 ----------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void FormatSize_四个量级(long bytes, string expected)
        => Assert.Equal(expected, ThumbnailItemViewModel.FormatSize(bytes));

    // ---------- FileIcon：Extension 缺失时回落到文件名 ----------

    [Fact]
    public void 老记录没有Extension时从文件名补()
    {
        // 早期版本没写 Extension 字段；回落失败会得到一个糊掉的 FILE 图标
        var vm = new ThumbnailItemViewModel(new MediaItem
        {
            Id = 1, Path = @"C:\m\a.mp4", FileName = "a.mp4",
            Extension = string.Empty, MediaType = MediaType.Video,
        });

        Assert.Equal("MP4", vm.FileIcon.Label);
    }

    [Fact]
    public void 文件名也没有扩展名时显示FILE()
    {
        var vm = new ThumbnailItemViewModel(new MediaItem
        {
            Id = 1, Path = @"C:\m\Makefile", FileName = "Makefile",
            Extension = string.Empty, MediaType = MediaType.Text,
        });

        Assert.Equal("FILE", vm.FileIcon.Label);
    }

    // ---------- 类型标记 ----------

    [Fact]
    public void 视频是视觉类型且IsVideo()
    {
        var vm = new ThumbnailItemViewModel(Media(MediaType.Video, @"C:\m\a.mp4"));
        Assert.True(vm.IsVideo);
        Assert.True(vm.IsVisual);
        Assert.False(vm.IsNonVisual);
    }

    [Fact]
    public void 音频文档压缩包都是非视觉类型()
    {
        Assert.True(new ThumbnailItemViewModel(Media(MediaType.Audio, @"C:\m\a.mp3")).IsNonVisual);
        Assert.True(new ThumbnailItemViewModel(Media(MediaType.Text, @"C:\m\a.txt")).IsNonVisual);
        Assert.True(new ThumbnailItemViewModel(Media(MediaType.Archive, @"C:\m\a.zip")).IsNonVisual);
    }

    [Fact]
    public void TypeLabel用图标描述()
    {
        var vm = new ThumbnailItemViewModel(Media(MediaType.Text, @"C:\m\报告.pdf"));
        Assert.Equal("PDF 文档", vm.TypeLabel);
    }

    // ---------- 其它展示 ----------

    [Fact]
    public void ResolutionDisplay_有尺寸时显示乘号格式()
    {
        var media = Media(MediaType.Image, @"C:\m\a.jpg");
        media.Width = 1920;
        media.Height = 1080;
        var vm = new ThumbnailItemViewModel(media);
        Assert.Equal("1920×1080", vm.ResolutionDisplay);
    }

    [Fact]
    public void ResolutionDisplay_没有尺寸时显示未知()
    {
        var vm = new ThumbnailItemViewModel(Media(MediaType.Image, @"C:\m\a.jpg"));
        Assert.Equal("未知", vm.ResolutionDisplay);
    }

    [Fact]
    public void ToString返回文件名让读屏和自动化能读到名字()
    {
        // 网格项以前没有 ToString，UIA Name 是类名，读屏软件等于没读（实测踩过）
        var vm = new ThumbnailItemViewModel(Media(MediaType.Image, @"C:\m\DSC_0151.jpg"));
        Assert.Equal("DSC_0151.jpg", vm.ToString());
    }

    [Fact]
    public void 可观察属性变化会发通知()
    {
        var vm = new ThumbnailItemViewModel(Media(MediaType.Image, @"C:\m\a.jpg"));
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        vm.IsSelected = true;
        vm.IsFavorite = true;
        vm.Rating = 5;

        Assert.Contains(nameof(ThumbnailItemViewModel.IsSelected), names);
        Assert.Contains(nameof(ThumbnailItemViewModel.IsFavorite), names);
        Assert.Contains(nameof(ThumbnailItemViewModel.Rating), names);
    }
}

/// <summary>
/// 侧栏导航项的展示逻辑。
/// <see cref="NavigationItemViewModel.Title"/> 只是显示名（末级目录名），完整路径在
/// ToolTipText —— 历史上「把 Title 当路径」的代码都是 bug，这条约定值得钉一个测试。
/// </summary>
public class NavigationItemViewModelTests
{
    [Fact]
    public void ToString返回Title让读屏和自动化能读到名字()
    {
        var nav = new NavigationItemViewModel { Title = "Captures" };
        Assert.Equal("Captures", nav.ToString());
    }

    [Fact]
    public void Title是显示名_完整路径放ToolTipText()
    {
        // 两个不同目录可以有一样的 Title；区分它们的是 ToolTipText，不是 Title
        var a = new NavigationItemViewModel
        {
            Title = "照片",
            ToolTipText = @"C:\Users\upmen\Pictures\照片",
        };
        var b = new NavigationItemViewModel
        {
            Title = "照片",
            ToolTipText = @"D:\备份\照片",
        };

        Assert.Equal(a.Title, b.Title);
        Assert.NotEqual(a.ToolTipText, b.ToolTipText);
    }
}

public class TypeFilterOptionTests
{
    [Fact]
    public void 顶部类型筛选默认全选()
    {
        // 默认必须全选：新装的库一打开「什么都没有」会被当成 bug 报上来
        var option = new TypeFilterOption { Name = "图片", MediaType = MediaType.Image };
        Assert.True(option.IsSelected);
    }
}
