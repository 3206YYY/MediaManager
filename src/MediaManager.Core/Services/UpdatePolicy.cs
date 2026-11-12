namespace MediaManager.Core.Services;

/// <summary>一次版本检查的结果。</summary>
public sealed class UpdateCheckResult
{
    public bool Succeeded { get; init; }

    public bool HasUpdate { get; init; }

    public string CurrentVersion { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    /// <summary>Release 页面地址（用户手动去看的地方）。</summary>
    public string? ReleaseUrl { get; init; }

    public string? ReleaseName { get; init; }

    /// <summary>本次更新说明（Release 正文，Markdown 原文）。</summary>
    public string? ReleaseNotes { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>安装包直链（有就省掉「先去网页再点下载」这一步）。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>安装包字节数，用于在提示里显示体积。</summary>
    public long? DownloadSize { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// 更新提示里真正要显示的东西：一句概括 + 几条要点。
/// <para>为什么不是全文：更新说明写成一篇博客也不会有人读，提示框只该回答
/// 「这版加/改了什么，我要不要现在换」。</para>
/// </summary>
public sealed record UpdateDigest(string? Summary, IReadOnlyList<string> Bullets, int TotalBullets)
{
    public bool HasMore => TotalBullets > Bullets.Count;
}

/// <summary>
/// 更新提示的判定与文案处理。
/// <para>
/// 这里只放纯逻辑（不碰网络、不碰界面），因为「该不该弹」和「说明怎么显示」
/// 是最容易出细微错误又最难靠人眼验证的部分，必须能单测。
/// </para>
/// <para>
/// 设计原则：本软件**不后台静默下载安装**。检查到新版本只提示，
/// 下载由用户点按钮跳到浏览器，装不装、什么时候装都由用户决定。
/// </para>
/// </summary>
public static class UpdatePolicy
{
    /// <summary>启动时检查新版本（"1" / "0"）。缺省视为开启。</summary>
    public const string CheckOnStartupKey = "UpdateCheckOnStartup";

    /// <summary>用户选择「跳过这个版本」后记住的版本号，等于它就不再提示。</summary>
    public const string SkippedVersionKey = "UpdateSkippedVersion";

    /// <summary>上次检查时间（UTC，ISO 8601），仅用于设置页显示。</summary>
    public const string LastCheckUtcKey = "UpdateLastCheckUtc";

    /// <summary>更新提示里最多列几条要点，超出的让用户自己去发布页看。</summary>
    public const int MaxBullets = 5;

    /// <summary>概括最多多少字，超了截断（提示框不是阅读器）。</summary>
    public const int MaxSummaryChars = 120;

    /// <summary>
    /// 单条要点最多多少字：大约两行。
    /// 卡太紧（试过 38）会切在逗号上，读起来像话说一半被抢走，
    /// 反而不如让它占两行。
    /// </summary>
    public const int MaxBulletChars = 60;

    /// <summary>启动时检查默认开：不下载不安装，只提示，没有打扰成本。</summary>
    public static bool DefaultCheckOnStartup => true;

    public static bool ParseBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var text = raw.Trim();
        if (text is "1" or "true" or "True" or "TRUE" or "yes" or "on") return true;
        if (text is "0" or "false" or "False" or "FALSE" or "no" or "off") return false;
        return fallback;
    }

    /// <summary>把 <c>v1.2.5</c> / <c>1.2.5-beta.1</c> 洗成纯数字段。</summary>
    public static string? NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) text = text[..dash];

        return text.Length == 0 ? null : text;
    }

    /// <summary>按数字段比较（1.10.0 &gt; 1.9.9，不能按字符串比）。</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = Parse(latest);
        var b = Parse(current);

        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }

        return false;
    }

    /// <summary>用户点过「跳过这个版本」的，同一个版本不再打扰。</summary>
    public static bool IsSkipped(string? latestVersion, string? skippedVersion)
    {
        var latest = NormalizeVersion(latestVersion);
        var skipped = NormalizeVersion(skippedVersion);
        if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(skipped)) return false;
        return string.Equals(latest, skipped, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这一次检查的结果，值不值得弹窗告诉用户。</summary>
    public static bool ShouldNotify(UpdateCheckResult? result, string? skippedVersion)
    {
        if (result is null || !result.Succeeded || !result.HasUpdate) return false;
        return !IsSkipped(result.LatestVersion, skippedVersion);
    }

    /// <summary>
    /// 把 GitHub Release 正文压成「一句概括 + 几条要点」。
    /// <para>
    /// 约定：写更新说明时**开头先写一段概括**（一两句话说清这版加/改了什么），
    /// 紧接着**一组要点**列清改动，细节再往后放。这里扒的就是那段概括，加上第一组要点。
    /// </para>
    /// <para>
    /// 为什么只认**第一组**要点：后面的小节（「更新提示长什么样」「验证」「安装」）
    /// 里的列表和编号不是「改动」。以前一路收下来，12 条的说明会显示
    /// 「另外还有 7 条改动」——数字没错，但那些是测试和安装说明，等于骗人点进去。
    /// 所以第一组之后的要点一律不计入统计，要看全的点「查看完整说明」。
    /// </para>
    /// <para>
    /// 为什么不做全文显示：说明经常写成一篇博客，塞进提示框的结果是没人看，
    /// 用户直接点「以后再说」——那这次更新提示就白做了。
    /// </para>
    /// </summary>
    public static UpdateDigest BuildDigest(string? markdown, int maxBullets = MaxBullets)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new UpdateDigest(null, Array.Empty<string>(), 0);

        var bullets = new List<string>();
        var summaryLines = new List<string>();
        var seenBullet = false;
        var summaryClosed = false;
        var bulletsClosed = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = raw.Trim();

            // 空行意味着「第一段结束了」：概括只取开头那一段，
            // 后面的内容一律按要点处理，别把整篇说明吸进摘要里。
            if (text.Length == 0)
            {
                if (summaryLines.Count > 0) summaryClosed = true;
                continue;
            }

            if (IsNoise(text)) continue;

            if (IsBullet(text, out var bulletText))
            {
                // 第一组要点之后的列表属于「细节」，不参与统计
                if (bulletsClosed) continue;

                seenBullet = true;
                summaryClosed = true;
                var item = CleanInline(bulletText);
                if (item.Length > 0) bullets.Add(Shorten(item, MaxBulletChars));
                continue;
            }

            // 小节标题不进摘要：它只是分组，不说明改了什么
            if (text.StartsWith('#'))
            {
                summaryClosed = true;
                // 已经收过要点又遇到新小节 → 要点块结束（这里就是「细节」的边界）
                if (seenBullet) bulletsClosed = true;
                continue;
            }

            if (!seenBullet && !summaryClosed && summaryLines.Count < 3)
                summaryLines.Add(CleanInline(text));
        }

        string? summary = summaryLines.Count == 0
            ? null
            : string.Join(string.Empty, summaryLines).Trim();
        if (string.IsNullOrEmpty(summary)) summary = null;
        if (summary is { Length: > MaxSummaryChars })
            summary = summary[..MaxSummaryChars].TrimEnd() + "…";

        var shown = bullets.Count > maxBullets ? bullets.Take(maxBullets).ToList() : bullets;
        return new UpdateDigest(summary, shown, bullets.Count);
    }

    /// <summary>无序列表和无序编号都算要点（更新说明里两种写法都有）。</summary>
    private static bool IsBullet(string text, out string body)
    {
        if (text.StartsWith("- ", StringComparison.Ordinal)
            || text.StartsWith("* ", StringComparison.Ordinal)
            || text.StartsWith("+ ", StringComparison.Ordinal))
        {
            body = text[2..].Trim();
            return true;
        }

        var match = NumberedItem.Match(text);
        if (match.Success)
        {
            body = match.Groups[1].Value.Trim();
            return true;
        }

        body = string.Empty;
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex NumberedItem
        = new(@"^\d{1,2}[.)]\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>注释、分隔线、代码围栏这类东西不该出现在提示里。</summary>
    private static bool IsNoise(string text)
        => text.StartsWith("<!--", StringComparison.Ordinal)
           || text is "---" or "***" or "___"
           || text.StartsWith("```", StringComparison.Ordinal);

    /// <summary>
    /// 把一条要点压到一行以内：优先切在句号/分号后，实在没有才硬截。
    /// 切在标点后面读起来是「说完了」，而不是「被砍断了」。
    /// </summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;

        var slice = text[..max];

        // 先找第一个句末标点：那才是「这句话说完了」的位置。
        // 用 LastIndexOf 会切到最后一个逗号上，读起来像话说一半被抢走。
        foreach (var mark in "。！？")
        {
            var index = slice.IndexOf(mark);
            if (index >= 12) return slice[..(index + 1)];
        }

        var cut = slice.LastIndexOf('，');
        if (cut >= 12) return slice[..(cut + 1)];

        // 硬截时把省略号算进额度，否则出来 max+1 个字，正好挤出第二行
        return text[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>内联标记清洗：粗体、斜体、行内代码、链接、删除线、HTML 标签。</summary>
    private static string CleanInline(string text)
    {
        var s = text;

        // [文字](链接) → 文字
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // <img ...> / <br> 之类标签直接去掉
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", string.Empty);
        // **粗** / __粗__ → 粗
        s = s.Replace("**", string.Empty).Replace("__", string.Empty);
        // *斜* / _斜_ 只去掉成对的星号（下划线常出现在路径里，不动）
        s = s.Replace("*", string.Empty);
        // `代码` → 代码
        s = s.Replace("`", string.Empty);
        // ~~删除线~~ → 删除线
        s = s.Replace("~~", string.Empty);

        // 个别 emoji 在 Windows 自带字体里没有字形，会显示成空心方框（🗑 就是典型）。
        // 连它后面的空格一起去掉，否则「🗑 批量删除」会变成「删除 批量删除」这种重复。
        s = s.Replace("\U0001F5D1\uFE0F ", string.Empty)
                      .Replace("\U0001F5D1 ", string.Empty)
                      .Replace("\U0001F5D1\uFE0F", string.Empty)
                      .Replace("\U0001F5D1", string.Empty);

        return s.Trim();
    }

    private static int[] Parse(string version)
        => version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .ToArray();
}
