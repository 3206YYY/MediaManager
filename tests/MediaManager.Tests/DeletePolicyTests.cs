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

    // ---------- 「每次询问」 ----------

    [Theory]
    [InlineData(DeleteBehavior.Ask, true)]
    [InlineData(DeleteBehavior.RecycleBin, false)]
    [InlineData(DeleteBehavior.Permanent, false)]
    public void NeedsChoice_只有每次询问才弹二选一(DeleteBehavior behavior, bool expected)
        => Assert.Equal(expected, DeletePolicy.NeedsChoice(behavior));

    [Fact]
    public void ChoiceMessage_两种后果都说清楚()
    {
        var text = DeletePolicy.ChoiceMessage(2);

        Assert.Contains("2 个文件", text);
        Assert.Contains("回收站", text);
        Assert.Contains("永久删除", text);
        Assert.Contains("无法恢复", text);
    }

    [Fact]
    public void ChoiceMessage_单文件用文件名()
    {
        var text = DeletePolicy.ChoiceMessage(1, "猫.png");

        Assert.Contains("猫.png", text);
        Assert.DoesNotContain("1 个文件", text);
    }

    // ---------- 跨窗口文案：不能承诺当前窗口没有的补救方式 ----------

    [Fact]
    public void ConfirmMessage_私密空间不提底部撤销也不提最近删除()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.RecycleBin, 2,
            undoOffer: DeleteUndoOffer.RecycledOnly);

        Assert.Contains("回收站", text);
        Assert.DoesNotContain("撤销", text);
        Assert.DoesNotContain("最近删除", text);
    }

    [Fact]
    public void ConfirmMessage_去重窗口提最近删除但不提底部撤销()
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.RecycleBin, 2,
            undoOffer: DeleteUndoOffer.RecentDeleted);

        Assert.Contains("最近删除", text);
        Assert.DoesNotContain("撤销", text);
    }

    [Theory]
    [InlineData(DeleteUndoOffer.UndoToast)]
    [InlineData(DeleteUndoOffer.RecentDeleted)]
    [InlineData(DeleteUndoOffer.RecycledOnly)]
    public void ConfirmMessage_永久删除在任何窗口都不提补救(DeleteUndoOffer offer)
    {
        var text = DeletePolicy.ConfirmMessage(DeleteBehavior.Permanent, 2, undoOffer: offer);

        Assert.Contains("无法恢复", text);
        Assert.DoesNotContain("撤销", text);
        Assert.DoesNotContain("还原", text);
    }

    // ---------- 界面文案（以前写死在 XAML 里，执行端改了它不改） ----------

    [Fact]
    public void 界面文案_永久删除时一个撤销都不许提()
    {
        var b = DeleteBehavior.Permanent;

        Assert.DoesNotContain("撤销", DeletePolicy.DeleteModeHint(b));
        Assert.DoesNotContain("撤销", DeletePolicy.BatchDeleteTooltip(b));
        Assert.DoesNotContain("撤销", DeletePolicy.SelectionDeleteTooltip(b));
        Assert.DoesNotContain("撤销", DeletePolicy.ConfirmMessage(b, 1, "a.jpg"));
        // 点删这条最危险：必须让用户知道会弹确认
        Assert.Contains("确认", DeletePolicy.DeleteModeHint(b));
    }

    [Fact]
    public void 界面文案_每次询问时说清楚会问()
    {
        var b = DeleteBehavior.Ask;

        Assert.Contains("问", DeletePolicy.DeleteModeHint(b));
        Assert.Contains("问", DeletePolicy.BatchDeleteTooltip(b));
        Assert.Contains("问", DeletePolicy.SelectionDeleteTooltip(b));
    }

    [Fact]
    public void 界面文案_回收站时照旧承诺撤销条()
    {
        var b = DeleteBehavior.RecycleBin;

        Assert.Contains("撤销", DeletePolicy.DeleteModeHint(b));
        Assert.Contains("撤销", DeletePolicy.SelectionDeleteTooltip(b));
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
