using System.Windows;
using System.Windows.Media;
using MediaManager.Core.Enums;

namespace MediaManager.App.Services;

/// <summary>
/// 非图片/视频文件的彩色类型图标（网格里替代原来那个黑白 emoji 占位）。
///
/// 为什么不用 emoji：<c>🎵 / 📄 / 🗜️</c> 在 WPF 里的呈现完全取决于系统装了什么字体，
/// 很多机器会回落到 Segoe UI Symbol 的单色字形，看着像"图标没加载出来"。
/// 这里用纯矢量（渐变底色 + 白色标签）画，任何机器、任何 DPI 都一致。
///
/// 性能约定：所有画刷在静态构造时创建并 <see cref="Freezable.Freeze"/>，
/// 网格里几百个格子共用同一批实例，绑定不产生任何新对象。
/// </summary>
public sealed class FileTypeIcon
{
    /// <summary>渐变底色。</summary>
    public Brush Background { get; }

    /// <summary>图标中央的白色标签（最多 4 个字符）。</summary>
    public string Label { get; }

    /// <summary>类型说明（"PDF 文档" / "压缩包"），给 ToolTip 和状态栏用。</summary>
    public string Description { get; }

    private FileTypeIcon(Brush background, string label, string description)
    {
        Background = background;
        Label = label;
        Description = description;
    }

    // ---------- 分类调色板 ----------
    // 取的是「中饱和 + 中高明度」：深色主题下不刺眼，浅色主题下也不至于糊成一片。

    private static readonly Brush PdfBrush = Gradient(0xE5, 0x48, 0x4D, 0xB3, 0x1F, 0x2C);
    private static readonly Brush WordBrush = Gradient(0x3B, 0x76, 0xE8, 0x1E, 0x4C, 0xB8);
    private static readonly Brush ExcelBrush = Gradient(0x22, 0xB8, 0x6B, 0x0E, 0x7A, 0x46);
    private static readonly Brush PptBrush = Gradient(0xF0, 0x73, 0x2E, 0xC2, 0x41, 0x0C);
    private static readonly Brush TextBrush = Gradient(0x76, 0x84, 0x99, 0x4B, 0x55, 0x63);
    private static readonly Brush CodeBrush = Gradient(0x8B, 0x5C, 0xF6, 0x6D, 0x28, 0xD9);
    private static readonly Brush DataBrush = Gradient(0x38, 0xBD, 0xF8, 0x03, 0x69, 0xA1);
    private static readonly Brush ArchiveBrush = Gradient(0xF5, 0x9E, 0x0B, 0xB4, 0x53, 0x09);
    private static readonly Brush AudioBrush = Gradient(0x14, 0xB8, 0xA6, 0x0F, 0x76, 0x6E);
    private static readonly Brush EbookBrush = Gradient(0xC2, 0x7A, 0x3D, 0x8B, 0x45, 0x13);
    private static readonly Brush GenericBrush = Gradient(0x6B, 0x72, 0x80, 0x4B, 0x55, 0x63);

    // ---------- 兜底图标（没登记的扩展名按大类回落） ----------

    private static readonly FileTypeIcon AudioFallback = new(AudioBrush, "AUDIO", "音频");
    private static readonly FileTypeIcon ArchiveFallback = new(ArchiveBrush, "ARCH", "压缩包");
    private static readonly FileTypeIcon TextFallback = new(TextBrush, "TXT", "文档");
    private static readonly FileTypeIcon GenericFallback = new(GenericBrush, "FILE", "文件");

    /// <summary>
    /// 扩展名（不含点，小写）→ 图标。
    ///
    /// 标签怎么取：
    /// - Office 三件套 / PDF / 纯文本沿用业界习惯的短标签（DOC / XLS / PPT），看着才像"文件图标"；
    /// - 音频、压缩、字幕、数据、代码这些「同类长得一样」的，标签直接用扩展名 ——
    ///   否则 .flac 会显示成 "MP3"，用户会以为软件认错了文件。
    /// </summary>
    private static readonly Dictionary<string, FileTypeIcon> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文档
        ["pdf"] = new(PdfBrush, "PDF", "PDF 文档"),
        ["doc"] = new(WordBrush, "DOC", "Word 文档"),
        ["docx"] = new(WordBrush, "DOC", "Word 文档"),
        ["rtf"] = new(WordBrush, "DOC", "Word 文档"),
        ["odt"] = new(WordBrush, "DOC", "Word 文档"),
        ["wps"] = new(WordBrush, "DOC", "Word 文档"),
        ["xls"] = new(ExcelBrush, "XLS", "Excel 表格"),
        ["xlsx"] = new(ExcelBrush, "XLS", "Excel 表格"),
        ["ods"] = new(ExcelBrush, "XLS", "Excel 表格"),
        ["csv"] = new(ExcelBrush, "CSV", "CSV 表格"),
        ["tsv"] = new(ExcelBrush, "CSV", "CSV 表格"),
        ["ppt"] = new(PptBrush, "PPT", "演示文稿"),
        ["pptx"] = new(PptBrush, "PPT", "演示文稿"),
        ["odp"] = new(PptBrush, "PPT", "演示文稿"),

        // 文本
        ["txt"] = new(TextBrush, "TXT", "文本文档"),
        ["log"] = new(TextBrush, "LOG", "日志"),
        ["nfo"] = new(TextBrush, "NFO", "说明文件"),
        ["md"] = new(TextBrush, "MD", "Markdown 文档"),
        ["markdown"] = new(TextBrush, "MD", "Markdown 文档"),

        // 数据 / 配置
        ["json"] = new(DataBrush, "JSON", "JSON 数据"),
        ["xml"] = new(DataBrush, "XML", "XML 数据"),
        ["yaml"] = new(DataBrush, "YML", "YAML 配置"),
        ["yml"] = new(DataBrush, "YML", "YAML 配置"),
        ["toml"] = new(DataBrush, "TOML", "TOML 配置"),
        ["ini"] = new(DataBrush, "INI", "配置文件"),
        ["sql"] = new(DataBrush, "SQL", "SQL 脚本"),

        // 代码
        ["cs"] = new(CodeBrush, "C#", "C# 源码"),
        ["py"] = new(CodeBrush, "PY", "Python 源码"),
        ["js"] = new(CodeBrush, "JS", "JavaScript 源码"),
        ["jsx"] = new(CodeBrush, "JSX", "React 源码"),
        ["ts"] = new(CodeBrush, "TS", "TypeScript 源码"),
        ["tsx"] = new(CodeBrush, "TSX", "React 源码"),
        ["java"] = new(CodeBrush, "JAVA", "Java 源码"),
        ["go"] = new(CodeBrush, "GO", "Go 源码"),
        ["rs"] = new(CodeBrush, "RS", "Rust 源码"),
        ["c"] = new(CodeBrush, "C", "C 源码"),
        ["cpp"] = new(CodeBrush, "C++", "C++ 源码"),
        ["h"] = new(CodeBrush, "H", "头文件"),
        ["html"] = new(CodeBrush, "HTML", "网页"),
        ["htm"] = new(CodeBrush, "HTML", "网页"),
        ["css"] = new(CodeBrush, "CSS", "样式表"),
        ["sh"] = new(CodeBrush, "SH", "Shell 脚本"),
        ["ps1"] = new(CodeBrush, "PS1", "PowerShell 脚本"),
        ["bat"] = new(CodeBrush, "BAT", "批处理脚本"),
        ["vue"] = new(CodeBrush, "VUE", "Vue 组件"),

        // 压缩包
        ["zip"] = new(ArchiveBrush, "ZIP", "压缩包"),
        ["rar"] = new(ArchiveBrush, "RAR", "压缩包"),
        ["7z"] = new(ArchiveBrush, "7Z", "压缩包"),
        ["tar"] = new(ArchiveBrush, "TAR", "压缩包"),
        ["gz"] = new(ArchiveBrush, "GZ", "压缩包"),
        ["bz2"] = new(ArchiveBrush, "BZ2", "压缩包"),
        ["xz"] = new(ArchiveBrush, "XZ", "压缩包"),
        ["iso"] = new(ArchiveBrush, "ISO", "光盘镜像"),

        // 音频
        ["mp3"] = new(AudioBrush, "MP3", "音频"),
        ["wav"] = new(AudioBrush, "WAV", "音频"),
        ["flac"] = new(AudioBrush, "FLAC", "无损音频"),
        ["m4a"] = new(AudioBrush, "M4A", "音频"),
        ["aac"] = new(AudioBrush, "AAC", "音频"),
        ["ogg"] = new(AudioBrush, "OGG", "音频"),
        ["wma"] = new(AudioBrush, "WMA", "音频"),
        ["ape"] = new(AudioBrush, "APE", "无损音频"),

        // 电子书 / 字幕
        ["epub"] = new(EbookBrush, "EPUB", "电子书"),
        ["mobi"] = new(EbookBrush, "MOBI", "电子书"),
        ["azw3"] = new(EbookBrush, "AZW3", "电子书"),
        ["srt"] = new(DataBrush, "SRT", "字幕"),
        ["ass"] = new(DataBrush, "ASS", "字幕"),
        ["ssa"] = new(DataBrush, "SSA", "字幕"),
    };

    /// <summary>
    /// 按扩展名取图标。
    ///
    /// 没登记的扩展名不直接甩个灰色 FILE：
    /// 短扩展名（≤4）用大类的配色 + 扩展名当标签（"KDBX" / "TORRENT"），
    /// 长扩展名才回落到大类的默认图标。没有扩展名的（noext、无后缀文件）才是 FILE。
    /// </summary>
    public static FileTypeIcon For(string? extension, MediaType type)
    {
        var ext = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

        if (ext.Length > 0 && ByExtension.TryGetValue(ext, out var known))
            return known;

        // 连扩展名都没有（README、Makefile 这类）：不猜大类，老实显示 FILE。
        // 猜成 TXT 会误导用户，以为软件认出了文件类型。
        if (ext.Length == 0)
            return GenericFallback;

        var (fallback, accent, description) = type switch
        {
            MediaType.Audio => (AudioFallback, AudioBrush, "音频"),
            MediaType.Archive => (ArchiveFallback, ArchiveBrush, "压缩包"),
            MediaType.Text => (TextFallback, TextBrush, "文档"),
            _ => (GenericFallback, GenericBrush, "文件"),
        };

        if (ext.Length is > 0 and <= 4)
            return new FileTypeIcon(accent, ext.ToUpperInvariant(), description);

        return fallback;
    }

    private static Brush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(r1, g1, b1),
            Color.FromRgb(r2, g2, b2),
            new Point(0, 0),
            new Point(0.55, 1));
        brush.Freeze();
        return brush;
    }
}
