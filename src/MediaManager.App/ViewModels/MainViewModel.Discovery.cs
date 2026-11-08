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
        var recentVms = items.Select(ToViewModel).ToList();
        await ApplyCategoryMarksAsync(recentVms);
        if (generation != _loadGeneration) return;
        MediaItems.ReplaceAll(recentVms);
        EnsureThumbnails(items);
        StatusText = $"最近查看 · {MediaItems.Count} 项";
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
            SortField = SortField.DateAdded,
            SortDirection = SortDirection.Descending,
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

    // ---------- 命令 ----------
}
