using System.IO;
using System.Windows.Media.Imaging;

namespace MediaManager.App.Services;

/// <summary>
/// 缩略图内存缓存：LRU + 字节预算（默认 200MB）。
///
/// 为什么需要它：
/// 1) <c>Image.Source</c> 直接绑文件路径时，WPF 会在 UI 线程同步解码，快速滚动必然掉帧；
/// 2) 不做上限的字典缓存会把整库缩略图都留在内存里，几百 MB 起步；
/// 3) 淘汰后旧的 <see cref="BitmapImage"/> 只要没人引用就会被 GC 回收，
///    所以这里只管「当前活着的集合」，不额外持有什么东西。
///
/// 约定：解码只发生在线程池；交给 UI 的 BitmapImage 一律 Freeze（可跨线程安全使用、可被 GC 回收）。
/// </summary>
public static class ThumbnailMemoryCache
{
    /// <summary>默认内存预算：200MB（约 600~800 张 320px 缩略图）。</summary>
    public const long DefaultBudgetBytes = 200L * 1024 * 1024;

    /// <summary>缩略图解码长边（缩略图文件本身最长边就是 320，再大没有意义）。</summary>
    private const int DecodeWidth = 320;

    /// <summary>同时解码的数量。太小会看到空白，太大会让机械盘/低端机抖动。</summary>
    private static readonly SemaphoreSlim DecodeGate = new(4, 4);

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required BitmapImage Image { get; init; }
        public long Bytes { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Lru = new();     // 头部 = 最近使用

    private static long _budgetBytes = DefaultBudgetBytes;
    private static long _currentBytes;

    private static long _hits;
    private static long _misses;
    private static long _evictions;

    /// <summary>内存预算（字节）。改小会立刻触发淘汰。</summary>
    public static long BudgetBytes
    {
        get => Interlocked.Read(ref _budgetBytes);
        set
        {
            Interlocked.Exchange(ref _budgetBytes, Math.Max(16L * 1024 * 1024, value));
            lock (Gate) Trim();
        }
    }

    public static long CurrentBytes => Interlocked.Read(ref _currentBytes);

    public static int Count
    {
        get { lock (Gate) return Map.Count; }
    }

    public static (long Hits, long Misses, long Evictions) Stats
        => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _evictions));

    /// <summary>命中缓存（顺带把该项移到 LRU 头部）。</summary>
    public static bool TryGet(string path, out BitmapImage? image)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(path, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                image = node.Value.Image;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }

        image = null;
        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>
    /// 异步取图：命中直接返回；未命中则在线程池解码后入缓存。
    /// <paramref name="stillWanted"/> 用于「滚动已经把这个格子划走了」时提前放弃，避免白干活。
    /// </summary>
    public static async Task<BitmapImage?> GetOrLoadAsync(string path, Func<bool>? stillWanted = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (TryGet(path, out var hit)) return hit;

        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 排队期间可能已经被别的格子加载过；或者已经滚出视野
            if (stillWanted != null && !stillWanted()) return null;
            if (TryGet(path, out hit)) return hit;

            var bitmap = await Task.Run(() => Decode(path)).ConfigureAwait(false);
            if (bitmap != null) Add(path, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static void Add(string path, BitmapImage image)
    {
        // 解码后的像素占用（BGRA 4 字节/像素）。用像素估算而不是文件大小，
        // 因为占内存的是解码结果，而 JPEG 文件通常只有几十 KB。
        long bytes = (long)image.PixelWidth * image.PixelHeight * 4;
        if (bytes <= 0) bytes = 256 * 1024;

        lock (Gate)
        {
            if (Map.TryGetValue(path, out var existing))
            {
                Lru.Remove(existing);
                _currentBytes -= existing.Value.Bytes;
                Map.Remove(path);
            }

            var node = Lru.AddFirst(new Entry { Path = path, Image = image, Bytes = bytes });
            Map[path] = node;
            _currentBytes += bytes;

            Trim();
        }
    }

    /// <summary>超出预算时从最久未使用的开始淘汰。调用方需持有 <see cref="Gate"/>。</summary>
    private static void Trim()
    {
        var budget = Interlocked.Read(ref _budgetBytes);
        while (_currentBytes > budget && Lru.Last != null)
        {
            var node = Lru.Last;
            Lru.RemoveLast();
            Map.Remove(node.Value.Path);
            _currentBytes -= node.Value.Bytes;
            Interlocked.Increment(ref _evictions);
            // 这里不需要显式释放：节点被移除后，只要 Image 控件不再引用，
            // BitmapImage 就会被 GC 正常回收（它不持有文件句柄）。
        }
    }

    private static BitmapImage? Decode(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // 读完立即关闭文件，不锁盘
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;  // 缩略图不需要色彩管理，快很多
            bmp.DecodePixelWidth = DecodeWidth;
            using (var fs = new FileStream(
                       path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();                                    // 在线程池上完成真正解码
            }
            bmp.Freeze();                                         // 冻结后可安全跨线程使用
            return bmp;
        }
        catch
        {
            // 文件被占用/损坏/已删除：这一格先空着，下次滚动会重试
            return null;
        }
    }

    /// <summary>清空（用户手动清缓存、切换根目录时用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Lru.Clear();
            _currentBytes = 0;
        }
    }
}
