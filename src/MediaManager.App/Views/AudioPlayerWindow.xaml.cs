using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaManager.App.Services;

namespace MediaManager.App.Views;

/// <summary>音频播放器：复用 LibVLC，只做播放/进度/倍速（不创建视频窗口）。</summary>
public partial class AudioPlayerWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private readonly DispatcherTimer _timer;
    private bool _isSeeking;
    private CancellationTokenSource? _seekCts;
    private CancellationTokenSource? _rateCts;
    private float _currentRate = 1.0f;

    public AudioPlayerWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTimerTick;
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
    }

    public void Play(string path)
    {
        TitleText.Text = System.IO.Path.GetFileName(path);
        LibVLCSharp.Shared.Core.Initialize();

        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        _media = new Media(_libVlc, path);
        _player.Play(_media);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_player == null || !_player.IsPlaying || _isSeeking) return;
        var length = _player.Length > 0 ? _player.Length : 0;
        var time = _player.Time > 0 ? _player.Time : 0;
        ProgressSlider.Maximum = length;
        ProgressSlider.Value = time;
        TimeText.Text = $"{FormatTime(time)} / {FormatTime(length)}";
    }

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    private void OnSpeedHalf(object sender, RoutedEventArgs e) => SetRate(0.5f);
    private void OnSpeedNormal(object sender, RoutedEventArgs e) => SetRate(1.0f);
    private void OnSpeedOneHalf(object sender, RoutedEventArgs e) => SetRate(1.5f);
    private void OnSpeedDouble(object sender, RoutedEventArgs e) => SetRate(2.0f);

    /// <summary>防抖切换倍速：记录当前时间 → 设置倍速 → Seek 回原时间，避免跳秒卡顿。</summary>
    private async void SetRate(float rate)
    {
        if (_player == null) return;
        var currentTime = _player.Time;
        _rateCts?.Cancel();
        _rateCts = new CancellationTokenSource();
        var token = _rateCts.Token;
        try
        {
            await Task.Delay(80, token);
            if (token.IsCancellationRequested || _player == null) return;
            if (Math.Abs(_currentRate - rate) < 0.001f) return;
            _player.SetRate(rate);
            _currentRate = rate;
            _player.Time = currentTime;
        }
        catch (TaskCanceledException) { }
    }

    private void OnSeekStart(object sender, DragStartedEventArgs e) => _isSeeking = true;

    private void OnSeekEnd(object sender, DragCompletedEventArgs e) => ScheduleSeek((long)ProgressSlider.Value);

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e) => ScheduleSeek((long)ProgressSlider.Value);

    /// <summary>防抖 Seek：合并快速点按/拖拽，只保留最后一次。</summary>
    private async void ScheduleSeek(long targetMs)
    {
        _seekCts?.Cancel();
        _seekCts = new CancellationTokenSource();
        var token = _seekCts.Token;
        try
        {
            await Task.Delay(120, token);
            if (token.IsCancellationRequested) return;
            if (_player != null) _player.Time = targetMs;
        }
        catch (TaskCanceledException)
        {
            return;
        }
        _isSeeking = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                OnPlayPause(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                if (_player != null) ScheduleSeek(Math.Max(0, _player.Time - 5000));
                break;
            case Key.Right:
                if (_player != null) ScheduleSeek(_player.Time + 5000);
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _seekCts?.Cancel();
        _rateCts?.Cancel();
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
        base.OnClosed(e);
    }
}
