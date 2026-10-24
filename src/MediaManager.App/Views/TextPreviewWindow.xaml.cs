using System.Text;
using System.Windows;
using MediaManager.App.Services;

namespace MediaManager.App.Views;

/// <summary>文本只读预览：自动识别 UTF-8 / UTF-16 / GBK，大文件只读前 2 MB。</summary>
public partial class TextPreviewWindow : Window
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    public TextPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
    }

    public void Load(string path)
    {
        Title = System.IO.Path.GetFileName(path);
        try
        {
            var info = new System.IO.FileInfo(path);
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);

            var take = (int)Math.Min(fs.Length, MaxBytes);
            var buffer = new byte[take];
            var read = fs.Read(buffer, 0, take);
            if (read < take) buffer = buffer[..read];

            var encoding = DetectEncoding(buffer);
            var text = encoding.GetString(buffer);
            Viewer.Text = text;

            var lines = text.Count(c => c == '\n') + 1;
            var truncated = info.Length > MaxBytes;
            InfoText.Text = truncated
                ? $"{FormatSize(info.Length)} · {encoding.WebName} · 文件较大，仅预览前 2 MB"
                : $"{FormatSize(info.Length)} · {encoding.WebName} · {lines} 行";
        }
        catch (Exception ex)
        {
            Viewer.Text = "无法读取该文件：\n" + ex.Message;
            InfoText.Text = string.Empty;
        }
    }

    /// <summary>编码探测：BOM 优先；再尝试严格 UTF-8；都失败按 GBK（936）。</summary>
    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch
        {
            // 非法 UTF-8，按中文环境常见的 GBK 处理
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}
