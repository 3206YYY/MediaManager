using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaManager.Core.Diagnostics;

namespace MediaManager.App.Services;

/// <summary>
/// 缩略图加载状态的聚合器，供主界面状态栏显示「正在生成缩略图 (23/1500)」。
///
/// 三条硬约束：
/// 1) **不订阅每张图的完成事件**：几千次跨线程回调 = 几千个 Dispatcher 排队，那是卡顿源。
///    这里 250ms 轮询一次原子计数，无论多少张图，开销恒定（空闲时只有两次原子读）。
/// 2) **DispatcherTimer 必须在 UI 线程创建**（后台线程 new 的永远不会 Tick）。
/// 3) **只在文本真正变化时通知**，避免状态栏每 250ms 触发一次重排。
/// </summary>
public sealed class ThumbnailLoadMonitor : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan JumpWindow = TimeSpan.FromMilliseconds(1200);

    /// <summary>调试用帧率显示（启动前设环境变量 MM_FPS=1 即可）。</summary>
    private static readonly bool MeasureFps =
        string.Equals(Environment.GetEnvironmentVariable("MM_FPS"), "1", StringComparison.Ordinal);

    private readonly DispatcherTimer _timer;

    private long _generateBase;
    private long _decodeBase;
    private int _idleTicks;
    private long _jumpUntilTicks;

    private int _frames;
    private long _fpsStamp;
    private int _fps;

    public ThumbnailLoadMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += OnTick;

        if (MeasureFps)
        {
            _fpsStamp = Environment.TickCount64;
            CompositionTarget.Rendering += OnRendering;
        }

        _timer.Start();
    }

    // ---------- 供界面绑定 ----------

    private bool _isBusy;
    /// <summary>队列是否还有活。false 时状态栏指示自动隐藏。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _statusText = string.Empty;
    /// <summary>例如「正在生成缩略图 (23/1500)」。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private double _progress;
    /// <summary>本轮进度 0~100（只做显示，不驱动任何后台逻辑）。</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>是否开启帧率显示（调试用）。</summary>
    public bool ShowFps => MeasureFps;

    private string _fpsText = string.Empty;
    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    /// <summary>
    /// 用户拖动滚动条 / PageDown / 直接跳到大列表深处时调用：
    /// 状态栏先切到「正在定位并加载…」，让用户知道程序没死，只是在取图。
    /// 必须由 UI 线程调用。
    /// </summary>
    public void NotifyJump()
    {
        _jumpUntilTicks = Environment.TickCount64 + (long)JumpWindow.TotalMilliseconds;
        // 立刻出提示，不等下一个 250ms 节拍
        if (!IsBusy) OnTick(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (MeasureFps) UpdateFps();

        var generatePending = ThumbnailActivity.GeneratePending;
        var decodePending = ThumbnailActivity.DecodePending;
        var loading = generatePending > 0 || decodePending > 0;
        var locating = loading && Environment.TickCount64 < _jumpUntilTicks;

        if (!loading)
        {
            // 空闲分支：只花两次原子读就返回，避免 250ms 一次的无效工作
            if (!IsBusy) return;

            // 连续两次空闲才隐藏，防止临界抖动导致「显示-隐藏-显示」闪烁
            if (++_idleTicks < 2) return;
            _idleTicks = 0;
            IsBusy = false;
            StatusText = string.Empty;
            Progress = 0;
            return;
        }

        _idleTicks = 0;

        if (!IsBusy)
        {
            // 新一轮开始：把累计完成数当作基线，之后的差值就是这个批次的进度
            _generateBase = ThumbnailActivity.GenerateDone;
            _decodeBase = ThumbnailActivity.DecodeDone;
            IsBusy = true;
        }

        var done = (ThumbnailActivity.GenerateDone - _generateBase)
                 + (ThumbnailActivity.DecodeDone - _decodeBase);
        if (done < 0) done = 0;

        var pending = generatePending + decodePending;
        var total = done + pending;

        var verb = locating
            ? "正在定位并加载"
            : generatePending > 0 ? "正在生成缩略图" : "正在加载缩略图";

        StatusText = total > 0 ? $"{verb} ({Math.Min(done, total)}/{total})" : $"{verb}…";
        Progress = total > 0 ? Math.Min(100.0, done * 100.0 / total) : 0;
    }

    // ---------- 调试帧率（MM_FPS=1） ----------

    private void OnRendering(object? sender, EventArgs e) => _frames++;

    private void UpdateFps()
    {
        var now = Environment.TickCount64;
        var elapsed = now - _fpsStamp;
        if (elapsed < 500) return;

        _fps = (int)Math.Round(_frames * 1000.0 / elapsed);
        _frames = 0;
        _fpsStamp = now;

        var text = $"{_fps} fps";
        if (!string.Equals(FpsText, text, StringComparison.Ordinal)) FpsText = text;
    }
}
