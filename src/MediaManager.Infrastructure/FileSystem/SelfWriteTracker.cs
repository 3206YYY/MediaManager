using System.Collections.Concurrent;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>
/// 程序自己写出去的文件（例如编辑器保存的 "_edited" 副本）会被 FileSystemWatcher 当成"新文件"，
/// 从而弹「发现 1 个新文件」。这里登记一下，让监控在短时间内忽略它们。
/// </summary>
public static class SelfWriteTracker
{
    private static readonly ConcurrentDictionary<string, DateTime> Recent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    public static void Register(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Recent[Normalize(path)] = DateTime.UtcNow;
        Prune();
    }

    public static bool IsRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Recent.TryGetValue(Normalize(path), out var at)) return false;
        if (DateTime.UtcNow - at <= Window) return true;
        Recent.TryRemove(Normalize(path), out _);
        return false;
    }

    private static void Prune()
    {
        if (Recent.Count < 256) return;
        var cutoff = DateTime.UtcNow - Window;
        foreach (var kv in Recent)
        {
            if (kv.Value < cutoff) Recent.TryRemove(kv.Key, out _);
        }
    }

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }
    }
}
