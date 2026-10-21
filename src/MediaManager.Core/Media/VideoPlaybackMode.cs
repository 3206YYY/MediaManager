namespace MediaManager.Core.Media;

/// <summary>
/// 视频播完后的行为（播放器左下角可切换，持久化在 AppSetting）。
/// <para>
/// V1.2.5 起把原来含混的「连续播放」拆成两个明确的概念：
/// <list type="bullet">
/// <item><see cref="LoopList"/> 列表循环 —— 接着播列表里的下一个，播到最后一个回到第一个</item>
/// <item><see cref="LoopSingle"/> 单曲循环 —— 当前这个视频从头再播一遍</item>
/// </list>
/// 旧版本存的是 <c>"sequential"</c>，解析时自动迁移到「列表循环」。
/// </para>
/// </summary>
public enum VideoPlaybackMode
{
    /// <summary>停在最后一帧，等用户手动操作（默认）。</summary>
    PauseAtEnd = 0,

    /// <summary>按列表顺序播下一个，到末尾回到第一个。</summary>
    LoopList = 1,

    /// <summary>当前视频循环重复播放。</summary>
    LoopSingle = 2,

    /// <summary>随机播放列表里的另一个视频。</summary>
    Random = 3,
}

public static class VideoPlaybackModes
{
    public const string SettingKey = "VideoPlaybackMode";

    /// <summary>写库用的稳定字符串（不要用枚举 ToString 存库，改个类名就全废了）。</summary>
    public static string ToStorageValue(this VideoPlaybackMode mode) => mode switch
    {
        VideoPlaybackMode.LoopList => "list",
        VideoPlaybackMode.LoopSingle => "single",
        VideoPlaybackMode.Random => "random",
        _ => "pause",
    };

    /// <summary>默认行为：播完就停，等用户自己决定。</summary>
    public const VideoPlaybackMode Default = VideoPlaybackMode.PauseAtEnd;

    /// <summary>
    /// 解析库里的值；无法识别（或从未设置）时回落到「播完暂停」。
    /// 兼容旧版本的 <c>"sequential" / "next" / "auto"</c>（= 现在的列表循环）。
    /// </summary>
    public static VideoPlaybackMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "list" or "looplist" or "sequential" or "next" or "auto" => VideoPlaybackMode.LoopList,
        "single" or "loopsingle" or "repeat" or "repeatone" => VideoPlaybackMode.LoopSingle,
        "random" or "shuffle" => VideoPlaybackMode.Random,
        "pause" or "pauseatend" or "stop" => VideoPlaybackMode.PauseAtEnd,
        _ => Default,
    };

    /// <summary>界面显示名（短，用于按钮/菜单项）。</summary>
    public static string ToDisplay(this VideoPlaybackMode mode) => mode switch
    {
        VideoPlaybackMode.LoopList => "列表循环",
        VideoPlaybackMode.LoopSingle => "单曲循环",
        VideoPlaybackMode.Random => "随机播放",
        _ => "播完暂停",
    };

    /// <summary>菜单里的补充说明（第二行小字），彻底说清每种模式的意思。</summary>
    public static string ToHint(this VideoPlaybackMode mode) => mode switch
    {
        VideoPlaybackMode.LoopList => "按顺序播下一个，末尾接回第一个",
        VideoPlaybackMode.LoopSingle => "当前视频反复播放，直到你手动切换",
        VideoPlaybackMode.Random => "每次随机挑列表里的另一个视频",
        _ => "停在最后一帧，等你决定下一步",
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

        // 播完暂停：永远停在结束态
        if (mode == VideoPlaybackMode.PauseAtEnd) return -1;

        var now = current >= 0 && current < count ? current : -1;

        // 单曲循环：不管列表里有多少个，都回到自己
        if (mode == VideoPlaybackMode.LoopSingle) return now >= 0 ? now : 0;

        // 列表循环 / 随机：列表里只有一个视频时，「下一个」只能还是它自己。
        // 旧实现这里返回 -1，导致用户明明选了模式却仍然停在结束画面（V1.2.4 的 bug）。
        if (count == 1) return 0;

        if (mode == VideoPlaybackMode.Random)
        {
            // 从「除当前之外」的 count-1 个里挑，避免随机到同一集
            var pool = count - 1;
            var pick = (int)((uint)randomSeed % (uint)pool);
            if (now >= 0 && pick >= now) pick++;
            return pick;
        }

        // 列表循环：到末尾回到第一个
        return now + 1 >= count ? 0 : now + 1;
    }
}
