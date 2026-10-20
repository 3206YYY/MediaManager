using System.Security.Cryptography;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>
/// 重复媒体检测：三级漏斗——
/// 1. 按文件大小分组（大小唯一淘汰）；
/// 2. 同大小组读前 64KB 计算 MD5 快速哈希（快速哈希唯一淘汰）；
/// 3. 快速哈希相同再算完整 SHA-256（保证绝对准确）。
/// </summary>
public class DuplicateService : IDuplicateService
{
    private const int QuickHashBytes = 64 * 1024;
    private readonly IMediaRepository _repo;
    private readonly ILogger<DuplicateService> _logger;

    public DuplicateService(IMediaRepository repo, ILogger<DuplicateService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<List<DuplicateGroup>> FindDuplicatesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var all = await LoadAllItemsAsync(ct);
        if (all.Count == 0) return new List<DuplicateGroup>();

        progress?.Report($"共 {all.Count} 个文件，按大小分组…");

        // 第一级：按大小分组，只保留同大小 >1 的组
        var sizeGroups = all
            .Where(m => m.SizeBytes > 0 && !string.IsNullOrEmpty(m.Path))
            .GroupBy(m => m.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        progress?.Report($"候选 {sizeGroups.Count} 组，快速哈希筛选…");

        // 第二级：前 64KB MD5 快速哈希
        var quickGroups = new List<List<MediaItem>>();
        foreach (var group in sizeGroups)
        {
            ct.ThrowIfCancellationRequested();
            var byQuick = new Dictionary<string, List<MediaItem>>(StringComparer.Ordinal);
            foreach (var item in group)
            {
                ct.ThrowIfCancellationRequested();
                var qh = TryComputeQuickHash(item.Path);
                if (qh == null) continue;
                if (!byQuick.TryGetValue(qh, out var list))
                {
                    list = new List<MediaItem>();
                    byQuick[qh] = list;
                }
                list.Add(item);
            }
            foreach (var same in byQuick.Values)
                if (same.Count >= 2)
                    quickGroups.Add(same);
        }

        progress?.Report($"快速哈希后剩 {quickGroups.Count} 组，精确校验…");

        // 第三级：完整 SHA-256
        var result = new List<DuplicateGroup>();
        int processed = 0;
        foreach (var group in quickGroups)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            var byFull = new Dictionary<string, List<MediaItem>>(StringComparer.Ordinal);
            foreach (var item in group)
            {
                ct.ThrowIfCancellationRequested();
                var full = TryComputeFullHash(item.Path);
                if (full == null) continue;
                if (!byFull.TryGetValue(full, out var list))
                {
                    list = new List<MediaItem>();
                    byFull[full] = list;
                }
                list.Add(item);
            }

            foreach (var (fullHash, same) in byFull)
            {
                if (same.Count < 2) continue;
                result.Add(new DuplicateGroup
                {
                    SizeBytes = same[0].SizeBytes,
                    Hash = fullHash,
                    Items = same,
                });
            }

            if (processed % 20 == 0)
                progress?.Report($"精确校验中 {processed}/{quickGroups.Count} 组…");
        }

        progress?.Report($"完成，找到 {result.Count} 个重复组");
        return result;
    }

    private async Task<List<MediaItem>> LoadAllItemsAsync(CancellationToken ct)
    {
        var all = new List<MediaItem>();
        int offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _repo.QueryAsync(new MediaFilter
            {
                Offset = offset,
                Limit = 5000,
                SortField = Core.Enums.SortField.DateAdded,
                SortDirection = Core.Enums.SortDirection.Ascending,
            });
            if (page.Count == 0) break;
            all.AddRange(page);
            offset += page.Count;
            if (page.Count < 5000) break;
        }
        return all;
    }

    /// <summary>读前 64KB 计算 MD5（不足读全部）；失败返回 null。</summary>
    private static string? TryComputeQuickHash(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            var buffer = new byte[QuickHashBytes];
            int total = 0;
            while (total < QuickHashBytes)
            {
                int read = stream.Read(buffer, total, QuickHashBytes - total);
                if (read == 0) break;
                total += read;
            }
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(buffer, 0, total));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读整个文件计算 SHA-256；失败返回 null。</summary>
    private static string? TryComputeFullHash(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }
}
