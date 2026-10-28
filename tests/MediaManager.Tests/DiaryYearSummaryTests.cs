using MediaManager.Core.Services;

namespace MediaManager.Tests;

/// <summary>
/// 时光轴年视图的按月汇总。
///
/// 这几个用例盯的是边界而不是「正常情况」：
/// 12 月 31 日算不算这一年、上一年的残留数据会不会漏进来、
/// 空月份在字典里到底存不存在（界面直接按月份取下标，缺键就是 0 篇，不能是异常）。
/// </summary>
public class DiaryYearSummaryTests
{
    [Fact]
    public void Aggregate_SumsSameMonth_AndFillsEveryMonth()
    {
        var daily = new Dictionary<string, int>
        {
            ["2026-10-02"] = 1,
            ["2026-10-28"] = 2,
            ["2026-03-05"] = 4,
        };

        var byMonth = DiaryYearSummary.AggregateByMonth(daily, 2026);

        Assert.Equal(12, byMonth.Count);
        Assert.Equal(3, byMonth[10]);
        Assert.Equal(4, byMonth[3]);
        Assert.Equal(7, DiaryYearSummary.Total(byMonth));

        // 空月份必须是 0，而不是「缺这个键」
        Assert.Equal(0, byMonth[7]);
    }

    [Fact]
    public void Aggregate_IgnoresOtherYears()
    {
        // 仓储按字符串范围查，理应不会混进别的年份；但边界正确性不靠「理应」
        var daily = new Dictionary<string, int>
        {
            ["2025-12-31"] = 9,
            ["2026-01-01"] = 2,
            ["2027-01-01"] = 5,
        };

        var byMonth = DiaryYearSummary.AggregateByMonth(daily, 2026);

        Assert.Equal(2, byMonth[1]);
        Assert.Equal(2, DiaryYearSummary.Total(byMonth));
        Assert.Equal(0, byMonth[12]);
    }

    [Fact]
    public void Aggregate_HandlesDecember31_AndLeapFeb()
    {
        var daily = new Dictionary<string, int>
        {
            ["2024-12-31"] = 1,
            ["2024-02-29"] = 1,
        };

        var byMonth = DiaryYearSummary.AggregateByMonth(daily, 2024);

        Assert.Equal(1, byMonth[12]);
        Assert.Equal(1, byMonth[2]);
    }

    [Fact]
    public void Aggregate_SkipsUnparsableKeysAndNonPositiveCounts()
    {
        var daily = new Dictionary<string, int>
        {
            ["不是日期"] = 3,
            ["2026-06-10"] = 0,
            ["2026-06-11"] = -2,
            ["2026-06-12"] = 1,
        };

        var byMonth = DiaryYearSummary.AggregateByMonth(daily, 2026);

        Assert.Equal(1, byMonth[6]);
        Assert.Equal(1, DiaryYearSummary.Total(byMonth));
    }

    [Fact]
    public void Aggregate_EmptyInput_IsAllZero()
    {
        var byMonth = DiaryYearSummary.AggregateByMonth(
            new Dictionary<string, int>(), 2026);

        Assert.Equal(12, byMonth.Count);
        Assert.All(byMonth.Values, v => Assert.Equal(0, v));
        Assert.Equal(0, DiaryYearSummary.Total(byMonth));
    }
}
