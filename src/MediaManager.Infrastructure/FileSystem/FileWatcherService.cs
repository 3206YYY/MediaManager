using System.Collections.Concurrent;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>一次防抖后的文件变更批次：新增媒体文件数量用于「发现 N 个新文件」提示。</summary>
public sealed record FileChangeBatch(long RootFolderId, int NewMediaFiles);

/// <summary>文件系统监控：每个根目录一个 FileSystemWatcher，事件防抖 800ms。</summary>
public interface IFileWatcherService : IDisposable
{
    event EventHandler<FileChangeBatch>? Changed;
    void Start(long rootFolderId, string path);
    void Stop(long rootFolderId);
    void StopAll();
}

public class FileWatcherService : IFileWatcherService
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly ConcurrentDictionary<long, (FileSystemWatcher Watcher, CancellationTokenSource Cts)> _watchers = new();
    private readonly ConcurrentDictionary<long, Task> _debounceTasks = new();
    private readonly ConcurrentDictionary<long, DateTime> _lastEvent = new();

    /// <summary>本批次新建的媒体文件数（Created 事件里判定，防抖窗口结束后一次性取走）。</summary>
    private readonly ConcurrentDictionary<long, int> _newMediaCount = new();
    private const int DebounceMs = 800;

    public FileWatcherService(ILogger<FileWatcherService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<FileChangeBatch>? Changed;

    public void Start(long rootFolderId, string path)
    {
        if (_watchers.ContainsKey(rootFolderId)) return;
        if (!Directory.Exists(path)) return;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            watcher.Created += (s, e) => OnEvent(rootFolderId, e.FullPath, created: true);
            watcher.Deleted += (s, e) => OnEvent(rootFolderId, e.FullPath);
            watcher.Renamed += (s, e) => OnEvent(rootFolderId, e.FullPath);
            watcher.Changed += (s, e) => OnEvent(rootFolderId, e.FullPath);
            // 设备被拔出 / 缓冲区溢出 / 目录被删时会触发 Error，必须处理，
            // 否则监控线程会在后台反复抛异常，甚至句柄泄漏。
            watcher.Error += (s, e) => OnWatcherError(rootFolderId, e.GetException());

            _watchers[rootFolderId] = (watcher, new CancellationTokenSource());
            _logger.LogInformation("开始监控根目录：{Path}", path);
        }
        catch (Exception ex)
        {
            // 目录刚好在这两步之间消失（移动硬盘被拔）也不能让调用方崩
            _logger.LogWarning(ex, "无法监控根目录，已跳过：{Path}", path);
        }
    }

    /// <summary>监控出错：记录原因并停止该目录的监控，等待用户重新扫描（重新插入硬盘后可恢复）。</summary>
    private void OnWatcherError(long rootFolderId, Exception ex)
    {
        if (MediaManager.Core.Diagnostics.StorageErrors.IsDeviceUnavailable(ex))
            _logger.LogWarning("文件监控中断：设备已断开（可能是移动硬盘被拔出），已停止监控该目录");
        else
            _logger.LogWarning(ex, "文件监控发生错误，已停止监控该目录：{Id}", rootFolderId);

        // 不在 watcher 自己的回调里直接 Dispose，避免死锁
        _ = Task.Run(() => Stop(rootFolderId));
    }

    private void OnEvent(long rootFolderId, string path, bool created = false)
    {
        _lastEvent[rootFolderId] = DateTime.UtcNow;

        // 只统计「新增的媒体文件」：拷贝进来的一批照片会在这里累加，
        // 目录、临时文件、无关扩展名不计入，避免弹出一堆无意义提示。
        if (created && MediaFormat.IsSupported(path) && !SelfWriteTracker.IsRecent(path))
            _newMediaCount.AddOrUpdate(rootFolderId, 1, (_, v) => v + 1);

        // 防抖：取消旧的延迟任务，启动新的
        if (_debounceTasks.TryGetValue(rootFolderId, out var existing) && !existing.IsCompleted)
        {
            return; // 已有待执行的防抖任务，等待它触发
        }

        _debounceTasks[rootFolderId] = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs);
                // 等待期间若还有新事件，则再延迟
                while ((DateTime.UtcNow - _lastEvent[rootFolderId]).TotalMilliseconds < DebounceMs)
                {
                    await Task.Delay(100);
                }

                var newCount = _newMediaCount.TryRemove(rootFolderId, out var c) ? c : 0;
                _logger.LogDebug("文件变更：根目录 {Id}，新增媒体 {Count} 个", rootFolderId, newCount);
                Changed?.Invoke(this, new FileChangeBatch(rootFolderId, newCount));
            }
            catch (Exception ex)
            {
                // 订阅方（重扫、刷新）出错不能变成未观察异常
                _logger.LogWarning(ex, "处理文件变更事件失败：根目录 {Id}", rootFolderId);
            }
        });
    }

    public void Stop(long rootFolderId)
    {
        _newMediaCount.TryRemove(rootFolderId, out _);
        if (_watchers.TryRemove(rootFolderId, out var tuple))
        {
            tuple.Watcher.Dispose();
            tuple.Cts.Cancel();
            tuple.Cts.Dispose();
        }
    }

    public void StopAll()
    {
        foreach (var key in _watchers.Keys.ToList())
            Stop(key);
    }

    public void Dispose()
    {
        StopAll();
    }
}
