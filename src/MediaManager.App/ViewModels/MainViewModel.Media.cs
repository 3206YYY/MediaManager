using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Enums;
using MediaManager.Core.Media;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.App.Collections;
using MediaManager.App.Views.Dialogs;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.ViewModels;

/// <summary>主窗口视图模型 —— 媒体列表：列表加载与刷新、缩略图生成与缓存、详情面板数据。</summary>
public partial class MainViewModel
{
    private async Task LoadMediaAsync()
    {
        var generation = ++_loadGeneration;
        CanLoadMore = false;
        LoadMoreText = string.Empty;

        if (!HasSelectedType)
        {
            if (_currentViewKey == "discovery") IsDiscoveryView = true;
            MediaItems.Clear();
            StatusText = "未选择任何类型（顶部「类型」里至少勾选一个）";
            return;
        }

        if (_currentViewKey == "discovery")
        {
            await LoadDiscoveryAsync();
            return;
        }
        if (_currentViewKey == "recent-view")
        {
            await LoadRecentViewAsync();
            return;
        }

        _currentFilter = BuildFilter();
        var swLoad = System.Diagnostics.Stopwatch.StartNew();

        // 查询放到线程池：SQLite 的 await 不会让出 UI 线程
        var (total, items) = await DbAsync(async () =>
        {
            var count = await _repo.CountAsync(_currentFilter);
            var list = await _repo.QueryAsync(_currentFilter);
            return (count, list);
        });
        if (generation != _loadGeneration) return; // 期间又触发了新加载，丢弃这次结果
        _logger.LogDebug("  · 查询 {Ms} ms（{Count} 条）", swLoad.ElapsedMilliseconds, items.Count);

        var vms = items.Select(ToViewModel).ToList();

        // 分类标记必须在绑定之前算好：绑定之后再改 IsCategorized 会让每一项都触发
        // Visibility 变化 → 布局重算，50 项就要 400ms+（实测），这也是首屏慢的主因之一。
        await ApplyCategoryMarksAsync(vms);
        if (generation != _loadGeneration) return;

        // 一次性替换：只触发一次布局，比逐条 Add 快很多
        MediaItems.ReplaceAll(vms);

        _totalCount = total;
        UpdateLoadMoreState();

        // 一次性把缺缩略图的排队（同目录只探测一次，去掉逐条系统调用）
        EnsureThumbnails(items);
        _logger.LogDebug("  · 列表绑定 {Ms} ms", swLoad.ElapsedMilliseconds);

        StatusText = total > MediaItems.Count
            ? $"已加载 {MediaItems.Count} / 共 {total} 项"
            : $"共 {total} 项";

        // 0 项必须给出「为什么」，不能只甩一句共 0 项：用户分不清「真的没文件」
        // 和「移动硬盘没插上」。folder 视图有内容时也顺手同步离线标记。
        if (total == 0)
            await ExplainEmptyResultAsync(generation);
        else if (_currentViewKey == "folder" && _currentRootFolderId.HasValue)
            await RefreshFolderOfflineFlagAsync(_currentRootFolderId.Value, generation);

        _logger.LogDebug("  · 列表就绪 {Ms} ms", swLoad.ElapsedMilliseconds);
    }

    /// <summary>
    /// 查询结果为空时，在状态栏给出针对性的原因（而不是一句「共 0 项」）。
    /// folder 视图：查缺失计数 + 探测磁盘可达性；其他视图：指出筛选条件残留。
    /// 探测都在线程池做 —— 未挂载的移动硬盘上 Directory.Exists 可能阻塞上百毫秒。
    /// </summary>
    private async Task ExplainEmptyResultAsync(int generation)
    {
        if (_currentViewKey == "folder")
        {
            var rootId = _currentRootFolderId;
            var folderPath = _currentFolderPath
                ?? (rootId.HasValue ? FolderItems.FirstOrDefault(f => f.RelatedId == rootId)?.ToolTipText : null);
            var (diagTotal, missing, onDisk) = await Task.Run(async () =>
            {
                // 子文件夹没有自己的 RootFolderId，只能按路径判断可达性；
                // 缺失计数按根目录统计（子文件夹级别的缺失数意义不大，提示也用不上）
                var d = rootId.HasValue
                    ? await _repo.GetFolderDiagnosticsAsync(rootId.Value).ConfigureAwait(false)
                    : (Total: 0, Missing: 0);
                var exists = folderPath is null || System.IO.Directory.Exists(folderPath);
                return (d.Total, d.Missing, exists);
            }).ConfigureAwait(true);
            if (generation != _loadGeneration) return;   // 期间又触发了新加载，别覆盖新结果

            if (!onDisk)
                StatusText = diagTotal > 0
                    ? $"共 0 项 —— 此文件夹的 {diagTotal} 个文件当前不可达（磁盘未连接或路径已变更），重新连接后点「扫描」即可恢复"
                    : "共 0 项 —— 此文件夹当前无法访问（磁盘未连接或路径已变更）";
            else if (missing > 0)
                StatusText = $"共 0 项 —— 有 {missing} 个文件暂不可达，点「扫描」重新扫描此文件夹即可恢复";
            else
                StatusText = "共 0 项 —— 文件夹可达但里面没有匹配的文件（检查顶部「类型」勾选与搜索词）";
            return;
        }

        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(SearchText))
            hints.Add($"搜索词「{SearchText}」");
        var selectedTypes = TypeFilterOptions.Where(o => o.IsSelected).Select(o => o.MediaType).ToList();
        var allTypes = TypeFilterOptions.Select(o => o.MediaType).ToList();
        if (selectedTypes.Count > 0 && selectedTypes.Count < allTypes.Count)
            hints.Add("类型筛选（只勾了 " + string.Join("、", selectedTypes) + "）");
        if (EditedOnly)
            hints.Add("只看已编辑");
        StatusText = hints.Count > 0
            ? $"共 0 项 —— 没有文件同时满足 " + string.Join(" 和 ", hints) + "，清除筛选后再试试"
            : "共 0 项 —— 当前视图没有文件";
    }

    /// <summary>后台探测根目录可达性并同步侧栏离线标记（不改变状态栏）。</summary>
    private async Task RefreshFolderOfflineFlagAsync(long rootId, int generation)
    {
        var nav = FolderItems.FirstOrDefault(f => f.RelatedId == rootId);
        if (nav is null) return;
        var path = nav.ToolTipText;
        var exists = await Task.Run(() => path is null || System.IO.Directory.Exists(path)).ConfigureAwait(true);
        if (generation != _loadGeneration) return;
        nav.IsOffline = !exists;
    }

    /// <summary>
    /// 「加载更多」：只追加一页，保留当前滚动位置和已选项。
    /// 方案 A：按钮显式触发，不自动加载，避免滚动时位置跳动/重复请求。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_isLoadingMore || !CanLoadMore) return;
        if (_currentViewKey is "discovery" or "recent-view") return;

        _isLoadingMore = true;
        var generation = _loadGeneration;
        try
        {
            var filter = BuildFilter();
            filter.Offset = MediaItems.Count;
            filter.Limit = PageSize;

            var items = await DbAsync(() => _repo.QueryAsync(filter));
            if (generation != _loadGeneration) return;
            if (items.Count == 0)
            {
                // 库里的数据变少了：以实际为准，别让按钮一直挂着
                _totalCount = MediaItems.Count;
                UpdateLoadMoreState();
                return;
            }

            var vms = items.Select(ToViewModel).ToList();
            await ApplyCategoryMarksAsync(vms);
            if (generation != _loadGeneration) return;

            MediaItems.AddRange(vms);
            EnsureThumbnails(items);

            _totalCount = Math.Max(_totalCount, MediaItems.Count);
            UpdateLoadMoreState();
            StatusText = $"已加载 {MediaItems.Count} / 共 {_totalCount} 项";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载更多失败");
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private void UpdateLoadMoreState()
    {
        var remaining = _totalCount - MediaItems.Count;
        CanLoadMore = remaining > 0;
        LoadMoreText = remaining > 0 ? $"加载更多（还有 {remaining} 项）" : string.Empty;
    }

    /// <summary>MediaItem → 列表项（纯内存对象，不碰磁盘）。</summary>
    private static ThumbnailItemViewModel ToViewModel(MediaItem m) => new(m)
    {
        ThumbnailPath = m.ThumbnailPath,
        IsFavorite = m.Favorite,
        Rating = m.Rating,
        Note = m.Note,
    };

    /// <summary>
    /// 编辑器保存副本后调用：把副本补进库、刷新当前网格，并返回应当选中的列表项。
    ///
    /// 之前保存完只弹一个 2 秒的小 toast，主界面完全不动，用户会以为「保存没生效」。
    /// 这里保证副本一定能出现在列表里（当前筛选看不到时自动切到它所在的根目录）。
    /// </summary>
    public async Task<SaveRefreshResult> RefreshAfterSaveAsync(string savedPath, bool overwritten = false)
    {
        if (string.IsNullOrWhiteSpace(savedPath)) return SaveRefreshResult.None;

        var full = System.IO.Path.GetFullPath(savedPath);
        if (!System.IO.File.Exists(full)) return SaveRefreshResult.None;

        // 副本必须位于某个已加入库的根目录下；独立打开的文件（不在库里）直接跳过
        var roots = await DbAsync(() => _repo.GetRootFoldersAsync());
        var root = roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && IsUnder(full, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

        if (root == null) return SaveRefreshResult.None;

        try
        {
            var info = new System.IO.FileInfo(full);
            var item = new MediaItem
            {
                RootFolderId = root.Id,
                Path = full,
                FileName = info.Name,
                Extension = info.Extension,
                MediaType = MediaFormat.GetMediaType(full) ?? MediaType.Image,
                SizeBytes = info.Length,
                CreatedAtUtc = info.CreationTimeUtc.ToString("O"),
                ModifiedAtUtc = info.LastWriteTimeUtc.ToString("O"),
                LastScanUtc = DateTime.UtcNow.ToString("O"),
            };

            if (MediaSizeReader.TryReadImageSize(full, out var w, out var h))
            {
                item.Width = w;
                item.Height = h;
            }

            await DbAsync(() => _repo.UpsertMediaAsync(item));

            // 原地覆盖了副本：磁盘上的缩略图还是旧画面，必须让它失效并重建，
            // 否则列表里永远显示编辑前的样子。
            if (overwritten && item.Id > 0)
            {
                await DbAsync(() => _repo.InvalidateThumbnailAsync(item.Id));
                // 缓存文件名带内容签名，但旧文件还占着磁盘；删掉并同步清内存 LRU，
                // 这样无论 Id 是否被复用都不会命中上一轮的图。
                foreach (var stale in _thumbnailService.Invalidate(item.Id))
                    Services.ThumbnailMemoryCache.Remove(stale);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存副本入库失败：{Path}", full);
            return SaveRefreshResult.None;
        }

        // 刷新当前视图（发现页 / 最近查看 / 各种筛选都走各自的分支）
        await SafeReloadAsync();

        var vm = FindByPath(full);
        var viewChanged = false;
        long? folderId = null;

        // 当前视图看不到副本（典型：发现页、搜索过滤中）→ 切「全部媒体」，新副本按添加时间排在最前
        if (vm == null)
        {
            await SelectViewAsync("all");
            viewChanged = true;
            vm = FindByPath(full);
        }

        // 还被搜索词挡住 → 清掉搜索再找一次（用户刚保存完，最想看的就是这张图）
        if (vm == null && !string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
            await LoadMediaAsync();
            vm = FindByPath(full);
        }

        // 最后兜底：直接切到副本所在的根目录
        if (vm == null)
        {
            await SelectViewAsync($"folder:{root.Id}");
            viewChanged = true;
            folderId = root.Id;
            vm = FindByPath(full);
        }

        if (vm != null)
        {
            SelectedItem = vm;
            await RefreshStatusAsync();
            StatusText = overwritten ? $"已更新副本：{vm.FileName}" : $"已保存新副本：{vm.FileName}";
        }

        return new SaveRefreshResult(vm, viewChanged, folderId);
    }

    private async Task SafeReloadAsync()
    {
        try
        {
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            // 刷新失败不应该让「保存成功」变成异常提示，只记录日志
            _logger.LogWarning(ex, "保存副本后刷新列表失败");
        }
    }

    private ThumbnailItemViewModel? FindByPath(string fullPath)
        => MediaItems.FirstOrDefault(v =>
            string.Equals(v.Media.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnder(string file, string folder)
    {
        var full = System.IO.Path.GetFullPath(folder);
        if (!full.EndsWith(System.IO.Path.DirectorySeparatorChar))
            full += System.IO.Path.DirectorySeparatorChar;

        return file.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 批量投递缩略图任务。相比逐条 <c>File.Exists</c>/<c>Directory.Exists</c>，
    /// 这里只对「同一个根目录」探测一次在线状态，500 项能省掉近千次系统调用。
    /// </summary>
    private void EnsureThumbnails(List<MediaItem> items)
    {
        Dictionary<string, bool>? rootOnline = null;
        foreach (var m in items)
        {
            // 走系统默认程序打开的类型不生成缩略图（列表用固定图标），避免 worker 空转。
            // 判定必须问 MediaOpenStrategy，不要在这里另抄一份 Audio/Text/Archive ——
            // 否则将来加类型时这里和双击行为会悄悄不一致。
            if (MediaOpenStrategy.UseExternalApp(m.MediaType))
                continue;

            // 当前源文件（大小 + 修改时间）对应的缓存是否已经存在。
            // 存在就直接用；和库里记的旧路径不一致时切过去 —— 源文件被原地覆盖后
            // 旧缓存必须立刻作废，否则列表会一直显示编辑前的画面。
            var expected = _thumbnailService.GetCachedPath(m);
            if (expected != null)
            {
                if (!string.Equals(m.ThumbnailPath, expected, StringComparison.OrdinalIgnoreCase))
                    m.ThumbnailPath = expected;
                continue;
            }

            // 缓存不存在（首次入库 / 缩略图被删 / 源文件已变化）→ 清掉旧路径后重新生成
            m.ThumbnailPath = null;

            rootOnline ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var root = System.IO.Path.GetPathRoot(m.Path) ?? string.Empty;
            if (!rootOnline.TryGetValue(root, out var online))
            {
                // 源文件所在设备不在线（移动硬盘被拔）时先不排队，避免 worker 空转
                online = string.IsNullOrEmpty(root) || System.IO.Directory.Exists(root);
                rootOnline[root] = online;
            }
            if (!online) continue;

            _thumbnailService.Enqueue(m);
        }
    }

    /// <summary>
    /// 批量填充缩略图的分类标记（一次查询，避免逐条 N+1）。
    /// 只应在列表绑定到界面之前调用：绑定后改 IsCategorized 会触发逐项布局重算。
    /// </summary>
    private async Task ApplyCategoryMarksAsync(List<ThumbnailItemViewModel> targets)
    {
        if (targets.Count == 0) return;

        var ids = targets.Select(v => v.Id).ToList();
        var map = await DbAsync(() => _repo.GetMediaCategoryMapAsync(ids));
        var nameById = CategoryItems.ToDictionary(c => c.Id, c => c.Name);

        foreach (var vm in targets)
        {
            if (!map.TryGetValue(vm.Id, out var catIds) || catIds.Count == 0)
            {
                vm.CategoryIds.Clear();
                vm.CategoryNames = string.Empty;
                vm.RefreshCategoryState();
                continue;
            }

            vm.CategoryIds.Clear();
            var names = new List<string>();
            foreach (var cid in catIds)
            {
                vm.CategoryIds.Add(cid);
                if (nameById.TryGetValue(cid, out var n))
                    names.Add(n);
            }
            vm.CategoryNames = string.Join("、", names);
            vm.RefreshCategoryState();
        }
    }

    /// <summary>加载发现页（随机推荐，当日固定）。</summary>
    private async Task LoadDiscoveryAsync()
    {
        var generation = _loadGeneration;
        IsDiscoveryView = true;
        var swD = System.Diagnostics.Stopwatch.StartNew();
        var total = await DbAsync(() => _repo.GetTotalCountAsync());
        _logger.LogDebug("  [发现] 总数 {Ms} ms", swD.ElapsedMilliseconds);

        // 空库 → 欢迎引导
        if (total == 0)
        {
            MediaItems.Clear();
            DiscoveryShowWelcome = true;
            DiscoveryShowHint = false;
            StatusText = "欢迎使用媒体管理器";
            return;
        }
        DiscoveryShowWelcome = false;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var batch = await DbAsync(() => _repo.GetDiscoveryBatchAsync(today));
        _logger.LogDebug("  [发现] 批次 {Ms} ms", swD.ElapsedMilliseconds);

        List<MediaItem> media;
        var typeKey = GetDiscoveryTypeKey();
        if (batch != null && batch.MediaIds.Count > 0 &&
            string.Equals(batch.TypeFilter ?? "all", typeKey, StringComparison.Ordinal))
        {
            // 当天已有“同一类型范围”的批次，直接复用（同一天重开看到同一批）
            media = await DbAsync(() => _repo.GetByIdsAsync(batch.MediaIds));

            // 若批次里有媒体已被删除/还原后换了新 Id，查到的数量会变少，
            // 此时重新生成一批，避免发现页出现空白项
            if (media.Count < batch.MediaIds.Count)
            {
                media = await GenerateDiscoveryBatchAsync();
            }
        }
        else
        {
            // 没有批次，或类型筛选变了（旧批次只包含某个子集）→ 重新生成
            media = await GenerateDiscoveryBatchAsync();
        }

        var discoveryVms = media.Select(ToViewModel).ToList();
        await ApplyCategoryMarksAsync(discoveryVms);
        if (generation != _loadGeneration) return;
        MediaItems.ReplaceAll(discoveryVms);
        _logger.LogDebug("  [发现] 绑定 {Ms} ms（{Count} 项）", swD.ElapsedMilliseconds, media.Count);
        EnsureThumbnails(media);

        // 首次提示（仅一次）
        if (!_discoveryOnboardingShown && MediaItems.Count > 0)
        {
            DiscoveryHintText = $"这是随机挑出来的 {MediaItems.Count} 张。按 R 换一批，按 F 收藏喜欢的。";
            ShowDiscoveryHintForAWhile();
            _discoveryOnboardingShown = true;
            await DbAsync(() => _repo.SetSettingAsync("DiscoveryHintShown", "1"));
        }

        StatusText = _discoveryFallbackToAll
            ? $"发现 · {MediaItems.Count} 项随机推荐（收藏还是空的，这次先看全库）"
            : $"发现 · {MediaItems.Count} 项随机推荐";
    }

    /// <summary>
    /// 若缩略图路径为空、或文件已不存在（被清理/删除/磁盘整理），重新入队生成。
    /// 保证即使缓存文件丢失，也能自动重建，避免缩略图空白。
    /// </summary>
    private void EnsureThumbnail(MediaItem m)
    {
        if (!string.IsNullOrEmpty(m.ThumbnailPath) && System.IO.File.Exists(m.ThumbnailPath)) return;

        // 源文件所在设备不在线（移动硬盘被拔）时先不排队：
        // 否则每次刷新都会重新入队、worker 反复失败空转，白白吃 CPU。
        // 重新插上后重新扫描，即可正常生成。
        var root = System.IO.Path.GetPathRoot(m.Path);
        if (!string.IsNullOrEmpty(root) && !System.IO.Directory.Exists(root)) return;

        _thumbnailService.Enqueue(m);
    }

    /// <summary>缩略图内存缓存（解码后的位图）当前占用，供设置页显示。</summary>
    public static (long Bytes, int Count, long Budget) ThumbnailCacheInfo
        => (MediaManager.App.Services.ThumbnailMemoryCache.CurrentBytes,
            MediaManager.App.Services.ThumbnailMemoryCache.Count,
            MediaManager.App.Services.ThumbnailMemoryCache.BudgetBytes);

    /// <summary>按预算上限设置缩略图内存缓存（默认 200MB）。</summary>
    public static void SetThumbnailCacheBudget(long bytes)
        => MediaManager.App.Services.ThumbnailMemoryCache.BudgetBytes = bytes;

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedItem == null) return;
        var newName = CustomPrompt.Show("输入新的文件名（含扩展名）：", "重命名", SelectedItem.FileName);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedItem.FileName) return;

        try
        {
            await _fileOps.RenameAsync(SelectedItem.Path, newName);
            var newPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(SelectedItem.Path)!, newName);
            await _repo.UpdatePathAsync(SelectedItem.Id, newPath);
            await _opLog.LogAsync("重命名", SelectedItem.Path, newPath);
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重命名失败：{Path}", SelectedItem.Path);
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "重命名"), "重命名失败");
        }
    }

    // ---------- 批量重命名 / 批量导出 ----------

    private CancellationTokenSource? _detailCts;

    /// <summary>
    /// 详情面板按选中项加载。数据库与 EXIF 读盘都放到后台，并且带 120ms 防抖 +
    /// 可取消：用户按住方向键快速翻图时，只为最终停留的那张读 EXIF，不会排队卡顿。
    /// </summary>
    private async Task LoadDetailCoreAsync(ThumbnailItemViewModel? item, bool debounce)
    {
        if (item == null) return;

        // 名称/尺寸/日期都是内存里的字段，先立刻显示，面板不会有空白期
        DetailName = item.FileName;
        DetailMeta = item.Media.MediaType == Core.Enums.MediaType.Image
            ? $"{item.Media.Width}×{item.Media.Height}  {item.SizeDisplay}"
            : $"{item.TypeLabel}  ·  {item.SizeDisplay}";
        DetailDate = item.Media.ModifiedAtUtc ?? string.Empty;
        DetailIsVideo = item.IsVideo;
        NoteEditor = item.Note ?? string.Empty;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _detailCts, cts);
        previous?.Cancel();   // 不 Dispose：可能仍被上一轮的 Task.Delay 引用

        var ct = cts.Token;
        try
        {
            if (debounce) await Task.Delay(120, ct);

            // 已加入的分类（v1.3.2 起收藏夹已并入这里，只有一个列表）
            var catIds = await DbAsync(() => _repo.GetMediaCategoryIdsAsync(item.Id));
            ct.ThrowIfCancellationRequested();

            SelectedCategories.Clear();
            foreach (var id in catIds)
            {
                var cat = CategoryItems.FirstOrDefault(c => c.Id == id);
                if (cat != null) SelectedCategories.Add(cat);
            }

            // EXIF（仅图片）：命中缓存时几乎零成本，未命中才真正读文件
            ExifItems.Clear();
            if (item.Media.MediaType == Core.Enums.MediaType.Image)
            {
                var exif = await _exifService.ReadExifAsync(item.Path);
                ct.ThrowIfCancellationRequested();
                foreach (var kv in exif.Take(30))
                    ExifItems.Add(kv);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户切到别的图了，本轮作废
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取详情失败：{Path}", item.Path);
        }
    }

    partial void OnSelectedItemChanged(ThumbnailItemViewModel? value)
    {
        if (value != null)
            _ = LoadDetailCoreAsync(value, debounce: true);
    }

    private async Task RefreshStatusAsync()
    {
        var total = await DbAsync(() => _repo.GetTotalCountAsync());
        StatusText = $"共 {total} 项";
    }

    // ---------- 文件监控 ----------
}
