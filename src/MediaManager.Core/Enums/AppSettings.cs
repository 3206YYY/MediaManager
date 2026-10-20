namespace MediaManager.Core.Enums;

/// <summary>主题。</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>应用语言。</summary>
public enum AppLanguage
{
    Chinese,
    English,
}

/// <summary>删除行为。</summary>
public enum DeleteBehavior
{
    /// <summary>删除到回收站（默认）。</summary>
    RecycleBin,
    /// <summary>永久删除。</summary>
    Permanent,
    /// <summary>每次询问。</summary>
    Ask,
}
