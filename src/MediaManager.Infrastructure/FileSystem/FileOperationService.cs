using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>文件系统操作实现（回收站删除、移动、复制、重命名）。</summary>
public class FileOperationService : IFileOperationService
{
    private readonly ILogger<FileOperationService> _logger;

    public FileOperationService(ILogger<FileOperationService> logger)
    {
        _logger = logger;
    }

    public async Task DeleteToRecycleBinAsync(string path)
    {
        await Task.Run(async () =>
        {
            if (!System.IO.File.Exists(path))
            {
                _logger.LogWarning("删除跳过（文件不存在）：{Path}", path);
                return;
            }

            // 重试机制：文件可能被缩略图生成/查看器等短暂占用，
            // 逐次等待后重试，最多 3 次。
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    _logger.LogInformation("已删除到回收站：{Path}（第 {Attempt} 次尝试）", path, attempt);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除失败（第 {Attempt}/3 次）：{Path}", attempt, path);
                    if (attempt < 3)
                    {
                        await Task.Delay(attempt * 200); // 200ms / 400ms
                    }
                    else
                    {
                        // 最后一次失败，抛出让上层提示用户
                        _logger.LogError(ex, "删除最终失败：{Path}", path);
                        throw;
                    }
                }
            }
        });
    }

    public Task DeletePermanentAsync(string path)
    {
        return Task.Run(() =>
        {
            if (!System.IO.File.Exists(path)) return;
            System.IO.File.Delete(path);
            _logger.LogInformation("已永久删除：{Path}", path);
        });
    }

    public Task MoveAsync(string source, string destination)
    {
        return Task.Run(() =>
        {
            if (!System.IO.File.Exists(source)) throw new System.IO.FileNotFoundException(source);
            var dir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.Move(source, destination);
            _logger.LogInformation("已移动：{Source} -> {Dest}", source, destination);
        });
    }

    public Task CopyAsync(string source, string destination)
    {
        return Task.Run(() =>
        {
            if (!System.IO.File.Exists(source)) throw new System.IO.FileNotFoundException(source);
            var dir = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.Copy(source, destination);
            _logger.LogInformation("已复制：{Source} -> {Dest}", source, destination);
        });
    }

    public Task RenameAsync(string source, string newName)
    {
        return Task.Run(() =>
        {
            if (!System.IO.File.Exists(source)) throw new System.IO.FileNotFoundException(source);
            var dir = System.IO.Path.GetDirectoryName(source)!;
            var dest = System.IO.Path.Combine(dir, newName);
            System.IO.File.Move(source, dest);
            _logger.LogInformation("已重命名：{Source} -> {Dest}", source, dest);
        });
    }
}
