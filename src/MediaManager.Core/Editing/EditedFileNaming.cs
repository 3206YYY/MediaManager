using IOPath = System.IO.Path;

namespace MediaManager.Core.Editing;

/// <summary>
/// 编辑保存的命名规则（V1.2.5 起）：
/// <b>编辑原图 → 新建副本；编辑副本 → 直接改这个副本。</b>
/// </summary>
/// <remarks>
/// 放在 Core 而不是 App：命名规则是纯逻辑，必须能被单测覆盖
/// （App 是 WPF 项目，测试工程引用不了）。
/// </remarks>
public static class EditedFileNaming
{
    /// <summary>副本文件名后缀。</summary>
    public const string Suffix = "_edited";

    /// <summary>
    /// 判断路径是不是「编辑副本」（文件名主体以 <c>_edited</c> 结尾，忽略大小写）。
    /// 界面据此决定按钮文案（保存副本 / 更新副本）与保存结果提示。
    /// </summary>
    public static bool IsEditedCopy(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var name = IOPath.GetFileNameWithoutExtension(path);
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 算出保存目标路径。
    /// <list type="bullet">
    /// <item>输入是原图 → <c>原名_edited.扩展名</c>（新文件，原图绝不动）</item>
    /// <item>输入本身已是副本 → 原样返回，原地覆盖</item>
    /// </list>
    /// 第二条同时消除了旧逻辑「_edited_edited_edited」无限套娃的问题。
    /// </summary>
    public static string BuildOutputPath(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        // 已经在编辑副本 → 就改这个副本自己
        if (IsEditedCopy(input)) return input;

        var dir = IOPath.GetDirectoryName(input) ?? ".";
        var name = IOPath.GetFileNameWithoutExtension(input);

        // 兜底：历史遗留的多重后缀先剥干净，保证最多只带一个 _edited
        while (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^Suffix.Length];

        if (name.Length == 0) name = "image";

        return IOPath.Combine(dir, $"{name}{Suffix}{IOPath.GetExtension(input)}");
    }
}
