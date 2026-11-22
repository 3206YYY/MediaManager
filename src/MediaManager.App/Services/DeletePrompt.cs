using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Enums;
using MediaManager.Core.Services;

namespace MediaManager.App.Services;

/// <summary>
/// 删之前「问一句」的统一门面 —— 所有删除入口都从这里拿最终该用哪种方式。
/// <para>
/// <b>为什么单独一层</b>：<see cref="DeletePolicy"/> 在 Core，是纯逻辑、不能弹窗；
/// 而「要不要问」本身也是删除行为的一部分。放这儿以后，界面只需要
/// <c>var b = DeletePrompt.Ask(...); if (b is null) return;</c>，
/// 再拿 <c>b.Value</c> 往下删 —— 不会再出现「弹窗说可撤销、执行端却永久删」这种脱节。
/// </para>
/// <para>
/// 三种设置各问各的：
/// <list type="bullet">
/// <item><see cref="DeleteBehavior.RecycleBin"/>：普通确认框（危险按钮红），进回收站。</item>
/// <item><see cref="DeleteBehavior.Permanent"/>：确认框正文改成「无法恢复」，危险按钮红。</item>
/// <item><see cref="DeleteBehavior.Ask"/>：三按钮「移入回收站 / 永久删除 / 取消」，当场选。</item>
/// </list>
/// </para>
/// </summary>
public static class DeletePrompt
{
    /// <summary>
    /// 问一句，返回本次实际要用的删除方式；<c>null</c> = 用户取消，调用方必须直接 return。
    /// </summary>
    /// <param name="configured">设置里的删除方式（可能为 <see cref="DeleteBehavior.Ask"/>）。</param>
    /// <param name="count">本次删几个文件。</param>
    /// <param name="fileName">单个文件时传文件名；批量传 null。</param>
    /// <param name="silentWhenRecycle">
    /// 「点谁删谁」模式用：进回收站时不弹窗（安全网是回收站 + 5 秒撤销），
    /// 但<b>永久删除 / 每次询问仍然要弹</b> —— 否则用户把设置改成永久删除、
    /// 再取消「先勾选」，就会在零确认、零撤销的情况下把文件删干净。
    /// </param>
    /// <param name="undoOffer">当前窗口真实具备的补救方式，决定确认框能承诺什么。</param>
    public static DeleteBehavior? Ask(
        DeleteBehavior configured,
        int count,
        string? fileName = null,
        bool silentWhenRecycle = false,
        DeleteUndoOffer undoOffer = DeleteUndoOffer.UndoToast)
    {
        if (DeletePolicy.NeedsChoice(configured))
        {
            var choice = CustomMessageBox.Show(
                DeletePolicy.ChoiceMessage(count, fileName),
                "删除",
                MsgBoxKind.Question,
                MsgBoxButtons.RecyclePermanentCancel,
                primaryText: "移入回收站",
                secondaryText: "永久删除",
                cancelText: "取消");

            return choice switch
            {
                MsgBoxResult.Recycle => DeleteBehavior.RecycleBin,
                MsgBoxResult.Permanent => DeleteBehavior.Permanent,
                _ => null,
            };
        }

        if (silentWhenRecycle && !DeletePolicy.IsPermanent(configured))
            return configured;

        var ok = CustomMessageBox.ConfirmDanger(
            DeletePolicy.ConfirmMessage(configured, count, fileName, undoOffer), "确认删除");
        return ok ? configured : null;
    }
}
