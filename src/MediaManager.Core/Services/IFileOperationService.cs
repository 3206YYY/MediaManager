using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>文件系统操作服务接口（含回收站删除）。</summary>
public interface IFileOperationService
{
    Task DeleteToRecycleBinAsync(string path);
    Task DeletePermanentAsync(string path);
    Task MoveAsync(string source, string destination);
    Task CopyAsync(string source, string destination);
    Task RenameAsync(string source, string newName);
}

/// <summary>操作日志服务。</summary>
public interface IOperationLogService
{
    Task LogAsync(string action, string source, string? target, string? detail = null);
}
