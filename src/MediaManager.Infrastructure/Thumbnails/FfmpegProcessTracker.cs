using System.Collections.Concurrent;
using System.Diagnostics;

namespace MediaManager.Infrastructure.Thumbnails;

/// <summary>
/// 记录当前正在处理某个输入文件的 ffmpeg 进程。
///
/// 删除视频前必须确认没有 ffmpeg 还在读它，否则 Windows 会报
/// 「文件已在 ffmpeg 中打开」。抽帧结束（含取消 / 异常）后一定要注销。
/// </summary>
internal static class FfmpegProcessTracker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<Process>> Active =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string inputPath, Process process)
    {
        var key = Normalize(inputPath);
        lock (Gate)
        {
            if (!Active.TryGetValue(key, out var list))
            {
                list = new List<Process>();
                Active[key] = list;
            }
            list.Add(process);
        }
    }

    public static void Unregister(string inputPath, Process process)
    {
        var key = Normalize(inputPath);
        lock (Gate)
        {
            if (!Active.TryGetValue(key, out var list)) return;
            list.Remove(process);
            if (list.Count == 0) Active.Remove(key);
        }
    }

    /// <summary>等待指定文件的 ffmpeg 处理结束（最多 timeout）。</summary>
    public static async Task WaitForReleaseAsync(string inputPath, TimeSpan timeout)
    {
        var key = Normalize(inputPath);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            lock (Gate)
            {
                if (!Active.ContainsKey(key)) return;
            }
            await Task.Delay(80).ConfigureAwait(false);
        }
    }

    /// <summary>指定文件是否仍有 ffmpeg 在运行。</summary>
    public static bool IsActive(string inputPath)
    {
        var key = Normalize(inputPath);
        lock (Gate)
        {
            if (!Active.TryGetValue(key, out var list)) return false;
            list.RemoveAll(p => HasExited(p));
            if (list.Count == 0)
            {
                Active.Remove(key);
                return false;
            }
            return true;
        }
    }

    /// <summary>强杀处理某个文件的 ffmpeg（删除前兜底）。</summary>
    public static void KillForPath(string inputPath)
    {
        var key = Normalize(inputPath);
        List<Process>? processes = null;
        lock (Gate)
        {
            if (Active.TryGetValue(key, out var list))
            {
                processes = list.ToList();
                Active.Remove(key);
            }
        }
        Kill(processes);
    }

    /// <summary>程序退出时兜底：杀掉所有仍在跑的 ffmpeg，避免留下锁文件的孤儿进程。</summary>
    public static void KillAll()
    {
        List<Process>? processes = null;
        lock (Gate)
        {
            processes = Active.Values.SelectMany(v => v).ToList();
            Active.Clear();
        }
        Kill(processes);
    }

    private static void Kill(List<Process>? processes)
    {
        if (processes == null) return;
        foreach (var process in processes)
        {
            try
            {
                if (!HasExited(process)) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程已退出 / 无权结束：都不该影响删除流程
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
