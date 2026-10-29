using System.Windows;
using MediaManager.App.Services;

namespace MediaManager.App.Tests;

/// <summary>
/// 日记「拖进来」的接受判定。<see cref="MediaDrop.CanAccept"/> 决定拖动过程中的光标，
/// 判错的表现是「明明能拖却禁止」或「拖了没反应」。
/// </summary>
public class MediaDropTests
{
    [Fact]
    public void 空数据不接受()
        => Assert.False(MediaDrop.CanAccept(null));

    [Fact]
    public void 内部拖拽带媒体Id就接受()
    {
        var data = new DataObject();
        data.SetData(MediaDrop.MediaIdsFormat, new long[] { 1, 2, 3 });
        Assert.True(MediaDrop.CanAccept(data));
    }

    [Fact]
    public void 内部拖拽Id数组为空不接受()
    {
        var data = new DataObject();
        data.SetData(MediaDrop.MediaIdsFormat, Array.Empty<long>());
        Assert.False(MediaDrop.CanAccept(data));
    }

    [Fact]
    public void 外部文件里有图片或视频就接受()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\somewhere\not\exist\a.jpg", @"C:\x\readme.txt" });
        Assert.True(MediaDrop.CanAccept(data));
    }

    [Fact]
    public void 外部文件全是音频文档压缩包不接受()
    {
        // 日记的媒体格按缩略图排：音频/文档/压缩包放进去只会显示空方块，宁可明说「没加」
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\x\a.mp3", @"C:\x\b.txt", @"C:\x\c.zip" });
        Assert.False(MediaDrop.CanAccept(data));
    }

    [Fact]
    public void 外部文件目录不接受()
    {
        // 目录存在性真的会查：拿一个必然存在的目录验证 Directory.Exists 分支
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { System.IO.Path.GetTempPath() });
        Assert.False(MediaDrop.CanAccept(data));
    }

    [Fact]
    public async Task 解析null数据返回空结果不抛异常()
    {
        var result = await MediaDrop.ResolveAsync(null);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Skipped);
    }
}
