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

/// <summary>主窗口视图模型 —— 整理：重命名、批量导出、标签与分类、备注评分、排序。</summary>
public partial class MainViewModel
{
    private bool _isBatchRunning;

    /// <summary>批量重命名（文件改名 + 数据库路径同步 + 操作日志）。</summary>
    public async Task BatchRenameAsync(IReadOnlyList<MediaItem> items, BatchRenameOptions options)
    {
        if (items.Count == 0 || _isBatchRunning) return;

        var plan = BatchOperationService.BuildRenamePlan(items, options, out _);
        if (plan.Count == 0)
        {
            CustomMessageBox.Warn("没有可以重命名的文件。", "批量重命名");
            return;
        }

        _isBatchRunning = true;
        IsScanning = true;
        ScanProgress = 0;

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                ScanProgressText = $"重命名 {p.Done}/{p.Total}";
            });

            var result = await Task.Run(
                () => BatchOperationService.ExecuteRename(plan, progress));

            foreach (var (id, newPath) in result.RenamedItems)
                await _repo.UpdatePathAsync(id, newPath);

            var oldByNew = plan
                .GroupBy(p => p.NewPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().OldPath, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, newPath) in result.RenamedItems)
            {
                if (oldByNew.TryGetValue(newPath, out var oldPath))
                    await _opLog.LogAsync("批量重命名", oldPath, newPath);
            }

            await LoadMediaAsync();
            ScanProgressText = string.Empty;

            StatusText = $"批量重命名完成：成功 {result.Renamed}，跳过 {result.Skipped}，失败 {result.Failed}";

            if (result.Failed > 0)
            {
                CustomMessageBox.Warn(
                    $"已完成 {result.Renamed} 个，{result.Failed} 个失败：\n\n" +
                    string.Join("\n", result.Errors.Take(6)) +
                    (result.Errors.Count > 6 ? "\n…" : "") +
                    "\n\n常见原因：文件正被其他程序占用、或所在磁盘已拔出。",
                    "批量重命名");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量重命名失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "批量重命名"), "批量重命名失败");
        }
        finally
        {
            _isBatchRunning = false;
            IsScanning = false;
            ScanProgress = 0;
        }
    }

    /// <summary>批量导出：统一尺寸 / 统一压缩，输出到指定目录（不修改原图）。</summary>
    public async Task BatchExportAsync(IReadOnlyList<MediaItem> items, BatchExportOptions options)
    {
        if (items.Count == 0 || _isBatchRunning) return;

        _isBatchRunning = true;
        IsScanning = true;
        ScanProgress = 0;

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                ScanProgressText = $"导出 {p.Done}/{p.Total}";
            });

            var result = await Task.Run(
                () => BatchOperationService.ExportAsync(items, options, progress));

            ScanProgressText = string.Empty;
            StatusText = $"导出完成：成功 {result.Ok}，跳过 {result.Skipped}，失败 {result.Failed}";

            var detail = $"成功导出 {result.Ok} 个文件到：\n{result.OutputDir}";
            if (result.Skipped > 0) detail += $"\n\n跳过 {result.Skipped} 个（视频或文件已不存在）。";
            if (result.Failed > 0)
            {
                detail += $"\n\n失败 {result.Failed} 个：\n" +
                          string.Join("\n", result.Errors.Take(5)) +
                          (result.Errors.Count > 5 ? "\n…" : "");
            }

            if (result.Failed > 0)
                CustomMessageBox.Warn(detail, "批量导出");
            else
                CustomMessageBox.Success(detail + "\n\n原图没有被修改。", "批量导出完成");

            if (result.Ok > 0)
                Services.ShellHelper.OpenFolder(result.OutputDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量导出失败");
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "批量导出"), "批量导出失败");
        }
        finally
        {
            _isBatchRunning = false;
            IsScanning = false;
            ScanProgress = 0;
        }
    }

    [RelayCommand]
    private Task RefreshDetailAsync(ThumbnailItemViewModel? item)
        => LoadDetailCoreAsync(item ?? SelectedItem, debounce: false);

    [RelayCommand]
    private async Task RenameTagAsync(NavigationItemViewModel? tag)
    {
        if (tag?.RelatedId == null) return;
        var name = CustomPrompt.Show("输入新的标签名称：", "重命名标签", tag.Title);
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.RenameTagAsync(tag.RelatedId.Value, name.Trim());
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task DeleteTagAsync(NavigationItemViewModel? tag)
    {
        if (tag?.RelatedId == null) return;
        var confirm = CustomMessageBox.ConfirmDanger($"确定删除标签「{tag.Title}」？", "删除标签");
        if (!confirm) return;

        await _repo.DeleteTagAsync(tag.RelatedId.Value);
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task AssignTagAsync(string? tagIdStr)
    {
        if (SelectedItem == null || !long.TryParse(tagIdStr, out var tagId)) return;
        await _repo.AddMediaTagAsync(SelectedItem.Id, tagId);
    }

    // ---------- 分类管理 ----------

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = CustomPrompt.Show("给这个分类起个名字：", "新建分类");
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.AddCategoryAsync(new Category { Name = name.Trim() });
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task RenameCategoryAsync(Category? category)
    {
        if (category == null) return;
        var name = CustomPrompt.Show("输入新的分类名称：", "重命名分类", category.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.RenameCategoryAsync(category.Id, name.Trim());
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync(Category? category)
    {
        if (category == null) return;
        var confirm = CustomMessageBox.ConfirmDanger($"确定删除分类「{category.Name}」？", "删除分类");
        if (!confirm) return;

        await _repo.DeleteCategoryAsync(category.Id);
        await LoadNavigationAsync();
    }

    [RelayCommand]
    private async Task AssignCategoryAsync(string? categoryIdStr)
    {
        if (SelectedItem == null || !long.TryParse(categoryIdStr, out var catId)) return;

        await _repo.AddMediaCategoryAsync(SelectedItem.Id, catId);

        // 缩略图角标 + 详情面板同时跟上，别等下次刷新
        if (!SelectedItem.CategoryIds.Contains(catId))
            SelectedItem.CategoryIds.Add(catId);
        UpdateCategoryMark(SelectedItem);

        var name = CategoryItems.FirstOrDefault(c => c.Id == catId)?.Name ?? "分类";
        ShowActionToast($"✅ 已加入分类【{name}】");
        await RefreshDetailAsync(SelectedItem);
    }

    // ---------- 备注 ----------

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (SelectedItem == null) return;
        var note = string.IsNullOrWhiteSpace(NoteEditor) ? null : NoteEditor.Trim();
        SelectedItem.Note = note;
        await _repo.SetNoteAsync(SelectedItem.Id, note);
        StatusText = string.IsNullOrEmpty(note) ? "已清除备注" : "备注已保存";
    }

    // ---------- 刷新 ----------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // 手动刷新要连解码后的内存缓存一起丢掉：只重读数据库的话，
        // 源文件已被外部修改、缩略图文件却还命中的旧图会继续显示。
        Services.ThumbnailMemoryCache.Clear();
        await LoadNavigationAsync();
        await LoadMediaAsync();
        StatusText = "已刷新";
    }

    // ---------- 批量操作（供右键菜单 / 快捷键调用，作用于所有选中项） ----------

    /// <summary>批量收藏：全部设为收藏（若全部已收藏则取消收藏）。</summary>
    public async Task BatchToggleFavoriteAsync(IReadOnlyList<ThumbnailItemViewModel> items)
    {
        if (items.Count == 0) return;
        var allFav = items.All(x => x.IsFavorite);
        foreach (var item in items)
        {
            item.IsFavorite = !allFav;
            await _repo.SetFavoriteAsync(item.Id, item.IsFavorite);
        }
        StatusText = allFav ? $"已取消收藏 {items.Count} 项" : $"已收藏 {items.Count} 项";

        FavoriteCount = (int)await _repo.GetFavoriteCountAsync();
        if (!allFav)
            CheckFavoriteMilestone(FavoriteCount);
    }

    private static readonly long[] FavoriteMilestones = { 10, 50, 100, 500, 1000 };

    private readonly HashSet<long> _milestonesShown = new();

    /// <summary>收藏数跨过阈值时状态栏闪一行提示，2 秒后消失。</summary>
    private void CheckFavoriteMilestone(int count)
    {
        foreach (var m in FavoriteMilestones)
        {
            if (count >= m && _milestonesShown.Add(m))
            {
                _ = FlashStatusAsync($"你已收藏 {m} 张");
                break;
            }
        }
    }

    private async Task FlashStatusAsync(string message)
    {
        var prev = StatusText;
        StatusText = message;
        await Task.Delay(2000);
        if (StatusText == message)
            StatusText = prev;
    }

    public async Task BatchAssignCategoryAsync(IReadOnlyList<ThumbnailItemViewModel> items, long categoryId)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            await _repo.AddMediaCategoryAsync(item.Id, categoryId);
            if (!item.CategoryIds.Contains(categoryId))
                item.CategoryIds.Add(categoryId);
        }
        foreach (var item in items)
            UpdateCategoryMark(item);

        var name = CategoryItems.FirstOrDefault(c => c.Id == categoryId)?.Name ?? "分类";
        ShowActionToast(items.Count == 1
            ? $"✅ 已加入分类【{name}】"
            : $"✅ 已将 {items.Count} 项加入分类【{name}】");
        StatusText = $"已将 {items.Count} 项加入分类「{name}」";

        // 拖拽/批量也走这条路：如果当前详情面板正开着被拖的其中之一，
        // 「所属分类」必须立刻把新分类列出来，否则右侧看起来像没生效。
        if (SelectedItem != null && items.Any(i => i.Id == SelectedItem.Id))
            await RefreshDetailAsync(SelectedItem);
    }

    /// <summary>切换单张图片是否属于某分类（已加入→移出，未加入→加入）。</summary>
    public async Task ToggleCategoryAsync(ThumbnailItemViewModel item, long categoryId)
    {
        if (item.CategoryIds.Contains(categoryId))
        {
            await _repo.RemoveMediaCategoryAsync(item.Id, categoryId);
            item.CategoryIds.Remove(categoryId);
        }
        else
        {
            await _repo.AddMediaCategoryAsync(item.Id, categoryId);
            item.CategoryIds.Add(categoryId);
        }
        UpdateCategoryMark(item);
    }

    /// <summary>刷新单个缩略图的分类标记（名称 + 是否已分类）。</summary>
    private void UpdateCategoryMark(ThumbnailItemViewModel item)
    {
        var nameById = CategoryItems.ToDictionary(c => c.Id, c => c.Name);
        var names = item.CategoryIds
            .Where(id => nameById.ContainsKey(id))
            .Select(id => nameById[id])
            .ToList();
        item.CategoryNames = string.Join("、", names);
        item.RefreshCategoryState();
    }

    /// <summary>批量打标签（把网格里选中的媒体拖到侧栏标签上）。重复的由 INSERT OR IGNORE 忽略。</summary>
    public async Task BatchAssignTagAsync(IReadOnlyList<ThumbnailItemViewModel> items, long tagId)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
            await _repo.AddMediaTagAsync(item.Id, tagId);

        // 和「拖到分类」保持同一种反馈：底部轻提示 + 状态栏，不再只有状态栏
        var name = TagItems.FirstOrDefault(t => t.RelatedId == tagId)?.Title ?? "标签";
        ShowActionToast(items.Count == 1
            ? $"✅ 已打上标签【{name}】"
            : $"✅ 已给 {items.Count} 项打上标签【{name}】");
        StatusText = $"已给 {items.Count} 项打上标签「{name}」";
    }

    public async Task BatchSetRatingAsync(IReadOnlyList<ThumbnailItemViewModel> items, int rating)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            item.Rating = rating;
            await _repo.SetRatingAsync(item.Id, rating);
        }
        StatusText = $"已给 {items.Count} 项打 {rating} 星";
    }

    [RelayCommand]
    private async Task RemoveCategoryAsync(Category? category)
    {
        if (category == null || SelectedItem == null) return;

        await _repo.RemoveMediaCategoryAsync(SelectedItem.Id, category.Id);
        SelectedItem.CategoryIds.Remove(category.Id);
        UpdateCategoryMark(SelectedItem);
        ShowActionToast($"已从分类【{category.Name}】移出");
        await RefreshDetailAsync(SelectedItem);
    }

    /// <summary>标记单个文件为缺失（文件已不在原位置时调用）。</summary>
    public async Task MarkMissingAsync(ThumbnailItemViewModel item)
    {
        item.Media.IsMissing = true;
        await _repo.MarkMissingSingleAsync(item.Id);
        MediaItems.Remove(item);
        StatusText = $"已标记缺失：{item.FileName}";
    }

    /// <summary>记录一次查看（最近查看）。</summary>
    public async Task RecordViewAsync(long mediaId)
    {
        await _repo.RecordRecentViewAsync(mediaId);
    }

    // ---------- 拖动排序 ----------

    /// <summary>保存标签顺序（拖动排序后调用）。</summary>
    public async Task SaveTagOrderAsync()
    {
        var orders = new List<(long Id, int SortOrder)>();
        for (int i = 0; i < TagItems.Count; i++)
        {
            if (TagItems[i].RelatedId is long id)
                orders.Add((id, i + 1));
        }
        if (orders.Count > 0)
            await _repo.UpdateTagSortOrderAsync(orders);
    }

    /// <summary>保存分类顺序（拖动排序后调用）。</summary>
    public async Task SaveCategoryOrderAsync()
    {
        var orders = new List<(long Id, int SortOrder)>();
        for (int i = 0; i < CategoryItems.Count; i++)
            orders.Add((CategoryItems[i].Id, i + 1));
        if (orders.Count > 0)
            await _repo.UpdateCategorySortOrderAsync(orders);
    }

    /// <summary>将标签移动到指定索引（拖动松手时调用）。</summary>
    public void MoveTag(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= TagItems.Count) return;
        if (toIndex < 0 || toIndex >= TagItems.Count) return;
        if (fromIndex == toIndex) return;
        var item = TagItems[fromIndex];
        TagItems.Move(fromIndex, toIndex);
    }

    /// <summary>将分类移动到指定索引。</summary>
    public void MoveCategory(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= CategoryItems.Count) return;
        if (toIndex < 0 || toIndex >= CategoryItems.Count) return;
        if (fromIndex == toIndex) return;
        var item = CategoryItems[fromIndex];
        CategoryItems.Move(fromIndex, toIndex);
    }

    // ---------- 发现页命令 ----------

    /// <summary>换一批（仅发现页有效）。</summary>
    [RelayCommand]
    private async Task RefreshDiscoveryAsync()
    {
        if (!IsDiscoveryView) return;
        var media = await GenerateDiscoveryBatchAsync(forceRefresh: true);
        MediaItems.Clear();
        foreach (var m in media)
        {
            var vm = new ThumbnailItemViewModel(m)
            {
                ThumbnailPath = m.ThumbnailPath,
                IsFavorite = m.Favorite,
                Rating = m.Rating,
                Note = m.Note,
            };
            MediaItems.Add(vm);
            EnsureThumbnail(m);
        }
        StatusText = _discoveryFallbackToAll
            ? $"已换一批 · {MediaItems.Count} 项（收藏还是空的，这次看全库）"
            : $"已换一批 · {MediaItems.Count} 项";
    }

    /// <summary>池子类型切换。</summary>
    partial void OnDiscoveryPoolIndexChanged(int value)
    {
        if (value < 0 || value >= DiscoveryPoolOptions.Count) return;
        var pool = (DiscoveryPoolType)value;
        if (pool == _discoveryPool) return;
        _discoveryPool = pool;
        _ = SaveDiscoveryPoolAndReloadAsync();
    }

    /// <summary>「指定分类」选择变化。</summary>
    [RelayCommand]
    private async Task SelectDiscoveryCategoryAsync(Category? category)
    {
        if (category == null) return;
        _discoveryPoolId = category.Id;
        await _repo.SetSettingAsync("DiscoveryPoolId", category.Id.ToString());
        if (_discoveryPool == DiscoveryPoolType.Category)
            await LoadMediaAsync();
    }

    private async Task SaveDiscoveryPoolAndReloadAsync()
    {
        await _repo.SetSettingAsync("DiscoveryPoolType", _discoveryPool.ToString());
        if (_currentViewKey == "discovery")
            await LoadMediaAsync();
    }
}
