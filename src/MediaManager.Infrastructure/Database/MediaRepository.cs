using Dapper;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>基于 Dapper + SQLite 的媒体库数据访问实现。</summary>
public class MediaRepository : IMediaRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<MediaRepository> _logger;

    public MediaRepository(IDbConnectionFactory factory, ILogger<MediaRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // ---------- RootFolder ----------

    public async Task<long> AddRootFolderAsync(string path, string? volumeSerial, string? relativePath)
    {
        using var conn = _factory.CreateConnection();
        // 幂等：若路径已存在则直接返回已有 id，避免 UNIQUE 冲突
        var existing = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM RootFolder WHERE Path = @Path;", new { Path = path });
        if (existing.HasValue)
            return existing.Value;

        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO RootFolder(Path, VolumeSerial, RelativePath, IsEnabled, AddedAtUtc)
            VALUES(@Path, @VolumeSerial, @RelativePath, 1, @AddedAtUtc);
            SELECT last_insert_rowid();",
            new { Path = path, VolumeSerial = volumeSerial, RelativePath = relativePath,
                  AddedAtUtc = DateTime.UtcNow.ToString("O") });
    }

    public async Task RemoveRootFolderAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        // 先软标记缺失，再删除根目录（级联删除媒体项）
        await conn.ExecuteAsync("DELETE FROM RootFolder WHERE Id = @Id;", new { Id = id });
    }

    public async Task<List<RootFolder>> GetRootFoldersAsync()
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<RootFolder>(
            "SELECT * FROM RootFolder ORDER BY Id;");
        return list.ToList();
    }

    public async Task UpdateRootFolderAsync(RootFolder folder)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE RootFolder SET Path=@Path, VolumeSerial=@VolumeSerial,
                RelativePath=@RelativePath, IsEnabled=@IsEnabled, LastScanUtc=@LastScanUtc
            WHERE Id=@Id;", folder);
    }

    // ---------- MediaItem ----------

    public async Task<long> UpsertMediaAsync(MediaItem item)
    {
        using var conn = _factory.CreateConnection();
        var id = await conn.ExecuteScalarAsync<long?>(@"
            SELECT Id FROM MediaItem WHERE Path = @Path;", new { item.Path });
        if (id.HasValue)
        {
            item.Id = id.Value;
            // 注意：必须显式 MediaType = item.MediaType.ToString()，不能直接传 item 让 Dapper 序列化枚举，
            // 否则 Dapper 会把枚举存成数字 "0"/"1"，与筛选条件的 "Image"/"Video" 不一致导致按类型筛选失效。
            await conn.ExecuteAsync(@"
                UPDATE MediaItem SET
                    FileName=@FileName, Extension=@Extension, MediaType=@MediaType,
                    SizeBytes=@SizeBytes, CreatedAtUtc=@CreatedAtUtc, ModifiedAtUtc=@ModifiedAtUtc,
                    Width=COALESCE(@Width, Width), Height=COALESCE(@Height, Height),
                    IsMissing=0, LastScanUtc=@LastScanUtc
                WHERE Id=@Id;", new
            {
                item.FileName, item.Extension,
                MediaType = item.MediaType.ToString(),
                item.SizeBytes, item.CreatedAtUtc, item.ModifiedAtUtc,
                item.Width, item.Height,
                item.LastScanUtc, item.Id,
            });
            return id.Value;
        }

        item.DateAddedUtc = DateTime.UtcNow.ToString("O");
        var newId = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO MediaItem(RootFolderId, Path, FileName, Extension, MediaType, SizeBytes,
                Width, Height, CreatedAtUtc, ModifiedAtUtc, Favorite, Rating, IsMissing, DateAddedUtc, LastScanUtc)
            VALUES(@RootFolderId, @Path, @FileName, @Extension, @MediaType, @SizeBytes,
                @Width, @Height, @CreatedAtUtc, @ModifiedAtUtc, @Favorite, @Rating, 0, @DateAddedUtc, @LastScanUtc);
            SELECT last_insert_rowid();",
            new
            {
                item.RootFolderId, item.Path, item.FileName, item.Extension,
                MediaType = item.MediaType.ToString(),
                item.SizeBytes, item.Width, item.Height, item.CreatedAtUtc, item.ModifiedAtUtc,
                Favorite = item.Favorite ? 1 : 0, item.Rating,
                item.DateAddedUtc, item.LastScanUtc,
            });
        item.Id = newId;
        return newId;
    }

    /// <summary>取出还没有宽高的图片（用于启动后台补齐，一次处理一批）。</summary>
    public async Task<List<MediaItem>> GetMissingSizeAsync(int limit, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<MediaItem>(@"
            SELECT Id, Path, Extension, MediaType FROM MediaItem
            WHERE (Width IS NULL OR Width = 0 OR Height IS NULL OR Height = 0)
              AND MediaType = 'Image'
            LIMIT @Limit;", new { Limit = limit });
        return rows.AsList();
    }

    /// <summary>回写宽高。</summary>
    public async Task UpdateMediaSizeAsync(long id, int width, int height)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET Width=@Width, Height=@Height WHERE Id=@Id;",
            new { Id = id, Width = width, Height = height });
    }

    public async Task DeleteMediaAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        // 删除关联记录（标签/分类关联表；v1.3.2 起已无收藏夹关联）
        await conn.ExecuteAsync("DELETE FROM MediaCategory WHERE MediaId=@Id;", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM MediaTag WHERE MediaId=@Id;", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM RecentView WHERE MediaId=@Id;", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM DiscoveryHistory WHERE MediaId=@Id;", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM MediaItem WHERE Id=@Id;", new { Id = id });
    }

    public async Task<int> MarkMissingAsync(long rootFolderId, ISet<string> existingPaths)
    {
        // 将该根目录下数据库中所有不在磁盘的项标记为 IsMissing=1
        using var conn = _factory.CreateConnection();
        var all = await conn.QueryAsync<string>(
            "SELECT Path FROM MediaItem WHERE RootFolderId=@RootFolderId AND IsMissing=0;",
            new { RootFolderId = rootFolderId });
        var toMark = all.Where(p => !existingPaths.Contains(p)).ToList();
        if (toMark.Count == 0) return 0;

        var count = 0;
        foreach (var chunk in toMark.Chunk(500))
        {
            count += await conn.ExecuteAsync(
                "UPDATE MediaItem SET IsMissing=1 WHERE Path IN @Paths AND RootFolderId=@RootFolderId;",
                new { Paths = chunk, RootFolderId = rootFolderId });
        }
        return count;
    }

    public async Task MarkMissingSingleAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE MediaItem SET IsMissing=1 WHERE Id=@Id;", new { Id = id });
    }

    public async Task<List<MediaItem>> QueryAsync(MediaFilter filter)
    {
        filter = await ExpandCategoryAsync(filter);
        var (where, param) = BuildWhere(filter);
        var sql = $@"
            SELECT m.* FROM MediaItem m
            {where}
            ORDER BY {BuildOrderBy(filter)}
            LIMIT @Limit OFFSET @Offset;";
        param.Add("Limit", filter.Limit);
        param.Add("Offset", filter.Offset);

        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<MediaItem>(sql, param);
        return list.ToList();
    }

    public async Task<int> CountAsync(MediaFilter filter)
    {
        filter = await ExpandCategoryAsync(filter);
        var (where, param) = BuildWhere(filter);
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM MediaItem m {where};", param);
    }

    /// <summary>将分类筛选展开为含所有子分类的 id 列表。</summary>
    private async Task<MediaFilter> ExpandCategoryAsync(MediaFilter filter)
    {
        if (!filter.CategoryId.HasValue) return filter;

        using var conn = _factory.CreateConnection();
        var cats = await conn.QueryAsync<Category>("SELECT * FROM Category;");
        var all = cats.ToList();

        var result = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(filter.CategoryId.Value);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            foreach (var child in all.Where(c => c.ParentId == id))
                stack.Push(child.Id);
        }
        filter.CategoryIds = result.ToList();
        return filter;
    }

    public async Task<MediaItem?> GetByIdAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<MediaItem>(
            "SELECT * FROM MediaItem WHERE Id=@Id;", new { Id = id });
    }

    public async Task UpdateMediaAsync(MediaItem item)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE MediaItem SET
                Width=@Width, Height=@Height, DurationMs=@DurationMs,
                ThumbnailPath=@ThumbnailPath, QuickHash=@QuickHash,
                Favorite=@Favorite, Rating=@Rating, IsMissing=@IsMissing
            WHERE Id=@Id;",
            new
            {
                item.Id, item.Width, item.Height, item.DurationMs,
                item.ThumbnailPath, item.QuickHash,
                Favorite = item.Favorite ? 1 : 0, item.Rating,
                IsMissing = item.IsMissing ? 1 : 0,
            });
    }

    public async Task SetFavoriteAsync(long id, bool favorite)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE MediaItem SET Favorite=@F WHERE Id=@Id;",
            new { F = favorite ? 1 : 0, Id = id });
    }

    public async Task SetRatingAsync(long id, int rating)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE MediaItem SET Rating=@R WHERE Id=@Id;",
            new { R = rating, Id = id });
    }

    public async Task SetNoteAsync(long id, string? note)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE MediaItem SET Note=@Note WHERE Id=@Id;",
            new { Note = note, Id = id });
    }

    public async Task UpdatePathAsync(long id, string newPath)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET Path=@Path, FileName=@FileName, Extension=@Ext WHERE Id=@Id;",
            new
            {
                Path = newPath,
                FileName = Path.GetFileName(newPath),
                Ext = Path.GetExtension(newPath).TrimStart('.'),
                Id = id,
            });
    }

    // ---------- Tag ----------

    public async Task<long> AddTagAsync(Tag tag)
    {
        using var conn = _factory.CreateConnection();
        if (tag.SortOrder == 0)
        {
            var max = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(SortOrder),0)+1 FROM Tag;");
            tag.SortOrder = max;
        }
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO Tag(Name, Color, SortOrder, CreatedAtUtc)
            VALUES(@Name, @Color, @SortOrder, @CreatedAtUtc);
            SELECT last_insert_rowid();", tag);
    }

    public async Task RenameTagAsync(long id, string name)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Tag SET Name=@Name WHERE Id=@Id;", new { Name = name, Id = id });
    }

    public async Task DeleteTagAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM Tag WHERE Id=@Id;", new { Id = id });
    }

    public async Task<List<Tag>> GetTagsAsync()
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<Tag>("SELECT * FROM Tag ORDER BY SortOrder, Name;");
        return list.ToList();
    }

    public async Task SetTagColorAsync(long id, string? color)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Tag SET Color=@Color WHERE Id=@Id;", new { Color = color, Id = id });
    }

    // ---------- MediaTag ----------

    public async Task AddMediaTagAsync(long mediaId, long tagId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO MediaTag(MediaId, TagId) VALUES(@M, @T);",
            new { M = mediaId, T = tagId });
    }

    public async Task RemoveMediaTagAsync(long mediaId, long tagId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM MediaTag WHERE MediaId=@M AND TagId=@T;",
            new { M = mediaId, T = tagId });
    }

    public async Task<List<long>> GetMediaTagIdsAsync(long mediaId)
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<long>(
            "SELECT TagId FROM MediaTag WHERE MediaId=@M;", new { M = mediaId });
        return list.ToList();
    }

    // ---------- Category ----------

    public async Task<long> AddCategoryAsync(Category category)
    {
        using var conn = _factory.CreateConnection();
        if (category.SortOrder == 0)
        {
            var max = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(SortOrder),0)+1 FROM Category;");
            category.SortOrder = max;
        }
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO Category(Name, ParentId, SortOrder, CreatedAtUtc)
            VALUES(@Name, @ParentId, @SortOrder, @CreatedAtUtc);
            SELECT last_insert_rowid();", category);
    }

    public async Task RenameCategoryAsync(long id, string name)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Category SET Name=@Name WHERE Id=@Id;", new { Name = name, Id = id });
    }

    public async Task DeleteCategoryAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM Category WHERE Id=@Id;", new { Id = id });
    }

    public async Task<List<Category>> GetCategoriesAsync()
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<Category>(
            "SELECT * FROM Category ORDER BY SortOrder, Name;");
        return list.ToList();
    }

    public async Task AddMediaCategoryAsync(long mediaId, long categoryId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO MediaCategory(MediaId, CategoryId) VALUES(@M, @C);",
            new { M = mediaId, C = categoryId });
    }

    public async Task RemoveMediaCategoryAsync(long mediaId, long categoryId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM MediaCategory WHERE MediaId=@M AND CategoryId=@C;",
            new { M = mediaId, C = categoryId });
    }

    public async Task<List<long>> GetMediaCategoryIdsAsync(long mediaId)
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<long>(
            "SELECT CategoryId FROM MediaCategory WHERE MediaId=@M;", new { M = mediaId });
        return list.ToList();
    }

    public async Task<Dictionary<long, List<long>>> GetMediaCategoryMapAsync(IEnumerable<long> mediaIds)
    {
        var result = new Dictionary<long, List<long>>();
        var ids = mediaIds?.Distinct().ToList() ?? new List<long>();
        if (ids.Count == 0) return result;

        using var conn = _factory.CreateConnection();
        // 分批查询，避免 IN 列表过长（SQLite 参数上限）
        foreach (var chunk in ids.Chunk(500))
        {
            var rows = await conn.QueryAsync<(long MediaId, long CategoryId)>(
                "SELECT MediaId, CategoryId FROM MediaCategory WHERE MediaId IN @Ids;",
                new { Ids = chunk });
            foreach (var (mid, cid) in rows)
            {
                if (!result.TryGetValue(mid, out var list))
                {
                    list = new List<long>();
                    result[mid] = list;
                }
                list.Add(cid);
            }
        }
        return result;
    }

    // ---------- v1.3.2：旧收藏夹 id 兼容 ----------

    public async Task<List<long>> MapLegacyCollectionIdsAsync(IEnumerable<long> collectionIds)
    {
        var ids = collectionIds?.Distinct().ToList() ?? new List<long>();
        if (ids.Count == 0) return new List<long>();

        using var conn = _factory.CreateConnection();
        // 映射表由 DatabaseInitializer.MigrateCollectionsIntoCategories 建立；
        // 查不到说明这条收藏夹当时没搬过来（比如原始数据已损坏），直接丢弃，不让还原失败。
        var list = await conn.QueryAsync<long>(@"
            SELECT m.CategoryId
            FROM CollectionMigrationMap m
            JOIN Category c ON c.Id = m.CategoryId
            WHERE m.CollectionId IN @Ids;", new { Ids = ids });
        return list.Distinct().ToList();
    }

    // ---------- Settings ----------

    public async Task<string?> GetSettingAsync(string key)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT Value FROM AppSetting WHERE Key=@Key;", new { Key = key });
    }

    public async Task SetSettingAsync(string key, string? value)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO AppSetting(Key, Value) VALUES(@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value=@Value;", new { Key = key, Value = value });
    }

    // ---------- FTS ----------

    /// <summary>
    /// 全量重建搜索索引。
    /// <para>
    /// MediaFts 是 external content 表（content='MediaItem', content_rowid='Id'），但索引里多出
    /// Tags / Categories 两个「虚拟列」—— 它们不在 MediaItem 里。所以 fts5 的
    /// <c>'rebuild'</c> 和直接 UPDATE 都会报 <c>no such column: T.Tags</c>
    /// （rebuild 会去 content 表读这两列）。
    /// 可靠做法只有一条：<c>delete-all</c> 清空索引，再用 SELECT 把 MediaItem 连标签/分类名字一起灌回去。
    /// </para>
    /// </summary>
    public async Task RebuildFtsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("INSERT INTO MediaFts(MediaFts) VALUES('delete-all');", transaction: tx);
        await conn.ExecuteAsync(@"
            INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
            SELECT m.Id, m.FileName, m.Path,
              (SELECT group_concat(t.Name, ' ') FROM MediaTag mt JOIN Tag t ON t.Id=mt.TagId WHERE mt.MediaId=m.Id),
              (SELECT group_concat(c.Name, ' ') FROM MediaCategory mc JOIN Category c ON c.Id=mc.CategoryId WHERE mc.MediaId=m.Id)
            FROM MediaItem m;", transaction: tx);

        tx.Commit();
    }

    public async Task UpdateFtsAsync(MediaItem item, string tags, string categories)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO MediaFts(MediaFts, rowid, FileName, Path, Tags, Categories)
            VALUES('delete', @Id, @FileName, @Path, @Tags, @Categories);
            INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
            VALUES(@Id, @FileName, @Path, @Tags, @Categories);",
            new { item.Id, item.FileName, item.Path, Tags = tags, Categories = categories });
    }

    public async Task RemoveFtsAsync(long mediaId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO MediaFts(MediaFts, rowid, FileName, Path, Tags, Categories)
            VALUES('delete', @Id, '', '', '', '');", new { Id = mediaId });
    }

    public async Task<long> GetTotalCountAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM MediaItem WHERE IsMissing=0;");
    }

    // ---------- Discovery（发现页随机推荐） ----------

    public async Task<List<MediaItem>> QueryDiscoveryAsync(DiscoveryPoolType poolType, long? poolId, int limit, List<long>? excludeIds, IReadOnlyList<MediaType>? types = null)
    {
        using var conn = _factory.CreateConnection();
        var clauses = new List<string> { "m.IsMissing = 0" };

        switch (poolType)
        {
            case DiscoveryPoolType.Favorite:
                clauses.Add("m.Favorite = 1");
                break;
            case DiscoveryPoolType.Unfiled:
                clauses.Add("NOT EXISTS (SELECT 1 FROM MediaCategory mc WHERE mc.MediaId = m.Id)");
                break;
            case DiscoveryPoolType.Category:
                if (poolId.HasValue)
                    clauses.Add("EXISTS (SELECT 1 FROM MediaCategory mcl WHERE mcl.MediaId = m.Id AND mcl.CategoryId = @PoolId)");
                break;
            case DiscoveryPoolType.All:
            default:
                break;
        }

        var param = new DynamicParameters();
        if (poolType == DiscoveryPoolType.Category && poolId.HasValue)
            param.Add("PoolId", poolId.Value);

        if (excludeIds is { Count: > 0 })
        {
            clauses.Add("m.Id NOT IN @Exclude");
            param.Add("Exclude", excludeIds);
        }

        AppendTypeClause(clauses, param, "m.MediaType", types);

        param.Add("Limit", limit);
        var where = " WHERE " + string.Join(" AND ", clauses);
        var list = await conn.QueryAsync<MediaItem>(
            $"SELECT m.* FROM MediaItem m{where} ORDER BY RANDOM() LIMIT @Limit;", param);
        return list.ToList();
    }

    public async Task<long> CountDiscoveryPoolAsync(DiscoveryPoolType poolType, long? poolId, IReadOnlyList<MediaType>? types = null)
    {
        using var conn = _factory.CreateConnection();
        var clauses = new List<string> { "IsMissing = 0" };
        switch (poolType)
        {
            case DiscoveryPoolType.Favorite:
                clauses.Add("Favorite = 1");
                break;
            case DiscoveryPoolType.Unfiled:
                clauses.Add("NOT EXISTS (SELECT 1 FROM MediaCategory mc WHERE mc.MediaId = MediaItem.Id)");
                break;
            case DiscoveryPoolType.Category:
                if (poolId.HasValue)
                    clauses.Add("EXISTS (SELECT 1 FROM MediaCategory mcl WHERE mcl.MediaId = MediaItem.Id AND mcl.CategoryId = @PoolId)");
                break;
        }

        var param = new DynamicParameters();
        if (poolType == DiscoveryPoolType.Category && poolId.HasValue)
            param.Add("PoolId", poolId.Value);
        AppendTypeClause(clauses, param, "MediaItem.MediaType", types);

        var where = " WHERE " + string.Join(" AND ", clauses);
        return await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM MediaItem{where};", param);
    }

    /// <summary>按媒体类型集合追加 IN 条件（兼容历史数字写法）。</summary>
    private static void AppendTypeClause(List<string> clauses, DynamicParameters param, string column, IReadOnlyList<MediaType>? types)
    {
        if (types is not { Count: > 0 }) return;
        var names = types.Select(t => t.ToString()).ToList();
        names.AddRange(types.Select(t => ((int)t).ToString()));
        clauses.Add($"{column} IN @TypeNames");
        param.Add("TypeNames", names);
    }

    public async Task SaveDiscoveryBatchAsync(string date, List<long> mediaIds, DiscoveryPoolType poolType, long? poolId, string? typeFilter = null)
    {
        using var conn = _factory.CreateConnection();
        var json = System.Text.Json.JsonSerializer.Serialize(mediaIds);
        await conn.ExecuteAsync(@"
            INSERT INTO DailyDiscovery(Date, MediaIds, PoolType, PoolId, TypeFilter) VALUES(@Date, @MediaIds, @PoolType, @PoolId, @TypeFilter)
            ON CONFLICT(Date) DO UPDATE SET MediaIds=@MediaIds, PoolType=@PoolType, PoolId=@PoolId, TypeFilter=@TypeFilter;",
            new { Date = date, MediaIds = json, PoolType = poolType.ToString(), PoolId = poolId, TypeFilter = typeFilter });
    }

    public async Task<DiscoveryBatch?> GetDiscoveryBatchAsync(string date)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<(string Date, string MediaIds, string PoolType, long? PoolId, string? TypeFilter)>(
            "SELECT Date, MediaIds, PoolType, PoolId, TypeFilter FROM DailyDiscovery WHERE Date=@Date;", new { Date = date });
        if (row.Date == null) return null;
        var ids = string.IsNullOrEmpty(row.MediaIds)
            ? new List<long>()
            : System.Text.Json.JsonSerializer.Deserialize<List<long>>(row.MediaIds) ?? new List<long>();
        return new DiscoveryBatch
        {
            Date = row.Date,
            MediaIds = ids,
            PoolType = row.PoolType,
            PoolId = row.PoolId,
            TypeFilter = row.TypeFilter,
        };
    }

    public async Task RecordDiscoveryHistoryAsync(List<long> mediaIds)
    {
        if (mediaIds.Count == 0) return;
        using var conn = _factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        var rows = mediaIds.Select(id => new { MediaId = id, PushedAtUtc = now });
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO DiscoveryHistory(MediaId, PushedAtUtc) VALUES(@MediaId, @PushedAtUtc);", rows);
    }

    public async Task<List<long>> GetDiscoveryHistoryIdsAsync(int days)
    {
        using var conn = _factory.CreateConnection();
        var since = DateTime.UtcNow.AddDays(-days).ToString("O");
        var list = await conn.QueryAsync<long>(
            "SELECT DISTINCT MediaId FROM DiscoveryHistory WHERE PushedAtUtc >= @Since;", new { Since = since });
        return list.ToList();
    }

    // ---------- RecentView（最近查看） ----------

    public async Task RecordRecentViewAsync(long mediaId)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO RecentView(MediaId, ViewedAtUtc) VALUES(@MediaId, @ViewedAtUtc)
            ON CONFLICT(MediaId) DO UPDATE SET ViewedAtUtc=@ViewedAtUtc;",
            new { MediaId = mediaId, ViewedAtUtc = DateTime.UtcNow.ToString("O") });
        // 只保留最近 100 条
        await conn.ExecuteAsync(@"
            DELETE FROM RecentView WHERE MediaId NOT IN (
              SELECT MediaId FROM RecentView ORDER BY ViewedAtUtc DESC LIMIT 100);");
    }

    public async Task<List<MediaItem>> GetRecentViewAsync(int limit)
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<MediaItem>(@"
            SELECT m.* FROM MediaItem m
            JOIN RecentView rv ON rv.MediaId = m.Id
            WHERE m.IsMissing = 0
            ORDER BY rv.ViewedAtUtc DESC
            LIMIT @Limit;", new { Limit = limit });
        return list.ToList();
    }

    // ---------- 排序（拖动排序，事务批量写回 SortOrder） ----------

    public async Task UpdateTagSortOrderAsync(IReadOnlyList<(long Id, int SortOrder)> orders)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (id, sortOrder) in orders)
            await conn.ExecuteAsync("UPDATE Tag SET SortOrder=@SortOrder WHERE Id=@Id;",
                new { SortOrder = sortOrder, Id = id }, tx);
        tx.Commit();
    }

    public async Task UpdateCategorySortOrderAsync(IReadOnlyList<(long Id, int SortOrder)> orders)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (id, sortOrder) in orders)
            await conn.ExecuteAsync("UPDATE Category SET SortOrder=@SortOrder WHERE Id=@Id;",
                new { SortOrder = sortOrder, Id = id }, tx);
        tx.Commit();
    }

    // ---------- 按 id 列表查询媒体 ----------

    public async Task<List<MediaItem>> GetByIdsAsync(IEnumerable<long> ids)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new List<MediaItem>();

        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<MediaItem>(
            "SELECT * FROM MediaItem WHERE Id IN @Ids AND IsMissing=0;", new { Ids = idList });

        // 保持传入顺序
        var map = list.ToDictionary(m => m.Id);
        return idList.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }

    // ---------- 侧栏数量统计 ----------

    public async Task<long> GetFavoriteCountAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM MediaItem WHERE Favorite=1 AND IsMissing=0;");
    }

    public async Task<long> GetTagCountAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Tag;");
    }

    public async Task<long> GetCategoryCountAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Category;");
    }

    // ---------- 构建查询 ----------

    private static (string Where, DynamicParameters Param) BuildWhere(MediaFilter f)
    {
        var param = new DynamicParameters();
        var clauses = new List<string> { "m.IsMissing = 0" };

        if (!string.IsNullOrWhiteSpace(f.SearchText))
        {
            // FTS 全文匹配（标签/分类/文件名分词）+ 文件名/路径子串匹配（instr，大小写不敏感）。
            // 子串匹配兜底：用户直接搜文件名片段（如 image_202610916、001 (1)）也能精确命中，
            // 不受 FTS 分词和特殊字符（下划线/括号/连字符）影响。
            clauses.Add("(m.Id IN (SELECT rowid FROM MediaFts WHERE MediaFts MATCH @Search) " +
                        "OR instr(lower(m.FileName), lower(@RawSearch)) > 0 " +
                        "OR instr(lower(m.Path), lower(@RawSearch)) > 0)");
            param.Add("Search", FtsEscape(f.SearchText!));
            param.Add("RawSearch", f.SearchText!);
        }
        if (f.MediaType.HasValue)
        {
            // 兼容历史数据：早期存 "0"/"1"，现在存 "Image"/"Video"，两者都能匹配
            clauses.Add("(m.MediaType = @MediaType OR m.MediaType = @MediaTypeNum)");
            param.Add("MediaType", f.MediaType.Value.ToString());
            param.Add("MediaTypeNum", ((int)f.MediaType.Value).ToString());
        }
        if (f.MediaTypes is { Count: > 0 })
        {
            var names = f.MediaTypes.Select(t => t.ToString()).ToList();
            names.AddRange(f.MediaTypes.Select(t => ((int)t).ToString()));
            clauses.Add("m.MediaType IN @MediaTypes");
            param.Add("MediaTypes", names);
        }
        if (f.Favorite.HasValue)
        {
            clauses.Add("m.Favorite = @Favorite");
            param.Add("Favorite", f.Favorite.Value ? 1 : 0);
        }
        if (f.EditedOnly == true)
        {
            clauses.Add("instr(lower(m.FileName), '_edited') > 0");
        }
        if (f.RootFolderId.HasValue)
        {
            clauses.Add("m.RootFolderId = @RootFolderId");
            param.Add("RootFolderId", f.RootFolderId.Value);
        }
        if (f.Unfiled.HasValue && f.Unfiled.Value)
        {
            clauses.Add("NOT EXISTS (SELECT 1 FROM MediaCategory mc WHERE mc.MediaId = m.Id)");
        }
        if (f.TagId.HasValue)
        {
            clauses.Add("EXISTS (SELECT 1 FROM MediaTag mt WHERE mt.MediaId = m.Id AND mt.TagId = @TagId)");
            param.Add("TagId", f.TagId.Value);
        }
        if (f.CategoryId.HasValue && f.CategoryIds is { Count: > 0 })
        {
            clauses.Add("EXISTS (SELECT 1 FROM MediaCategory mc WHERE mc.MediaId = m.Id AND mc.CategoryId IN @CatIds)");
            param.Add("CatIds", f.CategoryIds);
        }
        if (f.DateFrom.HasValue)
        {
            clauses.Add("m.ModifiedAtUtc >= @DateFrom");
            param.Add("DateFrom", f.DateFrom.Value.ToString("O"));
        }
        if (f.DateTo.HasValue)
        {
            clauses.Add("m.ModifiedAtUtc <= @DateTo");
            param.Add("DateTo", f.DateTo.Value.ToString("O"));
        }
        if (f.MinSizeBytes.HasValue)
        {
            clauses.Add("m.SizeBytes >= @MinSize");
            param.Add("MinSize", f.MinSizeBytes.Value);
        }
        if (f.MaxSizeBytes.HasValue)
        {
            clauses.Add("m.SizeBytes <= @MaxSize");
            param.Add("MaxSize", f.MaxSizeBytes.Value);
        }
        if (f.RecentDays.HasValue)
        {
            var since = DateTime.UtcNow.AddDays(-f.RecentDays.Value).ToString("O");
            clauses.Add("m.DateAddedUtc >= @Recent");
            param.Add("Recent", since);
        }

        return (" WHERE " + string.Join(" AND ", clauses), param);
    }

    private static string BuildOrderBy(MediaFilter f)
    {
        var col = f.SortField switch
        {
            SortField.Name => "m.FileName COLLATE NOCASE",
            SortField.ModifiedAt => "m.ModifiedAtUtc",
            SortField.Size => "m.SizeBytes",
            SortField.MediaType => "m.MediaType",
            SortField.Favorite => "m.Favorite",
            SortField.DateAdded => "m.DateAddedUtc",
            _ => "m.DateAddedUtc",
        };
        var dir = f.SortDirection == SortDirection.Ascending ? "ASC" : "DESC";
        return $"{col} {dir}, m.Id DESC";
    }

    /// <summary>FTS5 MATCH 查询转义。</summary>
    private static string FtsEscape(string text)
    {
        // 将每个词用引号包裹，并转义内部引号
        var words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => "\"" + w.Replace("\"", "\"\"") + "\""));
    }

    // ---------- 最近删除 / 撤销 ----------

    public async Task<long> AddDeletedAsync(DeletedMedia item)
    {
        using var conn = _factory.CreateConnection();
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO DeletedItem(OriginalMediaId, RootFolderId, Path, FileName, MediaType, SizeBytes,
                ThumbnailPath, DeletedAtUtc, BatchId, SnapshotJson, TagIdsJson, CategoryIdsJson,
                CollectionIdsJson, IsRestored)
            VALUES(@OriginalMediaId, @RootFolderId, @Path, @FileName, @MediaType, @SizeBytes,
                @ThumbnailPath, @DeletedAtUtc, @BatchId, @SnapshotJson, @TagIdsJson, @CategoryIdsJson,
                @CollectionIdsJson, 0);
            SELECT last_insert_rowid();", item);
        item.Id = id;
        return id;
    }

    public async Task<List<DeletedMedia>> GetRecentlyDeletedAsync(int limit = 200)
    {
        using var conn = _factory.CreateConnection();
        var list = await conn.QueryAsync<DeletedMedia>(@"
            SELECT * FROM DeletedItem
            WHERE IsRestored = 0
            ORDER BY DeletedAtUtc DESC
            LIMIT @Limit;", new { Limit = limit });
        return list.ToList();
    }

    public async Task MarkDeletedRestoredAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE DeletedItem SET IsRestored = 1 WHERE Id = @Id;", new { Id = id });
    }

    public async Task RemoveDeletedAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM DeletedItem WHERE Id = @Id;", new { Id = id });
    }

    public async Task ClearDeletedAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM DeletedItem;");
    }

    public async Task ClearThumbnailPathsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE MediaItem SET ThumbnailPath = NULL;");
    }

    public async Task InvalidateThumbnailAsync(long mediaId)
    {
        using var conn = _factory.CreateConnection();
        // QuickHash 一并清掉：缓存文件名是 {Id}_{QuickHash}.jpg，
        // 只清 ThumbnailPath 会让 EnsureThumbnails 又算回同一个旧文件名，结果还是旧图。
        await conn.ExecuteAsync(
            "UPDATE MediaItem SET ThumbnailPath = NULL, QuickHash = NULL WHERE Id = @mediaId;",
            new { mediaId });
    }

    /// <summary>
    /// 还原媒体项：尽量沿用原 Id（这样标签/分类关联、收藏状态都能恢复）；
    /// 若原 Id 已被新记录占用，则退化为按原路径查找/新插入。
    /// </summary>
    public async Task<long> RestoreMediaAsync(MediaItem item, IEnumerable<long> tagIds,
        IEnumerable<long> categoryIds)
    {
        using var conn = _factory.CreateConnection();

        var byPath = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM MediaItem WHERE Path = @Path;", new { item.Path });
        if (byPath.HasValue)
        {
            await ApplyLinksAsync(conn, byPath.Value, tagIds, categoryIds);
            return byPath.Value;
        }

        var param = new
        {
            item.Id,
            item.RootFolderId,
            item.Path,
            item.FileName,
            item.Extension,
            MediaType = item.MediaType.ToString(),
            item.SizeBytes,
            item.CreatedAtUtc,
            item.ModifiedAtUtc,
            item.Width,
            item.Height,
            item.DurationMs,
            Favorite = item.Favorite ? 1 : 0,
            item.Rating,
            item.Note,
            item.ThumbnailPath,
            item.QuickHash,
            item.DateAddedUtc,
            item.LastScanUtc,
        };

        const string insertWithId = @"
            INSERT INTO MediaItem(Id, RootFolderId, Path, FileName, Extension, MediaType, SizeBytes,
                CreatedAtUtc, ModifiedAtUtc, Width, Height, DurationMs, Favorite, Rating, Note,
                ThumbnailPath, QuickHash, IsMissing, DateAddedUtc, LastScanUtc)
            VALUES(@Id, @RootFolderId, @Path, @FileName, @Extension, @MediaType, @SizeBytes,
                @CreatedAtUtc, @ModifiedAtUtc, @Width, @Height, @DurationMs, @Favorite, @Rating, @Note,
                @ThumbnailPath, @QuickHash, 0, @DateAddedUtc, @LastScanUtc);";

        long newId;
        var idInUse = item.Id > 0
            ? await conn.ExecuteScalarAsync<long?>("SELECT Id FROM MediaItem WHERE Id = @Id;", new { item.Id })
            : null;

        if (item.Id > 0 && !idInUse.HasValue)
        {
            await conn.ExecuteAsync(insertWithId, param);
            newId = item.Id;
        }
        else
        {
            newId = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO MediaItem(RootFolderId, Path, FileName, Extension, MediaType, SizeBytes,
                    CreatedAtUtc, ModifiedAtUtc, Width, Height, DurationMs, Favorite, Rating, Note,
                    ThumbnailPath, QuickHash, IsMissing, DateAddedUtc, LastScanUtc)
                VALUES(@RootFolderId, @Path, @FileName, @Extension, @MediaType, @SizeBytes,
                    @CreatedAtUtc, @ModifiedAtUtc, @Width, @Height, @DurationMs, @Favorite, @Rating, @Note,
                    @ThumbnailPath, @QuickHash, 0, @DateAddedUtc, @LastScanUtc);
                SELECT last_insert_rowid();", param);
        }

        await ApplyLinksAsync(conn, newId, tagIds, categoryIds);
        return newId;
    }

    private static async Task ApplyLinksAsync(Microsoft.Data.Sqlite.SqliteConnection conn, long mediaId,
        IEnumerable<long> tagIds, IEnumerable<long> categoryIds)
    {
        foreach (var tagId in tagIds.Distinct())
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO MediaTag(MediaId, TagId) VALUES(@MediaId, @TagId);",
                new { MediaId = mediaId, TagId = tagId });
        foreach (var categoryId in categoryIds.Distinct())
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO MediaCategory(MediaId, CategoryId) VALUES(@MediaId, @CategoryId);",
                new { MediaId = mediaId, CategoryId = categoryId });
    }
}
