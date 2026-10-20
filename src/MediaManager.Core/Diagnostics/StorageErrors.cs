using System.IO;

namespace MediaManager.Core.Diagnostics;

/// <summary>
/// 文件 / 磁盘异常的识别与中文提示。
/// 重点覆盖「移动硬盘、U 盘被拔出」这类场景：底层异常文案通常是英文或系统错误码，
/// 直接弹给用户看不懂，这里统一翻译成能照做的提示。
/// </summary>
public static class StorageErrors
{
    // Win32 错误码（HRESULT 低 16 位）
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorWriteProtect = 19;
    private const int ErrorNotReady = 21;
    private const int ErrorGenFailure = 31;
    private const int ErrorSharingViolation = 32;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorBadNetPath = 53;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorDiskFull = 112;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorDeviceRemoved = 1617;

    /// <summary>是否为「设备已断开 / 未就绪」类错误（移动硬盘、U 盘被拔出的典型表现）。</summary>
    public static bool IsDeviceUnavailable(Exception? ex)
    {
        if (ex == null) return false;

        foreach (var e in Chain(ex))
        {
            if (e is DriveNotFoundException) return true;

            if (e is IOException && IsDeviceWin32Code(e.HResult & 0xFFFF))
                return true;

            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;

            if (Contains(msg, "设备未就绪") || Contains(msg, "设备未连接") ||
                Contains(msg, "设备已被移除") || Contains(msg, "设备已被删除") ||
                Contains(msg, "驱动器未就绪") || Contains(msg, "尚未将磁盘插入") ||
                Contains(msg, "device is not ready") || Contains(msg, "device is not connected") ||
                Contains(msg, "device has been removed") || Contains(msg, "device was removed") ||
                Contains(msg, "network path was not found") || Contains(msg, "网络路径不存在"))
                return true;
        }

        return false;
    }

    /// <summary>把异常翻译成一句用户能看懂、能照做的中文提示。</summary>
    public static string FriendlyMessage(Exception ex)
    {
        if (IsDeviceUnavailable(ex))
            return "设备已断开或未就绪（可能是移动硬盘 / U 盘被拔出）。请重新插好后重试；如果文件就在本机磁盘上，请确认该磁盘是否正常。";

        var chain = Chain(ex).ToList();

        if (chain.Any(e => (e.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull))
            return "磁盘空间不足，无法完成写入。请清理空间后重试。";

        if (chain.Any(e => (e.HResult & 0xFFFF) == ErrorWriteProtect))
            return "目标磁盘处于写保护状态，无法写入。请解除写保护后重试。";

        if (chain.Any(e => (e.HResult & 0xFFFF) == ErrorSharingViolation))
            return "文件正被其他程序占用，请关闭相关程序后重试。";

        if (chain.Any(e => e is UnauthorizedAccessException || (e.HResult & 0xFFFF) == ErrorAccessDenied))
            return "没有权限访问该文件或文件夹。请检查文件是否被占用，或以更高权限运行程序。";

        if (chain.Any(e => e is FileNotFoundException || (e.HResult & 0xFFFF) == ErrorFileNotFound))
            return "文件已不存在（可能被移动、重命名或删除）。";

        if (chain.Any(e => e is DirectoryNotFoundException || (e.HResult & 0xFFFF) == ErrorPathNotFound))
            return "所在文件夹已不存在（可能被移动、重命名，或所在磁盘未连接）。";

        // 兜底：取最有信息量的一条消息，避免只显示「One or more errors occurred」
        var msg = chain.Select(e => e.Message)
            .LastOrDefault(m => !string.IsNullOrWhiteSpace(m)
                                && !m.Contains("One or more errors occurred", StringComparison.OrdinalIgnoreCase))
            ?? "未知错误";

        return "读写文件时出错：" + msg.Trim();
    }

    /// <summary>带动作前缀的提示，例如 WithAction(ex, "删除") → "删除失败：设备已断开…"。</summary>
    public static string WithAction(Exception ex, string action)
        => $"{action}失败：{FriendlyMessage(ex)}";

    /// <summary>是否为「文件不存在 / 找不到」类错误（可当作正常情况跳过，不必惊动用户）。</summary>
    public static bool IsMissingFile(Exception? ex)
    {
        if (ex == null) return false;
        foreach (var e in Chain(ex))
        {
            if (e is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException)
                return true;
            if (e is IOException && (e.HResult & 0xFFFF) is ErrorFileNotFound or ErrorPathNotFound)
                return true;
        }
        return false;
    }

    private static bool IsDeviceWin32Code(int code) => code switch
    {
        ErrorNotReady => true,
        ErrorGenFailure => true,
        ErrorBadNetPath => true,
        ErrorNetNameDeleted => true,
        ErrorDeviceNotConnected => true,
        ErrorDeviceRemoved => true,
        _ => false,
    };

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>展开异常链（含 AggregateException 的多个内部异常），已去重。</summary>
    private static IEnumerable<Exception> Chain(Exception ex)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Exception>();
        queue.Enqueue(ex);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!seen.Add(cur)) continue;
            yield return cur;

            if (cur is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    queue.Enqueue(inner);
            }
            else if (cur.InnerException != null)
            {
                queue.Enqueue(cur.InnerException);
            }
        }
    }
}
