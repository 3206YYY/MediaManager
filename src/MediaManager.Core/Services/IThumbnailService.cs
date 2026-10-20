using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>缩略图服务接口。</summary>
public interface IThumbnailService
{
    /// <summary>某张缩略图生成完成（参数：mediaId, 缩略图文件路径）。</summary>
    event Action<long, string>? ThumbnailReady;

    /// <summary>将媒体项加入缩略图生成队列。</summary>
    void Enqueue(MediaItem item);

    /// <summary>获取缩略图缓存路径（若存在）。</summary>
    string? GetCachedPath(MediaItem item);

    /// <summary>清理缩略图缓存。</summary>
    Task<long> CleanCacheAsync(CancellationToken ct = default);
}
