using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MediaManager.App.Services;

/// <summary>一次版本检查的结果。</summary>
public sealed class UpdateCheckResult
{
    public bool Succeeded { get; init; }
    public bool HasUpdate { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseName { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 检查 GitHub 上有没有新版本。
/// <para>
/// 只读公开接口，不需要 token；没有网络 / 被墙 / 触发限流都当作「检查失败」处理，
/// 绝不弹错，只在设置页里显示一行说明。
/// </para>
/// </summary>
public static class UpdateChecker
{
    /// <summary>仓库主页（用户下载最新版的地方）。</summary>
    public const string RepositoryUrl = "https://github.com/3206YYY/MediaManager";

    /// <summary>Releases 列表（比 latest 更稳妥：没发布正式版时也能打开）。</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    private const string LatestApi = "https://api.github.com/repos/3206YYY/MediaManager/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub API 强制要求 UA，否则直接 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaManager/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>当前程序版本（<c>1.2.5</c> 这种三段式）。</summary>
    public static string CurrentVersion
        => Plugins.PluginService.AppVersion;

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;

        try
        {
            using var response = await Http.GetAsync(LatestApi, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 仓库还没有正式 Release：不算错误，提示去 Releases 页面自己看
                return new UpdateCheckResult
                {
                    Succeeded = true,
                    HasUpdate = false,
                    CurrentVersion = current,
                    ReleaseUrl = ReleasesUrl,
                    Error = "还没有正式发布的版本记录",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(current, response.StatusCode == HttpStatusCode.Forbidden
                    ? "接口访问次数超限，请稍后再试"
                    : $"GitHub 返回 {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            var latest = Normalize(tag);
            var hasUpdate = latest != null && IsNewer(latest, current);

            return new UpdateCheckResult
            {
                Succeeded = true,
                HasUpdate = hasUpdate,
                CurrentVersion = current,
                LatestVersion = latest ?? tag,
                ReleaseName = name,
                ReleaseUrl = string.IsNullOrWhiteSpace(url) ? ReleasesUrl : url,
            };
        }
        catch (TaskCanceledException)
        {
            return Fail(current, "连接超时（网络不通或被拦截）");
        }
        catch (Exception ex)
        {
            return Fail(current, ex.Message);
        }
    }

    private static UpdateCheckResult Fail(string current, string error) => new()
    {
        Succeeded = false,
        CurrentVersion = current,
        ReleaseUrl = ReleasesUrl,
        Error = error,
    };

    /// <summary>把 <c>v1.2.5</c> / <c>1.2.5-beta</c> 洗成纯数字段。</summary>
    private static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        // 去掉预发布后缀：1.2.5-beta.1 → 1.2.5
        var dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) text = text[..dash];

        return text.Length == 0 ? null : text;
    }

    /// <summary>按数字段比较（1.10.0 &gt; 1.9.9，不能按字符串比）。</summary>
    internal static bool IsNewer(string latest, string current)
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

    private static int[] Parse(string version)
        => version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .ToArray();
}
