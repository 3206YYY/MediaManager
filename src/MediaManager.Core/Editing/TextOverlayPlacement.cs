namespace MediaManager.Core.Editing;

/// <summary>
/// 新增文字块时的落点计算（纯函数，方便单测）。
///
/// 之前每次「添加文字 / 水印」都用同一个固定坐标（X=0.18，Y=0.4 / 0.78），
/// 一旦连续添加，多个文本框就会严丝合缝地叠在一起，看起来像「同一个框被刷了很多层」。
/// 这里按画布上已有文字数量做阶梯错位，保证新加的框一定不会盖住上一个。
/// </summary>
public static class TextOverlayPlacement
{
    /// <summary>阶梯错位步长（归一化）。</summary>
    public const double Step = 0.06;

    /// <summary>阶梯循环长度：错开到第 N 个后回到起点，避免越加越靠边。</summary>
    public const int CascadeSlots = 6;

    /// <summary>画布安全边距（归一化），保证文字起点不会贴到边缘外。</summary>
    public const double Margin = 0.04;

    /// <summary>
    /// 计算第 <paramref name="existingCount"/> 个文字块的落点。
    /// </summary>
    /// <param name="existingCount">画布上已有的文字块数量（&lt; 0 按 0 处理）。</param>
    /// <param name="isWatermark">水印默认落在画面下方，普通文字默认落在中间偏上。</param>
    public static (double X, double Y) Next(int existingCount, bool isWatermark)
    {
        var slot = Math.Max(0, existingCount) % CascadeSlots;
        var baseX = isWatermark ? 0.18 : 0.16;
        var baseY = isWatermark ? 0.70 : 0.34;

        // 水印向上错开、普通文字向下错开，两者互不抢占同一片区域
        var y = isWatermark ? baseY - slot * Step : baseY + slot * Step;

        var x = Clamp(baseX + Math.Floor(slot / 2.0) * Step);
        y = Clamp(y);

        return (Math.Round(x, 4), Math.Round(y, 4));
    }

    private static double Clamp(double value)
        => Math.Clamp(value, Margin, 1.0 - Margin);
}
