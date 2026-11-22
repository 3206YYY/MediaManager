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

/// <summary>主窗口视图模型 —— 发现页：随机批次、发现池、类型与分类筛选、防抖重载。</summary>
public partial class MainViewModel
{
    /// <summary>生成一批新的随机推荐。</summary>
    private async Task<List<MediaItem>> GenerateDiscoveryBatchAsync(bool forceRefresh = false)
    {
        var poolType = _discoveryPool;
        var count = _discoveryCount;
        var poolId = poolType == DiscoveryPoolType.Category ? _discoveryPoolId : null;
        var discoveryTypes = GetDiscoveryTypes();

        // 池子总数，用于决定是否排除最近 7 天已推
        var poolTotal = await DbAsync(() => _repo.CountDiscoveryPoolAsync(poolType, poolId, discoveryTypes));

        // 池子空着就回退到「全部」。
        // 默认推荐池是「收藏」，新用户一张都没收藏，发现页就一片空白 ——
        // 明明是整库几千张照片，点进来却像坏了，这个功能等于白做。
        if (poolTotal == 0 && poolType != DiscoveryPoolType.All)
        {
            _logger.LogInformation("发现页推荐池为空（{Pool}），本次回退到全部媒体", poolType);
            poolType = DiscoveryPoolType.All;
            poolId = null;
            _discoveryFallbackToAll = true;
            poolTotal = await DbAsync(() => _repo.CountDiscoveryPoolAsync(poolType, poolId, discoveryTypes));
        }
        else
        {
            _discoveryFallbackToAll = false;
        }

        List<long>? exclude = null;
        if (poolTotal >= count * 3)
        {
            var recentIds = await DbAsync(() => _repo.GetDiscoveryHistoryIdsAsync(7));
            if (recentIds.Count > 0)
                exclude = recentIds;
        }

        var media = await DbAsync(() => _repo.QueryDiscoveryAsync(poolType, poolId, count, exclude, discoveryTypes));

        // 若排除后不足 N，补齐（不排除）
        if (media.Count < count)
        {
            var existingIds = media.Select(m => m.Id).ToList();
            var more = await DbAsync(() => _repo.QueryDiscoveryAsync(poolType, poolId, count - media.Count,
                existingIds.Count > 0 ? existingIds : null, discoveryTypes));
            media.AddRange(more);
        }

        var ids = media.Select(m => m.Id).Distinct().ToList();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var typeKey = GetDiscoveryTypeKey();
        await DbAsync(async () =>
        {
            await _repo.SaveDiscoveryBatchAsync(today, ids, poolType, poolId, typeKey);
            await _repo.RecordDiscoveryHistoryAsync(ids);
        });

        return media;
    }

    /// <summary>加载最近查看。</summary>
    private async Task LoadRecentViewAsync()
    {
        var generation = _loadGeneration;
        var items = await DbAsync(() => _repo.GetRecentViewAsync(100));

        // 这里的过滤是 v1.4.8 补的：原本取完 100 条直接绑定，
        // 顶部搜索词 / 类型筛选 / 只看已编辑 对这个视图完全无效。
        items = ApplyQuickFilters(items);

        var recentVms = items.Select(ToViewModel).ToList();
        await ApplyCategoryMarksAsync(recentVms);
        if (generation != _loadGeneration) return;
        MediaItems.ReplaceAll(recentVms);
        EnsureThumbnails(items);
        _totalCount = MediaItems.Count;
        NotifyHeaderChanged();
        StatusText = HasQuickFilter
            ? $"最近查看 · 筛出 {MediaItems.Count} 项"
            : $"最近查看 · {MediaItems.Count} 项";

        // 最近查看是内存里的访问顺序，0 项时要说清「为什么空」，别只甩一句 0 项
        if (MediaItems.Count == 0)
        {
            CenterEmptyTitle = HasQuickFilter ? "没有符合筛选的文件" : "最近查看还是空的";
            CenterEmptyText = HasQuickFilter
                ? "最近查看里没有同时满足当前搜索 / 类型 / 已编辑条件的文件，清除筛选后再试。"
                : "打开、预览或编辑过的文件会出现在这里。";
        }
    }

    /// <summary>从 AppSetting 加载发现页配置。</summary>
    private async Task LoadDiscoverySettingsAsync()
    {
        var (poolStr, countStr, poolIdStr, hintStr) = await DbAsync(async () => (
            await _repo.GetSettingAsync("DiscoveryPoolType"),
            await _repo.GetSettingAsync("DiscoveryCount"),
            await _repo.GetSettingAsync("DiscoveryPoolId"),
            await _repo.GetSettingAsync("DiscoveryHintShown")));

        if (Enum.TryParse<DiscoveryPoolType>(poolStr, out var pool))
            _discoveryPool = pool;
        else
            _discoveryPool = DiscoveryPoolType.Favorite;

        if (int.TryParse(countStr, out var cnt) && cnt > 0)
            _discoveryCount = cnt;

        if (long.TryParse(poolIdStr, out var pid))
            _discoveryPoolId = pid;

        _discoveryOnboardingShown = hintStr == "1";

        DiscoveryPoolIndex = (int)_discoveryPool;
    }

    private MediaFilter BuildFilter()
    {
        var f = new MediaFilter
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            // 排序跟用户在抬头选的一致（偏好持久化在 AppSetting）。
            // 用 this. 限定：属性名与枚举类型同名，裸写 `SortField` 会被读成类型。
            SortField = this.SortField,
            SortDirection = this.SortDirection,
            Limit = PageSize,
            // 主界面所有视图（含搜索、类型筛选、文件夹、标签）一律查普通内容；
            // 保险箱内容由独立的 VaultWindow 用 IncludePrivate=true 自行查询
            IncludePrivate = false,
        };

        switch (_currentViewKey)
        {
            case "favorites":
                f.Favorite = true;
                break;
            case "unfiled":
                f.Unfiled = true;
                break;
            case "recent":
                f.RecentDays = 7;
                break;
            case "folder":
                f.RootFolderId = _currentRootFolderId;
                // 同时带路径：嵌套根目录、子文件夹节点名下可能一条记录都没有，
                // 只按 RootFolderId 查必然 0 项。路径才是「这个文件夹里有什么」的真答案。
                f.FolderPath = _currentFolderPath
                    ?? (_currentRootFolderId.HasValue
                        ? FolderItems.FirstOrDefault(x => x.RelatedId == _currentRootFolderId)?.ToolTipText
                        : null);
                break;
            case "tag":
                f.TagId = _currentTagId;
                break;
            case "category":
                f.CategoryId = _currentCategoryId;
                break;
        }

        // 顶部类型多选：全部勾选 = 不筛选；部分勾选 = 只显示选中的类型
        var selectedTypes = TypeFilterOptions.Where(o => o.IsSelected).Select(o => o.MediaType).ToList();
        if (selectedTypes.Count > 0 && selectedTypes.Count < TypeFilterOptions.Count)
            f.MediaTypes = selectedTypes;

        if (EditedOnly)
            f.EditedOnly = true;

        return f;
    }

    /// <summary>
    /// 顶部的搜索词 / 类型筛选 / 只看已编辑 有没有真的在起作用。
    /// <para>
    /// <b>为什么需要它</b>：发现页和「最近查看」以前在 <see cref="BuildFilter"/> <b>之前</b>
    /// 就 <c>return</c> 跑掉了（LoadMediaAsync 开头那两个 if）。结果是用户搜了个关键词、
    /// 或者只勾了「视频」，点进发现页 / 最近查看仍然是全量随机推荐 —— 筛选被整个绕过。
    /// 分流条件必须先问这里。
    /// </para>
    /// </summary>
    private bool HasQuickFilter
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText)) return true;
            if (EditedOnly) return true;
            var on = TypeFilterOptions.Count(o => o.IsSelected);
            return on > 0 && on < TypeFilterOptions.Count;
        }
    }

    /// <summary>
    /// 把顶部快筛条件套到一份已经取出来的列表上。
    /// 「最近查看」最多 100 条，内存过滤完全够，不用为它改数据层查询。
    /// 判定口径和 SQL 版保持一致（见 <c>MediaRepository</c> 的 BuildWhere）：
    /// 搜索 = 文件名包含（不区分大小写）；已编辑 = 文件名含 <c>_edited</c>。
    /// </summary>
    private List<MediaItem> ApplyQuickFilters(IEnumerable<MediaItem> items)
    {
        var q = items;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var kw = SearchText.Trim();
            q = q.Where(m => m.FileName.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var selected = TypeFilterOptions.Where(o => o.IsSelected)
                                        .Select(o => o.MediaType)
                                        .ToHashSet();
        if (selected.Count > 0 && selected.Count < TypeFilterOptions.Count)
            q = q.Where(m => selected.Contains(m.MediaType));

        if (EditedOnly)
            q = q.Where(m => m.FileName.Contains("_edited", StringComparison.OrdinalIgnoreCase));

        return q.ToList();
    }

    // ---------- 顶部类型筛选变化 ----------

    partial void OnEditedOnlyChanged(bool value) => _ = DebounceFilterReloadAsync();

    /// <summary>是否有勾选任何类型。</summary>
    private bool HasSelectedType => TypeFilterOptions.Any(o => o.IsSelected);

    private void OnTypeFilterOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TypeFilterOption.IsSelected))
            _ = DebounceFilterReloadAsync(includeDiscovery: true);
    }

    /// <summary>全选类型。</summary>
    public void SelectAllTypes()
    {
        foreach (var o in TypeFilterOptions) o.IsSelected = true;
    }

    /// <summary>全不选类型（列表会显示为空）。</summary>
    public void ClearAllTypes()
    {
        foreach (var o in TypeFilterOptions) o.IsSelected = false;
    }

    /// <summary>发现页类型筛选键：全部 / 具体子集，用来判断缓存的每日批次是否还适用。</summary>
    private string GetDiscoveryTypeKey()
    {
        var selected = TypeFilterOptions.Where(o => o.IsSelected)
            .Select(o => o.MediaType)
            .OrderBy(t => t)
            .ToList();
        if (selected.Count == 0) return "none";
        if (selected.Count == TypeFilterOptions.Count) return "all";
        return string.Join(",", selected);
    }

    /// <summary>发现页当前允许的媒体类型：全部勾选返回 null（不限制）；部分勾选返回选中的；一个没选返回空。</summary>
    private IReadOnlyList<Core.Enums.MediaType>? GetDiscoveryTypes()
    {
        var selected = TypeFilterOptions.Where(o => o.IsSelected).Select(o => o.MediaType).ToList();
        if (selected.Count == 0) return Array.Empty<Core.Enums.MediaType>();
        if (selected.Count == TypeFilterOptions.Count) return null;
        return selected;
    }

    private CancellationTokenSource? _filterCts;

    private async Task DebounceFilterReloadAsync(bool includeDiscovery = false)
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;
        try
        {
            await Task.Delay(200, token);
            // 类型筛选对发现页也生效；其余属性筛选仍跳过独立视图
            if (!token.IsCancellationRequested &&
                (includeDiscovery || _currentViewKey is not "discovery" and not "recent-view"))
                await LoadMediaAsync();
        }
        catch (TaskCanceledException) { }
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
            _totalCount = 0;
            NotifyHeaderChanged();
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
        // 发现页每次只有一批（默认 30），抬头按本批条数显示；库里的总数不是「当前结果数」。
        _totalCount = MediaItems.Count;
        NotifyHeaderChanged();
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

        // 库里有东西，但这个池子/类型筛出来 0 项：中央别留一片空白
        if (MediaItems.Count == 0)
        {
            CenterEmptyTitle = "这个推荐池里没有可推荐的";
            CenterEmptyText = "当前类型筛选或指定分类下没有文件，换个推荐池或清除筛选再试。";
        }
    }

    // ---------- 命令 ----------
}
