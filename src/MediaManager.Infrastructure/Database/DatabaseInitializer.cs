using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>数据库初始化：建表 + PRAGMA 配置。</summary>
public class DatabaseInitializer
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbConnectionFactory factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public void Initialize()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        // PRAGMA 配置
        Execute(cmd, "PRAGMA journal_mode = WAL;");
        Execute(cmd, "PRAGMA synchronous = NORMAL;");
        Execute(cmd, "PRAGMA foreign_keys = ON;");
        Execute(cmd, "PRAGMA busy_timeout = 5000;");

        // 建表
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS RootFolder (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              Path         TEXT    NOT NULL UNIQUE,
              VolumeSerial TEXT,
              RelativePath TEXT,
              IsEnabled    INTEGER NOT NULL DEFAULT 1,
              LastScanUtc  TEXT,
              AddedAtUtc   TEXT    NOT NULL
            );
            """);

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS MediaItem (
              Id            INTEGER PRIMARY KEY AUTOINCREMENT,
              RootFolderId  INTEGER NOT NULL REFERENCES RootFolder(Id) ON DELETE CASCADE,
              Path          TEXT    NOT NULL UNIQUE,
              FileName      TEXT    NOT NULL,
              Extension     TEXT    NOT NULL,
              MediaType     TEXT    NOT NULL,
              SizeBytes     INTEGER NOT NULL,
              CreatedAtUtc  TEXT,
              ModifiedAtUtc TEXT,
              Width         INTEGER,
              Height        INTEGER,
              DurationMs    INTEGER,
              Favorite      INTEGER NOT NULL DEFAULT 0,
              Rating        INTEGER NOT NULL DEFAULT 0,
              Note          TEXT,
              ThumbnailPath TEXT,
              QuickHash     TEXT,
              IsMissing     INTEGER NOT NULL DEFAULT 0,
              DateAddedUtc  TEXT NOT NULL,
              LastScanUtc   TEXT
            );
            """);

        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Path       ON MediaItem(Path);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Type       ON MediaItem(MediaType);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Favorite   ON MediaItem(Favorite);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Modified   ON MediaItem(ModifiedAtUtc);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Size       ON MediaItem(SizeBytes);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Root       ON MediaItem(RootFolderId);");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_DateAdded  ON MediaItem(DateAddedUtc);");

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS Tag (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              Name         TEXT NOT NULL UNIQUE,
              Color        TEXT,
              CreatedAtUtc TEXT NOT NULL
            );
            """);

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS MediaTag (
              MediaId INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
              TagId   INTEGER NOT NULL REFERENCES Tag(Id)       ON DELETE CASCADE,
              PRIMARY KEY (MediaId, TagId)
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_MediaTag_Tag ON MediaTag(TagId);");

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS Category (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              Name         TEXT NOT NULL,
              ParentId     INTEGER REFERENCES Category(Id) ON DELETE CASCADE,
              SortOrder    INTEGER NOT NULL DEFAULT 0,
              CreatedAtUtc TEXT NOT NULL
            );
            """);

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS MediaCategory (
              MediaId    INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
              CategoryId INTEGER NOT NULL REFERENCES Category(Id)  ON DELETE CASCADE,
              PRIMARY KEY (MediaId, CategoryId)
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_MediaCategory_Cat ON MediaCategory(CategoryId);");

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS Collection (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              Name         TEXT NOT NULL,
              Color        TEXT,
              SortOrder    INTEGER NOT NULL DEFAULT 0,
              CreatedAtUtc TEXT NOT NULL
            );
            """);

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS MediaCollection (
              MediaId      INTEGER NOT NULL REFERENCES MediaItem(Id)  ON DELETE CASCADE,
              CollectionId INTEGER NOT NULL REFERENCES Collection(Id) ON DELETE CASCADE,
              PRIMARY KEY (MediaId, CollectionId)
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_MediaCollection_Col ON MediaCollection(CollectionId);");

        // 旧库迁移：给 MediaItem 补 Note 列
        MigrateColumn(cmd, "MediaItem", "Note", "TEXT");
        // 旧库迁移：给 Tag 补 SortOrder 列（拖动排序）
        MigrateColumn(cmd, "Tag", "SortOrder", "INTEGER NOT NULL DEFAULT 0");

        // 旧库迁移：MediaType 历史数据曾被 Dapper 序列化成 "0"/"1"，统一改为 "Image"/"Video"
        // （幂等：迁移后无 "0"/"1"，每次启动匹配 0 行，几乎零成本）
        Execute(cmd, "UPDATE MediaItem SET MediaType='Image' WHERE MediaType='0';");
        Execute(cmd, "UPDATE MediaItem SET MediaType='Video' WHERE MediaType='1';");

        // 发现页：每日随机推荐批次
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS DailyDiscovery (
              Date     TEXT PRIMARY KEY,
              MediaIds TEXT NOT NULL,
              PoolType TEXT NOT NULL,
              PoolId   INTEGER
            );
            """);

        // 旧库迁移：给 DailyDiscovery 补 TypeFilter 列（记录生成批次时的类型筛选键）
        MigrateColumn(cmd, "DailyDiscovery", "TypeFilter", "TEXT");

        // 发现页：推送历史（用于最近 7 天排除）
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS DiscoveryHistory (
              MediaId     INTEGER NOT NULL,
              PushedAtUtc TEXT NOT NULL,
              PRIMARY KEY (MediaId, PushedAtUtc)
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_DiscoveryHistory_Media ON DiscoveryHistory(MediaId);");

        // 最近查看（记录最近 100 张打开过的媒体）
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS RecentView (
              MediaId     INTEGER NOT NULL PRIMARY KEY,
              ViewedAtUtc TEXT NOT NULL
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_RecentView_ViewedAt ON RecentView(ViewedAtUtc);");

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS AppSetting (
              Key   TEXT PRIMARY KEY,
              Value TEXT
            );
            """);

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS OperationLog (
              Id         INTEGER PRIMARY KEY AUTOINCREMENT,
              Action     TEXT NOT NULL,
              Source     TEXT,
              Target     TEXT,
              Detail     TEXT,
              CreatedUtc TEXT NOT NULL
            );
            """);

        // 最近删除（软记录）：删除时把文件快照留在这里，
        // 用于「5 秒撤销」和「最近删除」页面把文件从回收站还原回来。
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS DeletedItem (
              Id             INTEGER PRIMARY KEY AUTOINCREMENT,
              OriginalMediaId INTEGER NOT NULL DEFAULT 0,
              RootFolderId   INTEGER NOT NULL DEFAULT 0,
              Path           TEXT    NOT NULL,
              FileName       TEXT    NOT NULL,
              MediaType      TEXT    NOT NULL DEFAULT 'Image',
              SizeBytes      INTEGER NOT NULL DEFAULT 0,
              ThumbnailPath  TEXT,
              DeletedAtUtc   TEXT    NOT NULL,
              BatchId        TEXT,
              SnapshotJson   TEXT,
              TagIdsJson     TEXT,
              CategoryIdsJson TEXT,
              CollectionIdsJson TEXT,
              IsRestored     INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_DeletedItem_DeletedAt ON DeletedItem(DeletedAtUtc DESC);");

        // FTS5 表
        Execute(cmd, """
            CREATE VIRTUAL TABLE IF NOT EXISTS MediaFts USING fts5(
              FileName, Path, Tags, Categories,
              content='MediaItem', content_rowid='Id',
              tokenize='unicode61'
            );
            """);

        // FTS 触发器
        Execute(cmd, """
            CREATE TRIGGER IF NOT EXISTS MediaItem_AI AFTER INSERT ON MediaItem BEGIN
              INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
              VALUES (new.Id, new.FileName, new.Path, '', '');
            END;
            """);
        Execute(cmd, """
            CREATE TRIGGER IF NOT EXISTS MediaItem_AD AFTER DELETE ON MediaItem BEGIN
              INSERT INTO MediaFts(MediaFts, rowid, FileName, Path, Tags, Categories)
              VALUES ('delete', old.Id, old.FileName, old.Path, '', '');
            END;
            """);
        Execute(cmd, """
            CREATE TRIGGER IF NOT EXISTS MediaItem_AU AFTER UPDATE ON MediaItem BEGIN
              INSERT INTO MediaFts(MediaFts, rowid, FileName, Path, Tags, Categories)
              VALUES ('delete', old.Id, old.FileName, old.Path, '', '');
              INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
              VALUES (new.Id, new.FileName, new.Path, '', '');
            END;
            """);

        _logger.LogInformation("数据库初始化完成：{Path}", _factory.DatabasePath);
    }

    private static void Execute(SqliteCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>旧库迁移：若列不存在则 ALTER TABLE 添加。</summary>
    private static void MigrateColumn(SqliteCommand cmd, string table, string column, string type)
    {
        var columns = new List<string>();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        if (columns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)))
            return; // 已存在

        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        cmd.ExecuteNonQuery();
    }
}
