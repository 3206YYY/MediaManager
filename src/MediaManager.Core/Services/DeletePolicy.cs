using MediaManager.Core.Enums;

namespace MediaManager.Core.Services;

/// <summary>
/// 删完之后还能怎么把文件找回来 —— 决定确认框里能承诺什么。
/// <para>
/// 以前三处界面共用同一句「删除后 5 秒内可在底部撤销」，但撤销条只长在主窗口底部：
/// 私密空间、去重窗口照抄这句，等于承诺了它们没有的东西。
/// </para>
/// </summary>
public enum DeleteUndoOffer
{
    /// <summary>主窗口：删完底部弹 5 秒撤销条。</summary>
    UndoToast,

    /// <summary>没有撤销条，但留了快照，可以到「最近删除」里还原（去重窗口等）。</summary>
    RecentDeleted,

    /// <summary>既没撤销条也没快照，只能自己去系统回收站翻（私密空间）。</summary>
    RecycledOnly,
}

/// <summary>
/// 「删除到底进回收站还是永久删」的唯一真值来源 —— 读设置里的 <see cref="DeleteBehavior"/>。
/// <para>
/// <b>为什么要有这个类</b>：v1.4.8 之前，设置页的「删除方式」是个<b>空开关</b> ——
/// 下拉的值写进库了，但主界面批量删除、点谁删谁、单张删除、私密空间删除四处
/// 全都写死 <c>DeleteToRecycleBinAsync</c>，没有任何代码读这个设置。
/// 用户选了「永久删除」，文件照样进回收站；私密空间的确认框还写着
/// 「可在设置里改为永久删除」，属于明着骗人。
/// </para>
/// <para>
/// 现在所有删除都过这里（和 <see cref="MediaOpenStrategy"/> 一个思路）：
/// <b>行为、文案、要不要先问一句</b> 都在这一个文件里，界面只负责把字贴上去。
/// 以前「执行端已经能永久删了，界面还写着 5 秒内可撤销」这种错位，
/// 就是因为文案散在 XAML 和代码里各处。
/// </para>
/// </summary>
public static class DeletePolicy
{
    /// <summary>这个设置值是不是「永久删除」。</summary>
    public static bool IsPermanent(DeleteBehavior behavior) => behavior == DeleteBehavior.Permanent;

    /// <summary>
    /// 这个设置值要不要在每次删除前问「回收站还是永久删除」。
    /// <para>
    /// 以前 <see cref="DeleteBehavior.Ask"/> 是个<b>半空开关</b>：下拉能选、能写库、
    /// <c>AppConfigService</c> 也能解析，但执行端 <c>IsPermanent(Ask)</c> 为 false，
    /// 于是静默按回收站删掉，从来不问 —— 用户以为自己选了「每次都问我」。
    /// </para>
    /// </summary>
    public static bool NeedsChoice(DeleteBehavior behavior) => behavior == DeleteBehavior.Ask;

    /// <summary>按设置删一个文件。（<see cref="DeleteBehavior.Ask"/> 应先由界面问出结果，不能直接走到这里）</summary>
    public static Task DeleteAsync(IFileOperationService ops, string path, DeleteBehavior behavior)
        => IsPermanent(behavior)
            ? ops.DeletePermanentAsync(path)
            : ops.DeleteToRecycleBinAsync(path);

    /// <summary>操作日志里记什么动作名。</summary>
    public static string LogAction(DeleteBehavior behavior)
        => IsPermanent(behavior) ? "永久删除" : "删除到回收站";

    /// <summary>
    /// 能不能撤销：<b>只有进回收站的才有撤销可言</b>。
    /// 永久删除后文件已经从磁盘上没了，再弹「5 秒内可撤销」就是骗人，
    /// 点还原也只会报错 —— 所以永久删除时既不写最近删除快照，也不弹撤销条。
    /// </summary>
    public static bool CanUndo(DeleteBehavior behavior) => !IsPermanent(behavior);

    /// <summary>删完之后状态栏那一句。</summary>
    public static string StatusAfterDelete(DeleteBehavior behavior, int count)
        => IsPermanent(behavior) ? $"已永久删除 {count} 个文件" : $"已删除 {count} 个文件";

    // ---------- 确认框 ----------

    /// <summary>
    /// 删除确认框的正文。<paramref name="fileName"/> 为空表示批量（用 <paramref name="count"/>）。
    /// <para>
    /// <paramref name="undoOffer"/> 必须按<b>当前窗口真实有的补救方式</b>传，
    /// 别在哪个窗口都默认 <see cref="DeleteUndoOffer.UndoToast"/> —— 私密空间没有撤销条，
    /// 写了就是骗人。
    /// </para>
    /// </summary>
    public static string ConfirmMessage(
        DeleteBehavior behavior,
        int count,
        string? fileName = null,
        DeleteUndoOffer undoOffer = DeleteUndoOffer.UndoToast)
    {
        if (IsPermanent(behavior))
        {
            return fileName is null
                ? $"将永久删除选中的 {count} 个文件。\n\n文件不会进入回收站，删除后无法恢复。"
                : $"将永久删除下面的文件：\n\n{fileName}\n\n文件不会进入回收站，删除后无法恢复。";
        }

        var rescue = undoOffer switch
        {
            DeleteUndoOffer.UndoToast => "删除后 5 秒内可在底部撤销。",
            DeleteUndoOffer.RecentDeleted => "删除后可在「最近删除」里还原。",
            _ => "删除后可以在系统回收站里还原。",
        };

        return fileName is null
            ? $"将把选中的 {count} 个文件移入回收站。\n\n{rescue}"
            : $"将把下面的文件移入回收站：\n\n{fileName}\n\n{rescue}";
    }

    /// <summary>
    /// 「每次询问」用的正文：把两种方式的后果一次说清，让用户当场选。
    /// </summary>
    public static string ChoiceMessage(int count, string? fileName = null)
    {
        var head = fileName is null
            ? $"要处理选中的 {count} 个文件"
            : $"要处理下面的文件：\n\n{fileName}";

        return head + "\n\n· 移入回收站：误删还能从回收站还原。\n· 永久删除：直接从磁盘抹掉，无法恢复。";
    }

    // ---------- 界面文案（以前写死在 XAML 里，和执行端脱节） ----------

    /// <summary>删除模式顶部红条上那句话。</summary>
    public static string DeleteModeHint(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Permanent => "删除模式 · 点谁删谁（永久删除，不可恢复，每次都会确认）",
        DeleteBehavior.Ask => "删除模式 · 点谁删谁（每次都会问你：回收站还是永久删除）",
        _ => "删除模式 · 点谁删谁（进回收站，5 秒内可撤销）",
    };

    /// <summary>顶部「批量删除」按钮的提示。</summary>
    public static string BatchDeleteTooltip(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Permanent =>
            "进入删除模式：单击文件打勾，选好点底部按钮永久删除（删了回不来）。再点一下退出。",
        DeleteBehavior.Ask =>
            "进入删除模式：单击文件打勾，选好点底部按钮删除，每次都会问你进回收站还是永久删除。再点一下退出。",
        _ => "进入删除模式：之后单击文件即可打勾 / 移入回收站（5 秒内可撤销）。再点一下退出。",
    };

    /// <summary>底部悬浮条那个实心删除按钮的提示。</summary>
    public static string SelectionDeleteTooltip(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Permanent => "删除当前选中的文件：永久删除，不进回收站，无法恢复",
        DeleteBehavior.Ask => "删除当前选中的文件：会先问你进回收站还是永久删除",
        _ => "删除当前选中的文件：确认后进回收站，5 秒内可撤销（和 Delete 键一样）",
    };
}
