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

/// <summary>主窗口视图模型 —— 浏览：导航加载、侧栏文件夹树、视图切换、搜索与收藏评分。</summary>
public partial class MainViewModel
{
    private async Task LoadNavigationAsync()
    {
        // 一次后台往返把导航数据全部取回（SQLite 的"异步"实际同步执行，必须离开 UI 线程）
        var data = await DbAsync(async () => (
            Folders: await _repo.GetRootFoldersAsync(),
            Tags: await _repo.GetTagsAsync(),
            Categories: await _repo.GetCategoriesAsync(),
            FavoriteCount: await _repo.GetFavoriteCountAsync(),
            TagCount: await _repo.GetTagCountAsync(),
            CategoryCount: await _repo.GetCategoryCountAsync(),
            // 库里有媒体的目录：侧栏文件夹树要能展开到子文件夹
            MediaDirs: await _repo.GetMediaFolderPathsAsync()));

        // 保险箱锚点目录（路径 = 数据目录下的 Vault）只为满足外键存在，
        // 不能出现在侧栏 —— 否则用户点进去看到一片空白，还暴露了保险箱目录位置
        var vaultDir = System.IO.Path.Combine(_config.DataDirectory, "Vault")
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // 老库自愈：父目录和子目录都被添加过（v1.4.4 及以前的用户很容易踩到）。
        // 光靠「按路径查」能让它显示正确，但子根目录还留着，后果是：
        // 在子目录上点「扫描」→ 这批媒体记录改归属到子根 → 再「移除文件夹」时
        // RootFolder 的级联删除会把它们一起带走。并掉才是干净的。
        if (await HealNestedRootsAsync(data.Folders, vaultDir))
            data.Folders = await DbAsync(() => _repo.GetRootFoldersAsync());

        BuildFolderTree(data.Folders, data.MediaDirs, vaultDir);

        TagItems.Clear();
        foreach (var t in data.Tags)
        {
            TagItems.Add(new NavigationItemViewModel
            {
                Title = t.Name,
                Icon = "🔖",
                Key = "tag",
                RelatedId = t.Id,
            });
        }

        CategoryItems.Clear();
        foreach (var c in data.Categories)
            CategoryItems.Add(c);

        // 发现页「指定分类」的候选（v1.3.2 起收藏夹已并入分类，只列顶级分类）
        DiscoveryPoolCategories.Clear();
        foreach (var c in data.Categories.Where(c => c.ParentId == null))
            DiscoveryPoolCategories.Add(c);

        // 侧栏数量
        FavoriteCount = (int)data.FavoriteCount;
        TagCount = (int)data.TagCount;
        CategoryCount = (int)data.CategoryCount;

        // 后台探测每个根目录在磁盘上是否可达。不能在 UI 线程逐个 Directory.Exists：
        // 未挂载的移动硬盘 / 断开的网络盘上它可能阻塞几百毫秒甚至更久，侧栏会卡住。
        await ProbeFolderOfflineAsync();
    }

    /// <summary>
    /// 把「父目录也在库里」的嵌套根目录并进父目录，返回是否真的并过。
    ///
    /// 只清掉多余的 <c>RootFolder</c> 记录，媒体一条不删（改归属，见
    /// <c>MediaRepository.MergeRootFoldersIntoAsync</c>）；侧栏里的子文件夹节点由
    /// <see cref="BuildFolderTree"/> 按真实目录重建，用户看到的层级不变。
    /// </summary>
    private async Task<bool> HealNestedRootsAsync(IReadOnlyList<Core.Models.RootFolder> folders, string vaultDir)
    {
        var byParent = FolderNestingPolicy.GroupNestedRoots(
            folders.Select(f => (f.Id, (string?)f.Path)), vaultDir);
        if (byParent.Count == 0) return false;

        foreach (var (parentId, childIds) in byParent)
        {
            foreach (var cid in childIds) _watcher.Stop(cid);
            var moved = await DbAsync(() => _repo.MergeRootFoldersIntoAsync(parentId, childIds));
            _logger.LogInformation("侧栏自愈：{Count} 个嵌套根目录并入 RootFolder#{Parent}（迁移 {Moved} 条记录）",
                childIds.Count, parentId, moved);
        }
        return true;
    }

    /// <summary>
    /// 逐个探测根目录可达性并写入侧栏项的 IsOffline。
    /// 在线程池做，结果一次回 UI 线程设置 —— 侧栏就能在用户点进去**之前**
    /// 把离线的文件夹标出来，而不是让人点进去看到「共 0 项」摸不着头脑。
    /// </summary>
    // ---------- 侧栏文件夹树 ----------

    /// <summary>展开状态（按路径记，重建列表时不丢）。</summary>
    private readonly HashSet<string> _expandedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否已经做过「默认展开顶层根目录」。
    ///
    /// 为什么要默认展开：子文件夹藏在三角后面，用户点「文件夹」分组看到只有一个根目录，
    /// 会以为子文件夹「不见了」（测试员就是这么理解的）。默认展开一层，层级结构一眼看全；
    /// 只有这一层默认开，二级往下仍收起，避免一上来刷出几百行。
    /// 用一次性开关而不是「集合为空就展开」：用户手动全部收起后，不该又被自动展开。
    /// </summary>
    private bool _autoExpandedFolders;

    /// <summary>目录 → 直接子目录（只收录库里有媒体的目录链）。</summary>
    private Dictionary<string, SortedSet<string>> _folderChildren = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>根目录路径 → RootFolder.Id（子文件夹没有 Id）。</summary>
    private Dictionary<string, long> _folderRootIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>顶层根目录（未被其它根包含的），按路径排序。</summary>
    private List<string> _topFolderRoots = new();

    /// <summary>
    /// 构建侧栏文件夹树：根目录 + 库里有媒体的子目录层级。
    ///
    /// 为什么要有子目录这一层：以前侧栏只列根目录，用户加了一个大文件夹后，
    /// 想只看其中某个子文件夹根本点不到 —— 而「把子文件夹再添加一次」会撞上
    /// 「媒体记录按 Path 唯一、归属父级」的规则，点进去就是共 0 项（v1.4.5 的 P0）。
    /// 现在嵌套的根目录也渲染成树上的节点，不再单列一行。
    /// </summary>
    private void BuildFolderTree(
        IReadOnlyList<Core.Models.RootFolder> roots,
        IEnumerable<string> mediaDirs,
        string vaultDir)
    {
        _folderRootIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _folderChildren = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        var normalizedVault = FolderNestingPolicy.Normalize(vaultDir);
        var rootPaths = new List<string>();
        foreach (var f in roots)
        {
            var p = FolderNestingPolicy.Normalize(f.Path);
            if (string.IsNullOrEmpty(p)) continue;
            // 保险箱锚点不能出现在侧栏（进私密空间要走独立窗口）
            if (string.Equals(p, normalizedVault, StringComparison.OrdinalIgnoreCase)) continue;
            _folderRootIds[p] = f.Id;
            rootPaths.Add(p);
        }

        _topFolderRoots = rootPaths
            .Where(p => !rootPaths.Any(other =>
                !string.Equals(other, p, StringComparison.OrdinalIgnoreCase)
                && FolderNestingPolicy.IsUnder(p, other)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 第一次建树时把顶层根目录展开，让子文件夹一眼可见（只展开这一层）
        if (!_autoExpandedFolders)
        {
            foreach (var r in _topFolderRoots) _expandedFolderPaths.Add(r);
            _autoExpandedFolders = true;
        }

        void AddEdge(string parent, string child)
        {
            if (!_folderChildren.TryGetValue(parent, out var set))
                _folderChildren[parent] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(child);
        }

        foreach (var raw in mediaDirs)
        {
            var dir = FolderNestingPolicy.Normalize(raw);
            if (string.IsNullOrEmpty(dir)) continue;

            // 只收在某个根目录下面的目录；顶层根优先，嵌套根也认
            var owner = _topFolderRoots.FirstOrDefault(r => FolderNestingPolicy.IsUnder(dir, r))
                        ?? rootPaths.FirstOrDefault(r => FolderNestingPolicy.IsUnder(dir, r));
            if (owner == null) continue;

            var cur = dir;
            var guard = 0;
            while (!string.Equals(cur, owner, StringComparison.OrdinalIgnoreCase) && guard++ < 64)
            {
                var parent = System.IO.Path.GetDirectoryName(cur)?.TrimEnd('\\', '/');
                if (string.IsNullOrEmpty(parent)) break;
                // 中间层没有媒体文件也要出现在树上，否则子目录会「凭空冒出来」
                AddEdge(parent, cur);
                cur = parent;
            }
        }

        RebuildFolderList();
    }

    /// <summary>按展开状态把树拍平成侧栏可见列表（ListBox 只认扁平 ItemsSource）。</summary>
    private void RebuildFolderList()
    {
        FolderItems.Clear();
        foreach (var root in _topFolderRoots)
            AddFolderNode(root, depth: 0);
        _logger.LogDebug("文件夹树重建：顶层 {Roots}，可见 {Count} 行",
            _topFolderRoots.Count, FolderItems.Count);
    }

    private void AddFolderNode(string path, int depth)
    {
        var (title, subTitle) = SplitFolderPath(path);
        var hasChildren = _folderChildren.TryGetValue(path, out var kids) && kids.Count > 0;

        var nav = new NavigationItemViewModel
        {
            // 侧栏只放末级目录名：以前直接显示整条路径，宽度不够被截成
            // 「C:\Users\xxx\Videos\Capt…」，两个目录看起来一模一样，选哪个全靠猜
            Title = title,
            // 副标题是**上级目录**，不是本节点的路径。前面加「在」就是为了不被误读成
            // 「这个文件夹的路径写错了」——测试员正是把它当成了路径 Bug。
            SubTitle = subTitle == "整个磁盘" ? subTitle : "在 " + subTitle,
            ToolTipText = path,
            Icon = depth == 0 ? "📂" : "📁",
            Key = "folder",
            // 嵌套根目录保留自己的 Id（扫描 / 移除要用）；普通子目录没有 Id
            RelatedId = _folderRootIds.TryGetValue(path, out var id) ? id : null,
            IndentLevel = depth,
            HasChildren = hasChildren,
            IsExpanded = hasChildren && _expandedFolderPaths.Contains(path),
        };
        FolderItems.Add(nav);

        if (nav.IsExpanded && kids != null)
            foreach (var child in kids)
                AddFolderNode(child, depth + 1);
    }

    /// <summary>点小三角展开 / 收起子文件夹。</summary>
    [RelayCommand]
    private void ToggleFolderNode(NavigationItemViewModel? node)
    {
        if (node?.ToolTipText is not { Length: > 0 } path || !node.HasChildren) return;

        if (!_expandedFolderPaths.Remove(path))
            _expandedFolderPaths.Add(path);

        _logger.LogDebug("树节点 {Path} 展开={Expanded}，子目录 {Count}",
            path, _expandedFolderPaths.Contains(path),
            _folderChildren.TryGetValue(path, out var kids) ? kids.Count : 0);

        RebuildFolderList();
    }

    private async Task ProbeFolderOfflineAsync()    {
        var roots = FolderItems.Where(f => f.RelatedId.HasValue).ToList();
        if (roots.Count == 0) return;

        var results = await Task.Run(() =>
        {
            var dict = new Dictionary<long, bool>(roots.Count);
            foreach (var r in roots)
                dict[r.RelatedId!.Value] = System.IO.Directory.Exists(r.ToolTipText ?? string.Empty);
            return dict;
        }).ConfigureAwait(true);

        foreach (var r in roots)
            r.IsOffline = !results[r.RelatedId!.Value];
    }

    private void StartWatchers()
    {
        foreach (var f in FolderItems)
        {
            // 可达性已在 ProbeFolderOfflineAsync 探测过（线程池），这里复用结果：
            // 原来在这里逐个 Directory.Exists 是 UI 线程调用，离线盘会卡住启动
            if (f.RelatedId.HasValue && !f.IsOffline && System.IO.Directory.Exists(f.ToolTipText))
                _watcher.Start(f.RelatedId.Value, f.ToolTipText!);
        }
    }

    /// <summary>
    /// 把一条文件夹路径拆成「侧栏标题 + 副标题」。
    /// 例：<c>C:\Users\me\Pictures\Screenshots</c> → （<c>Screenshots</c>, <c>C:\…\Pictures</c>）。
    /// 盘根本身（<c>D:\</c>）没有末级目录名，直接用路径当标题。
    /// </summary>
    private static (string Title, string SubTitle) SplitFolderPath(string path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
            return (trimmed, "整个磁盘");

        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? string.Empty;
        var parentName = System.IO.Path.GetFileName(parent.TrimEnd('\\', '/'));
        var root = System.IO.Path.GetPathRoot(trimmed) ?? string.Empty;
        root = root.TrimEnd('\\', '/');

        // 副标题带盘符：不同盘上同名的两个目录（都有 Screenshots）靠它一眼分开
        var subTitle = string.IsNullOrEmpty(parentName)
            ? root
            : $"{root}\\…\\{parentName}";

        return (name, subTitle);
    }

    /// <summary>列表加载代次：并发刷新时只有最后一次的结果能落到界面，避免旧查询覆盖新结果。</summary>
    private int _loadGeneration;

    [RelayCommand]
    private async Task SelectViewAsync(string key)
    {
        _currentRootFolderId = null;
        _currentFolderPath = null;
        _currentTagId = null;
        _currentCategoryId = null;

        if (key.StartsWith("folderpath:", StringComparison.Ordinal))
        {
            // 子文件夹：没有独立 RootFolder 记录，只能按路径查（含它下面的所有层级）
            _currentViewKey = "folder";
            _currentFolderPath = key.Substring("folderpath:".Length);
        }
        else if (key.StartsWith("folder:"))
        {
            _currentViewKey = "folder";
            _currentRootFolderId = long.Parse(key.Substring(7));
        }
        else if (key.StartsWith("tag:"))
        {
            _currentViewKey = "tag";
            _currentTagId = long.Parse(key.Substring(4));
        }
        else if (key.StartsWith("category:"))
        {
            _currentViewKey = "category";
            _currentCategoryId = long.Parse(key.Substring(9));
        }
        else
        {
            _currentViewKey = key;
        }

        IsDiscoveryView = _currentViewKey == "discovery";
        await LoadMediaAsync();
    }

    /// <summary>
    /// 只重读侧栏（文件夹 / 标签 / 分类 / 数量），不动当前网格、不清缩略图内存缓存。
    /// 用于「导入了一张图，侧栏里凭空多了个文件夹」这种小变化 ——
    /// 走整表刷新会把已解码的缩略图全部丢掉，代价太大。
    /// </summary>
    public Task ReloadNavigationAsync() => LoadNavigationAsync();

    /// <summary>重新统计保险箱里的媒体数量（移入 / 移出 / 删除后调用）。</summary>
    public async Task RefreshPrivateCountAsync()
    {
        PrivateCount = await DbAsync(() => _repo.GetPrivateCountAsync());
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await LoadMediaAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        // 防抖搜索
        _ = DebounceSearchAsync(value);
    }

    private CancellationTokenSource? _searchCts;

    private async Task DebounceSearchAsync(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
                await LoadMediaAsync();
        }
        catch (TaskCanceledException) { }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ThumbnailItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item == null) return;
        item.IsFavorite = !item.IsFavorite;
        await _repo.SetFavoriteAsync(item.Id, item.IsFavorite);
        await RefreshDetailAsync(item);
    }

    [RelayCommand]
    private async Task SetRatingAsync(string? ratingStr)
    {
        if (SelectedItem == null || !int.TryParse(ratingStr, out var rating)) return;
        SelectedItem.Rating = rating;
        await _repo.SetRatingAsync(SelectedItem.Id, rating);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem == null) return;

        var confirm = CustomMessageBox.ConfirmDanger(
            $"将把下面的文件移入回收站：\n\n{SelectedItem.FileName}\n\n删除后 5 秒内可在底部撤销。",
            "确认删除");
        if (!confirm) return;

        try
        {
            await _fileOps.DeleteToRecycleBinAsync(SelectedItem.Path);
        }
        catch (Exception ex)
        {
            // 常见：移动硬盘被拔出（设备未就绪）、文件被占用、无权限
            _logger.LogWarning(ex, "删除文件失败：{Path}", SelectedItem.Path);
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, "删除"), "删除失败");
            return;
        }

        await _opLog.LogAsync("删除到回收站", SelectedItem.Path, null);

        // 先记录快照（此时数据库里的标签/分类关联还在），再删库；
        // 缩略图缓存保留，撤销后可以立刻显示，也方便「最近删除」里预览
        var record = await _deletedItems.TrackAsync(SelectedItem.Media, SelectedItem.ThumbnailPath);

        // 同步删除数据库记录，保证状态一致（还原后由撤销逻辑重新写回）
        await _repo.DeleteMediaAsync(SelectedItem.Id);

        MediaItems.Remove(SelectedItem);
        SelectedItem = null;
        await RefreshStatusAsync();

        ShowUndoToast(new[] { record });
    }

    // ---------- 操作轻提示（只反馈，不带按钮） ----------
}
