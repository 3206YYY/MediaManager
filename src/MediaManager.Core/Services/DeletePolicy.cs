using MediaManager.Core.Enums;

namespace MediaManager.Core.Services;

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
/// 要改行为只改这个文件，测试也只需要测这个文件。
/// </para>
/// </summary>
public static class DeletePolicy
{
    /// <summary>这个设置值是不是「永久删除」。</summary>
    public static bool IsPermanent(DeleteBehavior behavior) => behavior == DeleteBehavior.Permanent;

    /// <summary>按设置删一个文件。</summary>
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

    /// <summary>删除确认框的正文。<paramref name="fileName"/> 为空表示批量（用 <paramref name="count"/>）。</summary>
    public static string ConfirmMessage(DeleteBehavior behavior, int count, string? fileName = null)
    {
        if (fileName is null)
        {
            return IsPermanent(behavior)
                ? $"将永久删除选中的 {count} 个文件。\n\n文件不会进入回收站，删除后无法恢复。"
                : $"将把选中的 {count} 个文件移入回收站。\n\n删除后 5 秒内可在底部撤销。";
        }

        return IsPermanent(behavior)
            ? $"将永久删除下面的文件：\n\n{fileName}\n\n文件不会进入回收站，删除后无法恢复。"
            : $"将把下面的文件移入回收站：\n\n{fileName}\n\n删除后 5 秒内可在底部撤销。";
    }

    /// <summary>删完之后状态栏那一句。</summary>
    public static string StatusAfterDelete(DeleteBehavior behavior, int count)
        => IsPermanent(behavior) ? $"已永久删除 {count} 个文件" : $"已删除 {count} 个文件";
}
