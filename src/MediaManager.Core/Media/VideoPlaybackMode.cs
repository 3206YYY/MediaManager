namespace MediaManager.Core.Media;

/// <summary>视频播完后的行为（左下角可切换，持久化在 AppSetting）。</summary>
public enum VideoPlaybackMode
{
    /// <summary>自动播放列表里的下一个视频。</summary>
    Sequential = 0,

    /// <summary>停在最后一帧，等用户手动操作。</summary>
    PauseAtEnd = 1,

    /// <summary>随机播放列表里的另一个视频。</summary>
    Random = 2,
}

public static class VideoPlaybackModes
{
    public const string SettingKey = "VideoPlaybackMode";

    /// <summary>写库用的稳定字符串（不要用枚举 ToString 存库，改个类名就全废了）。</summary>
    public static string ToStorageValue(this VideoPlaybackMode mode) => mode switch
    {
        VideoPlaybackMode.PauseAtEnd => "pause",
        VideoPlaybackMode.Random => "random",
        _ => "sequential",
    };

    /// <summary>默认行为：播完就停，等用户自己决定。</summary>
    public const VideoPlaybackMode Default = VideoPlaybackMode.PauseAtEnd;

    /// <summary>解析库里的值；无法识别（或从未设置）时回落到「播完暂停」。</summary>
    public static VideoPlaybackMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sequential" or "next" or "auto" => VideoPlaybackMode.Sequential,
        "random" or "shuffle" => VideoPlaybackMode.Random,
        "pause" or "pauseatend" or "stop" => VideoPlaybackMode.PauseAtEnd,
        _ => Default,
    };

    /// <summary>界面显示名。</summary>
    public static string ToDisplay(this VideoPlaybackMode mode) => mode switch
    {
        VideoPlaybackMode.PauseAtEnd => "播完暂停",
        VideoPlaybackMode.Random => "随机播放",
        _ => "连续播放",
    };
}

/// <summary>
/// 播放队列的「下一首」选择规则（纯逻辑，可单测）。
/// </summary>
public static class PlaybackQueue
{
    /// <summary>
    /// 计算下一首的下标。
    /// </summary>
    /// <param name="mode">播放模式。</param>
    /// <param name="current">当前下标（可以是 -1，表示还没播）。</param>
    /// <param name="count">列表长度。</param>
    /// <param name="randomSeed">随机源（真随机由调用方给，测试给固定值）。</param>
    /// <returns>下一个要播的下标；<c>-1</c> 表示没有下一首（应停在结束态）。</returns>
    public static int Next(VideoPlaybackMode mode, int current, int count, int randomSeed)
    {
        if (count <= 0) return -1;

        // 只有一个视频时，「下一个」还是自己 —— 直接判定为结束，
        // 否则会出现「播完立刻从头再来」的死循环。
        if (count == 1) return -1;

        if (mode == VideoPlaybackMode.PauseAtEnd) return -1;

        var now = current < 0 || current >= count ? -1 : current;

        if (mode == VideoPlaybackMode.Random)
        {
            // 从「除当前之外」的 count-1 个里挑，避免随机到同一集
            var pool = count - 1;
            var pick = (int)((uint)randomSeed % (uint)pool);
            if (now >= 0 && pick >= now) pick++;
            return pick;
        }

        // 连续播放：到末尾回到第一个，形成循环
        return now + 1 >= count ? 0 : now + 1;
    }
}
