using MediaManager.Infrastructure.FileSystem;

namespace MediaManager.Tests;

/// <summary>
/// 回收站还原的实机测试：真的把一个文件删进回收站，再从回收站还原回来。
/// 这段逻辑是「撤销删除」的地基，必须能拿到原路径、删得进、还原得回。
/// </summary>
public class RecycleBinTests : IDisposable
{
    private readonly string _tempDir;

    public RecycleBinTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerRecycleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void DeleteThenRestore_ShouldReturnFileToOriginalPath()
    {
        var path = Path.Combine(_tempDir, "recycle_test.txt");
        File.WriteAllText(path, "hello-recycle");

        var deletedAt = DateTime.Now;

        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

        Assert.False(File.Exists(path));

        // 回收站里应该能读到「原路径」（$I 元数据解析）
        var entries = RecycleBin.List();
        Assert.Contains(entries, e =>
            e.OriginalPath.EndsWith("recycle_test.txt", StringComparison.OrdinalIgnoreCase));

        var ok = RecycleBin.TryRestore(path, deletedAt, out var message);
        Assert.True(ok, message);
        Assert.True(File.Exists(path));
        Assert.Equal("hello-recycle", File.ReadAllText(path));
    }
}
