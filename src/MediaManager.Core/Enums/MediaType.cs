namespace MediaManager.Core.Enums;

/// <summary>媒体类型。</summary>
public enum MediaType
{
    Image = 0,
    Video = 1,

    /// <summary>音频（只读浏览）。</summary>
    Audio = 2,

    /// <summary>文档/文本（只读预览）。</summary>
    Text = 3,

    /// <summary>压缩包（只读查看条目）。</summary>
    Archive = 4,
}
