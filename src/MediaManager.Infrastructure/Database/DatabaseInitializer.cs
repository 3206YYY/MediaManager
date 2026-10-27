using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>数据库初始化：建表 + PRAGMA 配置 + 一次性迁移。</summary>
public class DatabaseInitializer
{
    /// <summary>v1.3.2「收藏夹并入分类」迁移在 SchemaMigration 里的名字。</summary>
    public const string CollectionMergeMigration = "v1.3.2_CollectionToCategory";

    /// <summary>v1.3.3「清理误入库的系统垃圾文件」迁移在 SchemaMigration 里的名字。</summary>
    public const string JunkMediaPurgeMigration = "v1.3.3_JunkMediaPurge";

    /// <summary>v1.3.3「构建文件名/路径 trigram 搜索索引」迁移在 SchemaMigration 里的名字。</summary>
    public const string TrigramFtsMigration = "v1.3.3_TrigramFts";

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
              LastScanUtc   TEXT,
              -- v1.3.4 私密空间：1 = 已移入保险箱（文件在 Data/Vault 下），
              -- OriginalPath 记录移入前的绝对路径，还原时原样挪回去
              IsPrivate     INTEGER NOT NULL DEFAULT 0,
              OriginalPath  TEXT
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

        // —— 以下是 v1.3.1 及更早版本的「收藏夹」旧表 ——
        // v1.3.2 起收藏夹已并入「分类」，程序里不再有任何读写路径。
        // 这里仍然 CREATE TABLE IF NOT EXISTS：① 老库的数据要留给下面的迁移读；
        // ② 新库建出空表，迁移逻辑可以无分支地跑（0 行 = 直接标记完成）。
        // 数据搬完、映射记进 CollectionMigrationMap 之后，这两张表就只剩归档价值，可以随时手工删掉。
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

        // 一次性迁移的执行台账：同一个迁移无论启动多少次都只跑一遍。
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS SchemaMigration (
              Name       TEXT PRIMARY KEY,
              AppliedUtc TEXT NOT NULL
            );
            """);

        // 旧收藏夹 id → 迁移后的分类 id。
        // 「最近删除」里 v1.3.2 之前留下的记录还存着收藏夹 id，还原时靠这张表把归属续上。
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS CollectionMigrationMap (
              CollectionId INTEGER PRIMARY KEY,
              CategoryId   INTEGER NOT NULL,
              MigratedUtc  TEXT    NOT NULL
            );
            """);

        // 旧库迁移：给 MediaItem 补 Note 列
        MigrateColumn(cmd, "MediaItem", "Note", "TEXT");
        // v1.3.4 私密空间：IsPrivate 标记 + 移入前的原路径（还原时用）
        MigrateColumn(cmd, "MediaItem", "IsPrivate", "INTEGER NOT NULL DEFAULT 0");
        MigrateColumn(cmd, "MediaItem", "OriginalPath", "TEXT");

        // 索引必须建在 MigrateColumn **之后**：
        // 老库的表早就存在，CREATE TABLE IF NOT EXISTS 不会补列，
        // 紧跟建表语句建索引会在旧库上直接报 "no such column: IsPrivate"，
        // 整个初始化随之失败（实测踩过）。
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_Private    ON MediaItem(IsPrivate);");

        // 私密空间的分区（v1.4.0 二轮）：私密内容自己的一套分区，
        // 故意不复用 Category —— 复用的话主界面侧栏会露出「私密分区」名字和数量，
        // 等于在门口贴清单。分区被删时只把文件变回「未分区」，绝不碰文件本身。
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS VaultSection (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              Name         TEXT NOT NULL,
              SortOrder    INTEGER NOT NULL DEFAULT 0,
              CreatedAtUtc TEXT NOT NULL
            );
            """);
        MigrateColumn(cmd, "MediaItem", "VaultSectionId", "INTEGER");
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Media_VaultSection ON MediaItem(VaultSectionId);");
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

        // 日记（v1.3.4 从零加入，不是改造）：一天可以写多条，每条能挂多个图片/视频
        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS DiaryEntry (
              Id           INTEGER PRIMARY KEY AUTOINCREMENT,
              -- 归属日期用 yyyy-MM-dd 字符串存，不做 UTC 换算：
              -- 用户写的「3 月 5 日」就该永远显示成 3 月 5 日，跨时区/夏令时都不该漂移
              EntryDate    TEXT    NOT NULL,
              Title        TEXT,
              Content      TEXT,
              Mood         TEXT,
              CreatedAtUtc TEXT    NOT NULL,
              UpdatedAtUtc TEXT    NOT NULL
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_Diary_Date ON DiaryEntry(EntryDate);");

        Execute(cmd, """
            CREATE TABLE IF NOT EXISTS DiaryMedia (
              DiaryId   INTEGER NOT NULL REFERENCES DiaryEntry(Id) ON DELETE CASCADE,
              MediaId   INTEGER NOT NULL REFERENCES MediaItem(Id) ON DELETE CASCADE,
              SortOrder INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (DiaryId, MediaId)
            );
            """);
        Execute(cmd, "CREATE INDEX IF NOT EXISTS IX_DiaryMedia_Media ON DiaryMedia(MediaId);");

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

        // FTS5 trigram：文件名/路径的「子串」搜索走索引（需要 SQLite 3.34+）。
        // 不支持时（老引擎 / 精简构建）整段跳过，BuildWhere 会自动退回 instr 全表扫描，功能不受影响。
        try
        {
            Execute(cmd, """
                CREATE VIRTUAL TABLE IF NOT EXISTS MediaFtsTrigram USING fts5(
                  FileName, Path,
                  content='MediaItem', content_rowid='Id',
                  tokenize='trigram'
                );
                """);
            Execute(cmd, """
                CREATE TRIGGER IF NOT EXISTS MediaItem_AI_Tri AFTER INSERT ON MediaItem BEGIN
                  INSERT INTO MediaFtsTrigram(rowid, FileName, Path) VALUES (new.Id, new.FileName, new.Path);
                END;
                """);
            Execute(cmd, """
                CREATE TRIGGER IF NOT EXISTS MediaItem_AD_Tri AFTER DELETE ON MediaItem BEGIN
                  INSERT INTO MediaFtsTrigram(MediaFtsTrigram, rowid, FileName, Path) VALUES ('delete', old.Id, old.FileName, old.Path);
                END;
                """);
            Execute(cmd, """
                CREATE TRIGGER IF NOT EXISTS MediaItem_AU_Tri AFTER UPDATE ON MediaItem
                WHEN old.FileName <> new.FileName OR old.Path <> new.Path
                BEGIN
                  INSERT INTO MediaFtsTrigram(MediaFtsTrigram, rowid, FileName, Path) VALUES ('delete', old.Id, old.FileName, old.Path);
                  INSERT INTO MediaFtsTrigram(rowid, FileName, Path) VALUES (new.Id, new.FileName, new.Path);
                END;
                """);
            SqliteFeatures.TrigramFts = true;
        }
        catch (Exception ex)
        {
            SqliteFeatures.TrigramFts = false;
            _logger.LogWarning(ex, "FTS5 trigram 不可用，子串搜索将退回全表扫描");
        }

        // v1.3.2 一次性迁移：旧「收藏夹」→「分类」（必须在 FTS 表建好之后跑，尾部要同步搜索索引）
        MigrateCollectionsIntoCategories(conn);

        // v1.3.3 一次性迁移：清掉历史扫描误收录的 desktop.ini / Thumbs.db 等系统文件
        PurgeJunkMedia(conn);

        // v1.3.3 一次性迁移：把已有媒体灌进 trigram 搜索索引
        BuildTrigramFts(conn);

        _logger.LogInformation("数据库初始化完成：{Path}", _factory.DatabasePath);
    }

    /// <summary>
    /// v1.3.2 一次性迁移：把旧「收藏夹（Collection）」整体并入「分类（Category）」。
    /// <para>
    /// 规则：每个旧收藏夹变成一个<b>同名顶级分类</b>；已经有同名顶级分类就直接复用，不建重复的。
    /// 媒体归属从 MediaCollection 搬到 MediaCategory（INSERT OR IGNORE，纯增量，不动已有分类）。
    /// </para>
    /// <para>
    /// 安全性：整个过程在一个事务里，失败全回滚，下次启动重跑；成功后在 SchemaMigration 记一笔，
    /// 之后每次启动只多一次 COUNT 查询。旧表一行都不删——用户的原始数据永远还在。
    /// </para>
    /// </summary>
    private void MigrateCollectionsIntoCategories(SqliteConnection conn)
    {
        // 1. 台账检查：跑过了就直接返回（幂等）
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM SchemaMigration WHERE Name = @Name;";
            check.Parameters.AddWithValue("@Name", CollectionMergeMigration);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0)
                return;
        }

        var legacy = new List<(long Id, string Name, long SortOrder, string CreatedAt)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT Id, Name, SortOrder, CreatedAtUtc FROM Collection ORDER BY SortOrder, Id;";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                legacy.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0L : r.GetInt64(2),
                    r.IsDBNull(3) ? DateTime.UtcNow.ToString("O") : r.GetString(3)));
            }
        }

        if (legacy.Count == 0)
        {
            // 新库或本来就没用过收藏夹：什么都不用搬，只记台账
            using var empty = conn.CreateCommand();
            empty.CommandText = "INSERT OR REPLACE INTO SchemaMigration(Name, AppliedUtc) VALUES(@Name, @Now);";
            empty.Parameters.AddWithValue("@Name", CollectionMergeMigration);
            empty.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            empty.ExecuteNonQuery();
            return;
        }

        var now = DateTime.UtcNow.ToString("O");
        using var tx = conn.BeginTransaction();
        try
        {
            // 新分类排到现有顶级分类之后，保持原收藏夹的相对顺序
            long nextSort;
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Category WHERE ParentId IS NULL;";
                nextSort = Convert.ToInt64(q.ExecuteScalar() ?? 0L);
            }

            int createdCategories = 0, createdLinks = 0;
            using var work = conn.CreateCommand();
            work.Transaction = tx;

            foreach (var col in legacy)
            {
                var name = col.Name.Trim();
                if (name.Length == 0) name = "未命名收藏夹 " + col.Id;

                // 2. 已有同名顶级分类？复用。不区分大小写，避免「旅行」和「旅行 」拆成两个
                long categoryId;
                work.Parameters.Clear();
                work.CommandText = "SELECT Id FROM Category WHERE ParentId IS NULL AND Name = @Name COLLATE NOCASE LIMIT 1;";
                work.Parameters.AddWithValue("@Name", name);
                var existing = work.ExecuteScalar();
                if (existing is not null and not DBNull)
                {
                    categoryId = Convert.ToInt64(existing);
                }
                else
                {
                    // 分两步：INSERT 和 last_insert_rowid() 分开执行。
                    // 多语句 ExecuteScalar 在不同 provider 下取哪条语句的结果集并不一致，这里不赌。
                    work.CommandText = """
                        INSERT INTO Category(Name, ParentId, SortOrder, CreatedAtUtc)
                        VALUES(@Name, NULL, @SortOrder, @CreatedAt);
                        """;
                    work.Parameters.Clear();
                    work.Parameters.AddWithValue("@Name", name);
                    work.Parameters.AddWithValue("@SortOrder", nextSort++);
                    work.Parameters.AddWithValue("@CreatedAt", col.CreatedAt);
                    work.ExecuteNonQuery();

                    work.CommandText = "SELECT last_insert_rowid();";
                    work.Parameters.Clear();
                    categoryId = Convert.ToInt64(work.ExecuteScalar() ?? 0L);
                    createdCategories++;
                }

                // 3. 媒体归属搬家（重复的忽略，一条不丢也不重）
                //    JOIN MediaItem 不能省：MediaCategory 对 MediaItem 有外键，而 SQLite 的
                //    OR IGNORE 只忽略唯一约束、**不忽略外键冲突**。旧库里万一有指向已删媒体的
                //    残留行（历史版本关过 foreign_keys），整批 INSERT 会抛错 → 整个迁移回滚 →
                //    以后每次启动都失败。这里直接把这些孤儿行滤掉。
                work.CommandText = """
                    INSERT OR IGNORE INTO MediaCategory(MediaId, CategoryId)
                    SELECT mc.MediaId, @CategoryId
                    FROM MediaCollection mc
                    JOIN MediaItem m ON m.Id = mc.MediaId
                    WHERE mc.CollectionId = @CollectionId;
                    """;
                work.Parameters.Clear();
                work.Parameters.AddWithValue("@CategoryId", categoryId);
                work.Parameters.AddWithValue("@CollectionId", col.Id);
                createdLinks += work.ExecuteNonQuery();

                // 4. 记下 id 对应关系，供「最近删除」还原旧记录用
                work.CommandText = """
                    INSERT INTO CollectionMigrationMap(CollectionId, CategoryId, MigratedUtc)
                    VALUES(@CollectionId, @CategoryId, @Now)
                    ON CONFLICT(CollectionId) DO UPDATE SET CategoryId=@CategoryId, MigratedUtc=@Now;
                    """;
                work.Parameters.Clear();
                work.Parameters.AddWithValue("@CollectionId", col.Id);
                work.Parameters.AddWithValue("@CategoryId", categoryId);
                work.Parameters.AddWithValue("@Now", now);
                work.ExecuteNonQuery();
            }

            // 5. 搜索索引里的「分类」列要跟上，否则按新分类名搜不到这些媒体。
            //    注意：MediaFts 是 external content 表（content='MediaItem'），Tags/Categories
            //    这两列在 MediaItem 里并不存在，所以 UPDATE / rebuild 都会报
            //    "no such column: T.Tags"。唯一可靠的办法是 delete-all 之后按 MediaItem 重新灌一遍。
            work.Parameters.Clear();
            work.CommandText = "INSERT INTO MediaFts(MediaFts) VALUES('delete-all');";
            work.ExecuteNonQuery();

            work.CommandText = """
                INSERT INTO MediaFts(rowid, FileName, Path, Tags, Categories)
                SELECT m.Id, m.FileName, m.Path,
                  (SELECT group_concat(t.Name, ' ') FROM MediaTag mt JOIN Tag t ON t.Id = mt.TagId WHERE mt.MediaId = m.Id),
                  (SELECT group_concat(c.Name, ' ') FROM MediaCategory mc JOIN Category c ON c.Id = mc.CategoryId WHERE mc.MediaId = m.Id)
                FROM MediaItem m;
                """;
            work.ExecuteNonQuery();

            // 6. 落台账
            work.CommandText = "INSERT OR REPLACE INTO SchemaMigration(Name, AppliedUtc) VALUES(@Name, @Now);";
            work.Parameters.Clear();
            work.Parameters.AddWithValue("@Name", CollectionMergeMigration);
            work.Parameters.AddWithValue("@Now", now);
            work.ExecuteNonQuery();

            tx.Commit();

            _logger.LogInformation(
                "收藏夹已并入分类：{Collections} 个收藏夹 → 新建 {Created} 个分类（{Reused} 个同名复用），迁移 {Links} 条媒体归属",
                legacy.Count, createdCategories, legacy.Count - createdCategories, createdLinks);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            // 刻意不往上抛：迁移失败就保持原样（旧表一行没动，数据不会丢），
            // 只让用户暂时看不到旧收藏夹，也不该让程序打不开。下次启动会自动重试。
            _logger.LogError(ex, "收藏夹并入分类失败，已回滚；旧表数据完好，下次启动会重试");
        }
    }

    /// <summary>
    /// v1.3.3 一次性迁移：清掉历史版本误收录的系统垃圾文件。
    /// <para>
    /// 老版本的扫描器不过滤隐藏/系统属性，且把 <c>.ini</c> 当文本文档，于是每个目录里的
    /// <c>desktop.ini</c>（以及 <c>Thumbs.db</c> / <c>.DS_Store</c>）都被扫进了图库。
    /// 只按<b>垃圾文件名</b>删除，不按扩展名一刀切：用户自己导入的 .ini 记录保留
    /// （文件在硬盘上，仍可查看），避免清理垃圾时误伤正常数据。
    /// 这里只删数据库记录，<b>不动硬盘上的文件</b>；删记录会触发 FTS 的 AFTER DELETE 触发器，
    /// 搜索索引自动同步，不需要额外 rebuild。
    /// </para>
    /// <para>幂等：跑过一次就在 SchemaMigration 记一笔，之后每次启动只多一次 COUNT。</para>
    /// </summary>
    private void PurgeJunkMedia(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigration WHERE Name = @Name;";
        check.Parameters.AddWithValue("@Name", JunkMediaPurgeMigration);
        if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;

        // 与 MediaFormat.SystemJunkNames 保持一致（这里不引用 Infrastructure.FileSystem，
        // 数据库层不该依赖文件系统层；以后加名字记得两边都改）。
        const string junkNames = """
            'desktop.ini','thumbs.db','ehthumbs.db','ehthumbs_vista.db',
            '.ds_store','.localized','.directory','albumthumbs.ini','.picasa.ini'
            """;

        try
        {
            using var tx = conn.BeginTransaction();

            using var count = conn.CreateCommand();
            count.Transaction = tx;
            count.CommandText = $"""
                SELECT COUNT(*) FROM MediaItem
                WHERE lower(FileName) IN ({junkNames});
                """;
            var found = Convert.ToInt64(count.ExecuteScalar() ?? 0L);

            if (found > 0)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"""
                    DELETE FROM MediaItem
                    WHERE lower(FileName) IN ({junkNames});
                    """;
                var deleted = del.ExecuteNonQuery();
                _logger.LogInformation("已从图库移除 {Count} 条系统垃圾文件记录（desktop.ini 等）", deleted);
            }

            using var mark = conn.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "INSERT OR REPLACE INTO SchemaMigration(Name, AppliedUtc) VALUES(@Name, @Now);";
            mark.Parameters.AddWithValue("@Name", JunkMediaPurgeMigration);
            mark.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();

            tx.Commit();
        }
        catch (Exception ex)
        {
            // 清理失败不影响启动：下次启动会重试，图库里最多多几条垃圾记录
            _logger.LogWarning(ex, "清理系统垃圾文件记录失败，下次启动会重试");
        }
    }

    /// <summary>
    /// v1.3.3 一次性迁移：把已有媒体灌进 trigram 索引。
    /// 新库为空，只记台账；旧库首次升级时批量补索引（一个事务）。
    /// </summary>
    private void BuildTrigramFts(SqliteConnection conn)
    {
        if (!SqliteFeatures.TrigramFts) return;

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigration WHERE Name = @Name;";
        check.Parameters.AddWithValue("@Name", TrigramFtsMigration);
        if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;

        try
        {
            using var tx = conn.BeginTransaction();

            // external content 表的 delete-all 只清索引，不动 MediaItem
            using var clear = conn.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = "INSERT INTO MediaFtsTrigram(MediaFtsTrigram) VALUES('delete-all');";
            clear.ExecuteNonQuery();

            using var bulk = conn.CreateCommand();
            bulk.Transaction = tx;
            bulk.CommandText = "INSERT INTO MediaFtsTrigram(rowid, FileName, Path) SELECT Id, FileName, Path FROM MediaItem;";
            bulk.ExecuteNonQuery();

            using var mark = conn.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "INSERT OR REPLACE INTO SchemaMigration(Name, AppliedUtc) VALUES(@Name, @Now);";
            mark.Parameters.AddWithValue("@Name", TrigramFtsMigration);
            mark.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();

            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "构建 trigram 搜索索引失败，下次启动会重试");
        }
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
