using System.Collections.ObjectModel;
using System.Windows;
using MediaManager.App.Services;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Views;

/// <summary>
/// 最近删除窗口：查看自己删了什么，决定「还原」还是「彻底删除」。
/// </summary>
public partial class RecentlyDeletedWindow : Window
{
    private readonly ObservableCollection<DeletedMedia> _items = new();
    private readonly DeletedItemsService _service;

    public RecentlyDeletedWindow()
    {
        InitializeComponent();
        _service = App.Services.GetRequiredService<DeletedItemsService>();
        ItemsList.ItemsSource = _items;
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var list = await _service.GetRecentAsync();
            _items.Clear();
            foreach (var d in list)
                _items.Add(d);

            EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _items.Count == 0 ? string.Empty : $"共 {_items.Count} 项";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取最近删除失败：" + ex.Message;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeletedMedia item }) return;

        try
        {
            StatusText.Text = $"正在还原 {item.FileName}…";
            var (ok, message) = await _service.RestoreAsync(item);
            if (ok)
            {
                _items.Remove(item);
                StatusText.Text = $"已还原：{item.FileName}";
            }
            else
            {
                StatusText.Text = message;
                CustomMessageBox.Warn(message, "还原失败");
            }

            EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "还原失败：" + ex.Message;
            CustomMessageBox.Error(Core.Diagnostics.StorageErrors.WithAction(ex, "还原"), "还原失败");
        }
    }

    private async void OnPurgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeletedMedia item }) return;

        var ok = CustomMessageBox.ConfirmDanger(
            $"彻底删除后无法恢复：\n\n{item.FileName}\n\n" +
            "这会把文件从系统回收站中永久清除。确定继续吗？",
            "彻底删除");
        if (!ok) return;

        try
        {
            await _service.PurgeAsync(item);
            _items.Remove(item);
            StatusText.Text = $"已彻底删除：{item.FileName}";
            EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "彻底删除失败：" + ex.Message;
            CustomMessageBox.Error(Core.Diagnostics.StorageErrors.WithAction(ex, "彻底删除"), "删除失败");
        }
    }

    private async void OnClearRecords(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var ok = CustomMessageBox.Confirm(
            "清空后列表将不再显示这些记录。\n\n文件仍留在系统回收站里，需要彻底删除请逐条点「彻底删除」，或到回收站清空。",
            "清空列表记录", confirmText: "清空记录");
        if (!ok) return;

        await _service.ClearRecordsAsync();
        await ReloadAsync();
        StatusText.Text = "已清空最近删除记录";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
