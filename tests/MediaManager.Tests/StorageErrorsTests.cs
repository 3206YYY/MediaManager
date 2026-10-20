using System.Runtime.InteropServices;
using MediaManager.Core.Diagnostics;

namespace MediaManager.Tests;

/// <summary>
/// 存储异常识别测试：确保「移动硬盘被拔出」这类底层异常能被翻译成可读提示，
/// 而不是把英文错误码直接丢给用户。
/// </summary>
public class StorageErrorsTests
{
    [Fact]
    public void DeviceNotReady_IsRecognized_AndGivesActionableMessage()
    {
        // HRESULT_FROM_WIN32(ERROR_NOT_READY = 21)：移动硬盘拔出时的典型错误
        var ex = Marshal.GetExceptionForHR(unchecked((int)0x80070015));
        Assert.NotNull(ex);

        Assert.True(StorageErrors.IsDeviceUnavailable(ex));
        var msg = StorageErrors.FriendlyMessage(ex!);
        Assert.Contains("设备已断开", msg);
        Assert.Contains("重新插好", msg);
    }

    [Fact]
    public void DeviceRemoved_IsRecognized()
    {
        // HRESULT_FROM_WIN32(ERROR_DEVICE_REMOVED = 1617)
        var ex = Marshal.GetExceptionForHR(unchecked((int)0x80070651));

        Assert.True(StorageErrors.IsDeviceUnavailable(ex));
    }

    [Fact]
    public void FileNotFound_IsRecognized_AsMissingFile()
    {
        var ex = new FileNotFoundException("找不到文件");

        Assert.True(StorageErrors.IsMissingFile(ex));
        Assert.False(StorageErrors.IsDeviceUnavailable(ex));
        Assert.Contains("文件已不存在", StorageErrors.FriendlyMessage(ex));
    }

    [Fact]
    public void Unauthorized_IsRecognized_AsPermissionProblem()
    {
        var ex = new UnauthorizedAccessException("拒绝访问");

        Assert.Contains("没有权限", StorageErrors.FriendlyMessage(ex));
    }

    [Fact]
    public void AggregateException_IsUnwrapped()
    {
        var inner = Marshal.GetExceptionForHR(unchecked((int)0x80070015))!;
        var ex = new AggregateException("外层包装", inner);

        Assert.True(StorageErrors.IsDeviceUnavailable(ex));
        Assert.Contains("设备已断开", StorageErrors.FriendlyMessage(ex));
    }

    [Fact]
    public void UnknownError_FallsBackToOriginalMessage()
    {
        var ex = new InvalidOperationException("某种未知错误");

        Assert.False(StorageErrors.IsDeviceUnavailable(ex));
        Assert.Contains("某种未知错误", StorageErrors.FriendlyMessage(ex));
    }

    [Fact]
    public void WithAction_PrefixesAction()
    {
        var ex = new FileNotFoundException("找不到文件");

        Assert.StartsWith("删除失败：", StorageErrors.WithAction(ex, "删除"));
    }
}
