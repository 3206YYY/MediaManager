namespace MediaManager.Core.Services;

/// <summary>添加文件夹时的嵌套判定结果。</summary>
public enum FolderAddVerdict
{
    /// <summary>可以正常添加。</summary>
    Ok,

    /// <summary>同一个文件夹已经在库里。</summary>
    Duplicate,

    /// <summary>新文件夹已经在某个已添加的根目录里面 —— 单独添加必然查出 0 项。</summary>
    CoveredByExisting,

    /// <summary>新文件夹是某些已添加根目录的上级 —— 应该合并，而不是并存两份。</summary>
    ContainsExisting,
}

/// <summary>
/// 「能不能把这个文件夹加进库」的纯判定逻辑。
///
/// 为什么必须有它：媒体记录用 Path 唯一，扫描父目录时子目录里的文件已经入库、
/// 归属父级 RootFolderId。此时再把子目录当独立根目录加进去，它名下一条记录都没有
/// —— 用户点进去就是「共 0 项」，怎么刷新扫描都没用（v1.4.5 修的正是这个）。
/// 所以添加时必须拦：子目录已包含 → 拒绝并提示；父目录包含已有根 → 合并。
/// </summary>
public static class FolderNestingPolicy
{
    /// <summary>
    /// 判定 <paramref name="newPath"/> 与已有根目录的关系。
    /// </summary>
    /// <param name="newPath">要添加的文件夹（绝对路径）。</param>
    /// <param name="existingPaths">库里已有的根目录路径。</param>
    /// <param name="coveringRoot">当结果为 <see cref="FolderAddVerdict.CoveredByExisting"/> 时，覆盖它的那个已有根目录。</param>
    /// <param name="containedRoots">当结果为 <see cref="FolderAddVerdict.ContainsExisting"/> 时，被它包含的已有根目录。</param>
    public static FolderAddVerdict Evaluate(
        string newPath,
        IEnumerable<string> existingPaths,
        out string? coveringRoot,
        out IReadOnlyList<string> containedRoots)
    {
        coveringRoot = null;
        containedRoots = Array.Empty<string>();
        var contained = new List<string>();

        var target = Normalize(newPath);
        if (string.IsNullOrEmpty(target))
            return FolderAddVerdict.Ok;

        foreach (var raw in existingPaths)
        {
            var existing = Normalize(raw);
            if (string.IsNullOrEmpty(existing)) continue;

            if (string.Equals(target, existing, StringComparison.OrdinalIgnoreCase))
                return FolderAddVerdict.Duplicate;

            if (IsUnder(target, existing))
            {
                coveringRoot = existing;
                return FolderAddVerdict.CoveredByExisting;
            }

            if (IsUnder(existing, target))
                contained.Add(existing);
        }

        containedRoots = contained;
        return contained.Count > 0 ? FolderAddVerdict.ContainsExisting : FolderAddVerdict.Ok;
    }

    /// <summary>
    /// 找出库里「父目录也在列表里」的嵌套根目录，按最顶层祖先分组：
    /// 返回「父根 Id → 要并进来的子根 Id 列表」。
    ///
    /// 为什么要自愈而不是放着不管：这类子根目录名下通常一条记录都没有（文件早就
    /// 扫进父目录了），但它在「扫描」时会把记录改归属到自己身上 —— 之后用户点
    /// 「移除文件夹」，<c>RootFolder</c> 的级联删除就把这批媒体一起带走。
    /// </summary>
    /// <param name="roots">根目录 Id + 路径。</param>
    /// <param name="excludePath">要排除的路径（保险箱锚点目录），可为 null。</param>
    public static IReadOnlyDictionary<long, IReadOnlyList<long>> GroupNestedRoots(
        IEnumerable<(long Id, string? Path)> roots,
        string? excludePath = null)
    {
        var excluded = Normalize(excludePath);
        var entries = roots
            .Select(r => (r.Id, Path: Normalize(r.Path)))
            .Where(e => !string.IsNullOrEmpty(e.Path)
                        && (excluded.Length == 0 || !string.Equals(e.Path, excluded, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var byParent = new Dictionary<long, List<long>>();
        if (entries.Count < 2)
            return new Dictionary<long, IReadOnlyList<long>>();   // 只有一个根目录，谈不上嵌套

        // 挂到**最顶层**的那个根上：A、A\B、A\B\C 并存时一次并完，
        // 不靠反复重载一层层往上爬。
        foreach (var e in entries)
        {
            var ancestor = entries
                .Where(o => o.Id != e.Id && IsUnder(e.Path, o.Path))
                .OrderBy(o => o.Path.Length)
                .FirstOrDefault();
            if (ancestor.Id == 0) continue;

            if (!byParent.TryGetValue(ancestor.Id, out var kids))
                byParent[ancestor.Id] = kids = new List<long>();
            kids.Add(e.Id);
        }
        return byParent.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<long>)kv.Value);
    }

    /// <summary>
    /// 判断 <paramref name="path"/> 是否在 <paramref name="folder"/> 里面（不含自身）。
    /// 比较前统一去掉尾部斜杠、补上分隔符再比 —— 纯字符串 StartsWith 会让
    /// <c>C:\测试2</c> 误判成在 <c>C:\测试</c> 里。
    /// </summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(f)) return false;
        if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase)) return false;

        var prefix = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 规范化：展开完整路径、统一反斜杠、去掉尾部斜杠。
    /// 盘根（<c>C:\</c>）必须保留那道斜杠，否则 <c>C:</c> 在 Windows 上表示「C 盘当前目录」。
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path!.Trim();

        // 盘符（C: / C:\ / c:/）必须先于 GetFullPath 处理：
        // 在 Windows 上单独的 "C:" 表示「C 盘当前目录」，GetFullPath 会返回一个具体目录，
        // 盘根就永远匹配不上了。
        if (trimmed.Length is >= 2 and <= 3
            && char.IsLetter(trimmed[0]) && trimmed[1] == ':'
            && (trimmed.Length == 2 || trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return char.ToUpperInvariant(trimmed[0]) + ":\\";
        }

        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }

        full = full.Replace('/', '\\').TrimEnd('\\');
        if (full.Length == 2 && full[1] == ':') full += "\\";
        return full;
    }
}
