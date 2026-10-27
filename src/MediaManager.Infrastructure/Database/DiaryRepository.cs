using Dapper;
using MediaManager.Core.Models;
using MediaManager.Core.Services;

namespace MediaManager.Infrastructure.Database;

/// <summary>日记的数据访问（Dapper + SQLite）。</summary>
public sealed class DiaryRepository : IDiaryRepository
{
    private readonly IDbConnectionFactory _factory;

    public DiaryRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<List<DiaryEntry>> GetEntriesAsync(string fromDate, string toDate)
    {
        using var conn = _factory.CreateConnection();

        var entries = (await conn.QueryAsync<DiaryEntry>(@"
            SELECT * FROM DiaryEntry
            WHERE EntryDate >= @From AND EntryDate <= @To
            ORDER BY EntryDate ASC, CreatedAtUtc ASC, Id ASC;",
            new { From = fromDate, To = toDate }).ConfigureAwait(false)).ToList();

        if (entries.Count == 0) return entries;

        await AttachMediaAsync(conn, entries).ConfigureAwait(false);
        return entries;
    }

    public async Task<DiaryEntry?> GetEntryAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        var entry = await conn.QueryFirstOrDefaultAsync<DiaryEntry>(
            "SELECT * FROM DiaryEntry WHERE Id = @Id;", new { Id = id }).ConfigureAwait(false);
        if (entry == null) return null;

        await AttachMediaAsync(conn, new List<DiaryEntry> { entry }).ConfigureAwait(false);
        return entry;
    }

    public async Task<long> SaveEntryAsync(DiaryEntry entry)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var now = DateTime.UtcNow.ToString("O");
        entry.UpdatedAtUtc = now;

        long id;
        if (entry.Id > 0)
        {
            id = entry.Id;
            await conn.ExecuteAsync(@"
                UPDATE DiaryEntry SET EntryDate = @EntryDate, Title = @Title,
                    Content = @Content, Mood = @Mood, UpdatedAtUtc = @UpdatedAtUtc
                WHERE Id = @Id;",
                new { entry.Id, entry.EntryDate, entry.Title, entry.Content, entry.Mood, entry.UpdatedAtUtc },
                tx).ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrEmpty(entry.CreatedAtUtc)) entry.CreatedAtUtc = now;
            id = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO DiaryEntry(EntryDate, Title, Content, Mood, CreatedAtUtc, UpdatedAtUtc)
                VALUES(@EntryDate, @Title, @Content, @Mood, @CreatedAtUtc, @UpdatedAtUtc);
                SELECT last_insert_rowid();",
                new
                {
                    entry.EntryDate, entry.Title, entry.Content, entry.Mood,
                    entry.CreatedAtUtc, entry.UpdatedAtUtc,
                }, tx).ConfigureAwait(false);
            entry.Id = id;
        }

        // 关联媒体整体重写：顺序就是用户在编辑框里拖出来的顺序
        await conn.ExecuteAsync("DELETE FROM DiaryMedia WHERE DiaryId = @Id;", new { Id = id }, tx)
            .ConfigureAwait(false);

        var order = 0;
        foreach (var media in entry.Media)
        {
            await conn.ExecuteAsync(@"
                INSERT OR IGNORE INTO DiaryMedia(DiaryId, MediaId, SortOrder) VALUES(@DiaryId, @MediaId, @SortOrder);",
                new { DiaryId = id, MediaId = media.Id, SortOrder = order++ }, tx).ConfigureAwait(false);
        }

        tx.Commit();
        return id;
    }

    public async Task DeleteEntryAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        // DiaryMedia 由外键 ON DELETE CASCADE 一起删；媒体文件本身不动
        await conn.ExecuteAsync("DELETE FROM DiaryEntry WHERE Id = @Id;", new { Id = id }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, int>> GetDateCountsAsync(string fromDate, string toDate)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<(string Date, int Count)>(@"
            SELECT EntryDate, COUNT(*) FROM DiaryEntry
            WHERE EntryDate >= @From AND EntryDate <= @To
            GROUP BY EntryDate;",
            new { From = fromDate, To = toDate }).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Date, r => r.Count);
    }

    public async Task<long> GetEntryCountAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM DiaryEntry;").ConfigureAwait(false);
    }

    public async Task MoveEntryAsync(long id, string newDate)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE DiaryEntry SET EntryDate = @Date, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = id, Date = newDate, Now = DateTime.UtcNow.ToString("O") }).ConfigureAwait(false);
    }

    // ---------- 内部 ----------

    private sealed class DiaryMediaLink
    {
        public long DiaryId { get; set; }
        public long MediaId { get; set; }
    }

    /// <summary>
    /// 把媒体挂到各自的日记上。
    ///
    /// 分两次查而不是 JOIN：JOIN 出来的行里 DiaryId 和 MediaItem 的列混在一起，
    /// Dapper 的 splitOn 在这种「前缀列 + 全表列」的组合上很容易出错，
    /// 而日记一个月最多几十条，多一次查询的代价可以忽略。
    /// </summary>
    private static async Task AttachMediaAsync(Microsoft.Data.Sqlite.SqliteConnection conn,
        List<DiaryEntry> entries)
    {
        var ids = entries.Select(e => e.Id).ToList();

        var links = (await conn.QueryAsync<DiaryMediaLink>(@"
            SELECT DiaryId, MediaId FROM DiaryMedia
            WHERE DiaryId IN @Ids
            ORDER BY DiaryId, SortOrder, MediaId;",
            new { Ids = ids }).ConfigureAwait(false)).ToList();

        if (links.Count == 0) return;

        var mediaIds = links.Select(l => l.MediaId).Distinct().ToList();
        var media = (await conn.QueryAsync<MediaItem>(
            "SELECT * FROM MediaItem WHERE Id IN @Ids AND IsMissing = 0;",
            new { Ids = mediaIds }).ConfigureAwait(false)).ToDictionary(m => m.Id);

        var byDiary = links
            .GroupBy(l => l.DiaryId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.MediaId).ToList());

        foreach (var entry in entries)
        {
            if (!byDiary.TryGetValue(entry.Id, out var ordered)) continue;

            foreach (var mediaId in ordered)
            {
                if (media.TryGetValue(mediaId, out var item))
                    entry.Media.Add(item);
            }
        }
    }
}
