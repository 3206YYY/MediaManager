namespace MediaManager.Core.Services;

/// <summary>
/// 时光轴「年视图」的纯计算：把「某天有几篇」汇总成「某月有几篇」。
///
/// 单独放在 Core 里是为了能单测 —— 这段逻辑最容易出错的地方全是边界：
/// 跨年数据该不该算、12 月 31 日属于哪一年、闰年 2 月、空月份要不要补 0。
/// 放在窗口代码里就只能靠眼睛看，看不出来。
/// </summary>
public static class DiaryYearSummary
{
    /// <summary>
    /// 汇总成一整年 12 个月的篇数。返回的字典**一定包含 1-12 全部键**（没有的月份是 0），
    /// 这样界面直接取下标就行，不用到处写 GetValueOrDefault。
    ///
    /// 不属于 <paramref name="year"/> 的日期、以及解析不了的 key 一律忽略
    /// （仓储查询是按字符串范围取的，理论上不会混进别的年份，但边界上不该靠「理论上」）。
    /// </summary>
    public static Dictionary<int, int> AggregateByMonth(IEnumerable<KeyValuePair<string, int>> daily, int year)
    {
        var result = new Dictionary<int, int>(12);
        for (var month = 1; month <= 12; month++) result[month] = 0;

        if (daily == null) return result;

        foreach (var pair in daily)
        {
            if (pair.Value <= 0) continue;
            if (!DateTime.TryParse(pair.Key, out var date)) continue;
            if (date.Year != year) continue;

            result[date.Month] += pair.Value;
        }

        return result;
    }

    /// <summary>全年总篇数（年视图标题里的「共 N 篇」）。</summary>
    public static int Total(IReadOnlyDictionary<int, int> byMonth)
        => byMonth == null ? 0 : byMonth.Values.Sum();
}
