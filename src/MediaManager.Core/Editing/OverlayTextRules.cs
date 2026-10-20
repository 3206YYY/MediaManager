namespace MediaManager.Core.Editing;

/// <summary>
/// 文字叠加层的有效性判定（纯函数，方便单测）。
///
/// 保存时必须只绘制「真正有内容」的文字：
/// 空字符串、只有空白、坐标跑到画布外的文本框一律跳过，
/// 否则一个被清空的文本框仍会参与布局，坐标一错就会在导出图上留下错位的残影。
/// </summary>
public static class OverlayTextRules
{
    /// <summary>文字内容是否有效（非空、非纯空白）。</summary>
    public static bool HasContent(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>坐标是否是有效的归一化坐标（落在画布内）。</summary>
    public static bool HasValidPosition(double x, double y)
        => double.IsFinite(x) && double.IsFinite(y)
           && x >= 0 && x < 1
           && y >= 0 && y < 1;

    /// <summary>是否应该被绘制 / 导出。</summary>
    public static bool IsRenderable(string? text, double x, double y)
        => HasContent(text) && HasValidPosition(x, y);
}
