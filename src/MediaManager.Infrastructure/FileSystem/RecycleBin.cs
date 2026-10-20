using System.IO;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>回收站里的一条记录。</summary>
public sealed record RecycleBinEntry(
    string OriginalPath,
    string RecyclePath,
    string MetadataPath,
    long SizeBytes,
    DateTime DeletedAtUtc);

/// <summary>
/// 直接读写 Windows 回收站（$Recycle.Bin）：
/// 读取 $I 元数据文件拿到"原路径 + 删除时间"，还原时把 $R 文件移回原位置。
/// 这样不依赖系统语言（Shell 动词"还原"在不同语言下名字不同）。
/// </summary>
public static class RecycleBin
{
    private const int MaxEntries = 20000;

    // 批量撤销时避免每条都重新扫描所有磁盘：3 秒内复用同一次扫描结果
    private static readonly object CacheLock = new();
    private static List<RecycleBinEntry>? _cache;
    private static DateTime _cacheAtUtc = DateTime.MinValue;

    /// <summary>枚举所有磁盘回收站中的文件记录（带 3 秒短期缓存）。</summary>
    public static List<RecycleBinEntry> List()
    {
        lock (CacheLock)
        {
            if (_cache != null && (DateTime.UtcNow - _cacheAtUtc).TotalSeconds < 3)
                return new List<RecycleBinEntry>(_cache);
        }

        var result = Enumerate();

        lock (CacheLock)
        {
            _cache = new List<RecycleBinEntry>(result);
            _cacheAtUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAtUtc = DateTime.MinValue;
        }
    }

    private static List<RecycleBinEntry> Enumerate()
    {
        var result = new List<RecycleBinEntry>();

        foreach (var drive in SafeDrives())
        {
            var root = System.IO.Path.Combine(drive, "$Recycle.Bin");
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> sidDirs;
            try { sidDirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var sidDir in sidDirs)
            {
                IEnumerable<string> metaFiles;
                try { metaFiles = Directory.EnumerateFiles(sidDir, "$I*"); }
                catch { continue; }

                foreach (var meta in metaFiles)
                {
                    if (result.Count >= MaxEntries) return result;

                    var name = System.IO.Path.GetFileName(meta);
                    if (name.Length < 3) continue;

                    var payload = System.IO.Path.Combine(sidDir, "$R" + name.Substring(2));
                    if (!File.Exists(payload)) continue;

                    if (!TryReadMetadata(meta, out var originalPath, out var size, out var deletedAt))
                        continue;

                    long length = 0;
                    try { length = new FileInfo(payload).Length; } catch { }

                    result.Add(new RecycleBinEntry(
                        originalPath,
                        payload,
                        meta,
                        length > 0 ? length : size,
                        deletedAt));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 从回收站还原指定文件到原路径。返回 false 时 message 为可直接展示给用户的原因。
    /// </summary>
    public static bool TryRestore(string originalPath, DateTime deletedAtLocal, out string message)
    {
        message = string.Empty;

        try
        {
            var candidates = List()
                .Where(e => PathEquals(e.OriginalPath, originalPath))
                .ToList();

            if (candidates.Count == 0)
            {
                message = "系统回收站里没有找到这个文件（可能已被清空或被其他软件恢复）。";
                return false;
            }

            // 优先匹配删除时间最接近的那一条（同名文件可能被删过多次）
            var targetUtc = deletedAtLocal.ToUniversalTime();
            var entry = candidates
                .OrderBy(e => Math.Abs((e.DeletedAtUtc - targetUtc).TotalMinutes))
                .First();

            var destination = originalPath;
            if (File.Exists(destination))
                destination = MakeUniquePath(originalPath);

            var dir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 清掉只读属性，否则移动会失败
            try
            {
                var attrs = File.GetAttributes(entry.RecyclePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry.RecyclePath, attrs & ~FileAttributes.ReadOnly);
            }
            catch { /* 忽略 */ }

            try
            {
                File.Move(entry.RecyclePath, destination);
            }
            catch (IOException)
            {
                // 跨卷或占用时退化为复制 + 删除
                File.Copy(entry.RecyclePath, destination, overwrite: false);
                File.Delete(entry.RecyclePath);
            }

            // 删除元数据，避免回收站里留下孤儿条目
            try { File.Delete(entry.MetadataPath); } catch { }
            InvalidateCache();

            message = destination;
            return true;
        }
        catch (Exception ex)
        {
            message = "还原失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>把回收站里的这个文件彻底删除（不进回收站）。</summary>
    public static bool TryDeletePermanently(string originalPath, DateTime deletedAtLocal)
    {
        try
        {
            var candidates = List()
                .Where(e => PathEquals(e.OriginalPath, originalPath))
                .ToList();
            if (candidates.Count == 0) return false;

            var targetUtc = deletedAtLocal.ToUniversalTime();
            var entry = candidates.OrderBy(e => Math.Abs((e.DeletedAtUtc - targetUtc).TotalMinutes)).First();

            try
            {
                var attrs = File.GetAttributes(entry.RecyclePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry.RecyclePath, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }

            File.Delete(entry.RecyclePath);
            try { File.Delete(entry.MetadataPath); } catch { }
            InvalidateCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 内部 ----------

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in drives)
        {
            bool ready;
            try { ready = d.IsReady; } catch { ready = false; }
            if (ready) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>
    /// 解析 $I 文件：版本(8) + 文件大小(8) + 删除时间 FILETIME(8) + [长度(4)] + UTF-16 原路径。
    /// </summary>
    private static bool TryReadMetadata(string metaPath, out string originalPath, out long size, out DateTime deletedAtUtc)
    {
        originalPath = string.Empty;
        size = 0;
        deletedAtUtc = DateTime.UtcNow;

        try
        {
            var bytes = File.ReadAllBytes(metaPath);
            if (bytes.Length < 28) return false;

            long version = BitConverter.ToInt64(bytes, 0);
            size = BitConverter.ToInt64(bytes, 8);
            long fileTime = BitConverter.ToInt64(bytes, 16);
            try { deletedAtUtc = DateTime.FromFileTimeUtc(fileTime); }
            catch { deletedAtUtc = DateTime.UtcNow; }

            int offset;
            int charCount;

            if (version >= 2)
            {
                var len = BitConverter.ToInt32(bytes, 24);
                offset = 28;
                charCount = len > 0 ? len : (bytes.Length - offset) / 2;
            }
            else
            {
                offset = 24;
                charCount = (bytes.Length - offset) / 2;
            }

            if (charCount <= 0) return false;

            var available = (bytes.Length - offset) / 2;
            if (charCount > available) charCount = available;

            var text = System.Text.Encoding.Unicode.GetString(bytes, offset, charCount * 2);
            var end = text.IndexOf('\0');
            if (end >= 0) text = text.Substring(0, end);

            text = text.TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(text)) return false;

            originalPath = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        static string Normalize(string p)
        {
            p = p.Replace('/', '\\').TrimEnd('\\');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                p = p.Substring(4);
            return p;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeUniquePath(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);

        for (int i = 1; i < 100; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} (还原{i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return System.IO.Path.Combine(dir, $"{name} (还原){ext}");
    }
}
