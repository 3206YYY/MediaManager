using MediaManager.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>SQLite 连接工厂。每次打开新连接，用于 Dapper。</summary>
public interface IDbConnectionFactory
{
    SqliteConnection CreateConnection();
    string DatabasePath { get; }
}

public class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(IPortableModeDetector detector, ILogger<SqliteConnectionFactory> logger)
    {
        _logger = logger;
        var dataDir = detector.DataDirectory;
        Directory.CreateDirectory(dataDir);
        DatabasePath = Path.Combine(dataDir, "library.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 私有页缓存：本项目每次操作新开连接，又已启用 WAL。
            // 共享缓存（Shared）会让多个连接在表级互相加锁，容易出现 SQLITE_LOCKED，
            // 并不能带来实际收益；SQLite 官方也建议优先用私有缓存 + WAL。
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString();
        _logger.LogInformation("数据库路径：{Path}", DatabasePath);
    }

    public string DatabasePath { get; }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
