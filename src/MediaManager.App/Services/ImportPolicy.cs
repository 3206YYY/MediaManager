using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Services;

/// <summary>「这个文件不在媒体库里，要怎么处理」的三种回答。</summary>
internal enum ImportFolderChoice
{
    /// <summary>文件所在的目录加进媒体库（文件原地不动）。</summary>
    AddParentFolder,

    /// <summary>文件复制到「导入文件夹」再加入（原件不动）。</summary>
    CopyToImportFolder,

    /// <summary>不导入了。</summary>
    Cancel,
}

/// <summary>
/// 外部文件导入前的「文件夹归属」决策。
///
/// 拖一张桌面上的图进日记 / 私密空间时，它不属于任何媒体文件夹。
/// 默默替用户把桌面收成媒体文件夹是错的（侧栏多一条莫名其妙、下次扫描把整个桌面吸进库）；
/// 强行复制文件也是错的（动了用户没让动的东西）。所以这里必须问一次，
/// 并给一个「记住我的选择」—— 问过一次还每次都问就是骚扰。
/// </summary>
internal static class ImportPolicy
{
    /// <summary>策略设置键：Ask（每次问）/ AddParentFolder / CopyToImportFolder。</summary>
    public const string PolicySettingKey = "Import.UnknownFolderPolicy";

    /// <summary>导入文件夹位置设置键（CopyToImportFolder 模式的目标目录）。</summary>
    public const string FolderSettingKey = "Import.FolderPath";

    /// <summary>当前已记住的策略（没记住过返回 null = 每次询问）。</summary>
    public static async Task<ImportFolderChoice?> GetRememberedPolicyAsync()
    {
        var repo = App.Services.GetRequiredService<IMediaRepository>();
        var value = await repo.GetSettingAsync(PolicySettingKey);
        return value switch
        {
            "AddParentFolder" => ImportFolderChoice.AddParentFolder,
            "CopyToImportFolder" => ImportFolderChoice.CopyToImportFolder,
            _ => null,
        };
    }

    /// <summary>当前配置的导入文件夹（没配置过用默认位置，不落库 —— 落库会把默认值写死）。</summary>
    public static async Task<string> GetImportFolderAsync()
    {
        var repo = App.Services.GetRequiredService<IMediaRepository>();
        var value = await repo.GetSettingAsync(FolderSettingKey);
        return string.IsNullOrWhiteSpace(value) ? MediaImportOptions.DefaultImportFolder : value;
    }

    /// <summary>
    /// 导入前的决策入口：文件都在库内时直接返回默认策略（不弹任何窗）；
    /// 有不在库内的文件时，按「记住的策略」走，没记住就弹窗问一次。
    /// 返回 null 表示用户取消了这批导入。
    /// </summary>
    public static async Task<MediaImportOptions?> ResolveAsync(IReadOnlyList<string> files)
    {
        var import = App.Services.GetRequiredService<IMediaImportService>();

        var uncovered = await import.FindUncoveredAsync(files);
        if (uncovered.Count == 0)
            return new MediaImportOptions();   // 全部已被媒体文件夹覆盖，不需要决策

        var remembered = await GetRememberedPolicyAsync();
        var choice = remembered ?? await AskAsync(uncovered.Count, uncovered.Count == 1 ? uncovered[0] : null);
        if (choice == null || choice == ImportFolderChoice.Cancel) return null;

        return new MediaImportOptions
        {
            UnknownFolderPolicy = choice == ImportFolderChoice.CopyToImportFolder
                ? UnknownFolderPolicy.CopyToImportFolder
                : UnknownFolderPolicy.AddParentFolder,
            ImportFolder = choice == ImportFolderChoice.CopyToImportFolder
                ? await GetImportFolderAsync()
                : null,
        };
    }

    /// <summary>弹窗问一次；勾了「记住」就把选择写进设置。</summary>
    private static async Task<ImportFolderChoice?> AskAsync(int count, string? samplePath)
    {
        var folder = await GetImportFolderAsync();

        var message = count == 1
            ? "这个文件还不在媒体库里，要怎么加入？\n\n"
            : $"这 {count} 个文件还不在媒体库里，要怎么加入？\n\n";

        if (!string.IsNullOrEmpty(samplePath))
            message += $"文件：{samplePath}\n\n";

        message += "· 复制到导入文件夹（推荐）—— 原件不动，副本进库\n"
                 + $"  位置：{folder}（可在设置里改）\n"
                 + "· 加入它所在的文件夹 —— 文件原地不动，那个目录会成为媒体文件夹\n"
                 + "· 取消 —— 这次不加了";

        var actions = new List<DialogAction>
        {
            new()
            {
                Text = "复制到导入文件夹",
                Result = MsgBoxResult.Yes,
                Kind = DialogButtonKind.Primary,
                IsDefault = true,
            },
            new() { Text = "加入所在文件夹", Result = MsgBoxResult.No },
            new() { Text = "取消", Result = MsgBoxResult.Cancel },
        };

        var dialog = new DialogWindow("文件不在媒体库里", message, MsgBoxKind.Question, actions,
            checkboxText: "记住我的选择，以后不再询问（可在设置里修改）");

        if (App.Current.MainWindow is { IsVisible: true } owner)
            dialog.Owner = owner;

        dialog.ShowDialog();

        if (dialog.Remember && dialog.Result is MsgBoxResult.Yes or MsgBoxResult.No)
        {
            var repo = App.Services.GetRequiredService<IMediaRepository>();
            await repo.SetSettingAsync(PolicySettingKey,
                dialog.Result == MsgBoxResult.Yes ? "CopyToImportFolder" : "AddParentFolder");
        }

        return dialog.Result switch
        {
            MsgBoxResult.Yes => ImportFolderChoice.CopyToImportFolder,
            MsgBoxResult.No => ImportFolderChoice.AddParentFolder,
            _ => ImportFolderChoice.Cancel,
        };
    }
}
