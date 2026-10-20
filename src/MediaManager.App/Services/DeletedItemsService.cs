using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.Services;

/// <summary>
/// 「最近删除 / 撤销」业务逻辑：
/// 删除时记录快照（文件本身进系统回收站），撤销时把文件从回收站移回并恢复数据库记录。
/// </summary>
public class DeletedItemsService
{
    private readonly IMediaRepository _repo;
    private readonly ILogger<DeletedItemsService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public DeletedItemsService(IMediaRepository repo, ILogger<DeletedItemsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>删除文件「之前」调用：抓取快照（含标签/分类/收藏夹），写入最近删除表。</summary>
    public async Task<DeletedMedia> TrackAsync(MediaItem item, string? thumbnailPath, string? batchId = null)
    {
        var deleted = new DeletedMedia
        {
            OriginalMediaId = item.Id,
            RootFolderId = item.RootFolderId,
            Path = item.Path,
            FileName = item.FileName,
            MediaType = item.MediaType.ToString(),
            SizeBytes = item.SizeBytes,
            ThumbnailPath = thumbnailPath,
            DeletedAtUtc = DateTime.UtcNow.ToString("O"),
            BatchId = batchId,
            SnapshotJson = JsonSerializer.Serialize(item, JsonOptions),
        };

        try
        {
            deleted.TagIdsJson = JsonSerializer.Serialize(await _repo.GetMediaTagIdsAsync(item.Id));
            deleted.CategoryIdsJson = JsonSerializer.Serialize(await _repo.GetMediaCategoryIdsAsync(item.Id));
            deleted.CollectionIdsJson = JsonSerializer.Serialize(await _repo.GetMediaCollectionIdsAsync(item.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "抓取删除前标签/分类快照失败：{Path}", item.Path);
        }

        try
        {
            await _repo.AddDeletedAsync(deleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入最近删除记录失败：{Path}", item.Path);
        }

        return deleted;
    }

    public Task<List<DeletedMedia>> GetRecentAsync() => _repo.GetRecentlyDeletedAsync();

    /// <summary>从回收站还原一个文件并恢复数据库记录。</summary>
    public async Task<(bool Ok, string Message)> RestoreAsync(DeletedMedia deleted)
    {
        // 1) 文件还在原位置（例如用户手动还原过）就不用动回收站
        if (!File.Exists(deleted.Path))
        {
            if (!RecycleBin.TryRestore(deleted.Path, deleted.DeletedAtLocal, out var error))
                return (false, error);
        }

        // 2) 恢复数据库记录（沿用原 Id，收藏/评分/备注/标签/分类都回来）
        try
        {
            MediaItem media;
            try
            {
                media = JsonSerializer.Deserialize<MediaItem>(deleted.SnapshotJson ?? "{}", JsonOptions)
                        ?? new MediaItem();
            }
            catch
            {
                media = new MediaItem();
            }

            media.Id = deleted.OriginalMediaId;
            media.Path = deleted.Path;
            media.FileName = string.IsNullOrWhiteSpace(media.FileName) ? deleted.FileName : media.FileName;
            media.RootFolderId = deleted.RootFolderId;
            if (media.SizeBytes <= 0) media.SizeBytes = deleted.SizeBytes;
            if (string.IsNullOrWhiteSpace(media.ThumbnailPath)) media.ThumbnailPath = deleted.ThumbnailPath;
            media.IsMissing = false;

            await _repo.RestoreMediaAsync(
                media,
                DeserializeIds(deleted.TagIdsJson),
                DeserializeIds(deleted.CategoryIdsJson),
                DeserializeIds(deleted.CollectionIdsJson));

            await _repo.MarkDeletedRestoredAsync(deleted.Id);
            return (true, deleted.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "还原媒体记录失败：{Path}", deleted.Path);
            return (false, "文件已还原到原位置，但媒体库记录恢复失败：" + ex.Message);
        }
    }

    /// <summary>彻底删除：把文件从回收站永久移除，并删除记录与缩略图。</summary>
    public async Task PurgeAsync(DeletedMedia deleted)
    {
        try
        {
            RecycleBin.TryDeletePermanently(deleted.Path, deleted.DeletedAtLocal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从回收站永久删除失败：{Path}", deleted.Path);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(deleted.ThumbnailPath) && File.Exists(deleted.ThumbnailPath))
                File.Delete(deleted.ThumbnailPath);
        }
        catch { }

        await _repo.RemoveDeletedAsync(deleted.Id);
    }

    /// <summary>清空最近删除记录（不动物理文件，它们仍在回收站里）。</summary>
    public async Task ClearRecordsAsync()
    {
        var all = await _repo.GetRecentlyDeletedAsync(1000);
        foreach (var d in all)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(d.ThumbnailPath) && File.Exists(d.ThumbnailPath))
                    File.Delete(d.ThumbnailPath);
            }
            catch { }
        }
        await _repo.ClearDeletedAsync();
    }

    /// <summary>启动时清理超过 N 天的记录（回收站里的文件交给系统按自己的策略处理）。</summary>
    public async Task<int> PurgeOldRecordsAsync(int days = 30)
    {
        var all = await _repo.GetRecentlyDeletedAsync(1000);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        int removed = 0;

        foreach (var d in all.Where(x => x.DeletedAtLocal.ToUniversalTime() < cutoff))
        {
            try
            {
                await _repo.RemoveDeletedAsync(d.Id);
                if (!string.IsNullOrWhiteSpace(d.ThumbnailPath) && File.Exists(d.ThumbnailPath))
                    File.Delete(d.ThumbnailPath);
                removed++;
            }
            catch { }
        }

        return removed;
    }

    private static List<long> DeserializeIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<long>();
        try { return JsonSerializer.Deserialize<List<long>>(json) ?? new List<long>(); }
        catch { return new List<long>(); }
    }
}
