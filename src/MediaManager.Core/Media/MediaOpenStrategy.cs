using MediaManager.Core.Enums;

namespace MediaManager.Core.Media;

/// <summary>
/// 「双击 / 打开这个媒体，到底该开内置窗口还是交给系统默认程序」的唯一判断点。
/// <para>
/// <b>图片 / 视频必须内置</b>：随机跳转、快捷键无缝切图、直接调起编辑器、日记与时光轴，
/// 这些卖点只有内置窗口才成立；而且深色模式融合、移动硬盘拔了不崩，也都靠内置。
/// </para>
/// <para>
/// <b>音频 / 文档 / 压缩包交给系统</b>：Foobar、VS Code、WinRAR 这类专业工具用户机器上
/// 本来就有，内置个半成品既要为每种编码和换行符踩坑，也让安装包和代码白养一份维护面。
/// </para>
/// <para>
/// 主页右键 / Ctrl+O 的「用系统默认程序打开」是另一回事：它对所有类型都可用，
/// 目的是让用户临时用 PS、PotPlayer 看单张图 / 单个视频，不改变这里的默认行为。
/// </para>
/// </summary>
public static class MediaOpenStrategy
{
    /// <summary>这类媒体默认是否交给系统默认程序（true）而不是内置窗口（false）。</summary>
    public static bool UseExternalApp(MediaType type) => type switch
    {
        // 内置沉浸查看器：核心卖点，绝不外放
        MediaType.Image => false,
        MediaType.Video => false,

        // 交给系统默认关联程序
        MediaType.Audio => true,
        MediaType.Text => true,
        MediaType.Archive => true,

        // 将来新增的类型先按「外部」处理：宁可少一个内置窗口，也不要因为漏判把
        // 未知格式塞进图片查看器导致加载失败。
        _ => true,
    };

    /// <summary>这类媒体是否走内置沉浸查看器（图片 / 视频）。</summary>
    public static bool UseBuiltInViewer(MediaType type) => !UseExternalApp(type);
}
