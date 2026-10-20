using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Infrastructure.FileSystem;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MediaManager.Tests;

/// <summary>批量重命名 / 批量导出 的核心逻辑测试（全部在临时目录里做，不碰真实照片）。</summary>
public class BatchOperationTests : IDisposable
{
    private readonly string _dir;

    public BatchOperationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mm_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    private string Touch(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static MediaItem Item(long id, string path) => new()
    {
        Id = id,
        Path = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path).TrimStart('.'),
        MediaType = MediaType.Image,
    };

    [Fact]
    public void BuildRenamePlan_按前缀与位数生成序号()
    {
        var items = new List<MediaItem>
        {
            Item(1, Path.Combine(_dir, "a.jpg")),
            Item(2, Path.Combine(_dir, "b.jpg")),
            Item(3, Path.Combine(_dir, "c.png")),
        };

        var plan = BatchOperationService.BuildRenamePlan(
            items, new BatchRenameOptions("旅行_", 1, 3), out var conflicts);

        Assert.Empty(conflicts);
        Assert.Equal(3, plan.Count);
        Assert.Equal("旅行_001.jpg", plan[0].NewName);
        Assert.Equal("旅行_002.jpg", plan[1].NewName);
        Assert.Equal("旅行_003.png", plan[2].NewName, ignoreCase: true);
    }

    [Fact]
    public void BuildRenamePlan_起始序号与后缀生效()
    {
        var items = new List<MediaItem> { Item(1, Path.Combine(_dir, "a.jpg")) };

        var plan = BatchOperationService.BuildRenamePlan(
            items, new BatchRenameOptions("IMG", 10, 2, "_ok"), out _);

        Assert.Single(plan);
        Assert.Equal("IMG10_ok.jpg", plan[0].NewName);
    }

    [Fact]
    public void BuildRenamePlan_目标已存在时记为冲突()
    {
        Touch("旅行_001.jpg", "already here");
        var items = new List<MediaItem> { Item(1, Path.Combine(_dir, "a.jpg")) };

        var plan = BatchOperationService.BuildRenamePlan(
            items, new BatchRenameOptions("旅行_", 1, 3), out var conflicts);

        Assert.Empty(plan);
        Assert.Single(conflicts);
    }

    [Fact]
    public void ExecuteRename_连环改名不互相覆盖()
    {
        var a = Touch("a.jpg", "AAA");
        var b = Touch("b.jpg", "BBB");

        var plan = new List<BatchRenamePlanItem>
        {
            new(1, a, "a.jpg", "b.jpg", Path.Combine(_dir, "b.jpg")),
            new(2, b, "b.jpg", "c.jpg", Path.Combine(_dir, "c.jpg")),
        };

        var result = BatchOperationService.ExecuteRename(plan);

        Assert.Equal(2, result.Renamed);
        Assert.Equal(0, result.Failed);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(_dir, "b.jpg")));
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(_dir, "c.jpg")));
        Assert.Equal(2, result.RenamedItems.Count);
    }

    [Fact]
    public void ExecuteRename_文件不存在时跳过而不是崩()
    {
        var plan = new List<BatchRenamePlanItem>
        {
            new(1, Path.Combine(_dir, "ghost.jpg"), "ghost.jpg", "x.jpg", Path.Combine(_dir, "x.jpg")),
        };

        var result = BatchOperationService.ExecuteRename(plan);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ExportAsync_统一长边且不改原图()
    {
        var src = Path.Combine(_dir, "big.png");
        using (var img = new Image<Rgba32>(800, 400))
        {
            img.SaveAsPng(src);
        }
        var originalLength = new FileInfo(src).Length;

        var outDir = Path.Combine(_dir, "out");
        var result = await BatchOperationService.ExportAsync(
            new List<MediaItem> { Item(1, src) },
            new BatchExportOptions(outDir, 200, 90));

        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.Failed);

        var exported = Directory.GetFiles(outDir).Single();
        using var check = await SixLabors.ImageSharp.Image.LoadAsync(exported);
        Assert.Equal(200, Math.Max(check.Width, check.Height));

        // 原图必须原封不动
        Assert.Equal(originalLength, new FileInfo(src).Length);
        using var sourceCheck = await SixLabors.ImageSharp.Image.LoadAsync(src);
        Assert.Equal(800, sourceCheck.Width);
    }

    [Fact]
    public async Task ExportAsync_同名不覆盖自动改名()
    {
        var src = Path.Combine(_dir, "p.png");
        using (var img = new Image<Rgba32>(50, 50)) { img.SaveAsPng(src); }

        var outDir = Path.Combine(_dir, "out2");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "p.png"), "occupied");

        var result = await BatchOperationService.ExportAsync(
            new List<MediaItem> { Item(1, src) },
            new BatchExportOptions(outDir, 0, 90));

        Assert.Equal(1, result.Ok);
        Assert.Equal("occupied", File.ReadAllText(Path.Combine(outDir, "p.png"))); // 原文件没被覆盖
        Assert.Equal(2, Directory.GetFiles(outDir).Length);
    }
}
