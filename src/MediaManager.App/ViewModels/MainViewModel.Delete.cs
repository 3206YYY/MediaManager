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
using MediaManager.App.Services;
using MediaManager.App.Views.Dialogs;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.ViewModels;

/// <summary>主窗口视图模型 —— 删除：删除确认、回收站、批量删除与点删模式、撤销。</summary>
public partial class MainViewModel
{
    /// <summary>底部轻提示是否显示。</summary>
    [ObservableProperty]
    private bool _isActionToastVisible;

    /// <summary>底部轻提示文案（如「✅ 已加入分类【旅行】」）。</summary>
    [ObservableProperty]
    private string _actionToastText = string.Empty;

    private System.Windows.Threading.DispatcherTimer? _actionToastTimer;

    /// <summary>
    /// 弹一条 1.8 秒的轻提示。连续调用会重置计时（拖 10 张进去不会叠 10 条，只留最后一条）。
    /// 与撤销条分工：撤销条带按钮、5 秒；轻提示只说结果。
    /// </summary>
    private void ShowActionToast(string text)
    {
        ActionToastText = text;
        IsActionToastVisible = true;

        _actionToastTimer?.Stop();
        _actionToastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1800),
        };
        _actionToastTimer.Tick += (_, _) =>
        {
            _actionToastTimer?.Stop();
            _actionToastTimer = null;
            IsActionToastVisible = false;
        };
        _actionToastTimer.Start();
    }

    // ---------- 最近删除 / 撤销 ----------

    /// <summary>底部撤销提示条是否显示。</summary>
    [ObservableProperty]
    private bool _isUndoToastVisible;

    /// <summary>撤销提示条文案（如「已删除 3 个项目」）。</summary>
    [ObservableProperty]
    private string _undoToastText = string.Empty;

    /// <summary>撤销倒计时秒数（5 → 0）。</summary>
    [ObservableProperty]
    private int _undoSecondsLeft = 5;

    private List<DeletedMedia> _pendingUndo = new();

    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    private System.Windows.Threading.DispatcherTimer? _discoveryHintTimer;

    /// <summary>
    /// 发现页首次提示：显示 8 秒后自己收起来。
    /// 不能再靠「下一次 LoadDiscovery 顺手关掉」——发现页会因文件监视器 / 缩略图刷新
    /// 再加载一次，那样提示只闪一下就没了（v1.4.5 用户只看到缩略图缝里露出的半行字）。
    /// </summary>
    private void ShowDiscoveryHintForAWhile()
    {
        DiscoveryShowHint = true;
        _discoveryHintTimer?.Stop();
        _discoveryHintTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        _discoveryHintTimer.Tick += (_, _) =>
        {
            _discoveryHintTimer?.Stop();
            DiscoveryShowHint = false;
        };
        _discoveryHintTimer.Start();
    }

    /// <summary>离开发现页时把提示收掉，别让它跟着飘到别的视图上。</summary>
    partial void OnIsDiscoveryViewChanged(bool value)
    {
        if (!value)
        {
            _discoveryHintTimer?.Stop();
            DiscoveryShowHint = false;
        }
    }

    /// <summary>删除成功后弹出 5 秒撤销条。</summary>
    private void ShowUndoToast(IReadOnlyList<DeletedMedia> records)
    {
        _pendingUndo = records.Where(r => r != null).ToList();
        if (_pendingUndo.Count == 0) return;

        UndoToastText = $"已删除 {_pendingUndo.Count} 个项目";
        UndoSecondsLeft = 5;
        IsUndoToastVisible = true;

        _undoTimer?.Stop();
        _undoTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _undoTimer.Tick += (_, _) =>
        {
            UndoSecondsLeft--;
            if (UndoSecondsLeft <= 0)
                HideUndoToast();
        };
        _undoTimer.Start();
    }

    private void HideUndoToast()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        IsUndoToastVisible = false;
        _pendingUndo = new List<DeletedMedia>();
        // 5 秒一过，本轮「点谁删谁」的累计也失效；否则隔很久再点一张，
        // 撤销条会冒出「已删除 12 个项目」并把早先已过期的文件一起算进去。
        _tapDeleteRecords.Clear();
    }

    /// <summary>点击「撤销」：把文件从系统回收站还原回原位置，并恢复媒体库记录。</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_pendingUndo.Count == 0)
        {
            HideUndoToast();
            return;
        }

        var records = _pendingUndo.ToList();
        _undoTimer?.Stop();
        _undoTimer = null;

        int ok = 0;
        var failures = new List<string>();

        foreach (var record in records)
        {
            try
            {
                var (success, message) = await _deletedItems.RestoreAsync(record);
                if (success) ok++;
                else failures.Add($"{record.FileName}：{message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "撤销删除失败：{Path}", record.Path);
                failures.Add($"{record.FileName}：{StorageErrors.FriendlyMessage(ex)}");
            }
        }

        HideUndoToast();
        await LoadMediaAsync();
        await RefreshStatusAsync();

        if (failures.Count == 0)
        {
            StatusText = $"已撤销删除，{ok} 个文件已还原";
        }
        else
        {
            StatusText = $"撤销完成：成功 {ok}，失败 {failures.Count}";
            CustomMessageBox.Warn(
                $"有 {failures.Count} 个文件没能还原：\n\n{string.Join("\n", failures.Take(5))}"
                + (failures.Count > 5 ? "\n…" : "") +
                "\n\n如果是移动硬盘已拔出，请重新插好后再到「最近删除」里还原。",
                "撤销失败");
        }
    }

    /// <summary>清理缩略图缓存后调用：清空内存与数据库里的缓存路径，并重新排队生成。</summary>
    public async Task ResetThumbnailCacheAsync()
    {
        try
        {
            await _repo.ClearThumbnailPathsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清空数据库缩略图路径失败");
        }

        foreach (var vm in MediaItems)
        {
            vm.ThumbnailPath = null;
            vm.Media.ThumbnailPath = null;
        }

        // 内存里的解码结果也一起丢掉，否则「清缓存」后仍占着上百 MB
        Services.ThumbnailMemoryCache.Clear();

        EnsureThumbnails(MediaItems.Select(v => v.Media).ToList());
    }

    /// <summary>按指定的删除方式删。所有删除入口都走这里，别再各写一份写死的回收站删除。</summary>
    private Task DeleteFileAsync(string path, DeleteBehavior behavior)
        => DeletePolicy.DeleteAsync(_fileOps, path, behavior);

    public async Task BatchDeleteAsync(IReadOnlyList<ThumbnailItemViewModel> items)
    {
        if (items.Count == 0) return;

        // 文案与「要不要问」全由 DeletePolicy / DeletePrompt 决定：
        // 永久删除时正文写「无法恢复」，设置了「每次询问」就当场三选一。
        var chosen = DeletePrompt.Ask(_config.DeleteBehavior, items.Count,
            confirmPermanent: _config.ConfirmPermanentDelete);
        if (chosen is null) return;
        var behavior = chosen.Value;

        var deleted = new List<ThumbnailItemViewModel>();
        var failures = new List<(string Path, Exception Error)>();
        var records = new List<DeletedMedia>();
        var batchId = Guid.NewGuid().ToString("N");

        foreach (var item in items)
        {
            try
            {
                await DeleteFileAsync(item.Path, behavior);
                await _opLog.LogAsync(DeletePolicy.LogAction(behavior), item.Path, null);

                // 快照要在删库之前抓，否则标签/分类关联已经没了。
                // 永久删除不留快照：文件回不来了，留个「已删除」条目只会让还原按钮骗人。
                if (DeletePolicy.CanUndo(behavior))
                    records.Add(await _deletedItems.TrackAsync(item.Media, item.ThumbnailPath, batchId));

                await _repo.DeleteMediaAsync(item.Id);
                deleted.Add(item);
            }
            catch (Exception ex)
            {
                // 单个失败不影响其余文件继续删（典型：某个盘被拔出、文件被占用）
                _logger.LogWarning(ex, "批量删除失败：{Path}", item.Path);
                failures.Add((item.Path, ex));
            }
        }

        foreach (var item in deleted)
            MediaItems.Remove(item);
        // 同上：只有被删的正是详情面板那一项时才清空，别把剩下的选择连坐清掉
        if (SelectedItem != null && deleted.Contains(SelectedItem)) SelectedItem = null;
        ResetEmptyCopyAfterRemoval();
        await RefreshStatusAsync();

        if (failures.Count > 0)
        {
            var detail = StorageErrors.FriendlyMessage(failures[0].Error);
            var summary = $"已删除 {deleted.Count} 个，{failures.Count} 个失败。\n\n{detail}";
            if (failures.Count == 1)
                summary += $"\n\n失败文件：{System.IO.Path.GetFileName(failures[0].Path)}";
            else
                summary += $"\n\n失败示例：{System.IO.Path.GetFileName(failures[0].Path)}";

            CustomMessageBox.Warn(summary, "部分文件删除失败");
            StatusText = $"删除完成：成功 {deleted.Count}，失败 {failures.Count}";
        }
        else
        {
            StatusText = DeletePolicy.StatusAfterDelete(behavior, deleted.Count);
        }

        if (records.Count > 0)
            ShowUndoToast(records);
    }

    // ---------- 删除模式（「点谁删谁」） ----------
    // 顶部按钮启用后，单击缩略图就走这里。和普通批量删除的区别只有两点：
    //   1. 进回收站时**不弹确认框** —— 用户已经主动进入了一个写着「点谁删谁」的模式，
    //      再弹窗等于没做；安全靠「系统回收站 + 5 秒撤销 + 常驻红色横幅 + Esc 一键退出」兜底。
    //      ⚠ 但这条「不弹」只对回收站成立：设置为永久删除、或设置为「每次询问」时，
    //      每一次单击都必须弹确认 —— 否则用户在零确认、零撤销的情况下把文件删干净
    //      （v1.4.8 用户指出：界面还写着「5 秒内可撤销」，实际永久删除根本没有撤销）。
    //   2. 连续点删的文件合并成一次撤销 —— 连点 10 张只显示一条「已删除 10 个项目」，
    //      点撤销一次全还原，而不是弹 10 条 toast 且只救得回最后一张。
    // 底层仍然是 DeletePolicy.DeleteAsync / TrackAsync / DeleteMediaAsync，绝不绕过任何一步。

    private readonly List<DeletedMedia> _tapDeleteRecords = new();

    /// <summary>进入删除模式时开启一轮新的撤销累计。</summary>
    public void BeginTapDeleteSession() => _tapDeleteRecords.Clear();

    /// <summary>退出删除模式时结束本轮累计（已弹出的撤销条仍保留到 5 秒到期）。</summary>
    public void EndTapDeleteSession() => _tapDeleteRecords.Clear();

    /// <summary>删除模式下单击一个文件，返回是否成功（用户取消也算未删）。</summary>
    public async Task<bool> DeleteOnTapAsync(ThumbnailItemViewModel item)
    {
        var chosen = DeletePrompt.Ask(_config.DeleteBehavior, 1, item.FileName,
            confirmPermanent: _config.ConfirmPermanentDelete,
            silentWhenRecycle: true);
        if (chosen is null) return false;
        var behavior = chosen.Value;

        try
        {
            await DeleteFileAsync(item.Path, behavior);
            await _opLog.LogAsync(DeletePolicy.LogAction(behavior), item.Path, null);

            // 快照要在删库之前抓，否则标签 / 分类关联已经没了，撤销恢复不全。
            // 永久删除不留快照（文件回不来，撤销条也弹不出来）。
            DeletedMedia? record = null;
            if (DeletePolicy.CanUndo(behavior))
            {
                record = await _deletedItems.TrackAsync(item.Media, item.ThumbnailPath,
                    Guid.NewGuid().ToString("N"));
            }

            await _repo.DeleteMediaAsync(item.Id);
            MediaItems.Remove(item);
            // 只清「详情面板正在看的那一项」。以前这里无条件 `SelectedItem = null`，
            // 它是 TwoWay 绑到 ListBox.SelectedItems 的：点删模式下删掉一张，
            // 手上勾好的另外 5 张会被一起清空（v1.4.8 实测 5 → 0）。
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            ResetEmptyCopyAfterRemoval();
            await RefreshStatusAsync();

            if (record != null) _tapDeleteRecords.Add(record);
            if (DeletePolicy.CanUndo(behavior))
            {
                ShowUndoToast(_tapDeleteRecords.ToList());
                StatusText = $"已删除 {_tapDeleteRecords.Count} 个文件";
            }
            else
            {
                StatusText = DeletePolicy.StatusAfterDelete(behavior, 1);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "点选删除失败：{Path}", item.Path);
            CustomMessageBox.Warn(StorageErrors.FriendlyMessage(ex), "删除失败");
            return false;
        }
    }

    // ---------- 移出分类（多分类支持：详情面板里可以单独移出某一个） ----------
}
