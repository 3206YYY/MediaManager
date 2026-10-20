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
            Cache = SqliteCacheMode.Shared,
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
