using MediaManager.Core.Enums;
using MediaManager.Core.Services;

namespace MediaManager.Tests;

/// <summary>
/// 「删除方式」这个设置的守门测试。
/// <para>
/// 背景：v1.4.8 之前它是<b>空开关</b> —— 设置页写得明明白白「回收站 / 永久删除」，
/// 值也存进库了，但主界面、点删模式、单张删除、私密空间四处全写死
/// <c>DeleteToRecycleBinAsync</c>，没有任何代码读它。用户选了永久删除，
/// 文件照样进回收站；私密空间的确认框还写着「可在设置里改为永久删除」。
/// </para>
/// <para>
/// 这组测试盯三件事：1) 行为真的跟着设置走；2) 永久删除不弹不可用的「撤销」；
/// 3) 确认框文案不能说反（永久删除绝不能写「可撤销」）。
/// </para>
/// </summary>
public class DeletePolicyTests
{
    [Theory]
    [InlineData(DeleteBehavior.RecycleBin, false)]
    [InlineData(DeleteBehavior.Permanent, true)]
    public void IsPermanent_跟随设置值(DeleteBehavior behavior, bool expected)
        => Assert.Equal(expected, DeletePolicy.IsPermanent(behavior));

    [Theory]
    [InlineData(DeleteBehavior.RecycleBin, true)]
    [InlineData(DeleteBehavior.Permanent, false)]
    public void CanUndo_只有进回收站才能撤销(DeleteBehavior behavior, bool canUndo)
        => Assert.Equal(canUndo, DeletePolicy.CanUndo(behavior));

    [Theory]
    [InlineData(DeleteBehavior.RecycleBin, "删除到回收站")]
    [InlineData(DeleteBehavior.Permanent, "永久删除")]
    public void LogAction_日志动作名区分得开(DeleteBehavior behavior, string expected)
        => Assert.Equal(expected, DeletePolicy.LogAction(behavior));

    [Fact]
    public async Task DeleteAsync_选永久删除时真的调永久删除()
    {
        var ops = new RecordingFileOps();
        await DeletePolicy.DeleteAsync(ops, @"C:\a.jpg", DeleteBehavior.Permanent);

        Assert.Equal(new[] { "permanent:C:\\a.jpg" }, ops.Calls);
    }

    [Fact]
    public async Task DeleteAsync_选回收站时调回收站删除()
    {
        var ops = new RecordingFileOps();
        await DeletePolicy.DeleteAsync(ops, @"C:\a.jpg", DeleteBehavior.RecycleBin);

        Assert.Equal(new[] { "recycle:C:\\a.jpg" }, ops.Calls);
    }

    [Theory]
    [InlineData(DeleteBehavior.RecycleBin)]
    [InlineData(DeleteBehavior.Permanent)]
    public async Task DeleteAsync_一次只调一种(DeleteBehavior behavior)
    {
        var ops = new RecordingFileOps();
        await DeletePolicy.DeleteAsync(ops, @"C:\b.png", behavior);

        Assert.Single(ops.Calls);
    }

    [Fact]
    public void ConfirmMessage_永久删除不能说可以撤销()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.Permanent, 3);

        Assert.Contains("永久删除", text);
        Assert.Contains("无法恢复", text);
        Assert.DoesNotContain("撤销", text);
    }

    [Fact]
    public void ConfirmMessage_回收站才提撤销()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.RecycleBin, 3);

        Assert.Contains("回收站", text);
        Assert.Contains("撤销", text);
    }

    [Fact]
    public void ConfirmMessage_单文件用文件名不用数字()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.RecycleBin, 1, "风景.jpg");

        Assert.Contains("风景.jpg", text);
        Assert.DoesNotContain("1 个文件", text);
    }

    [Fact]
    public void ConfirmMessage_批量报数量()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.RecycleBin, 7);

        Assert.Contains("7 个文件", text);
    }

    /// <summary>记账用的假文件操作：只记调用，不碰磁盘。</summary>
    private sealed class RecordingFileOps : IFileOperationService
    {
        public List<string> Calls { get; } = new();

        public Task DeleteToRecycleBinAsync(string path)
        {
            Calls.Add("recycle:" + path);
            return Task.CompletedTask;
        }

        public Task DeletePermanentAsync(string path)
        {
            Calls.Add("permanent:" + path);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string source, string destination) => Task.CompletedTask;

        public Task CopyAsync(string source, string destination) => Task.CompletedTask;

        public Task RenameAsync(string source, string newName) => Task.CompletedTask;
    }
}
