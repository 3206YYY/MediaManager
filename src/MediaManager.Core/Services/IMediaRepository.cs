using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>媒体库数据访问接口。</summary>
public interface IMediaRepository
{
    // RootFolder
    Task<long> AddRootFolderAsync(string path, string? volumeSerial, string? relativePath);
    Task RemoveRootFolderAsync(long id);
    Task<List<RootFolder>> GetRootFoldersAsync();
    Task UpdateRootFolderAsync(RootFolder folder);

    // MediaItem
    Task<long> UpsertMediaAsync(MediaItem item);

    /// <summary>批量写入（同一连接 + 同一事务）。扫描入库用，避免逐条开连接与逐条 SELECT。</summary>
    Task UpsertMediaBatchAsync(IReadOnlyList<MediaItem> items, CancellationToken ct = default);

    Task DeleteMediaAsync(long id);

    /// <summary>取出还没有宽高的图片（启动后台补齐用）。</summary>
    Task<List<MediaItem>> GetMissingSizeAsync(int limit, CancellationToken ct = default);

    /// <summary>回写图片宽高。</summary>
    Task UpdateMediaSizeAsync(long id, int width, int height);
    Task<int> MarkMissingAsync(long rootFolderId, ISet<string> existingPaths);
    Task MarkMissingSingleAsync(long id);
    Task<List<MediaItem>> QueryAsync(MediaFilter filter);
    Task<int> CountAsync(MediaFilter filter);
    Task<MediaItem?> GetByIdAsync(long id);
    Task UpdateMediaAsync(MediaItem item);
    Task SetFavoriteAsync(long id, bool favorite);
    Task SetRatingAsync(long id, int rating);
    Task SetNoteAsync(long id, string? note);
    Task UpdatePathAsync(long id, string newPath);

    // Tag
    Task<long> AddTagAsync(Tag tag);
    Task RenameTagAsync(long id, string name);
    Task DeleteTagAsync(long id);
    Task<List<Tag>> GetTagsAsync();
    Task SetTagColorAsync(long id, string? color);

    // MediaTag
    Task AddMediaTagAsync(long mediaId, long tagId);
    Task RemoveMediaTagAsync(long mediaId, long tagId);
    Task<List<long>> GetMediaTagIdsAsync(long mediaId);

    // Category
    Task<long> AddCategoryAsync(Category category);
    Task RenameCategoryAsync(long id, string name);
    Task DeleteCategoryAsync(long id);
    Task<List<Category>> GetCategoriesAsync();
    Task AddMediaCategoryAsync(long mediaId, long categoryId);
    Task RemoveMediaCategoryAsync(long mediaId, long categoryId);
    Task<List<long>> GetMediaCategoryIdsAsync(long mediaId);
    Task<Dictionary<long, List<long>>> GetMediaCategoryMapAsync(IEnumerable<long> mediaIds);

    /// <summary>
    /// 把旧版「收藏夹」id 映射成迁移后的分类 id（v1.3.2 合并时建立的对应关系）。
    /// 仅在还原「最近删除」里 v1.3.2 之前的记录时用得上，查不到的 id 直接丢弃。
    /// </summary>
    Task<List<long>> MapLegacyCollectionIdsAsync(IEnumerable<long> collectionIds);

    // Settings
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string? value);

    // FTS
    Task RebuildFtsAsync();
    Task UpdateFtsAsync(MediaItem item, string tags, string categories);
    Task RemoveFtsAsync(long mediaId);

    // Stats
    Task<long> GetTotalCountAsync();

    // Discovery（发现页随机推荐）
    Task<List<MediaItem>> QueryDiscoveryAsync(Enums.DiscoveryPoolType poolType, long? poolId, int limit, List<long>? excludeIds, IReadOnlyList<Enums.MediaType>? types = null);
    Task<long> CountDiscoveryPoolAsync(Enums.DiscoveryPoolType poolType, long? poolId, IReadOnlyList<Enums.MediaType>? types = null);
    Task SaveDiscoveryBatchAsync(string date, List<long> mediaIds, Enums.DiscoveryPoolType poolType, long? poolId, string? typeFilter = null);
    Task<DiscoveryBatch?> GetDiscoveryBatchAsync(string date);
    Task RecordDiscoveryHistoryAsync(List<long> mediaIds);
    Task<List<long>> GetDiscoveryHistoryIdsAsync(int days);

    // RecentView（最近查看）
    Task RecordRecentViewAsync(long mediaId);
    Task<List<MediaItem>> GetRecentViewAsync(int limit);

    // 排序（拖动排序）
    Task UpdateTagSortOrderAsync(IReadOnlyList<(long Id, int SortOrder)> orders);
    Task UpdateCategorySortOrderAsync(IReadOnlyList<(long Id, int SortOrder)> orders);

    // 按 id 列表查询媒体（发现页按批次 MediaIds 加载）
    Task<List<MediaItem>> GetByIdsAsync(IEnumerable<long> ids);

    // 侧栏数量统计
    Task<long> GetFavoriteCountAsync();
    Task<long> GetTagCountAsync();
    Task<long> GetCategoryCountAsync();

    // ---------- 最近删除 / 撤销 ----------

    /// <summary>记录一次删除（保存快照，供撤销与最近删除使用）。</summary>
    Task<long> AddDeletedAsync(DeletedMedia item);

    /// <summary>按删除时间倒序取最近删除记录（不含已还原的）。</summary>
    Task<List<DeletedMedia>> GetRecentlyDeletedAsync(int limit = 200);

    /// <summary>标记某条记录已还原（从最近删除列表移除）。</summary>
    Task MarkDeletedRestoredAsync(long id);

    /// <summary>删除某条最近删除记录（不再显示）。</summary>
    Task RemoveDeletedAsync(long id);

    /// <summary>清空所有最近删除记录。</summary>
    Task ClearDeletedAsync();

    /// <summary>还原媒体项（尽量沿用原 Id 与收藏/评分/备注），返回新的媒体 Id。</summary>
    Task<long> RestoreMediaAsync(MediaItem item, IEnumerable<long> tagIds, IEnumerable<long> categoryIds);

    /// <summary>清空所有媒体项的缩略图路径（清理缓存后调用，迫使重新生成）。</summary>
    Task ClearThumbnailPathsAsync();

    /// <summary>
    /// 让单张图的缩略图失效（清空 ThumbnailPath / QuickHash）。
    /// 编辑副本被原地覆盖后必须调用：缓存文件名带 QuickHash，不清就会一直显示旧画面。
    /// </summary>
    Task InvalidateThumbnailAsync(long mediaId);
}
