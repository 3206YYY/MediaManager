using MediaManager.App.Views.Dialogs;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaManager.App.Services;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Media;
using MediaManager.Core.Services;

namespace MediaManager.App.Views;

/// <summary>
/// 视频播放窗口（现代播放器形态）。
///
/// 界面结构上有一个必须知道的前提：
/// <see cref="LibVLCSharp.WPF.VideoView"/> 内部是 <c>HwndHost</c>（独立 HWND），
/// WPF 普通控件是画不到它上面的（airspace）。唯一可用的叠加方式是
/// <c>VideoView.Content</c> —— LibVLCSharp 会把这份内容放进一个跟随视频区域的
/// 透明前景窗口里，所以控制栏、文件名、错误提示都写在 VideoView.Content 里。
///
/// 进度条仍然遵守三条铁律：
/// 1) <b>单向数据流</b>：播放器 → 滑块 只有定时器这一条路径；
///    滑块 → 播放器 只在「用户松手」那一刻发生一次，<c>ValueChanged</c> 里绝不 Seek。
/// 2) <b>交互期间冻结自动刷新</b>（<see cref="_userSeeking"/>）。
/// 3) <b>Seek 串行 + 合并 + 线程池执行</b>（<see cref="SeekRequestCoalescer"/>）。
/// </summary>
public partial class VideoPlayerWindow : Window
{
    private const double IdleSeconds = 3.0;      // 鼠标静止多久后自动隐藏控制栏
    private const double BottomHotZone = 110;    // 底部热区（DIP）：鼠标进入后控制栏浮现
    private const long StepMs = 5000;            // 左右方向键步长

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;

    /// <summary>已经播过的 Media 不立刻 Dispose（LibVLC 可能还在读），统一留到窗口关闭。</summary>
    private readonly List<Media> _retiredMedia = new();

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private readonly SeekRequestCoalescer _seekCoalescer = new();

    // ---------- 播放列表 ----------
    private List<string> _playlist = new();
    private int _playlistIndex = -1;

    /// <summary>设置项：是否记住播放模式（"1" 记住 / "0" 每次回到播完暂停）。默认记住。</summary>
    public const string RememberModeSettingKey = "RememberVideoPlaybackMode";
    // 默认「播完暂停」：一部片子播完就停下，不替用户决定要不要继续看下一个。
    // 用户可在左下角自己切「列表循环 / 单曲循环 / 随机播放」，选择会写进 AppSetting 记住。
    private VideoPlaybackMode _playMode = VideoPlaybackMode.PauseAtEnd;

    /// <summary>是否记住用户选的播放模式（设置页可关；关掉后每次打开都是「播完暂停」）。</summary>
    private bool _rememberMode = true;
    private bool _endHandled;

    // ---------- 进度条状态 ----------
    private bool _userSeeking;
    private bool _seekInFlight;

    // ---------- 音量 ----------
    private int _volume = 80;
    private bool _muted;
    private bool _syncingVolume;

    // ---------- 倍速 ----------
    private float _currentRate = 1.0f;

    // ---------- 控制栏显隐 ----------
    private bool _chromeVisible = true;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private Point _lastCursor = new(-1, -1);
    private bool _popupOpen;
    private DateTime _lastToggleUtc = DateTime.MinValue;

    // ---------- 窗口状态 ----------
    private bool _isFullScreen;
    private WindowState _prevState = WindowState.Normal;
    private WindowStyle _prevStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResize = ResizeMode.CanResize;
    private CancellationTokenSource? _resyncCts;
    private bool _settingsLoaded;
    private CancellationTokenSource? _prefSaveCts;
    private System.Windows.Interop.HwndSource? _hwndSource;

    private enum CenterAction { None, Retry, Replay, Dismiss }

    private CenterAction _centerAction = CenterAction.None;

    // ===================================================================
    //  Win32：最大化必须铺满「整块显示器」而不是「工作区」
    //  WPF 默认最大化的上限由系统按 rcWork 计算，任务栏会露出来。
    //  我们在 WM_GETMINMAXINFO 里把上限改成 rcMonitor，才能真正全屏。
    // ===================================================================

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    // 注意：POINT 已在文件末尾的「光标位置」区定义，这里不要重复声明。
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public VideoPlayerWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTimerTick;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1100) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);

        // 进度条的按下/松开必须用 handledEventsToo: true 挂：
        // Slider 内部的 Track/Thumb 会在 Preview 阶段就把鼠标事件标记为已处理，
        // XAML 上普通的 Preview 处理器因此收不到（表现为「点了没反应、拖不动」）。
        ProgressSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekMouseDown), handledEventsToo: true);
        ProgressSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnSeekMouseUp), handledEventsToo: true);
        ProgressSlider.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnSeekMouseMove), handledEventsToo: true);

        // 画面点击 / 滚轮
        OverlayRoot.MouseLeftButtonDown += OnOverlayMouseDown;
        OverlayRoot.PreviewMouseWheel += OnOverlayMouseWheel;

        // 弹出菜单打开期间不能自动隐藏（否则菜单跟着消失）
        SpeedPopup.Opened += (_, _) => { _popupOpen = true; PingActivity(); };
        SpeedPopup.Closed += (_, _) => { _popupOpen = false; PingActivity(); };
        ModePopup.Opened += (_, _) => { _popupOpen = true; PingActivity(); };
        ModePopup.Closed += (_, _) => { _popupOpen = false; PingActivity(); };

        BuildSpeedMenu();
        BuildModeMenu();
        UpdateModeUi();
        UpdateVolumeGlyph();

        // 内容会被搬进 LibVLCSharp 的前景窗口，XAML 里的 ElementName 绑定不一定还生效，
        // 这里用代码兜底指定锚点，保证菜单始终贴着按钮弹出。
        SpeedPopup.PlacementTarget = SpeedButton;
        ModePopup.PlacementTarget = ModeButton;

        // 快捷键：用 PreProcessInput 而不是 Window.KeyDown。
        // 原因：control bar 在 LibVLCSharp 的「前景窗口」里，焦点常常不在本窗口上，
        // Window.KeyDown 收不到按键。PreProcessInput 是线程级入口，两个窗口都能覆盖。
        InputManager.Current.PreProcessInput += OnPreProcessInput;

        Loaded += OnWindowLoaded;
    }

    // ===================================================================
    //  对外入口
    // ===================================================================

    /// <summary>播放单个文件（独立打开、资源管理器右键等场景）。</summary>
    public void Play(string path) => SetPlaylist(new[] { path }, 0);

    /// <summary>
    /// 播放一个视频列表，并从 <paramref name="startIndex"/> 开始 —— 播完可以自动连播下一个。
    /// </summary>
    public void SetPlaylist(IReadOnlyList<string> videos, int startIndex)
    {
        _playlist = videos
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_playlist.Count == 0)
        {
            ShowCenterMessage("没有可播放的视频", "当前列表里没有视频文件。", CenterAction.Dismiss);
            return;
        }

        _playlistIndex = startIndex < 0 || startIndex >= _playlist.Count ? 0 : startIndex;

        try
        {
            EnsurePlayer();
        }
        catch (Exception ex)
        {
            ShowCenterMessage("无法启动播放器", StorageErrors.WithAction(ex, "启动视频播放"), CenterAction.Dismiss);
            return;
        }

        PlayCurrent();
    }

    // ===================================================================
    //  播放器初始化 / 切换文件
    // ===================================================================

    private void EnsurePlayer()
    {
        if (_player != null) return;

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _player = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _player;

        _player.EndReached += OnPlayerEndReached;
        _player.EncounteredError += OnPlayerEncounteredError;
        _player.Playing += OnPlayerTransportChanged;
        _player.Paused += OnPlayerTransportChanged;
        _player.Stopped += OnPlayerTransportChanged;
        _player.LengthChanged += OnPlayerLengthChanged;
    }

    /// <summary>播放 <see cref="_playlistIndex"/> 指向的文件。</summary>
    private void PlayCurrent()
    {
        if (_player == null || _playlist.Count == 0) return;

        if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count) _playlistIndex = 0;

        var path = _playlist[_playlistIndex];
        var name = System.IO.Path.GetFileName(path);

        Title = $"{name} - 视频播放器";
        TitleText.Text = name;
        PlaylistIndexText.Text = _playlist.Count > 1 ? $"({_playlistIndex + 1} / {_playlist.Count})" : string.Empty;

        _endHandled = false;
        HideCenterMessage();
        ResetProgressUi();
        VideoBackdrop.Visibility = Visibility.Visible;   // 换文件期间先盖黑，避免闪出桌面

        if (!File.Exists(path))
        {
            ShowCenterMessage("文件打不开", $"文件不存在，可能已被移动或所在磁盘已断开：\n{path}",
                CenterAction.Dismiss);
            return;
        }

        try
        {
            var media = new Media(_libVlc!, path);

            // 旧 media 先留着，切完再回收，避免「刚换文件就 Dispose」导致 LibVLC 读已释放句柄
            if (_media != null) _retiredMedia.Add(_media);
            _media = media;

            _player!.Play(media);
            _timer.Start();

            // 音量 / 静音是播放器级状态，换文件后要重新贴上
            ApplyVolumeToPlayer();
            _ = ApplyScaleFixLaterAsync();

            PingActivity();
            UpdateTransportUi();
        }
        catch (Exception ex)
        {
            ShowCenterMessage("无法播放此视频", StorageErrors.WithAction(ex, "打开视频"), CenterAction.Retry);
        }
    }

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        // ⚠ 这里跑在 LibVLC 的回调线程：绝对不能直接 Play（会死锁），先切回 UI 线程
        Dispatcher.BeginInvoke(new Action(() => _ = HandlePlaybackEndedAsync()), DispatcherPriority.Background);
    }

    private async Task HandlePlaybackEndedAsync()
    {
        if (_endHandled) return;
        _endHandled = true;

        // 结束瞬间 vout 还没完全释放，立刻 Play 下一个容易黑屏 / 失败
        try { await Task.Delay(180); } catch { }

        if (_player == null) return;

        var next = PlaybackQueue.Next(_playMode, _playlistIndex, _playlist.Count, Random.Shared.Next());
        if (next >= 0)
        {
            _playlistIndex = next;
            PlayCurrent();
            ShowToast(_playMode switch
            {
                VideoPlaybackMode.LoopSingle => "单曲循环：重新播放",
                VideoPlaybackMode.Random => "随机播放下一个",
                _ => "列表循环：播放下一个",
            });
            return;
        }

        ShowEndedPanel();
    }

    private void OnPlayerEncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _timer.Stop();
            ShowCenterMessage("无法播放此视频",
                "解码失败：格式 / 编码不受支持，或文件已损坏。\n可以先「播放下一个」，或检查文件是否完整。",
                CenterAction.Retry);
        }), DispatcherPriority.Background);
    }

    private void OnPlayerTransportChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateTransportUi), DispatcherPriority.Background);

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Length > 0) TotalText.Text = FormatTime(e.Length);
        }), DispatcherPriority.Background);

    private void ShowEndedPanel()
    {
        var name = _playlistIndex >= 0 && _playlistIndex < _playlist.Count
            ? System.IO.Path.GetFileName(_playlist[_playlistIndex])
            : string.Empty;

        var detail = _playMode == VideoPlaybackMode.PauseAtEnd
            ? "按当前设置「播完暂停」。可以在左下角切换为「列表循环」「单曲循环」或「随机播放」。"
            : "列表里已经没有可播放的视频了。";

        ShowCenterMessage("播放结束", string.IsNullOrEmpty(name) ? detail : $"{name}\n{detail}", CenterAction.Replay);
        UpdateTransportUi();
        PingActivity();
    }

    // ===================================================================
    //  进度同步（播放器 → 界面，单向）
    // ===================================================================

    private void OnTimerTick(object? sender, EventArgs e)
    {
        RefreshProgress();
        PollMouseActivity();
        GuardStuckSeek();
        SyncTransportUi();
    }

    private void RefreshProgress(bool force = false)
    {
        var player = _player;
        if (player == null) return;

        long length;
        long time;
        try
        {
            length = player.Length;
            time = player.Time;
        }
        catch
        {
            return; // 播放器正在释放
        }

        if (length <= 0) return;

        if (Math.Abs(ProgressSlider.Maximum - length) > 0.5)
        {
            ProgressSlider.Maximum = length;
            // Slider 默认 SmallChange=0.1，而这里单位是毫秒 → 点一下只走 0.1ms（像点不动）
            ProgressSlider.SmallChange = Math.Max(1, length / 1000.0);
            ProgressSlider.LargeChange = Math.Max(1, length / 20.0);
            TotalText.Text = FormatTime(length);
        }

        // 用户交互中 / Seek 还没落地：绝不用播放器时间反写滑块
        if (!force && (_userSeeking || _seekInFlight)) return;

        if (time < 0) time = 0;
        if (time > length) time = length;

        ProgressSlider.Value = time;
        TimeText.Text = FormatTime(time);
    }

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private static string FormatRate(float rate) => $"{rate:0.##}x";

    private void ResetProgressUi()
    {
        ProgressSlider.Maximum = 1;
        ProgressSlider.Value = 0;
        TimeText.Text = "00:00";
        TotalText.Text = "00:00";
    }

    private long SafeTime()
    {
        try { return _player?.Time ?? 0; } catch { return 0; }
    }

    private long SafeLength()
    {
        try { return _player?.Length ?? 0; } catch { return 0; }
    }

    // ===================================================================
    //  进度条交互（界面 → 播放器，只在松手时一次）
    // ===================================================================

    private void OnProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 只做时间文字的实时预览；这里绝不 Seek
        if (!_userSeeking) return;
        TimeText.Text = FormatTime((long)e.NewValue);
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => BeginSeekInteraction();

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e) => CommitSeekInteraction();

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e) => BeginSeekInteraction();

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e) => CommitSeekInteraction();

    /// <summary>按住轨道一路拖动（WPF 默认只在按下那一下跳，后续移动不更新）。</summary>
    private void OnSeekMouseMove(object sender, MouseEventArgs e)
    {
        if (!_userSeeking) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (ProgressSlider.Template?.FindName("PART_Track", ProgressSlider) is not Track track) return;

        var value = track.ValueFromPoint(e.GetPosition(track));
        if (double.IsNaN(value)) return;

        ProgressSlider.Value = Math.Clamp(value, ProgressSlider.Minimum, ProgressSlider.Maximum);
        TimeText.Text = FormatTime((long)ProgressSlider.Value);
    }

    private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e) => CommitSeekInteraction();

    private void OnWindowLostMouseCapture(object sender, MouseEventArgs e) => CommitSeekInteraction();

    private void BeginSeekInteraction()
    {
        if (_player == null) return;
        _userSeeking = true;
        PingActivity();
    }

    private void CommitSeekInteraction()
    {
        if (!_userSeeking) return;
        _userSeeking = false;
        RequestSeek((long)Math.Round(ProgressSlider.Value));
    }

    /// <summary>
    /// 兜底：鼠标在窗口外松开、或被别的窗口接走捕获时，WPF 收不到 MouseUp。
    /// 每 200ms 检查一次真实按键状态，避免永久卡在「拖动中」。
    /// </summary>
    private void GuardStuckSeek()
    {
        if (!_userSeeking) return;
        if (IsLeftButtonDown()) return;
        CommitSeekInteraction();
    }

    // ===================================================================
    //  Seek（串行 + 合并 + 异步）
    // ===================================================================

    private void RequestSeek(long targetMs)
    {
        var player = _player;
        if (player == null) return;

        var length = SafeLength();
        if (length > 0) targetMs = Math.Clamp(targetMs, 0, length);
        if (targetMs < 0) targetMs = 0;

        if (_seekCoalescer.TryBegin(targetMs, out var first))
        {
            _seekInFlight = true;
            _ = RunSeekLoopAsync(player, first);
        }
    }

    private void SeekRelative(long deltaMs)
    {
        var player = _player;
        if (player == null) return;

        var length = SafeLength();
        if (length <= 0) return;

        var target = Math.Clamp(SafeTime() + deltaMs, 0, Math.Max(0, length - 400));
        RequestSeek(target);

        // 让滑块立刻跟手，不必等 LibVLC 回调
        ProgressSlider.Value = target;
        TimeText.Text = FormatTime(target);

        ShowToast(deltaMs > 0 ? $"快进 {deltaMs / 1000} 秒" : $"快退 {-deltaMs / 1000} 秒");
        PingActivity();
    }

    private async Task RunSeekLoopAsync(MediaPlayer player, long firstTarget)
    {
        var target = firstTarget;

        try
        {
            while (true)
            {
                await Task.Run(() => ApplyNativeSeek(player, target));
                if (!_seekCoalescer.TryContinue(out target)) break;
            }
        }
        finally
        {
            _seekInFlight = false;
            _seekCoalescer.Reset();
            if (ReferenceEquals(_player, player)) _userSeeking = false;
        }

        if (ReferenceEquals(_player, player)) RefreshProgress(force: true);
    }

    /// <summary>
    /// 真正调用 LibVLC 定位（线程池执行；Time setter 是同步阻塞调用，UI 线程碰不得）。
    /// 播放结束 / 停止后单纯赋值不会重新开始播放，所以先重启再定位。
    /// </summary>
    private static void ApplyNativeSeek(MediaPlayer player, long targetMs)
    {
        try
        {
            var state = player.State;
            var needRestart = state is VLCState.Ended or VLCState.Stopped or VLCState.Error;

            if (needRestart)
            {
                player.Play();
                Thread.Sleep(60);
            }

            player.Time = targetMs;

            if (needRestart)
            {
                Thread.Sleep(60);
                if (Math.Abs(player.Time - targetMs) > 1500) player.Time = targetMs;
            }
        }
        catch
        {
            // 播放器已释放 / 设备被拔出：交给外层状态兜底
        }
    }

    // ===================================================================
    //  播放控制
    // ===================================================================

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        var player = _player;
        if (player == null) return;

        // 防止「画面点击」与「窗口点击」双路径同时触发造成连切两次
        var now = DateTime.UtcNow;
        if ((now - _lastToggleUtc).TotalMilliseconds < 150) return;
        _lastToggleUtc = now;

        try
        {
            if (player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
            {
                PlayCurrent();
                return;
            }

            if (player.IsPlaying) player.Pause();
            else player.Play();
        }
        catch { /* 播放器正在释放 */ }

        PingActivity();
        UpdateTransportUi();
        _ = UpdateTransportUiLaterAsync();
    }

    private async Task UpdateTransportUiLaterAsync()
    {
        // LibVLC 的 Pause/Play 是异步生效的，稍等一下再取一次真实状态
        try { await Task.Delay(140); } catch { }
        if (_player != null) UpdateTransportUi();
    }

    private void UpdateTransportUi()
    {
        var playing = false;
        try { playing = _player?.IsPlaying == true; } catch { }

        var glyph = playing ? "\uE769" : "\uE768";   // Pause / Play
        if (PlayPauseGlyph.Text != glyph) PlayPauseGlyph.Text = glyph;

        PlayPauseButton.ToolTip = playing ? "暂停（空格）" : "播放（空格）";
        UpdateVideoBackdrop();
    }

    /// <summary>
    /// 只有真正有画面（Playing / Paused）时才让 VideoView 的透明前景窗口露出来；
    /// 其余状态用不透明底色兜住，避免看到桌面。
    /// </summary>
    private void UpdateVideoBackdrop()
    {
        var state = VLCState.NothingSpecial;
        try { if (_player != null) state = _player.State; } catch { }

        // 只有 Playing / Paused 才保证画面已经出来；Opening / Buffering 也先盖黑
        var show = state is not (VLCState.Playing or VLCState.Paused);

        var target = show ? Visibility.Visible : Visibility.Collapsed;
        if (VideoBackdrop.Visibility != target) VideoBackdrop.Visibility = target;
    }

    /// <summary>定时器里的轻量校正：图标与实际状态不一致时才写。</summary>
    private void SyncTransportUi()
    {
        var playing = false;
        try { playing = _player?.IsPlaying == true; } catch { }
        var expected = playing ? "\uE769" : "\uE768";
        if (PlayPauseGlyph.Text != expected) PlayPauseGlyph.Text = expected;
        UpdateVideoBackdrop();
    }

    // ---------- 倍速 ----------

    private void OnSpeedButtonClick(object sender, RoutedEventArgs e)
    {
        PingActivity();
        SpeedPopup.IsOpen = !SpeedPopup.IsOpen;
    }

    private void BuildSpeedMenu()
    {
        foreach (var rate in new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f })
        {
            var button = new Button
            {
                Style = (Style)FindResource("PopupItem"),
                Tag = rate,
                Content = new TextBlock { Text = FormatRate(rate) },
            };
            button.Click += (s, args) =>
            {
                SpeedPopup.IsOpen = false;
                if (s is Button b && b.Tag is float r) _ = SetRateAsync(r);
            };
            SpeedList.Children.Add(button);
        }
        UpdateSpeedMenuState();
    }

    private void UpdateSpeedMenuState()
    {
        foreach (var child in SpeedList.Children)
        {
            if (child is not Button b || b.Tag is not float rate) continue;
            var active = Math.Abs(rate - _currentRate) < 0.001f;
            SetMenuText(b, active ? $"✓  {FormatRate(rate)}" : FormatRate(rate), active);
        }
        SpeedText.Text = FormatRate(_currentRate);
    }

    private async Task SetRateAsync(float rate)
    {
        var player = _player;
        if (player == null) return;

        if (Math.Abs(_currentRate - rate) < 0.001f)
        {
            ShowToast($"当前已是 {FormatRate(rate)}");
            return;
        }

        var time = SafeTime();

        try
        {
            await Task.Run(() =>
            {
                try { player.SetRate(rate); } catch { }
            });
        }
        catch { }

        _currentRate = rate;
        UpdateSpeedMenuState();

        // SetRate 会重置时钟基准，重新定位回原时间，否则会跳秒
        if (time > 0) RequestSeek(time);

        ShowToast($"播放速度 {FormatRate(rate)}");
        PingActivity();
    }

    // ---------- 音量 ----------

    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        var player = _player;
        if (player == null) return;

        _muted = !_muted;
        try { player.Mute = _muted; } catch { }

        UpdateVolumeGlyph();
        ShowToast(_muted ? "已静音" : $"音量 {_volume}%");
        PingActivity();
        ScheduleSavePreferences();
    }

    private void StepVolume(int delta)
    {
        var player = _player;
        if (player == null) return;

        ApplyVolume(_volume + delta);
        ShowToast(_muted || _volume == 0 ? "已静音" : $"音量 {_volume}%");
        PingActivity();
    }

    private void ApplyVolume(int value)
    {
        _volume = Math.Clamp(value, 0, 100);

        _syncingVolume = true;
        try
        {
            if (Math.Abs(VolumeSlider.Value - _volume) > 0.5) VolumeSlider.Value = _volume;
        }
        finally
        {
            _syncingVolume = false;
        }

        // 拖音量条 / 按方向键时，「有声音」就自动取消静音
        if (_volume > 0 && _muted) _muted = false;

        ApplyVolumeToPlayer();
        UpdateVolumeGlyph();
        ScheduleSavePreferences();
    }

    private void ApplyVolumeToPlayer()
    {
        var player = _player;
        if (player == null) return;
        try
        {
            player.Volume = _volume;
            player.Mute = _muted;
        }
        catch { }
    }

    private void UpdateVolumeGlyph()
    {
        VolumeGlyph.Text = _muted || _volume == 0 ? "\uE74F" : "\uE767";
        VolumeButton.ToolTip = _muted ? "取消静音（M）" : "静音（M）";
    }

    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolume) return;
        ApplyVolume((int)Math.Round(e.NewValue));
    }

    // ---------- 播放模式 ----------

    private void OnModeButtonClick(object sender, RoutedEventArgs e)
    {
        PingActivity();
        ModePopup.IsOpen = !ModePopup.IsOpen;
    }

    private void BuildModeMenu()
    {
        // 顺序固定：最保守的「播完暂停」排第一，后面才是各种自动继续
        foreach (var mode in new[]
                 {
                     VideoPlaybackMode.PauseAtEnd,
                     VideoPlaybackMode.LoopList,
                     VideoPlaybackMode.LoopSingle,
                     VideoPlaybackMode.Random,
                 })
        {
            // 两行：主标题 + 一句说明。
            // 只写「列表循环 / 单曲循环」还是会有人分不清，干脆在菜单里直接解释清楚。
            var title = new TextBlock { FontSize = 13 };
            var hint = new TextBlock
            {
                FontSize = 10.5,
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 238,
                Text = mode.ToHint(),
            };
            var panel = new StackPanel();
            panel.Children.Add(title);
            panel.Children.Add(hint);

            var button = new Button
            {
                Style = (Style)FindResource("PopupItem"),
                Tag = mode,
                Content = panel,
            };
            button.Click += (s, _) =>
            {
                ModePopup.IsOpen = false;
                if (s is Button b && b.Tag is VideoPlaybackMode m) SetPlayMode(m);
            };
            ModeList.Children.Add(button);
        }
    }

    private void SetPlayMode(VideoPlaybackMode mode)
    {
        _playMode = mode;
        UpdateModeUi();
        ShowToast($"播放结束后：{mode.ToDisplay()}");
        PingActivity();
        // 单击切换，立即落库（不要走防抖：用户切完常常直接关窗）
        ScheduleSavePreferences(0);
    }

    private void UpdateModeUi()
    {
        ModeText.Text = _playMode.ToDisplay();
        foreach (var child in ModeList.Children)
        {
            if (child is not Button b || b.Tag is not VideoPlaybackMode m) continue;
            if (b.Content is not StackPanel panel || panel.Children.Count == 0) continue;
            if (panel.Children[0] is not TextBlock title) continue;

            var active = m == _playMode;
            title.Text = (active ? "✓  " : "     ") + m.ToDisplay();
            title.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            title.Foreground = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xB4, 0xFF))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));

            // 说明文字始终是暗白：隐式 TextBlock 样式会覆盖继承色，必须显式给
            if (panel.Children[1] is TextBlock hint)
                hint.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(active ? (byte)0xB4 : (byte)0x8A, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>
    /// 弹出菜单项的文字必须显式上色：主题里有隐式 TextBlock 样式（Foreground 绑主题色），
    /// 会覆盖从 Button 继承下来的颜色 —— 深色菜单上会出现「浅色主题下黑字看不见」。
    /// </summary>
    private static void SetMenuText(Button button, string text, bool active)
    {
        if (button.Content is not TextBlock block)
        {
            block = new TextBlock();
            button.Content = block;
        }

        block.Text = text;
        block.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        block.Foreground = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xB4, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
    }

    // ---------- 画面点击 / 滚轮 ----------

    /// <summary>画面点击 → 播放/暂停。快速连点会被合并成一次（见 <see cref="OnOverlayMouseDown"/>）。</summary>
    private long _lastSurfaceClickMs;

    /// <summary>两次画面点击之间的合并窗口（毫秒）。</summary>
    private const int SurfaceClickCoalesceMs = 320;

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在控制栏 / 中央面板上时不要触发播放暂停
        if (IsInsideChrome(e.OriginalSource as DependencyObject)) return;
        if (e.ChangedButton != MouseButton.Left) return;

        // 画面双击不再切换全屏：系统的 500ms 双击判定会把「快速点两下播放/暂停」误当成双击，
        // 导致画面突然放大。全屏统一走 F 键 / Esc / 控制栏按钮。
        //
        // 去掉双击分支后，一次双击会连续触发两次 MouseLeftButtonDown（ClickCount 1 再 2），
        // 暂停后立刻又播放，看起来就是「点了没反应」。这里把窗口内的连点合并成一次。
        var now = Environment.TickCount64;
        if (now - _lastSurfaceClickMs < SurfaceClickCoalesceMs)
        {
            _lastSurfaceClickMs = now;
            e.Handled = true;
            return;
        }
        _lastSurfaceClickMs = now;

        TogglePlayPause();
        e.Handled = true;
    }

    private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsInsideChrome(e.OriginalSource as DependencyObject)) return;
        StepVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private bool IsInsideChrome(DependencyObject? node)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ControlBar) || ReferenceEquals(node, CenterPanel)
                || ReferenceEquals(node, TopBar)) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ===================================================================
    //  全屏 / 视频表面同步
    // ===================================================================

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen() => SetFullScreen(!_isFullScreen);

    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen) return;

        // 必须先改标志位：WindowState 一变就会触发 WM_GETMINMAXINFO，
        // 钩子要靠这个标志决定「最大化 = 铺满整块显示器（含任务栏）」。
        _isFullScreen = on;

        try
        {
            if (on)
            {
                _prevState = WindowState;
                _prevStyle = WindowStyle;
                _prevResize = ResizeMode;

                // 先去掉边框再最大化，避免出现「最大化却还带标题栏」的一帧
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = _prevStyle;
                ResizeMode = _prevResize;
                WindowState = _prevState;
            }
        }
        catch { /* 窗口正在关闭 */ }

        FullScreenGlyph.Text = on ? "\uE73F" : "\uE740";   // BackToWindow / FullScreen
        FullScreenButton.ToolTip = on ? "退出全屏（F / Esc）" : "全屏（F）";

        ScheduleSurfaceResync();
        ShowChrome();
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleSurfaceResync();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged || sizeInfo.HeightChanged) ScheduleSurfaceResync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    /// <summary>
    /// 全屏必须铺满「整块显示器」而不是「工作区」——
    /// WPF 默认最大化只到工作区，底部会露出任务栏（看起来就是没真正全屏）。
    /// 处理 WM_GETMINMAXINFO 并填入 rcMonitor（不是 rcWork）即可。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO && _isFullScreen)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMaxPosition.X = info.rcMonitor.Left;
                mmi.ptMaxPosition.Y = info.rcMonitor.Top;
                mmi.ptMaxSize.X = info.rcMonitor.Right - info.rcMonitor.Left;
                mmi.ptMaxSize.Y = info.rcMonitor.Bottom - info.rcMonitor.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ScheduleSurfaceResync()
    {
        // 立刻来一次（轻量，不动 Scale）
        Dispatcher.BeginInvoke(new Action(() => ResyncVideoSurface(resetScale: false)),
            DispatcherPriority.ContextIdle);

        // 尺寸稳定 320ms 后再来一次重的（含 Scale 重置）：拖拽改窗口时不会反复打断 vout
        _resyncCts?.Cancel();
        _resyncCts = new CancellationTokenSource();
        _ = ResyncSurfaceDelayedAsync(_resyncCts.Token);
    }

    private async Task ResyncSurfaceDelayedAsync(CancellationToken token)
    {
        try
        {
            // 最大化 / 还原有系统动画，结束后再对一次才能保证最终尺寸正确
            await Task.Delay(320, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => ResyncVideoSurface(resetScale: true));
    }

    private async Task ApplyScaleFixLaterAsync()
    {
        try { await Task.Delay(220); } catch { return; }
        await Dispatcher.InvokeAsync(() => ResyncVideoSurface(resetScale: true));
    }

    /// <summary>
    /// 修复「全屏 / 高 DPI 下画面留黑边、或者卡在原尺寸贴左上角」：
    /// 1) 让 LibVLC 用 Scale=0（自适应）重新算一次输出尺寸；
    /// 2) 抖 1px 边距再复原，强制 HwndHost 收到新的窗口矩形（它会转告 LibVLC 重新设置 vout）。
    /// </summary>
    private void ResyncVideoSurface(bool resetScale)
    {
        try
        {
            if (resetScale) ApplyScaleFix();

            VideoView.Margin = new Thickness(0, 0, 0, 1);
            VideoView.InvalidateMeasure();
            VideoView.InvalidateArrange();
            UpdateLayout();
            VideoView.Margin = new Thickness(0);
            UpdateLayout();
        }
        catch
        {
            // 窗口正在关闭时控件可能已释放
        }
    }

    private void ApplyScaleFix()
    {
        var player = _player;
        if (player == null) return;

        try
        {
            // 0 = 自动适应（铺满窗口，保持原始宽高比）
            if (player.Scale != 0) player.Scale = 0;
        }
        catch { }

        try
        {
            // 只在真的被设过值时才清掉，避免每次都触发 vout 重建
            if (!string.IsNullOrEmpty(player.AspectRatio)) player.AspectRatio = null;
        }
        catch { }
    }

    // ===================================================================
    //  控制栏显隐（鼠标静止 3 秒自动隐藏）
    // ===================================================================

    private void PingActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        ShowChrome();
    }

    private void ShowChrome()
    {
        _lastActivityUtc = DateTime.UtcNow;
        if (_chromeVisible) return;

        _chromeVisible = true;
        ChromeRoot.Visibility = Visibility.Visible;
        ChromeRoot.IsHitTestVisible = true;
        FadeChrome(1.0, 140);
    }

    private void HideChrome()
    {
        if (!_chromeVisible) return;

        _chromeVisible = false;
        ChromeRoot.IsHitTestVisible = false;
        FadeChrome(0.0, 220, collapseOnEnd: true);
    }

    private void FadeChrome(double to, int milliseconds, bool collapseOnEnd = false)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        if (collapseOnEnd)
        {
            animation.Completed += (_, _) =>
            {
                if (!_chromeVisible) ChromeRoot.Visibility = Visibility.Collapsed;
            };
        }

        ChromeRoot.BeginAnimation(OpacityProperty, animation);
        ChromeRoot.Opacity = to;   // FillBehavior.Stop 后要落到最终值
    }

    /// <summary>
    /// 视频窗口是 HwndHost，鼠标在画面上时 WPF 收不到 MouseMove，
    /// 所以「鼠标是否在动」用定时轮询真实光标位置来判断。
    /// </summary>
    private void PollMouseActivity()
    {
        if (!GetCursorPos(out var p)) return;

        Point local;
        try
        {
            local = PointFromScreen(new Point(p.X, p.Y));
        }
        catch
        {
            return;
        }

        var inside = local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
        if (!inside) return;

        var moved = Math.Abs(local.X - _lastCursor.X) > 2 || Math.Abs(local.Y - _lastCursor.Y) > 2;
        if (moved)
        {
            _lastCursor = local;
            PingActivity();
            return;
        }

        // 鼠标停在底部热区：保持控制栏可见
        if (local.Y >= ActualHeight - BottomHotZone)
        {
            PingActivity();
            return;
        }

        if (!_chromeVisible) return;
        if (_popupOpen || _userSeeking || _seekInFlight) return;
        if (_player == null || !IsPlayerPlaying()) return;       // 暂停时保持显示
        if (CenterPanel.Visibility == Visibility.Visible) return;

        if ((DateTime.UtcNow - _lastActivityUtc).TotalSeconds < IdleSeconds) return;
        HideChrome();
    }

    private bool IsPlayerPlaying()
    {
        try { return _player?.IsPlaying == true; } catch { return false; }
    }

    // ===================================================================
    //  提示（toast / 中央面板）
    // ===================================================================

    private void ShowToast(string text)
    {
        ActionToastText.Text = text;
        ActionToast.BeginAnimation(OpacityProperty, null);
        ActionToast.Opacity = 1;

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        ActionToast.BeginAnimation(OpacityProperty, animation);
        ActionToast.Opacity = 0;
    }

    private void ShowCenterMessage(string title, string detail, CenterAction action)
    {
        _centerAction = action;
        CenterTitle.Text = title;
        CenterDetail.Text = detail;

        CenterPrimaryButton.Content = action switch
        {
            CenterAction.Retry => "重试",
            CenterAction.Replay => "重新播放",
            _ => "知道了",
        };

        CenterSecondaryButton.Visibility = _playlist.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CenterPanel.Visibility = Visibility.Visible;
        ShowChrome();
    }

    private void HideCenterMessage()
    {
        _centerAction = CenterAction.None;
        CenterPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCenterPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_centerAction)
        {
            case CenterAction.Retry:
            case CenterAction.Replay:
                HideCenterMessage();
                PlayCurrent();
                break;
            default:
                HideCenterMessage();
                break;
        }
    }

    private void OnCenterSecondaryClick(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        if (_playlist.Count == 0) return;
        HideCenterMessage();
        // 手动「下一个」永远按列表顺序走（到末尾回到第一个），与自动播放模式无关
        var next = PlaybackQueue.Next(VideoPlaybackMode.LoopList, _playlistIndex, _playlist.Count, 0);
        if (next < 0)
        {
            ShowToast("没有下一个视频了");
            return;
        }
        _playlistIndex = next;
        PlayCurrent();
    }

    // ===================================================================
    //  快捷键（线程级 PreProcessInput：主窗口 + 前景叠加窗口都能覆盖）
    // ===================================================================

    private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not KeyEventArgs ke) return;
        if (ke.RoutedEvent != Keyboard.PreviewKeyDownEvent) return;

        // 注意：PreProcessInput 阶段 KeyEventArgs.Source 恒为 null，
        // 不能用 Source 判断归属，必须自己找「这次按键到底落在哪个窗口」。
        if (!IsKeyboardTarget()) return;

        if (HandleShortcut(ke)) ke.Handled = true;
    }

    /// <summary>
    /// 判断当前这次按键是否属于本播放器（主窗口 / LibVLCSharp 叠加层 / 叠加层弹出的菜单）。
    ///
    /// 两道判断：
    /// 1) 优先看 WPF 焦点元素所在的窗口（元素有焦点时最准）；
    /// 2) 焦点在 HwndHost（视频原生窗口）上时 WPF 拿不到焦点元素，退化为
    ///    「前台窗口 == 本窗口，或前台窗口的 Owner 链上有本窗口」。
    /// </summary>
    private bool IsKeyboardTarget()
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var window = Window.GetWindow(focused);
            if (window != null)
                return ReferenceEquals(window, this) || ReferenceEquals(window.Owner, this);
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        var mine = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (mine == IntPtr.Zero) return false;

        var current = foreground;
        for (var i = 0; i < 3 && current != IntPtr.Zero; i++)
        {
            if (current == mine) return true;
            current = GetWindow(current, GW_OWNER);
        }

        return false;
    }

    private bool HandleShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Ctrl 组合键留给系统 / 其它功能，不抢
        if ((modifiers & ModifierKeys.Control) != 0) return false;

        if (key == Key.Enter && modifiers == ModifierKeys.Alt)
        {
            ToggleFullScreen();
            return true;
        }

        switch (key)
        {
            case Key.Space:
                TogglePlayPause();
                return true;

            case Key.Left:
                SeekRelative(-StepMs);
                return true;

            case Key.Right:
                SeekRelative(StepMs);
                return true;

            case Key.Up:
                StepVolume(5);
                return true;

            case Key.Down:
                StepVolume(-5);
                return true;

            case Key.M:
                ToggleMute();
                return true;

            case Key.F:
            case Key.F11:
                ToggleFullScreen();
                return true;

            case Key.Escape:
                if (_isFullScreen) SetFullScreen(false);
                else Close();
                return true;

            case Key.PageDown:
                PlayNext();
                return true;

            case Key.PageUp:
                PlayPrevious();
                return true;
        }

        return false;
    }

    private void PlayPrevious()
    {
        if (_playlist.Count == 0) return;
        HideCenterMessage();
        var prev = _playlistIndex - 1;
        if (prev < 0) prev = _playlist.Count - 1;
        if (prev == _playlistIndex) return;
        _playlistIndex = prev;
        PlayCurrent();
    }

    // ===================================================================
    //  设置持久化
    // ===================================================================

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;

        try
        {
            var repo = App.Services.GetService(typeof(IMediaRepository)) as IMediaRepository;
            if (repo == null) return;

            var modeValue = await repo.GetSettingAsync(VideoPlaybackModes.SettingKey);
            // 只有「记住播放模式」打开时才沿用上次的选择；关掉就固定从播完暂停开始
            var rememberValue = await repo.GetSettingAsync(RememberModeSettingKey);
            _rememberMode = rememberValue != "0";
            _playMode = _rememberMode ? VideoPlaybackModes.Parse(modeValue) : VideoPlaybackMode.PauseAtEnd;

            var volumeValue = await repo.GetSettingAsync("VideoVolume");
            if (int.TryParse(volumeValue, out var volume)) _volume = Math.Clamp(volume, 0, 100);

            var mutedValue = await repo.GetSettingAsync("VideoMuted");
            _muted = mutedValue == "1";

            UpdateModeUi();
            ApplyVolume(_volume);
            UpdateVolumeGlyph();
        }
        catch
        {
            // 设置读失败不影响播放
        }
    }

    /// <summary>
    /// 防抖保存偏好。播放模式是单击操作，传 0 立即落库；
    /// 音量拖动才需要 400ms 防抖，否则拖一次滑块会写几十次数据库。
    /// </summary>
    private void ScheduleSavePreferences(int delayMs = 400)
    {
        _prefSaveCts?.Cancel();
        _prefSaveCts = new CancellationTokenSource();
        var token = _prefSaveCts.Token;
        _ = SavePreferencesAsync(token, delayMs);
    }

    private async Task SavePreferencesAsync(CancellationToken token, int delayMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, token);
            if (token.IsCancellationRequested) return;

            var repo = App.Services.GetService(typeof(IMediaRepository)) as IMediaRepository;
            if (repo == null) return;

            // 「记住播放模式」关掉后，每次打开都回到默认的播完暂停（音量/静音仍然记住）
            if (_rememberMode)
                await repo.SetSettingAsync(VideoPlaybackModes.SettingKey, _playMode.ToStorageValue());
            await repo.SetSettingAsync("VideoVolume", _volume.ToString());
            await repo.SetSettingAsync("VideoMuted", _muted ? "1" : "0");
        }
        catch (TaskCanceledException) { }
        catch
        {
            // 写设置失败忽略
        }
    }

    /// <summary>
    /// 关窗前把还没落库的偏好立刻写一次。
    /// 之前 OnClosed 只做 Cancel，用户「切完模式马上关窗」（400ms 内）的改动会直接丢失。
    /// </summary>
    private void FlushPreferences()
    {
        try
        {
            var repo = App.Services.GetService(typeof(IMediaRepository)) as IMediaRepository;
            if (repo == null) return;

            var mode = _playMode;
            var volume = _volume;
            var muted = _muted;
            _ = Task.Run(async () =>
            {
                try
                {
                    await repo.SetSettingAsync(VideoPlaybackModes.SettingKey, mode.ToStorageValue());
                    await repo.SetSettingAsync("VideoVolume", volume.ToString());
                    await repo.SetSettingAsync("VideoMuted", muted ? "1" : "0");
                }
                catch
                {
                    // 关窗兜底写入失败无副作用
                }
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ===================================================================
    //  释放
    // ===================================================================

    protected override void OnClosed(EventArgs e)
    {
        InputManager.Current.PreProcessInput -= OnPreProcessInput;

        _timer.Stop();
        _toastTimer.Stop();
        _seekCoalescer.Reset();
        _resyncCts?.Cancel();
        // 先把还没落库的偏好立刻写一次，再取消防抖任务：
        // 否则「切完播放模式 / 拖完音量马上关窗」的改动会全部丢失。
        FlushPreferences();
        _prefSaveCts?.Cancel();

        var player = _player;
        _player = null;

        try { VideoView.MediaPlayer = null; } catch { }

        // 必须先 Stop 再 Dispose，最后释放 media 和 libVLC，确保释放视频文件句柄
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
        try { _media?.Dispose(); } catch { }
        foreach (var media in _retiredMedia)
        {
            try { media.Dispose(); } catch { }
        }
        _retiredMedia.Clear();
        try { _libVlc?.Dispose(); } catch { }

        _media = null;
        _libVlc = null;

        base.OnClosed(e);
    }

    // ===================================================================
    //  Win32：光标位置 / 鼠标左键状态
    // ===================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int VK_LBUTTON = 0x01;
    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
}
