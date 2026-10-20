namespace MediaManager.Core.Media;

/// <summary>
/// 视频 Seek 请求合并器（纯逻辑、无 UI / 无 LibVLC 依赖，可单测）。
///
/// 背景：进度条被拖动或连续点击时，鼠标事件会在极短时间内产生几十个 Seek 请求。
/// 如果每个请求都直接下发 <c>MediaPlayer.Time = x</c>，LibVLC 的解码线程会被反复打断，
/// 表现为「画面卡死不动」「再也快进不了」，严重时整个播放内核卡住。
///
/// 本类保证两件事：
/// 1) 同一时刻只允许一个底层 Seek 在执行（串行化）；
/// 2) 执行期间到来的新请求只保留「最新那个」，旧目标直接丢弃（合并）。
///
/// 用法：
/// <code>
/// if (coalescer.TryBegin(target, out var first))
///     _ = RunSeekLoopAsync(first);       // 已有任务在跑时返回 false，无需再起任务
/// ...
/// while (true)
/// {
///     await RunNativeSeekAsync(current);
///     if (!coalescer.TryContinue(out current)) break;
/// }
/// </code>
/// </summary>
public sealed class SeekRequestCoalescer
{
    private readonly object _gate = new();

    private long? _pending;
    private bool _running;
    private long _executed;
    private long _dropped;

    /// <summary>当前是否有底层 Seek 正在执行。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>已经真正下发到底层的 Seek 次数（调试 / 测试用）。</summary>
    public long ExecutedCount
    {
        get { lock (_gate) return _executed; }
    }

    /// <summary>被合并（丢弃）的请求次数（调试 / 测试用）。</summary>
    public long DroppedCount
    {
        get { lock (_gate) return _dropped; }
    }

    /// <summary>
    /// 提交一个目标时间。
    /// 返回 true 表示调用方需要立刻去执行底层 Seek（<paramref name="target"/> 为要执行的目标）；
    /// 返回 false 表示已有任务在执行，本次目标已登记，执行中的任务会自己补跑最新目标。
    /// </summary>
    public bool TryBegin(long targetMs, out long target)
    {
        lock (_gate)
        {
            _pending = targetMs;

            if (_running)
            {
                _dropped++;
                target = 0;
                return false;
            }

            _running = true;
            target = _pending.Value;
            _pending = null;
            _executed++;
            return true;
        }
    }

    /// <summary>
    /// 一次底层 Seek 结束。
    /// 返回 true 表示期间又来了更新的请求（<paramref name="target"/> 为新目标，调用方应继续执行）；
    /// 返回 false 表示没有新请求，任务结束。
    /// </summary>
    public bool TryContinue(out long target)
    {
        lock (_gate)
        {
            if (_pending is long next)
            {
                target = next;
                _pending = null;
                _executed++;
                return true;
            }

            _running = false;
            target = 0;
            return false;
        }
    }

    /// <summary>清空状态（关闭播放器 / 切换视频时调用）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending = null;
            _running = false;
        }
    }
}
