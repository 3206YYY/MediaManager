using System.Diagnostics;
using System.Windows;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace MediaManager.App.Views;

/// <summary>
/// Interaction logic for SettingsWindow.xaml
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    private readonly IMediaRepository _repo;
    private readonly IAppConfigService _config;

    public SettingsWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        _repo = App.Services.GetRequiredService<IMediaRepository>();
        _config = App.Services.GetRequiredService<IAppConfigService>();
        // 窗口句柄就绪后，按当前主题设置标题栏深浅
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        // 不要在构造里同步等数据库：设置窗口打开时卡一下是最容易被感知的
        _ = LoadSettingsAsync();
        _ = RefreshCacheInfoAsync();
    }

    private async Task LoadSettingsAsync()
    {
        // 一次性把 4 个设置项从数据库取回（后台线程），避免 4 次同步阻塞
        var (discCountStr, closeStr, autoBackupStr, backupDir) = await Task.Run(async () => (
            await _repo.GetSettingAsync("DiscoveryCount"),
            await _repo.GetSettingAsync("CloseBehavior"),
            await _repo.GetSettingAsync("AutoBackupIntervalMinutes"),
            await _repo.GetSettingAsync("BackupDirectory")));

        DataDirBox.Text = _config.DataDirectory;
        CacheDirBox.Text = _config.ThumbnailDirectory;
        FfmpegBox.Text = _config.FfmpegPath ?? string.Empty;
        ConcurrencySlider.Value = _config.Concurrency;

        ThemeBox.SelectedIndex = _config.Theme == Core.Enums.AppTheme.Light ? 0 : 1;
        // 语言切换入口已隐藏（仅保留中文）：这里不再读 LanguageBox。
        DeleteBox.SelectedIndex = _config.DeleteBehavior switch
        {
            Core.Enums.DeleteBehavior.RecycleBin => 0,
            Core.Enums.DeleteBehavior.Permanent => 1,
            _ => 2,
        };

        // 发现页推荐数量
        var discCount = int.TryParse(discCountStr, out var c) ? c : 30;
        DiscoveryCountBox.SelectedIndex = discCount switch
        {
            10 => 0,
            20 => 1,
            50 => 3,
            100 => 4,
            _ => 2,
        };

        // 关闭窗口行为
        CloseBehaviorBox.SelectedIndex = closeStr switch
        {
            "Exit" => 1,
            "Tray" => 2,
            _ => 0,
        };

        // 备份信息 + 健康检查（备份目录不可达 / 长期没备份成功时明确提醒）
        var backup = App.Services.GetRequiredService<IDatabaseBackupService>();
        var backupDirPath = backup.BackupDirectory;
        var backupReachable = System.IO.Directory.Exists(backupDirPath);
        var lastBackup = backup.LastBackupTime;

        var autoBackup = int.TryParse(autoBackupStr, out var ab) ? ab : 0;

        var backupLines = new List<string>
        {
            lastBackup is DateTime t ? $"上次备份时间：{t:yyyy-MM-dd HH:mm:ss}" : "上次备份时间：尚未备份",
        };

        if (!backupReachable)
            backupLines.Add($"⚠ 备份目录当前不可用（设备未连接？）：{backupDirPath}");
        else if (autoBackup > 0 && lastBackup is DateTime lb
                 && DateTime.Now - lb > TimeSpan.FromMinutes(autoBackup * 2L))
            backupLines.Add($"⚠ 已超过 {autoBackup * 2} 分钟没有成功备份，请检查备份目录");

        LastBackupText.Text = string.Join("\n", backupLines);
        BackupDirText.Text = $"实际备份目录：{backupDirPath}";

        // 备份目录 + 自动备份间隔
        BackupDirBox.Text = backupDir ?? string.Empty;

        AutoBackupBox.SelectedIndex = autoBackup switch
        {
            15 => 1,
            30 => 2,
            60 => 3,
            120 => 4,
            360 => 5,
            _ => 0,
        };

        RefreshShellIntegrationState();
    }

    // ---------- 资源管理器右键集成 ----------

    /// <summary>按注册表真实状态刷新勾选与提示文案。</summary>
    private void RefreshShellIntegrationState()
    {
        var enabled = ShellIntegrationService.IsEnabled();
        ShellIntegrationBox.IsChecked = enabled;
        ShellIntegrationStateText.Text = enabled
            ? $"✅ 已启用：{ShellIntegrationService.ExecutablePath}"
            : "当前未启用。启用后立即生效，无需重启资源管理器。";
    }

    private void OnShellIntegrationClick(object sender, RoutedEventArgs e)
    {
        var want = ShellIntegrationBox.IsChecked == true;
        try
        {
            if (want)
                ShellIntegrationService.Enable();
            else
                ShellIntegrationService.Disable();

            RefreshShellIntegrationState();

            if (want)
                CustomMessageBox.Success(
                    "已在右键菜单中添加「用媒体管理器打开」。\n\n现在到资源管理器里右键点一张图片即可看到。",
                    "已启用");
            else
                CustomMessageBox.Info("已从右键菜单中移除。", "已关闭");
        }
        catch (Exception ex)
        {
            RefreshShellIntegrationState();
            CustomMessageBox.Error("修改右键菜单失败：" + ex.Message +
                                   "\n\n（通常是被安全软件拦截，或当前账户没有写入注册表的权限。）",
                                   "操作失败");
        }
    }

    // ---------- 缓存占用 ----------

    /// <summary>统计并显示缩略图缓存占用（后台线程统计，避免大目录卡界面）。</summary>
    private async Task RefreshCacheInfoAsync()
    {
        var dir = _config.ThumbnailDirectory;
        CacheSizeText.Text = "正在统计…";

        try
        {
            var (bytes, files) = await System.Threading.Tasks.Task.Run(() => ShellHelper.MeasureFolder(dir));

            // 顺带显示内存里已解码缩略图的占用（默认上限 200MB），让用户知道「缓存」有两层
            var (memBytes, memCount, memBudget) = MainViewModel.ThumbnailCacheInfo;

            CacheSizeText.Text = files == 0
                ? "当前缓存：0 MB（尚未生成缩略图）"
                : $"当前缓存：{ShellHelper.FormatSize(bytes)}（{files} 个缩略图文件）";
            CacheSizeText.Text +=
                $"\n内存缓存：{ShellHelper.FormatSize(memBytes)} / {ShellHelper.FormatSize(memBudget)}（{memCount} 张）";
        }
        catch (Exception ex)
        {
            CacheSizeText.Text = "缓存占用统计失败";
            System.Diagnostics.Debug.WriteLine("统计缓存占用失败：" + ex.Message);
        }
    }

    private async void OnCleanCache(object sender, RoutedEventArgs e)
    {
        var dir = _config.ThumbnailDirectory;
        var (bytes, files) = ShellHelper.MeasureFolder(dir);
        if (files == 0)
        {
            CustomMessageBox.Info("当前没有可清理的缩略图缓存。", "清理缓存");
            return;
        }

        var ok = CustomMessageBox.Confirm(
            $"将清理 {ShellHelper.FormatSize(bytes)} 缩略图缓存（共 {files} 个文件）。\n\n" +
            "原始图片和视频不会被删除；缩略图会在下次浏览时自动重新生成（首次浏览可能稍慢）。",
            "清理缓存", confirmText: "开始清理");
        if (!ok) return;

        try
        {
            CacheSizeText.Text = "正在清理…";
            var svc = App.Services.GetRequiredService<IThumbnailService>();
            var removed = await svc.CleanCacheAsync();

            // 数据库里记录的缓存路径也要清空，否则重新加载时会指向已被删除的文件
            await _mainViewModel.ResetThumbnailCacheAsync();

            _ = RefreshCacheInfoAsync();
            CustomMessageBox.Success($"已清理 {removed} 个缩略图文件。", "清理完成");
        }
        catch (Exception ex)
        {
            _ = RefreshCacheInfoAsync();
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "清理缓存"), "清理缓存失败");
        }
    }

    // ---------- 打开目录 ----------

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
        => OpenFolderSafely(_config.DataDirectory, "打开数据目录");

    private void OnOpenCacheDir(object sender, RoutedEventArgs e)
        => OpenFolderSafely(_config.ThumbnailDirectory, "打开缓存目录");

    private void OnOpenBackupDir(object sender, RoutedEventArgs e)
    {
        var backup = App.Services.GetRequiredService<IDatabaseBackupService>();
        OpenFolderSafely(backup.BackupDirectory, "打开备份目录");
    }

    private void OpenFolderSafely(string? dir, string action)
    {
        try
        {
            ShellHelper.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Warn(StorageErrors.WithAction(ex, action), "无法打开目录");
        }
    }

    /// <summary>选择备份目录（可设为 U 盘等）。使用现代文件夹选择器，可看到目录树与内容列表。</summary>
    private void OnBrowseBackupDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择数据备份目录（可设为 U 盘等位置）",
        };
        if (!string.IsNullOrWhiteSpace(BackupDirBox.Text) && System.IO.Directory.Exists(BackupDirBox.Text))
            dlg.InitialDirectory = BackupDirBox.Text;

        if (dlg.ShowDialog() == true)
            BackupDirBox.Text = dlg.FolderName;
    }

    /// <summary>清空备份目录 = 恢复默认（程序数据目录）。</summary>
    private void OnResetBackupDir(object sender, RoutedEventArgs e)
    {
        BackupDirBox.Text = string.Empty;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择 ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|所有文件|*.*" };
        if (dlg.ShowDialog() == true)
            FfmpegBox.Text = dlg.FileName;
    }

    /// <summary>导出数据到指定位置（如 U 盘），用于换机迁移。</summary>
    private async void OnExportData(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出数据到…",
            Filter = "媒体管理器备份 (*.mmdb)|*.mmdb|所有文件 (*.*)|*.*",
            FileName = $"MediaManager_backup_{DateTime.Now:yyyyMMdd_HHmmss}.mmdb",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var backup = App.Services.GetRequiredService<IDatabaseBackupService>();
            await backup.ExportAsync(dlg.FileName);
            CustomMessageBox.Success(
                $"数据已导出到：\n{dlg.FileName}\n\n收藏、分类、标签、备注等全部数据都在里面。\n" +
                "换新电脑后，在设置里点「导入数据」选择这个文件即可恢复。",
                "导出成功");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "导出"), "导出失败");
        }
    }

    /// <summary>从备份文件导入数据（覆盖当前数据）。</summary>
    private async void OnImportData(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择备份文件",
            Filter = "媒体管理器备份 (*.mmdb;*.db)|*.mmdb;*.db|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var confirm = CustomMessageBox.Confirm(
            "导入将用备份文件的内容覆盖当前所有数据（收藏、分类、标签、备注等）。\n\n确定继续吗？导入完成后请重启程序。",
            "确认导入", confirmText: "导入", danger: true);
        if (!confirm) return;

        try
        {
            var backup = App.Services.GetRequiredService<IDatabaseBackupService>();
            await backup.ImportAsync(dlg.FileName);
            CustomMessageBox.Success("数据已导入。请重启程序以加载新数据。", "导入完成");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(StorageErrors.WithAction(ex, "导入"), "导入失败");
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var themeTag = (ThemeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var deleteTag = (DeleteBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var discoveryTag = (DiscoveryCountBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
            var closeTag = (CloseBehaviorBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Ask";

            await _repo.SetSettingAsync("FfmpegPath", FfmpegBox.Text);
            await _repo.SetSettingAsync("Concurrency", ((int)ConcurrencySlider.Value).ToString());
            await _repo.SetSettingAsync("Theme", themeTag);
            // 语言切换入口已隐藏：保存时固定写回中文，不覆盖用户将来可能的手动配置以外的值
            await _repo.SetSettingAsync("Language", "Chinese");
            await _repo.SetSettingAsync("DeleteBehavior", deleteTag);
            await _repo.SetSettingAsync("DiscoveryCount", discoveryTag);
            await _repo.SetSettingAsync("CloseBehavior", closeTag);

            // 备份目录 + 自动备份间隔
            await _repo.SetSettingAsync("BackupDirectory",
                string.IsNullOrWhiteSpace(BackupDirBox.Text) ? null : BackupDirBox.Text.Trim());
            var autoBackupTag = (AutoBackupBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "0";
            await _repo.SetSettingAsync("AutoBackupIntervalMinutes", autoBackupTag);

            // 主题立即生效（无需重启）
            try
            {
                App.ApplyThemeNow(string.Equals(themeTag, "Dark", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("切换主题失败：" + ex.Message);
            }

            CustomMessageBox.Success("设置已保存。主题已立即生效，其余设置重启后生效。", "保存成功");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error("保存设置失败：" + ex.Message, "保存失败");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>卸载软件：运行安装目录里的 unins000.exe（卸载时会询问是否保留数据）。</summary>
    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        var uninstaller = System.IO.Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (System.IO.File.Exists(uninstaller))
        {
            var confirm = CustomMessageBox.Confirm(
                "确定要卸载「媒体管理器」吗？\n\n卸载时会询问是否保留你的收藏、分类、备注等数据。",
                "卸载", confirmText: "卸载", danger: true);
            if (!confirm) return;

            DialogResult = false;
            System.Diagnostics.Process.Start(uninstaller);
            Application.Current.Shutdown();
        }
        else
        {
            CustomMessageBox.Info(
                "当前是便携版（绿色版），无需卸载。\n直接删除程序所在文件夹即可。",
                "卸载");
        }
    }

    /// <summary>
    /// 赞助开发者：用默认浏览器打开爱发电主页。
    /// 通过 explorer.exe 拉起，确保走系统默认浏览器而非内置打开方式。
    /// </summary>
    private void OnSponsor(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", "https://afdian.com/a/3206YYY");
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error(
                "无法打开赞助链接，请手动访问：\nhttps://afdian.com/a/3206YYY\n\n" + ex.Message,
                "赞助");
        }
    }
}
