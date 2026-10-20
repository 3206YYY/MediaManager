using System.IO;
using System.Windows.Media.Imaging;
using MediaManager.Core.Diagnostics;

namespace MediaManager.App.Services;

/// <summary>
/// 缩略图内存缓存：LRU + 字节预算（默认 200MB）+ 按需优先的解码调度。
///
/// 为什么需要它：
/// 1) <c>Image.Source</c> 直接绑文件路径时，WPF 会在 UI 线程同步解码，快速滚动必然掉帧；
/// 2) 不做上限的字典缓存会把整库缩略图都留在内存里，几百 MB 起步；
/// 3) 淘汰后旧的 <see cref="BitmapImage"/> 只要没人引用就会被 GC 回收，
///    所以这里只管「当前活着的集合」，不额外持有什么东西。
///
/// 调度规则（V1.2.4 起）：
/// - 先进先出，但**取到请求的第一时间**再问一次「这张还有人要吗」，
///   滚动太快一闪而过的格子会在解码前被丢掉，一点 IO 都不浪费；
/// - 同一路径同时只有一次解码（虚拟化回收时很容易重复请求同一张图）；
/// - 解码并发固定 4 路，绝不因为滚动而加码 —— 加码只会让机械盘抖得更厉害。
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
    private const int DecodeWorkers = 4;

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required BitmapImage Image { get; init; }
        public long Bytes { get; init; }
    }

    /// <summary>一次待解码请求。<see cref="TaskCompletionSource"/> 让多个等待方共享同一次解码。</summary>
    private sealed class Request
    {
        public required string Path { get; init; }
        public Func<bool>? StillWanted { get; init; }
        public TaskCompletionSource<BitmapImage?> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---------- 缓存本体 ----------
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<Entry> Lru = new();     // 头部 = 最近使用

    private static long _budgetBytes = DefaultBudgetBytes;
    private static long _currentBytes;

    private static long _hits;
    private static long _misses;
    private static long _evictions;

    // ---------- 解码调度 ----------
    private static readonly object QueueGate = new();
    private static readonly Queue<Request> Queue = new();
    private static readonly Dictionary<string, Request> Inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim WorkSignal = new(0, int.MaxValue);
    private static int _workersStarted;

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

    /// <summary>排队中 + 解码中的请求数（状态栏显示用）。</summary>
    public static int PendingCount => ThumbnailActivity.DecodePending;

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
    /// 异步取图：命中直接返回；未命中则排队解码。
    /// <paramref name="stillWanted"/> 用于「滚动已经把这个格子划走了」时提前放弃，避免白干活。
    ///
    /// 注意：<paramref name="stillWanted"/> 会在**线程池**上被调用，
    /// 里面绝不能碰 WPF 对象（依赖属性/控件），否则会抛「调用线程无法访问此对象」。
    /// </summary>
    public static Task<BitmapImage?> GetOrLoadAsync(string path, Func<bool>? stillWanted = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<BitmapImage?>(null);
        if (TryGet(path, out var hit) && hit != null) return Task.FromResult<BitmapImage?>(hit);

        Request request;
        lock (QueueGate)
        {
            // 同一张图只解一次：虚拟化容器回收时，同一个路径被两格同时请求很常见
            if (Inflight.TryGetValue(path, out var existing))
                return existing.Done.Task;

            request = new Request { Path = path, StillWanted = stillWanted };
            Inflight[path] = request;
            Queue.Enqueue(request);
        }

        ThumbnailActivity.DecodeQueued();
        EnsureWorkers();
        WorkSignal.Release();
        return request.Done.Task;
    }

    private static void EnsureWorkers()
    {
        if (Interlocked.CompareExchange(ref _workersStarted, 1, 0) != 0) return;
        for (int i = 0; i < DecodeWorkers; i++)
            _ = Task.Run(WorkerLoopAsync);
    }

    private static async Task WorkerLoopAsync()
    {
        while (true)
        {
            await WorkSignal.WaitAsync().ConfigureAwait(false);

            Request? request = null;
            lock (QueueGate)
            {
                while (Queue.Count > 0)
                {
                    var candidate = Queue.Dequeue();

                    // 队列里的「占用标记」要跟着一起清掉，否则这张图以后再也不会被解
                    if (Inflight.TryGetValue(candidate.Path, out var owner) && ReferenceEquals(owner, candidate))
                        Inflight.Remove(candidate.Path);

                    // 划走了就丢：这一步发生在解码之前，所以「滑太快一闪而过」零成本
                    if (candidate.StillWanted is { } wanted && !IsStillWanted(wanted))
                    {
                        candidate.Done.TrySetResult(null);
                        ThumbnailActivity.DecodeFinished();
                        continue;
                    }

                    request = candidate;
                    break;
                }
            }

            if (request == null) continue;   // 整批都作废，计数已在上面结清

            BitmapImage? bitmap = null;
            try
            {
                if (TryGet(request.Path, out var cached) && cached != null)
                {
                    bitmap = cached;
                }
                else
                {
                    var path = request.Path;
                    bitmap = await Task.Run(() => Decode(path)).ConfigureAwait(false);
                    if (bitmap != null) Add(request.Path, bitmap);
                }
            }
            catch
            {
                // 单张失败不影响其它图
            }
            finally
            {
                ThumbnailActivity.DecodeFinished();
            }

            request.Done.TrySetResult(bitmap);
        }
    }

    /// <summary>谓词本身抛异常时按「还要」处理，宁可多解一张也不要留下空白格。</summary>
    private static bool IsStillWanted(Func<bool> predicate)
    {
        try { return predicate(); }
        catch { return true; }
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
