namespace MediaManager.Core.Input;

/// <summary>
/// 全局快捷键的修饰键位。
/// 数值刻意与 Win32 的 <c>MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN</c> 保持一致，
/// 界面层可以直接强转后传给 <c>RegisterHotKey</c>，少一层映射。
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 一条全局快捷键：修饰键 + 主键。
///
/// 存库格式固定为规范串（<c>Ctrl+Alt+M</c>），主键使用 WPF <c>Key</c> 的枚举名
/// （<c>M</c> / <c>F1</c> / <c>OemTilde</c>），界面层只做 <c>Key ↔ 名字</c> 的转换，
/// 解析、规范化、显示文案全部收敛到这里，方便单测，也避免各处各写一套。
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>未设置（空快捷键）。</summary>
    public static HotkeyGesture None => new(HotkeyModifiers.None, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

    /// <summary>存库 / 比较用的规范串，例如 <c>Ctrl+Shift+F1</c>；未设置时是空串。</summary>
    public string ToStorageValue()
        => IsEmpty ? string.Empty : Format(Modifiers, Key);

    /// <summary>界面胶囊上的显示文本，例如 <c>Ctrl + Shift + F1</c>；未设置时是「未设置」。</summary>
    public string ToDisplayString()
    {
        if (IsEmpty) return "未设置";

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(DisplayKey(Key));
        return string.Join(" + ", parts);
    }

    public override string ToString() => ToDisplayString();

    /// <summary>规范串（无空格），修饰键顺序固定 Ctrl→Alt→Shift→Win，保证比较稳定。</summary>
    public static string Format(HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(NormalizeKeyName(key) ?? key);
        return string.Join("+", parts);
    }

    /// <summary>
    /// 解析存库串。宁可失败也不猜：任何一段修饰键拼错、主键不认识，整条作废。
    /// 没有修饰键时只允许 F1~F24 —— 单按字母/数字会成为系统级劫持，连正常打字都受影响。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        var keyName = NormalizeKeyName(tokens[^1]);
        if (keyName == null) return false;

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "windows":
                case "meta":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == HotkeyModifiers.None && !IsFunctionKey(keyName)) return false;

        gesture = new HotkeyGesture(modifiers, keyName);
        return true;
    }

    /// <summary>F1~F24（单独按下也允许，和截图工具的 F1 习惯一致）。</summary>
    public static bool IsFunctionKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return false;
        var name = keyName.Trim().ToUpperInvariant();
        if (name.Length < 2 || name[0] != 'F') return false;
        return int.TryParse(name[1..], out var n) && n >= 1 && n <= 24;
    }

    /// <summary>把用户输入的按键名规范化成 WPF <c>Key</c> 的枚举名；不认识返回 null。</summary>
    public static string? NormalizeKeyName(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return null;
        var raw = keyName.Trim();
        return KnownKeyNames.TryGetValue(raw, out var canonical) ? canonical : null;
    }

    /// <summary>键名 → 胶囊上的短文本（方向键、标点、数字键都换成更好认的写法）。</summary>
    public static string DisplayKey(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return string.Empty;
        var name = NormalizeKeyName(keyName) ?? keyName.Trim();
        return name switch
        {
            "Back" => "Backspace",
            "PageUp" => "PgUp",
            "PageDown" => "PgDn",
            "PrintScreen" => "PrtSc",
            "Scroll" => "ScrollLock",
            "OemTilde" => "`",
            "OemPlus" => "=",
            "OemMinus" => "-",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemPipe" => "\\",
            "OemBackslash" => "\\",
            "Up" => "↑",
            "Down" => "↓",
            "Left" => "←",
            "Right" => "→",
            _ when name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) => name[1..],
            _ when name.StartsWith("NumPad", StringComparison.Ordinal) => "Num" + name["NumPad".Length..],
            _ => name,
        };
    }

    // ------------------------------------------------------------------
    //  合法键名表：键为小写别名，值为 WPF Key 枚举的规范名
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, string> KnownKeyNames = BuildKnownKeys();

    private static Dictionary<string, string> BuildKnownKeys()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string canonical, params string[] aliases)
        {
            map[canonical] = canonical;
            foreach (var alias in aliases) map[alias] = canonical;
        }

        // 字母
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ") Add(c.ToString());

        // 主键盘数字：WPF 里叫 D0~D9，界面上显示 0~9
        for (var i = 0; i <= 9; i++)
        {
            Add($"D{i}", i.ToString());
        }

        // 功能键
        for (var i = 1; i <= 24; i++) Add($"F{i}");

        // 常用控制键
        Add("Insert", "ins");
        Add("Delete", "del");
        Add("Home");
        Add("End");
        Add("PageUp", "pgup");
        Add("PageDown", "pgdn");
        Add("PrintScreen", "prtsc", "print");
        Add("Scroll");
        Add("Pause", "break");
        Add("Space", "spacebar");
        Add("Tab");
        Add("Enter", "return");
        Add("Back", "backspace");
        Add("Left");
        Add("Right");
        Add("Up");
        Add("Down");

        // 小键盘
        for (var i = 0; i <= 9; i++) Add($"NumPad{i}");
        Add("Multiply");
        Add("Add");
        Add("Subtract");
        Add("Decimal");
        Add("Divide");

        // 标点（WPF 用 Oem* 命名，别指望用户知道，界面上会换成符号）
        Add("OemSemicolon");
        Add("OemPlus");
        Add("OemComma");
        Add("OemMinus");
        Add("OemPeriod");
        Add("OemQuestion");
        Add("OemTilde");
        Add("OemOpenBrackets");
        Add("OemPipe");
        Add("OemCloseBrackets");
        Add("OemQuotes");
        Add("OemBackslash");

        // 媒体键（键盘上那几个播放/音量键）
        Add("MediaNextTrack");
        Add("MediaPreviousTrack");
        Add("MediaStop");
        Add("MediaPlayPause");
        Add("VolumeUp");
        Add("VolumeDown");
        Add("VolumeMute");

        return map;
    }
}
