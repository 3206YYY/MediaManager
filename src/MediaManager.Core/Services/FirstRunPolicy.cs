namespace MediaManager.Core.Services;

/// <summary>
/// 首次启动要不要问一句「你主要用它做什么」。
///
/// <para>
/// 背景：侧栏有 9 个顶级入口，新用户一打开会以为是「文件管理器全家桶」，
/// 而他多半只想看图。以前极简模式存在，但开关埋在设置第一页、默认关，
/// 等于没有。这里把「默认密度」这个决定交还给用户自己，代码不替他选。
/// </para>
///
/// <para>
/// **只在全新库上问**。库里已经有任何内容（根目录或媒体条目）就说明这人早就在用了，
/// 升级到新版本时突然弹一个选择框，会让人以为程序被重置、库被清空 —— 得不偿失。
/// </para>
/// </summary>
public static class FirstRunPolicy
{
    /// <summary>问过没有的设置键。值为 "1" 表示不再问。</summary>
    public const string DoneSettingKey = "Ui.FirstRunDone";

    /// <summary>该不该弹首启选择窗。</summary>
    public static bool ShouldAsk(bool alreadyDone, int rootFolderCount, long mediaCount)
        => !alreadyDone && rootFolderCount <= 0 && mediaCount <= 0;

    /// <summary>
    /// 该不该**静默**补上「已问过」标记（不弹窗）。
    /// 用在老库升级：用户早就选好了自己的用法，这里只补标记，免得下次启动又被判定成首启。
    /// </summary>
    public static bool ShouldSilentlyMarkDone(bool alreadyDone, int rootFolderCount, long mediaCount)
        => !alreadyDone && !ShouldAsk(alreadyDone, rootFolderCount, mediaCount);
}
