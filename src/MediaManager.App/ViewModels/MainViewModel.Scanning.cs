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

/// <summary>主窗口视图模型 —— 扫描：根目录增删、全盘与单目录扫描、文件系统监视、新文件提示。</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        // 系统原生文件夹选择器（IFileOpenDialog + FOS_PICKFOLDERS）：
        // 左侧导航、网络位置、新建文件夹等行为与资源管理器完全一致，不再自绘选择器。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择媒体文件夹",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        // 统一成反斜杠的完整路径：否则 C:/a/b 与 C://a//b 会被当成两个不同路径，
        // 扫描时会往库里插入重复记录（尤其是重命名后再增量扫描）。
        var path = System.IO.Path.GetFullPath(dialog.FolderName);
        var serial = VolumeHelper.GetVolumeSerial(path);
        var relative = VolumeHelper.GetRelativePath(path);

        // 检测是否已添加过（AddRootFolderAsync 已幂等，这里提前提示更友好）
        var existing = FolderItems.FirstOrDefault(f => f.RelatedId.HasValue
            && string.Equals(f.ToolTipText, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            StatusText = $"文件夹已存在：{path}";
            // 直接跳到该文件夹视图
            _currentViewKey = "folder";
            _currentRootFolderId = existing.RelatedId;
            await LoadMediaAsync();
            return;
        }

        // 嵌套拦截：媒体记录按 Path 唯一，扫父目录时子目录里的文件已经入库并归属父级。
        // 这时再把子目录当独立根目录加进来，它名下永远 0 条 —— 用户点进去就是「共 0 项」，
        // 刷新扫描都救不回来（除非按路径查，见 MediaFilter.FolderPath）。
        var existingPaths = FolderItems.Where(f => f.RelatedId.HasValue)
            .Select(f => f.ToolTipText ?? f.Title)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var verdict = FolderNestingPolicy.Evaluate(path, existingPaths, out var coveringRoot, out var containedRoots);

        if (verdict == FolderAddVerdict.CoveredByExisting)
        {
            var owner = FolderItems.FirstOrDefault(f => f.RelatedId.HasValue
                && string.Equals(FolderNestingPolicy.Normalize(f.ToolTipText), FolderNestingPolicy.Normalize(coveringRoot),
                    StringComparison.OrdinalIgnoreCase));
            StatusText = $"「{path}」已经在库中（属于「{coveringRoot}」），不需要单独添加";
            CustomMessageBox.Info(
                "这个文件夹已经在媒体库里了。\n\n" +
                $"它属于已添加的「{coveringRoot}」——里面的文件点开那个文件夹就能看到，" +
                "再单独添加一次只会多出一个永远查不到内容的空文件夹。\n\n" +
                "现在直接带你去看那个文件夹。",
                "不需要重复添加");
            if (owner?.RelatedId is long ownerId)
            {
                _currentViewKey = "folder";
                _currentRootFolderId = ownerId;
                await LoadMediaAsync();
            }
            return;
        }

        try
        {
            var id = await _repo.AddRootFolderAsync(path, serial, relative);

            // 反过来：新加的是某些已有根目录的上级（先加了子目录、后加父目录）。
            // 两份根目录并存会让同一个文件有两套「归属」、扫描互相打架 —— 合并成一份。
            if (containedRoots.Count > 0)
            {
                var childIds = FolderItems
                    .Where(f => f.RelatedId.HasValue && containedRoots.Any(c =>
                        string.Equals(FolderNestingPolicy.Normalize(f.ToolTipText), c, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => f.RelatedId!.Value)
                    .ToList();

                // 保险：绝不合并保险箱锚点目录（它的 Path 也挂在 RootFolder 表里）
                var vaultAnchor = FolderNestingPolicy.Normalize(System.IO.Path.Combine(_config.DataDirectory, "Vault"));
                childIds = childIds.Where(cid =>
                {
                    var p = FolderItems.FirstOrDefault(x => x.RelatedId == cid)?.ToolTipText;
                    return !string.Equals(FolderNestingPolicy.Normalize(p), vaultAnchor, StringComparison.OrdinalIgnoreCase);
                }).ToList();

                if (childIds.Count > 0)
                {
                    var moved = await _repo.MergeRootFoldersIntoAsync(id, childIds);
                    foreach (var cid in childIds) _watcher.Stop(cid);
                    _logger.LogInformation("已把 {Count} 个子文件夹合并进 {Path}（迁移 {Moved} 条记录）",
                        childIds.Count, path, moved);
                    StatusText = $"「{path}」包含已添加的子文件夹，已合并（迁移 {moved} 条记录）";
                }
            }

            await LoadNavigationAsync();
            _watcher.Start(id, path);

            await ScanFolderAsync(id);

            // 扫描后如果这个文件夹里没有任何受支持的文件，弹窗说明原因
            // （典型场景：关掉“扩展格式”后选了一个只有 zip 的文件夹）
            var count = await DbAsync(() => _repo.CountAsync(new MediaFilter { RootFolderId = id }));
            if (count == 0)
            {
                StatusText = "该文件夹里没有当前支持的媒体文件。";
                CustomMessageBox.Info(
                    "这个文件夹里没有当前支持的媒体文件。\n\n" +
                    "如果里面有音频 / 文档 / 压缩包，请到 设置 → 插件扩展 启用「扩展格式支持」，再扫描一次。",
                    "没有可管理的文件");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加文件夹失败：{Path}", path);
            StatusText = "添加文件夹失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(NavigationItemViewModel? folder)
    {
        if (folder?.RelatedId == null) return;

        var confirm = CustomMessageBox.Confirm(
            $"确定从媒体库中移除文件夹「{folder.Title}」？\n\n" +
            "只会移除该文件夹的收录记录，磁盘上的文件不会被删除。",
            "移除文件夹", confirmText: "移除");
        if (!confirm) return;

        var rootId = folder.RelatedId.Value;
        var path = folder.ToolTipText ?? folder.Title;

        // 停止对该目录的监控，避免删除过程中触发增量扫描
        _watcher.Stop(rootId);

        // 删除前先取出该文件夹下的媒体项，用于清理缩略图缓存
        List<MediaItem> toClean = new();
        try
        {
            toClean = await _repo.QueryAsync(new MediaFilter { RootFolderId = rootId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移除文件夹前查询媒体项失败：{Path}", path);
        }

        await _repo.RemoveRootFolderAsync(rootId);
        await _opLog.LogAsync("移除文件夹", path, null);

        // 清理缩略图缓存
        foreach (var m in toClean)
        {
            try
            {
                var cachePath = m.ThumbnailPath ?? _thumbnailService.GetCachedPath(m);
                if (!string.IsNullOrWhiteSpace(cachePath) && System.IO.File.Exists(cachePath))
                    System.IO.File.Delete(cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理缩略图缓存失败：{Path}", m.Path);
            }
        }

        // 若当前视图正是该文件夹，切回「全部媒体」，避免停留在已删除的文件夹视图
        if (_currentViewKey == "folder" && _currentRootFolderId == rootId)
        {
            _currentViewKey = "all";
            _currentRootFolderId = null;
            IsDiscoveryView = false;
        }

        await LoadNavigationAsync();
        await LoadMediaAsync();
        StatusText = $"已移除文件夹：{path}";
    }

    [RelayCommand]
    private async Task ScanAllAsync()
    {
        // 选中了某个文件夹就只扫它 —— 用户点「扫描」是想让当前这个文件夹刷新，
        // 「无头苍蝇式全局扫描」在移动硬盘离线时还会把整库拖慢。
        var scanTarget = _currentRootFolderId;
        if (scanTarget == null && _currentFolderPath is { Length: > 0 } subPath)
        {
            // 子文件夹本身没有 RootFolder 记录，扫描它所属的那个根目录
            scanTarget = FolderItems.FirstOrDefault(f => f.RelatedId.HasValue
                    && FolderNestingPolicy.IsUnder(subPath, f.ToolTipText ?? string.Empty))?.RelatedId;
        }
        if (_currentViewKey == "folder" && scanTarget.HasValue)
        {
            await ScanFolderAsync(scanTarget.Value);
            return;
        }

        foreach (var f in FolderItems.Where(x => x.RelatedId.HasValue))
        {
            await ScanFolderAsync(f.RelatedId!.Value);
        }
    }

    private async Task ScanFolderAsync(long rootFolderId)
    {
        if (IsScanning) return;
        IsScanning = true;
        ScanProgressText = "扫描中…";
        ScanProgress = 0;

        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressText = $"{p.ScannedFiles} 个文件";
            if (p.Total > 0)
                ScanProgress = p.IsCompleted ? 100 : (double)p.ScannedFiles / Math.Max(1, p.Total) * 100;
            StatusText = p.IsCompleted
                ? $"扫描完成：新增 {p.Added}，更新 {p.Updated}，缺失 {p.MarkedMissing}"
                : $"扫描中 {p.ScannedFiles} 项";
        });

        try
        {
            await _scanService.ScanRootFolderAsync(rootFolderId, progress, _scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            // 典型：扫描途中移动硬盘被拔出、或某个子目录无权限
            _logger.LogError(ex, "扫描失败：根目录 {Id}", rootFolderId);
            StatusText = StorageErrors.IsDeviceUnavailable(ex)
                ? "扫描中断：设备已断开，请重新连接移动硬盘后重新扫描"
                : "扫描失败：" + StorageErrors.FriendlyMessage(ex);
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = string.Empty;
            ScanProgress = 0;
            _scanCts?.Dispose();
            _scanCts = null;

            // 扫描后刷新缩略图（新项）
            await LoadMediaAsync();
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>
    /// 停掉所有会读写数据库 / 磁盘的界面侧后台任务。
    /// 只在「恢复历史快照」这种要直接覆盖 library.db 的场景调用 ——
    /// 通道里只要还有一条查询在跑，覆盖就可能撞上正在打开的连接。
    /// </summary>
    public void CancelBackgroundWork()
    {
        try { CancelScan(); } catch { /* 已结束 */ }
        try { _filterCts?.Cancel(); } catch { /* 已结束 */ }
        try { _searchCts?.Cancel(); } catch { /* 已结束 */ }
        try { _detailCts?.Cancel(); } catch { /* 已结束 */ }
    }

    private async void OnFileSystemChanged(object? sender, FileChangeBatch batch)
    {
        // 该事件来自 FileSystemWatcher 的后台线程。DispatcherTimer 必须建在 UI 线程的
        // Dispatcher 上，否则计时器没有消息循环、永远不会 Tick（提示条也就永远不会自动加入）。
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => OnFileSystemChanged(sender, batch));
            return;
        }

        var root = FolderItems.FirstOrDefault(x => x.RelatedId == batch.RootFolderId);
        if (root == null) return;

        // 提示条还没处理完时，后续的 Changed/Size 事件会再触发一次静默刷新，
        // 那会把文件直接加进库（等于绕过用户选择）。这里直接忽略。
        if (_pendingNewFiles.ContainsKey(batch.RootFolderId)) return;

        // 有新增的图片/视频：不直接入静默库，先在角落问一句「是否加入库」，
        // 让用户明确知道刚才拷进来的东西被发现了（而不是要他自己去点刷新）。
        if (batch.NewMediaFiles > 0)
        {
            ShowNewFilesToast(batch.RootFolderId, batch.NewMediaFiles);
            return;
        }

        await SilentRefreshAsync(batch.RootFolderId);
    }

    /// <summary>静默增量刷新（删除/重命名/修改内容时用，不需要打扰用户）。</summary>
    private async Task SilentRefreshAsync(long rootFolderId)
    {
        var root = FolderItems.FirstOrDefault(x => x.RelatedId == rootFolderId);
        if (root == null) return;

        try
        {
            await _scanService.ScanIncrementalAsync(rootFolderId, root.ToolTipText ?? root.Title);
            await LoadMediaAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "增量扫描失败：根目录 {Id}", rootFolderId);
        }
    }

    // ---------- 「发现 N 个新文件」提示条 ----------

    [ObservableProperty]
    private bool _isNewFilesToastVisible;

    [ObservableProperty]
    private string _newFilesToastText = string.Empty;

    [ObservableProperty]
    private int _newFilesSecondsLeft;

    /// <summary>待确认的根目录及其新增数量。</summary>
    private readonly Dictionary<long, int> _pendingNewFiles = new();

    private System.Windows.Threading.DispatcherTimer? _newFilesTimer;

    private const int NewFilesTimeoutSeconds = 12;

    private void ShowNewFilesToast(long rootFolderId, int count)
    {
        _pendingNewFiles.TryGetValue(rootFolderId, out var old);
        _pendingNewFiles[rootFolderId] = old + count;

        var total = _pendingNewFiles.Values.Sum();
        NewFilesToastText = $"发现 {total} 个新文件，是否加入库？";
        NewFilesSecondsLeft = NewFilesTimeoutSeconds;
        IsNewFilesToastVisible = true;

        // 已有计时器时只重置倒计时，不重建（避免提示条闪烁）
        if (_newFilesTimer == null)
        {
            _newFilesTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _newFilesTimer.Tick += (_, _) =>
            {
                NewFilesSecondsLeft--;
                if (NewFilesSecondsLeft > 0) return;

                // 超时视为「加入」：用户可能正埋头干别的，但库不能一直缺文件
                _ = AcceptNewFilesAsync(auto: true);
            };
        }

        _newFilesTimer.Stop();
        _newFilesTimer.Start();
    }

    private void HideNewFilesToast()
    {
        _newFilesTimer?.Stop();
        _newFilesTimer = null;
        IsNewFilesToastVisible = false;
        _pendingNewFiles.Clear();
    }

    /// <summary>用户点「加入库」。</summary>
    [RelayCommand]
    private Task JoinNewFilesAsync() => AcceptNewFilesAsync(auto: false);

    /// <summary>把新发现的文件加入媒体库（auto=true 表示是超时自动执行）。</summary>
    private async Task AcceptNewFilesAsync(bool auto)
    {
        var roots = _pendingNewFiles.Keys.ToList();
        var total = _pendingNewFiles.Values.Sum();
        HideNewFilesToast();
        if (roots.Count == 0) return;

        StatusText = auto ? $"正在自动加入 {total} 个新文件…" : $"正在加入 {total} 个新文件…";

        foreach (var id in roots)
            await SilentRefreshAsync(id);

        StatusText = auto
            ? $"已自动加入 {total} 个新文件（可随时在列表里查看）"
            : $"已加入 {total} 个新文件";
        await RefreshStatusAsync();
    }

    /// <summary>忽略这批新文件（不入库，下次再有变动时会重新提示）。</summary>
    [RelayCommand]
    private void IgnoreNewFiles()
    {
        HideNewFilesToast();
        StatusText = "已忽略这批新文件（文件仍在磁盘上，随时可手动扫描加入）";
    }

    // ---------- 缩略图大小 ----------

    partial void OnThumbnailSizeChanged(int value)
    {
        // 触发网格重排（绑定到 ItemWidth/Height）
        OnPropertyChanged(nameof(ThumbnailSize));
    }

    // ---------- 标签管理 ----------

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var name = CustomPrompt.Show("给这个标签起个名字：", "新建标签");
        if (string.IsNullOrWhiteSpace(name)) return;

        await _repo.AddTagAsync(new Tag { Name = name.Trim() });
        await LoadNavigationAsync();
    }
}
