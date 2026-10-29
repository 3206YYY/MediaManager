using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;

namespace MediaManager.App.Tests;

/// <summary>
/// 「点开一项媒体时，查看器里能翻哪些」—— 这段逻辑决定用户按 ←/→ 会走到什么。
/// 它以前散在 MainWindow / VaultWindow 各一份、日记里干脆没有，所以钉在这里。
/// </summary>
public class MediaOpenLauncherTests
{
    private static ThumbnailItemViewModel Item(long id, MediaType type, string path)
        => new(new MediaItem
        {
            Id = id,
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            Extension = System.IO.Path.GetExtension(path),
            MediaType = type,
        });

    [Fact]
    public void PickImageScope_只保留图片()
    {
        var jpg = Item(1, MediaType.Image, @"C:\m\a.jpg");
        var mp4 = Item(2, MediaType.Video, @"C:\m\b.mp4");
        var mp3 = Item(3, MediaType.Audio, @"C:\m\c.mp3");
        var txt = Item(4, MediaType.Text, @"C:\m\d.txt");

        var picked = MediaOpenLauncher.PickImageScope(new[] { jpg, mp4, mp3, txt }, jpg);

        Assert.Equal(new[] { jpg }, picked);
    }

    [Fact]
    public void PickImageScope_当前项在范围内时不重复添加()
    {
        var a = Item(1, MediaType.Image, @"C:\m\a.jpg");
        var b = Item(2, MediaType.Image, @"C:\m\b.jpg");

        var picked = MediaOpenLauncher.PickImageScope(new[] { a, b }, b);

        Assert.Equal(new[] { a, b }, picked);
    }

    [Fact]
    public void PickImageScope_当前项不在范围内时追加到末尾()
    {
        // 日记附件就是这个形状：范围是「这一篇日记的图」，要打开的那张可能刚被筛掉
        var a = Item(1, MediaType.Image, @"C:\m\a.jpg");
        var only = Item(9, MediaType.Image, @"C:\m\z.jpg");

        var picked = MediaOpenLauncher.PickImageScope(new[] { a }, only);

        Assert.Equal(new[] { a, only }, picked);
    }

    [Fact]
    public void PickImageScope_空范围也至少返回当前项()
    {
        // 返回空列表的话，查看器打开就是白板（历史上就是这么「点了没反应」的）
        var only = Item(9, MediaType.Image, @"C:\m\z.jpg");

        var picked = MediaOpenLauncher.PickImageScope(Array.Empty<ThumbnailItemViewModel>(), only);

        Assert.Single(picked);
        Assert.Same(only, picked[0]);
    }

    [Fact]
    public void PickImageScope_不同实例但同Id算已在范围内()
    {
        // 列表每次刷新都会重建 ViewModel 实例；文件被移动/改名后（私密空间搬文件就是这样）
        // 旧列表里的路径还是旧的，只有 Id 不变 —— 按引用或按路径比较都会误判成「不在范围里」，
        // 于是同样的图在查看器里出现两次，←/→ 翻到第二遍。
        var a = Item(1, MediaType.Image, @"C:\m\a.jpg");
        var b = Item(2, MediaType.Image, @"C:\m\b.jpg");
        var bAfterMove = Item(2, MediaType.Image, @"C:\m\moved\b.jpg");

        var picked = MediaOpenLauncher.PickImageScope(new[] { a, b }, bAfterMove);

        Assert.Equal(2, picked.Count);
        Assert.Equal(new long[] { 1, 2 }, picked.Select(x => x.Id));
    }

    [Fact]
    public void PickImageScope_同Id但Id为0时按路径判重()
    {
        // 独立打开的文件（不进数据库）Id 都是 0，这时候只能比路径
        var diskA = Item(0, MediaType.Image, @"C:\m\a.jpg");
        var diskBSame = Item(0, MediaType.Image, @"C:\m\b.jpg");
        var diskB = Item(0, MediaType.Image, @"C:\m\b.jpg");

        var picked = MediaOpenLauncher.PickImageScope(new[] { diskA, diskBSame }, diskB);

        Assert.Equal(2, picked.Count);
    }
}
