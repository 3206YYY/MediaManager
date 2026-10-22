namespace MediaManager.App.Services;

/// <summary>
/// 全局快捷键的动作清单：动作 id、界面文案、默认组合都放一处，
/// 设置页、注册服务、帮助文档都读同一份，避免三处各写一遍还对不上。
/// </summary>
public static class HotkeyActions
{
    public const string ShowWindow = "ShowWindow";
    public const string Discover = "Discover";
    public const string RandomMedia = "RandomMedia";
    public const string Settings = "Settings";

    /// <summary>存库键名。统一前缀，方便排查，也不会跟别的设置撞名。</summary>
    public static string SettingKey(string actionId) => "Hotkey." + actionId;

    public sealed record Definition(string Id, string Title, string Hint, string Default);

    /// <summary>
    /// 只给「最值得按一下」的一个动作配默认值，其余默认留空：
    /// 全局热键是从系统层面抢按键，不该在用户没同意的时候占一堆。
    ///
    /// 默认用三键 Ctrl+Alt+Shift+M 而不是更顺手的 Ctrl+Alt+M：实测 Ctrl+Alt+M / Ctrl+Alt+F
    /// 在装了开发工具或截图工具的机器上经常已被占用，三键组合几乎不会撞；
    /// 万一撞了，设置页会明确报「已被其它程序占用」，而不是静默失效。
    /// </summary>
    public static IReadOnlyList<Definition> All { get; } = new List<Definition>
    {
        new(ShowWindow,
            "显示 / 隐藏主窗口",
            "任何时候按一下就能把窗口叫出来；窗口已经在前台时再按一下收进托盘。",
            "Ctrl+Alt+Shift+M"),

        new(Discover,
            "打开「发现」页",
            "直接跳到随机推送那批素材，省得先在左边点一下。",
            ""),

        new(RandomMedia,
            "随机看一张（当前视图）",
            "在眼下这批素材里随机打开一张，翻老照片时很顺手。",
            ""),

        new(Settings,
            "打开设置",
            "改设置不用先回头找主界面。",
            ""),
    };
}
