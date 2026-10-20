using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaManager.Infrastructure.FileSystem;
using MediaManager.Core.Models;

namespace MediaManager.App.Views.Dialogs;

/// <summary>批量重命名对话框：前缀 + 序号 + 位数 + 重复名检测，带实时预览。</summary>
public partial class BatchRenameWindow : Window
{
    private readonly IReadOnlyList<MediaItem> _items;
    private bool _ready;

    /// <summary>点「开始重命名」后填好；取消则为 null。</summary>
    public BatchRenameOptions? Options { get; private set; }

    public BatchRenameWindow(IReadOnlyList<MediaItem> items)
    {
        InitializeComponent();
        _items = items;

        CountText.Text = $"将为选中的 {items.Count} 个文件按「前缀 + 序号」重命名，扩展名保持不变。";
        PrefixBox.Text = GuessPrefix(items.Count > 0 ? items[0].FileName : null);
        DragArea.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        _ready = true;
        UpdatePreview();
        Loaded += (_, _) => { PrefixBox.Focus(); PrefixBox.SelectAll(); };
    }

    /// <summary>从原文件名猜一个前缀：IMG_1234 → IMG_，"照片 001" → "照片 "。</summary>
    private static string GuessPrefix(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(name, @"^(.*?)([\s_\-]*\d+)?$");
        var prefix = m.Success ? m.Groups[1].Value : name;
        if (prefix.Length > 24) prefix = prefix[..24];
        return prefix;
    }

    private int Digits => int.TryParse(
        (DigitsBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var d) ? d : 3;

    private BatchRenameOptions BuildOptions()
    {
        var start = int.TryParse(StartBox.Text.Trim(), out var s) && s >= 0 ? s : 1;
        return new BatchRenameOptions(PrefixBox.Text, start, Digits, SuffixBox.Text);
    }

    private void OnDigitsChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_ready || PreviewText == null) return;

        var options = BuildOptions();
        var plan = BatchOperationService.BuildRenamePlan(_items, options, out var conflicts);

        if (plan.Count == 0)
        {
            PreviewText.Text = "（没有可重命名的文件）";
            ShowWarn(conflicts);
            return;
        }

        var lines = plan.Take(4).Select(p => $"  {p.OldName}  →  {p.NewName}").ToList();
        if (plan.Count > 4)
            lines.Add($"  …… 共 {plan.Count} 个（最后一个是 {plan[^1].NewName}）");

        PreviewText.Text = string.Join("\n", lines);
        ShowWarn(conflicts);
    }

    private void ShowWarn(List<string> conflicts)
    {
        if (conflicts.Count == 0)
        {
            WarnText.Visibility = Visibility.Collapsed;
            return;
        }

        WarnText.Text = $"⚠ 有 {conflicts.Count} 个文件会被跳过：\n" +
                        string.Join("\n", conflicts.Take(4)) +
                        (conflicts.Count > 4 ? "\n…" : "");
        WarnText.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions();
        var prefix = options.Prefix;

        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            CustomMessageBox.Warn("前缀里不能包含 \\ / : * ? \" < > | 这些字符。", "批量重命名");
            return;
        }

        var plan = BatchOperationService.BuildRenamePlan(_items, options, out var conflicts);
        if (plan.Count == 0)
        {
            CustomMessageBox.Warn("没有可以重命名的文件，请调整前缀或起始序号。", "批量重命名");
            return;
        }

        if (conflicts.Count > 0)
        {
            var ok = CustomMessageBox.Confirm(
                $"有 {conflicts.Count} 个文件因为重名会被跳过，其余 {plan.Count} 个正常重命名。\n\n继续吗？",
                "批量重命名", confirmText: "继续");
            if (!ok) return;
        }

        Options = options;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Options = null;
        DialogResult = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Options = null;
            DialogResult = false;
        }
    }
}
