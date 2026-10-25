using MediaManager.Core.Enums;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>媒体文件扩展名识别 + 类型注册表。</summary>
public static class MediaFormat
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico",
        ".heic", ".avif",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts",
        ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts", ".ogv", ".rmvb", ".vob",
    };

    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma",
        ".aif", ".aiff", ".amr", ".mka", ".ape", ".mid", ".midi",
    };

    // 刻意不含 ".ini"：Windows 几乎每个目录都塞一个 desktop.ini，
    // 照文档收录会让图库里凭空多出一堆「文档」条目。
    private static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".csv", ".xml", ".yaml", ".yml",
        ".htm", ".html", ".srt", ".ass", ".ssa", ".toml",
        ".py", ".js", ".css", ".cs", ".java", ".go", ".rs", ".sql", ".bat", ".ps1",
    };

    /// <summary>系统 / 资源管理器自己生成的目录说明文件，永远不该当媒体收录。</summary>
    private static readonly HashSet<string> SystemJunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", "ehthumbs.db", "ehthumbs_vista.db",
        ".ds_store", ".localized", ".directory", "albumthumbs.ini", ".picasa.ini",
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2", ".txz",
    };

    /// <summary>是否启用扩展格式（音频/文档/压缩包）。可在设置 → 插件扩展里关闭。</summary>
    public static bool ExtendedFormatsEnabled { get; private set; } = true;

    /// <summary>打开/关闭扩展格式扫描。关闭后已有条目仍保留，只是不再扫描新文件。</summary>
    public static void SetExtendedFormatsEnabled(bool enabled) => ExtendedFormatsEnabled = enabled;

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => Videos.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => Audios.Contains(Path.GetExtension(path));

    public static bool IsText(string path) => Texts.Contains(Path.GetExtension(path));

    public static bool IsArchive(string path) => Archives.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => GetMediaType(path) != null;

    /// <summary>
    /// 系统垃圾文件判定：目录说明文件（desktop.ini / Thumbs.db / .DS_Store …），
    /// 以及同时带「隐藏 + 系统」属性的操作系统文件。
    /// <para>
    /// 只过滤「隐藏 + 系统」双属性的文件，不动单纯被设成隐藏的用户文件
    /// （有些同步盘会把整个目录设成隐藏，那里的照片仍然要能收录）。
    /// </para>
    /// </summary>
    public static bool IsSystemJunk(string path, FileAttributes attributes)
    {
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(name) && SystemJunkNames.Contains(name)) return true;

        const FileAttributes both = FileAttributes.Hidden | FileAttributes.System;
        return (attributes & both) == both;
    }

    public static MediaType? GetMediaType(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return MediaType.Image;
        if (Videos.Contains(ext)) return MediaType.Video;

        if (!ExtendedFormatsEnabled) return null;
        if (Audios.Contains(ext)) return MediaType.Audio;
        if (Texts.Contains(ext)) return MediaType.Text;
        if (Archives.Contains(ext)) return MediaType.Archive;
        return null;
    }

    /// <summary>该类型是否由“扩展格式”提供。</summary>
    public static bool IsExtendedType(MediaType type)
        => type is MediaType.Audio or MediaType.Text or MediaType.Archive;

    /// <summary>
    /// 手动打开单个文件时使用：识别全部支持的类型，
    /// <b>不受</b>“扩展格式扫描开关”影响（关掉扫描只是不自动收录，手动打开仍应可用）。
    /// </summary>
    public static MediaType? GetMediaTypeForOpen(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return MediaType.Image;
        if (Videos.Contains(ext)) return MediaType.Video;
        if (Audios.Contains(ext)) return MediaType.Audio;
        if (Texts.Contains(ext)) return MediaType.Text;
        if (Archives.Contains(ext)) return MediaType.Archive;
        return null;
    }
}
