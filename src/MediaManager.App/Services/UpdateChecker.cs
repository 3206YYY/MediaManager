using System.Net;
using System.Net.Http;
using System.Text.Json;
using MediaManager.Core.Services;

namespace MediaManager.App.Services;

/// <summary>
/// 检查 GitHub 上有没有新版本。
/// <para>
/// 只读公开接口，不需要 token；没有网络 / 被墙 / 触发限流都当作「检查失败」处理，
/// 绝不弹错，只在设置页里显示一行说明。
/// </para>
/// <para>
/// 只查「有没有新版」，**不下载、不安装**。装不装由用户自己决定。
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
    {
        get
        {
            // 自动化验证用的钩子：真实发版时「当前版本」永远等于 GitHub 最新版，
            // 想让「发现新版本」这条路跑起来就只能临时把自己当成旧版。
            // 只有显式设了环境变量才生效，普通用户碰不到。
            var fake = Environment.GetEnvironmentVariable("MM_FAKE_CURRENT_VERSION");
            if (!string.IsNullOrWhiteSpace(fake)) return fake.Trim();

            return Plugins.PluginService.AppVersion;
        }
    }

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
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            DateTimeOffset? published = null;
            if (root.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pa.GetString(), out var when))
            {
                published = when;
            }

            var (downloadUrl, downloadSize) = PickInstaller(root);

            var latest = UpdatePolicy.NormalizeVersion(tag);
            var hasUpdate = latest != null && UpdatePolicy.IsNewer(latest, current);

            return new UpdateCheckResult
            {
                Succeeded = true,
                HasUpdate = hasUpdate,
                CurrentVersion = current,
                LatestVersion = latest ?? tag,
                ReleaseName = name,
                ReleaseUrl = string.IsNullOrWhiteSpace(url) ? ReleasesUrl : url,
                ReleaseNotes = body,
                PublishedAt = published,
                DownloadUrl = downloadUrl,
                DownloadSize = downloadSize,
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

    /// <summary>从 Release 附件里挑出安装包直链（有 Setup 就用它，没有就让用户去页面）。</summary>
    private static (string? Url, long? Size) PickInstaller(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            long? size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes)
                ? bytes
                : null;
            return (url, size);
        }

        return (null, null);
    }

    private static UpdateCheckResult Fail(string current, string error) => new()
    {
        Succeeded = false,
        CurrentVersion = current,
        ReleaseUrl = ReleasesUrl,
        Error = error,
    };
}
