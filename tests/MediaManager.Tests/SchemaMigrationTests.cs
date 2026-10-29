using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.Portable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests;

/// <summary>
/// 老库升级（v1.3.3 → 当前）。
///
/// 为什么必须有这组测试：初始化器里「建表」和「补列」是两段代码。老库跑到
/// <c>CREATE TABLE IF NOT EXISTS MediaItem</c> 时表早已存在，**一个列都不会补**；
/// 之后任何引用新列的语句都会 <c>no such column</c>，把整个初始化炸掉 ——
/// 表现就是旧版用户升级后程序起不来。
///
/// 这条路径只有从旧版真机升级才会走到，开发机上永远是新建库，所以特别容易漏。
/// v1.4.2 就差点踩进去：给主列表加的复合索引引用了 IsPrivate，
/// 而 IsPrivate 是 v1.3.4 才给老库 ALTER 补上的列。
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public SchemaMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MediaManagerSchemaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // WAL/SHM 偶尔晚一步释放，测试目录留在临时区也无妨
        }
    }

    private (DatabaseInitializer Init, string DbPath) Build()
    {
        var detector = new PortableModeDetector(NullLogger<PortableModeDetector>.Instance, _tempDir);
        var factory = new SqliteConnectionFactory(detector, NullLogger<SqliteConnectionFactory>.Instance);
        return (new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance), factory.DatabasePath);
    }

    /// <summary>造一个 v1.3.3 的老库：MediaItem 只有当年的列，没有 IsPrivate / OriginalPath / VaultSectionId。</summary>
    private static void CreateLegacyDb(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE RootFolder (
              Id          INTEGER PRIMARY KEY AUTOINCREMENT,
              Path        TEXT    NOT NULL UNIQUE,
              Name        TEXT    NOT NULL,
              IsEnabled   INTEGER NOT NULL DEFAULT 1,
              LastScanUtc TEXT,
              AddedAtUtc  TEXT    NOT NULL
            );
            CREATE TABLE MediaItem (
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
              DateAddedUtc  TEXT    NOT NULL,
              LastScanUtc   TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void LegacyDb_MissingIsPrivate_UpgradesWithoutThrowing()
    {
        var (init, dbPath) = Build();
        CreateLegacyDb(dbPath);

        // 老库升级：这一步不能抛。抛了 = 旧版用户升级后打不开程序。
        init.Initialize();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var columns = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(MediaItem);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }

        Assert.Contains("IsPrivate", columns);
        Assert.Contains("OriginalPath", columns);
        Assert.Contains("VaultSectionId", columns);

        var indexes = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='MediaItem';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(0));
        }

        // v1.4.2 的主列表复合索引，必须建在补列之后才建得出来
        Assert.Contains("IX_Media_Feed", indexes);
        Assert.Contains("IX_Media_NameFeed", indexes);
        Assert.Contains("IX_Media_Active", indexes);
        Assert.Contains("IX_Media_RootActive", indexes);
    }

    /// <summary>升级要幂等：同一台机器反复启动不能报错，也不能把索引重复建。</summary>
    [Fact]
    public void LegacyDb_UpgradeTwice_IsIdempotent()
    {
        var (init, dbPath) = Build();
        CreateLegacyDb(dbPath);

        init.Initialize();
        init.Initialize();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Media_Feed';";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// 新索引必须真的被查询用上。仅仅「建出来了」不等于「生效了」——
    /// SQLite 完全可能继续挑 IX_Media_Private，那这次改动就白做。
    /// </summary>
    [Fact]
    public void LegacyDb_MainListQuery_UsesFeedIndex()
    {
        var (init, dbPath) = Build();
        CreateLegacyDb(dbPath);
        init.Initialize();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT m.* FROM MediaItem m
            WHERE m.IsMissing = 0 AND m.IsPrivate = 0
            ORDER BY m.DateAddedUtc DESC, m.Id DESC
            LIMIT 100 OFFSET 0;
            """;

        var plan = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) plan.Add(reader.GetString(3));

        var text = string.Join(" | ", plan);
        Assert.Contains("IX_Media_Feed", text);
        Assert.DoesNotContain("TEMP B-TREE FOR ORDER BY", text);
    }
}
