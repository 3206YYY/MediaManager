using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>媒体扫描服务：递归枚举 + 批量事务写入 + 进度 + 取消。</summary>
public class ScanService : IScanService
{
    private readonly IMediaRepository _repo;
    private readonly ILogger<ScanService> _logger;
    private const int BatchSize = 500;

    public ScanService(IMediaRepository repo, ILogger<ScanService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task ScanRootFolderAsync(long rootFolderId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var root = (await _repo.GetRootFoldersAsync()).FirstOrDefault(r => r.Id == rootFolderId);
        if (root == null)
        {
            _logger.LogWarning("根目录 {Id} 不存在", rootFolderId);
            return;
        }

        if (!Directory.Exists(root.Path))
        {
            if (!await TryRestorePathAsync(root).ConfigureAwait(false))
            {
                _logger.LogWarning("根目录失联，无法恢复：{Path}", root.Path);
                return;
            }
        }

        var p = new ScanProgress { RootFolderId = rootFolderId };
        var scanned = new List<MediaItem>(BatchSize);
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumState = new EnumerationState();
        int added = 0, updated = 0, scannedCount = 0;
        var utcNow = DateTime.UtcNow.ToString("O");

        // 预加载该根目录下已有的路径，用于区分新增/更新
        var knownPaths = await LoadExistingPathsAsync(rootFolderId);

        foreach (var file in SafeEnumerateFiles(root.Path, enumState))
        {
            ct.ThrowIfCancellationRequested();

            var type = MediaFormat.GetMediaType(file);
            if (type == null) continue;

            MediaItem item;
            try
            {
                var fi = new FileInfo(file);
                // 文件可能在扫描途中被删除/移走（尤其是正在拷贝的盘）
                if (!fi.Exists) continue;

                // desktop.ini / Thumbs.db / .DS_Store 这类系统文件不算媒体
                if (MediaFormat.IsSystemJunk(file, fi.Attributes)) continue;

                item = new MediaItem
                {
                    RootFolderId = rootFolderId,
                    Path = file,
                    FileName = fi.Name,
                    Extension = fi.Extension.TrimStart('.'),
                    MediaType = type.Value,
                    SizeBytes = fi.Length,
                    CreatedAtUtc = fi.CreationTimeUtc.ToString("O"),
                    ModifiedAtUtc = fi.LastWriteTimeUtc.ToString("O"),
                    LastScanUtc = utcNow,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到就跳过，不能因为一个坏文件中断整次扫描
                _logger.LogDebug(ex, "跳过无法读取的文件：{Path}", file);
                continue;
            }

            scannedCount++;
            existingPaths.Add(file);

            var isNew = !knownPaths.Contains(file);

            // 计数在这里立刻累加。原来放在「攒够一批」的分支里，等于每 500 个文件只按
            // 最后一个文件的 isNew 记一次，最后不足一批的尾巴完全不计，进度数字永远是错的。
            if (isNew) added++; else updated++;

            // 新文件顺便把宽高读出来（只读文件头，成本极低）。
            // 之前完全没写这一列，导致界面上所有图片都显示「未知」。
            if (isNew && type.Value == MediaType.Image)
                FillSize(item);

            scanned.Add(item);

            if (scanned.Count >= BatchSize)
            {
                await FlushBatchAsync(scanned);
                p.ScannedFiles = scannedCount;
                p.Added = added;
                p.Updated = updated;
                progress?.Report(p);
                scanned.Clear();
            }
        }

        if (scanned.Count > 0)
        {
            await FlushBatchAsync(scanned);
        }

        // 扫描途中设备被拔（目录已不可达）：绝不能把还没扫到的文件标记为「缺失」，
        // 否则整库记录会被误标，插回硬盘后要重新扫描一遍才能恢复。
        if (!Directory.Exists(root.Path))
        {
            _logger.LogWarning("扫描过程中根目录失联（可能是设备被拔出），跳过缺失标记：{Path}", root.Path);
            p.Total = scannedCount;
            p.IsCompleted = true;
            p.Added = added;
            p.Updated = updated;
            progress?.Report(p);
            return;
        }

        // 有子目录读不到（无权限 / 坏道）时同样跳过缺失标记：
        // 否则那些目录里的文件会被误判为「已缺失」。
        if (!enumState.Complete)
        {
            _logger.LogWarning("扫描时有部分目录无法读取，跳过缺失标记以避免误判：{Path}", root.Path);
            p.Total = scannedCount;
            p.IsCompleted = true;
            p.Added = added;
            p.Updated = updated;
            progress?.Report(p);
            return;
        }

        p.MarkedMissing = await _repo.MarkMissingAsync(rootFolderId, existingPaths);
        p.Total = scannedCount;
        p.IsCompleted = true;
        p.Added = added;
        p.Updated = updated;
        progress?.Report(p);

        root.LastScanUtc = DateTime.UtcNow.ToString("O");
        await _repo.UpdateRootFolderAsync(root);

        _logger.LogInformation("扫描完成：{Path}，扫描 {Scanned}，新增 {Added}，更新 {Updated}，缺失 {Missing}",
            root.Path, scannedCount, added, updated, p.MarkedMissing);
    }

    /// <summary>
    /// 补齐历史数据里缺失的图片宽高。老版本扫描入库时根本没写 Width/Height，
    /// 界面上所有图都显示「未知」。这里分批（每次 400 条）慢慢补，不阻塞界面。
    /// </summary>
    public async Task BackfillSizesAsync(CancellationToken ct = default)
    {
        const int batch = 400;
        int fixedCount = 0;

        while (!ct.IsCancellationRequested)
        {
            var rows = await _repo.GetMissingSizeAsync(batch, ct);
            if (rows.Count == 0) break;

            var fixedInBatch = 0;
            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break;

                // 只处理还在硬盘上的图片：移动硬盘没插时不报错，等下次启动再补
                if (!File.Exists(row.Path)) continue;

                if (!MediaSizeReader.TryReadImageSize(row.Path, out var w, out var h))
                    continue;

                try
                {
                    await _repo.UpdateMediaSizeAsync(row.Id, w, h);
                    fixedCount++;
                    fixedInBatch++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "回写图片尺寸失败：{Path}", row.Path);
                }
            }

            // 只要这一批一条都没补上就立刻收工：剩余文件当前都读不到（盘离线 / 文件损坏）。
            // 原来只在「不足一批」时才退出，凑够 400 条读不到的记录会让这里反复查同一批行、
            // 每轮 400 次 File.Exists，CPU 和磁盘一直空转。
            if (fixedInBatch == 0) break;
            if (rows.Count < batch) break;
        }

        if (fixedCount > 0)
            _logger.LogInformation("图片尺寸补齐完成：{Count} 条", fixedCount);
    }

    /// <summary>填充图片宽高；读不到就留空（视频不做解码，靠缩略图阶段补）。</summary>
    private static void FillSize(MediaItem item)
    {
        if (MediaSizeReader.TryReadImageSize(item.Path, out var w, out var h))
        {
            item.Width = w;
            item.Height = h;
        }
    }

    public async Task ScanIncrementalAsync(long rootFolderId, string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return;
        var utcNow = DateTime.UtcNow.ToString("O");
        var items = new List<MediaItem>();

        foreach (var file in SafeEnumerateFiles(directory, new EnumerationState()))
        {
            ct.ThrowIfCancellationRequested();
            var type = MediaFormat.GetMediaType(file);
            if (type == null) continue;

            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) continue;

                // 与全量扫描保持一致：系统目录说明文件不入库
                if (MediaFormat.IsSystemJunk(file, fi.Attributes)) continue;

                var item = new MediaItem
                {
                    RootFolderId = rootFolderId,
                    Path = file,
                    FileName = fi.Name,
                    Extension = fi.Extension.TrimStart('.'),
                    MediaType = type.Value,
                    SizeBytes = fi.Length,
                    CreatedAtUtc = fi.CreationTimeUtc.ToString("O"),
                    ModifiedAtUtc = fi.LastWriteTimeUtc.ToString("O"),
                    LastScanUtc = utcNow,
                };
                if (type.Value == MediaType.Image) FillSize(item);
                items.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "增量扫描跳过无法读取的文件：{Path}", file);
            }
        }

        foreach (var chunk in items.Chunk(BatchSize))
            await _repo.UpsertMediaBatchAsync(chunk).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> LoadExistingPathsAsync(long rootFolderId)
    {
        // 通过分页查询加载已有路径
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        while (true)
        {
            var page = await _repo.QueryAsync(new MediaFilter
            {
                RootFolderId = rootFolderId,
                Offset = offset,
                Limit = 5000,
                SortField = SortField.DateAdded,
                SortDirection = SortDirection.Ascending,
            });
            if (page.Count == 0) break;
            foreach (var m in page) set.Add(m.Path);
            offset += page.Count;
            if (page.Count < 5000) break;
        }
        return set;
    }

    private async Task FlushBatchAsync(List<MediaItem> batch)
    {
        // 一批一个连接 + 一个事务：原来逐条 Upsert 是「每条开一次连接 + 一次 SELECT + 一次写」，
        // 500 个文件就是 1000 多次数据库往返，整个扫描的耗时基本都耗在这里。
        await _repo.UpsertMediaBatchAsync(batch).ConfigureAwait(false);
    }

    /// <summary>枚举过程的完成状态：有目录读不到时置为 false，用于决定是否可信地标记「缺失」。</summary>
    private sealed class EnumerationState
    {
        public bool Complete { get; set; } = true;
    }

    /// <summary>
    /// 扫描时整体跳过的目录（目前只有保险箱）。
    ///
    /// 为什么需要：保险箱里的文件 <c>IsPrivate=1</c>，但扫描是按路径 upsert 的。
    /// 用户如果把数据目录（或它的上级）也加成了媒体文件夹，Vault 里的文件会被
    /// 当成新文件重新入库成普通项 —— 直接在主网格里泄露。
    /// 这里从源头堵住：不枚举这个目录，一个文件都看不到。
    /// </summary>
    private static string[] _excludedDirectories = Array.Empty<string>();

    /// <summary>设置要跳过的目录（由启动流程注入保险箱路径）。</summary>
    public static void SetExcludedDirectories(IEnumerable<string> directories)
    {
        _excludedDirectories = directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
    }

    private static bool IsExcludedDirectory(string directory)
    {
        if (_excludedDirectories.Length == 0) return false;

        var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var excluded in _excludedDirectories)
        {
            if (string.Equals(normalized, excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 安全递归枚举文件：逐个目录 try-catch。
    /// 系统目录无权限、设备中途拔出、目录被删，都只跳过该目录，不会让整次扫描崩掉。
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, EnumerationState state)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            if (IsExcludedDirectory(dir)) continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                state.Complete = false;
                continue;
            }

            foreach (var file in files)
                yield return file;

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                state.Complete = false;
                continue;
            }

            foreach (var sub in subDirs)
                stack.Push(sub);
        }
    }

    /// <summary>盘符变化后尝试用卷序列号恢复路径。</summary>
    private async Task<bool> TryRestorePathAsync(RootFolder root)
    {
        if (string.IsNullOrWhiteSpace(root.VolumeSerial) || string.IsNullOrWhiteSpace(root.RelativePath))
            return false;

        var driveRoot = VolumeHelper.FindDriveBySerial(root.VolumeSerial);
        if (driveRoot == null) return false;

        var newPath = Path.Combine(driveRoot, root.RelativePath.TrimStart('\\', '/'));
        if (!Directory.Exists(newPath)) return false;

        var oldPath = root.Path;
        root.Path = newPath;
        // 调用方本来就是 async（扫描本身就在线程池上跑），直接 await：
        // 不再用 Task.Run(...).GetResult() 同步阻塞，避免 UI 线程被数据库写卡住。
        await _repo.UpdateRootFolderAsync(root).ConfigureAwait(false);
        _logger.LogInformation("已恢复路径：{Old} -> {New}", oldPath, newPath);
        return true;
    }
}
