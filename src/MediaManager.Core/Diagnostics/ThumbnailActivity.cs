namespace MediaManager.Core.Diagnostics;

/// <summary>
/// 缩略图工作量计数：告诉界面「后台还有多少活没干完」。
///
/// 为什么不走事件：缩略图高峰每秒上千次请求/完成，每完成一张就 Post 一次 Dispatcher，
/// UI 线程会瞬间积压几千个回调 —— 那才是真正的卡顿来源。
/// 这里只做原子计数，UI 侧按固定节拍（250ms）拉一次，无论多少张图，代价都是恒定的。
///
/// 两条链路各有一份计数：
/// - Generate：把原图解码 + 缩放 + 落盘成 320px 缓存文件（缩略图服务，4 worker）；
/// - Decode：把缓存文件解码成 <c>BitmapImage</c> 交给 Image 控件（4 worker）。
/// </summary>
public static class ThumbnailActivity
{
    private static int _generatePending;
    private static long _generateDone;
    private static int _decodePending;
    private static long _decodeDone;

    /// <summary>排队中 + 生成中的缩略图数量。</summary>
    public static int GeneratePending => System.Threading.Volatile.Read(ref _generatePending);

    /// <summary>累计生成完成数（只增不减，UI 用差值算本轮进度）。</summary>
    public static long GenerateDone => System.Threading.Interlocked.Read(ref _generateDone);

    /// <summary>排队中 + 解码中的请求数量。</summary>
    public static int DecodePending => System.Threading.Volatile.Read(ref _decodePending);

    /// <summary>累计解码完成数。</summary>
    public static long DecodeDone => System.Threading.Interlocked.Read(ref _decodeDone);

    public static void GenerationQueued() => System.Threading.Interlocked.Increment(ref _generatePending);

    public static void GenerationFinished()
    {
        System.Threading.Interlocked.Increment(ref _generateDone);
        // 通道满被丢弃、异常路径等都可能让「完成」多于「入队」，UI 只关心 0 与非 0，钳到 0 即可
        if (System.Threading.Interlocked.Decrement(ref _generatePending) < 0)
            System.Threading.Interlocked.Exchange(ref _generatePending, 0);
    }

    public static void DecodeQueued() => System.Threading.Interlocked.Increment(ref _decodePending);

    public static void DecodeFinished()
    {
        System.Threading.Interlocked.Increment(ref _decodeDone);
        if (System.Threading.Interlocked.Decrement(ref _decodePending) < 0)
            System.Threading.Interlocked.Exchange(ref _decodePending, 0);
    }

    /// <summary>启动新库/清空队列时复位（只影响显示，不干预正在跑的任务）。</summary>
    public static void Reset()
    {
        System.Threading.Interlocked.Exchange(ref _generatePending, 0);
        System.Threading.Interlocked.Exchange(ref _decodePending, 0);
    }
}
