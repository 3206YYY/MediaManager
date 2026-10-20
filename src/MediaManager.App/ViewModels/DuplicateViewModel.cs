using MediaManager.App.Views.Dialogs;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Models;
using MediaManager.Core.Services;

namespace MediaManager.App.ViewModels;

/// <summary>重复媒体项（去重窗口内，含删除勾选状态）。</summary>
public partial class DuplicateItemViewModel : ObservableObject
{
    public DuplicateItemViewModel(MediaItem media)
    {
        Media = media;
    }

    public MediaItem Media { get; }

    [ObservableProperty]
    private bool _isSelectedForDelete;

    public long Id => Media.Id;
    public string Path => Media.Path;
    public string FileName => Media.FileName;
    public string SizeDisplay => ThumbnailItemViewModel.FormatSize(Media.SizeBytes);
    public string? ThumbnailPath => Media.ThumbnailPath;
}

/// <summary>重复组视图模型。</summary>
public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        SizeDisplay = ThumbnailItemViewModel.FormatSize(group.SizeBytes);
        foreach (var item in group.Items)
        {
            // 默认保留第一张，其余勾选删除
            var vm = new DuplicateItemViewModel(item) { IsSelectedForDelete = Items.Count > 0 };
            Items.Add(vm);
        }
    }

    public string SizeDisplay { get; }
    public ObservableCollection<DuplicateItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>去重窗口视图模型。</summary>
public partial class DuplicateViewModel : ObservableObject
{
    private readonly IDuplicateService _duplicateService;
    private readonly IFileOperationService _fileOps;
    private readonly IMediaRepository _repo;
    private readonly Services.DeletedItemsService _deletedItems;

    public DuplicateViewModel(
        IDuplicateService duplicateService,
        IFileOperationService fileOps,
        IMediaRepository repo,
        Services.DeletedItemsService deletedItems)
    {
        _duplicateService = duplicateService;
        _fileOps = fileOps;
        _repo = repo;
        _deletedItems = deletedItems;
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private string _statusText = "点击「开始检测」查找重复文件";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _deleteSummary = string.Empty;

    [RelayCommand]
    private async Task StartAsync()
    {
        IsScanning = true;
        StatusText = "正在检测重复文件…";
        Groups.Clear();

        var progress = new Progress<string>(s => StatusText = s);
        try
        {
            var groups = await _duplicateService.FindDuplicatesAsync(progress);
            foreach (var g in groups)
                Groups.Add(new DuplicateGroupViewModel(g));

            HasResult = Groups.Count > 0;
            UpdateDeleteSummary();
            StatusText = HasResult
                ? $"找到 {Groups.Count} 组重复文件"
                : "未找到重复文件";
        }
        catch (OperationCanceledException)
        {
            StatusText = "检测已取消";
        }
        catch (Exception ex)
        {
            StatusText = "检测失败：" + StorageErrors.FriendlyMessage(ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => StatusText = "已请求取消";

    private void UpdateDeleteSummary()
    {
        int total = 0;
        foreach (var g in Groups)
            foreach (var item in g.Items)
                if (item.IsSelectedForDelete) total++;
        DeleteSummary = $"将删除 {total} 个文件";
    }

    public void RefreshSummary() => UpdateDeleteSummary();

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var toDelete = Groups
            .SelectMany(g => g.Items)
            .Where(i => i.IsSelectedForDelete)
            .ToList();
        if (toDelete.Count == 0)
        {
            StatusText = "请先勾选要删除的文件";
            return;
        }

        var confirm = CustomMessageBox.ConfirmDanger(
            $"将把选中的 {toDelete.Count} 个重复文件移入回收站。\n\n建议先确认它们确实是重复文件。", "确认删除");
        if (!confirm) return;

        int done = 0;
        var failures = new List<Exception>();
        foreach (var item in toDelete)
        {
            try
            {
                await _fileOps.DeleteToRecycleBinAsync(item.Path);
                // 与主界面删除一致：留下快照，可撤销 / 可在「最近删除」中还原
                await _deletedItems.TrackAsync(item.Media, item.Media.ThumbnailPath);
                await _repo.DeleteMediaAsync(item.Id);
                done++;
            }
            catch (Exception ex)
            {
                // 单个失败继续处理其余文件（典型：某个盘被拔出）
                failures.Add(ex);
            }
        }

        // 从组里移除已删除项
        foreach (var g in Groups.ToList())
        {
            foreach (var item in g.Items.Where(i => i.IsSelectedForDelete).ToList())
                g.Items.Remove(item);
            if (g.Items.Count < 2)
                Groups.Remove(g);
        }

        UpdateDeleteSummary();
        StatusText = failures.Count == 0
            ? $"已删除 {done} 个文件"
            : $"已删除 {done} 个，{failures.Count} 个失败：{StorageErrors.FriendlyMessage(failures[0])}";
    }
}
