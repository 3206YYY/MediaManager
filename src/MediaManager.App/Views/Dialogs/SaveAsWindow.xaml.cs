using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaManager.App.Editing;
using Microsoft.Win32;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 「另存为」对话框：选保存路径、格式与 JPEG/WebP 质量。
/// 只负责收集参数，真正的渲染与写盘由 <see cref="EditorRenderer.SaveAsAsync"/> 完成。
/// </summary>
public partial class SaveAsWindow : Window
{
    private readonly string _sourcePath;
    private bool _suppressFormatUi;

    public string OutputPath { get; private set; } = "";
    public SaveImageFormat Format { get; private set; } = SaveImageFormat.Png;
    public int Quality { get; private set; } = 92;

    public SaveAsWindow(string sourcePath, string suggestedFileName, SaveImageFormat initialFormat)
    {
        InitializeComponent();
        _sourcePath = sourcePath;

        _suppressFormatUi = true;
        try
        {
            FormatCombo.SelectedIndex = initialFormat switch
            {
                SaveImageFormat.Jpeg => 1,
                SaveImageFormat.Webp => 2,
                SaveImageFormat.Bmp => 3,
                _ => 0,
            };
        }
        finally
        {
            _suppressFormatUi = false;
        }

        Format = initialFormat;
        var dir = Path.GetDirectoryName(sourcePath) ?? "";
        PathBox.Text = Path.Combine(dir, suggestedFileName);
        UpdateQualityUi();

        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        };
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormatUi) return;

        Format = FormatOf(FormatCombo.SelectedItem as ComboBoxItem);
        UpdateQualityUi();

        // 换格式时同步扩展名，避免出现「文件名写 .png，内容却是 JPEG」
        var path = PathBox.Text?.Trim() ?? "";
        if (path.Length > 0)
        {
            var ext = EditorRenderer.ExtensionOf(Format);
            var current = Path.GetExtension(path);
            PathBox.Text = string.IsNullOrEmpty(current)
                ? path + ext
                : Path.ChangeExtension(path, ext);
        }
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        QualityText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "另存为",
            FileName = Path.GetFileName(PathBox.Text?.Trim() ?? ""),
            Filter = FilterFor(Format),
            AddExtension = true,
            OverwritePrompt = true,
        };

        var dir = Path.GetDirectoryName(PathBox.Text?.Trim() ?? "");
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dialog.InitialDirectory = dir;

        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            CustomMessageBox.Warn("请先选择保存位置。", "另存为");
            PathBox.Focus();
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn($"路径不合法：{ex.Message}", "另存为");
            return;
        }

        if (string.Equals(full, Path.GetFullPath(_sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            CustomMessageBox.Warn("不能覆盖正在编辑的原图，请换一个文件名。", "另存为");
            PathBox.Focus();
            return;
        }

        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            CustomMessageBox.Warn($"目标文件夹不存在：\n{dir}", "另存为");
            return;
        }

        if (File.Exists(full) &&
            !CustomMessageBox.Confirm($"文件已存在，要覆盖它吗？\n{full}", "另存为", "覆盖"))
        {
            return;
        }

        OutputPath = full;
        Format = FormatOf(FormatCombo.SelectedItem as ComboBoxItem);
        Quality = (int)Math.Round(QualitySlider.Value);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        OutputPath = "";
        DialogResult = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OutputPath = "";
        DialogResult = false;
    }

    private void UpdateQualityUi()
    {
        var supportsQuality = Format is SaveImageFormat.Jpeg or SaveImageFormat.Webp;
        QualitySlider.IsEnabled = supportsQuality;
        QualityText.Opacity = supportsQuality ? 1 : 0.4;
    }

    private static SaveImageFormat FormatOf(ComboBoxItem? item) => item?.Tag?.ToString() switch
    {
        "jpeg" => SaveImageFormat.Jpeg,
        "webp" => SaveImageFormat.Webp,
        "bmp" => SaveImageFormat.Bmp,
        _ => SaveImageFormat.Png,
    };

    private static string FilterFor(SaveImageFormat format) => format switch
    {
        SaveImageFormat.Jpeg => "JPEG 图片|*.jpg;*.jpeg",
        SaveImageFormat.Webp => "WebP 图片|*.webp",
        SaveImageFormat.Bmp => "BMP 图片|*.bmp",
        _ => "PNG 图片|*.png",
    };
}
