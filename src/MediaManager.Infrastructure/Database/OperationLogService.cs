using Dapper;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>操作日志服务。</summary>
public class OperationLogService : IOperationLogService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<OperationLogService> _logger;

    public OperationLogService(IDbConnectionFactory factory, ILogger<OperationLogService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task LogAsync(string action, string source, string? target, string? detail = null)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO OperationLog(Action, Source, Target, Detail, CreatedUtc)
            VALUES(@Action, @Source, @Target, @Detail, @CreatedUtc);",
            new
            {
                Action = action,
                Source = source,
                Target = target,
                Detail = detail,
                CreatedUtc = DateTime.UtcNow.ToString("O"),
            });
    }
}
