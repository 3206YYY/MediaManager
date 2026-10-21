using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MediaManager.App.Plugins;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Plugins;
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

        InitializePages();

        // 不要在构造里同步等数据库：设置窗口打开时卡一下是最容易被感知的
        _ = LoadSettingsAsync();
        _ = RefreshCacheInfoAsync();
    }

    // ===================================================================
    //  左侧分类导航
    // ===================================================================

    private IReadOnlyList<FrameworkElement> Pages => new[]
    {
        PageGeneral, PageMedia, PageIntegration, PageStorage, PagePlugins, PageAbout,
    };

    private void InitializePages()
    {
        // XAML 里给第一项设了 IsSelected，事件可能在字段赋值前就触发，
        // 所以这里统一显式指定一次初始页，别依赖事件时序
        ShowPage("general");

        var version = UpdateChecker.CurrentVersion;
        SidebarVersionText.Text = $"v{version}";
        VersionText.Text = $"v{version}";
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;
        ShowPage(item.Tag?.ToString());
    }

    /// <summary>同一时刻只显示一个分类页（和查看器的工具面板一样，不做「叠着看」）。</summary>
    private void ShowPage(string? key)
    {
        var target = key switch
        {
            "media" => PageMedia,
            "integration" => PageIntegration,
            "storage" => PageStorage,
            "plugins" => PagePlugins,
            "about" => PageAbout,
            _ => PageGeneral,
        };

        if (target == null) return;

        foreach (var page in Pages)
        {
            if (page == null) continue;
            page.Visibility = ReferenceEquals(page, target) ? Visibility.Visible : Visibility.Collapsed;
        }

        ContentScroll?.ScrollToTop();

        if (key == "plugins") RefreshPluginPage();
    }

    private async Task LoadSettingsAsync()
    {
        // 一次性把 5 个设置项从数据库取回（后台线程），避免 5 次同步阻塞
        var (discCountStr, closeStr, autoBackupStr, backupDir, rememberModeStr, themeStr) = await Task.Run(async () => (
            await _repo.GetSettingAsync("DiscoveryCount"),
            await _repo.GetSettingAsync("CloseBehavior"),
            await _repo.GetSettingAsync("AutoBackupIntervalMinutes"),
            await _repo.GetSettingAsync("BackupDirectory"),
            await _repo.GetSettingAsync(VideoPlayerWindow.RememberModeSettingKey),
            await _repo.GetSettingAsync("Theme")));

        DataDirBox.Text = _config.DataDirectory;
        CacheDirBox.Text = _config.ThumbnailDirectory;
        FfmpegBox.Text = _config.FfmpegPath ?? string.Empty;
        ConcurrencySlider.Value = _config.Concurrency;
        UpdateConcurrencyText();

        // 「记住播放模式」：只有明确存了 "0" 才是关闭，其余（含从未设置）都算开启
        RememberVideoModeBox.IsChecked = rememberModeStr != "0";

        // 主题下拉反映的是「用户选择的偏好」（跟随系统 / 浅色 / 深色），不是当前实际深浅。
        // 直接读库，不用 _config.Theme：那是启动时的快照，运行中切过主题不会自动更新。
        ThemeBox.SelectedIndex = themeStr?.Trim().ToLowerInvariant() switch
        {
            "dark" => 2,
            "light" => 1,
            _ => 0,
        };
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
            // 语言切换入口已隐藏：保存时固定写回中文，不覆盖用户将来可能的手动配置以外的值
            await _repo.SetSettingAsync("Language", "Chinese");
            await _repo.SetSettingAsync("DeleteBehavior", deleteTag);
            await _repo.SetSettingAsync("DiscoveryCount", discoveryTag);
            await _repo.SetSettingAsync("CloseBehavior", closeTag);

            // 视频播放模式是否记忆（关掉后每次打开都是播完暂停）
            await _repo.SetSettingAsync(VideoPlayerWindow.RememberModeSettingKey,
                RememberVideoModeBox.IsChecked == true ? "1" : "0");

            // 备份目录 + 自动备份间隔
            await _repo.SetSettingAsync("BackupDirectory",
                string.IsNullOrWhiteSpace(BackupDirBox.Text) ? null : BackupDirBox.Text.Trim());
            var autoBackupTag = (AutoBackupBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "0";
            await _repo.SetSettingAsync("AutoBackupIntervalMinutes", autoBackupTag);

            // 内存配置必须跟刚写入的值对齐。
            // 否则它还是启动时的快照：下次打开设置窗口显示旧值，
            // 用户再改别的设置一保存，就把刚才这些改动全部写回旧值。
            try
            {
                await _config.ReloadAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("刷新内存配置失败：" + ex.Message);
            }

            // 主题立即生效（无需重启）：跟随系统 / 浅色 / 深色统一走这里，
            // 它会同时落库、写缓存并同步内存配置，避免只改界面不改库。
            await App.SetThemePreferenceAsync(themeTag ?? App.ThemeModeSystem);

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

    // ===================================================================
    //  插件扩展
    // ===================================================================

    private PluginService? _pluginService;

    private PluginService? PluginSvc => _pluginService ??= TryGetPluginService();

    private static PluginService? TryGetPluginService()
    {
        try { return App.Services.GetService<PluginService>(); }
        catch { return null; }
    }

    /// <summary>按宿主里的最新状态重画插件列表（加载/扫描完成后调用）。</summary>
    private void RefreshPluginPage()
    {
        var svc = PluginSvc;
        if (svc == null)
        {
            PluginSummaryText.Text = "插件服务未初始化（仅影响插件，其它设置照常可用）。";
            return;
        }

        PluginDirBox.Text = svc.PluginDirectory;

        var report = svc.LastReport;
        var list = report.Plugins;

        PluginSummaryText.Text = list.Count == 0
            ? "还没有插件。把插件文件夹（或 dll）放进上面的目录，然后点「重新扫描」。"
            : $"共 {list.Count} 个插件：{report.LoadedCount} 个已加载"
              + (report.FailedCount > 0 ? $"，{report.FailedCount} 个加载失败（见下方红框）" : string.Empty);

        PluginListPanel.Children.Clear();

        foreach (var info in list.OrderByDescending(p => p.IsLoaded).ThenBy(p => p.Name))
            PluginListPanel.Children.Add(BuildPluginRow(info));

        if (list.Count == 0)
        {
            PluginListPanel.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Child = new TextBlock
                {
                    Text = "（空）把别人写好的插件解压到插件目录，重启或点「重新扫描」即可。",
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
    }

    private Border BuildPluginRow(PluginInfo info)
    {
        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
            BorderBrush = info.IsLoaded
                ? (System.Windows.Media.Brush)FindResource("BorderBrush")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var panel = new StackPanel();

        var header = new DockPanel();
        var state = new TextBlock
        {
            Text = info.IsLoaded ? "● 已加载" : "● 加载失败",
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = info.IsLoaded
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xA0, 0x43))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)),
        };
        DockPanel.SetDock(state, Dock.Right);

        header.Children.Add(state);
        header.Children.Add(new TextBlock
        {
            Text = info.IsLoaded ? $"{info.Name}  v{info.Version}" : info.Name,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(header);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Author)) meta.Add("作者 " + info.Author);
        if (info.IsLoaded) meta.Add($"菜单动作 {info.ActionCount} 个");
        if (meta.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", meta),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            });
        }

        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = info.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            });
        }

        if (!info.IsLoaded && !string.IsNullOrWhiteSpace(info.Error))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "原因：" + info.Error,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)),
            });
        }

        card.Child = panel;
        return card;
    }

    private void OnOpenPluginDir(object sender, RoutedEventArgs e)
    {
        var dir = PluginSvc?.PluginDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch { /* 建不出来就在下面统一提示 */ }

        OpenFolderSafely(dir, "打开插件目录");
    }

    private async void OnRescanPlugins(object sender, RoutedEventArgs e)
    {
        var svc = PluginSvc;
        if (svc == null) return;

        PluginSummaryText.Text = "正在扫描…";
        try
        {
            var report = await svc.ReloadAsync();
            RefreshPluginPage();

            FooterHint.Text = report.LoadedCount > 0
                ? $"已加载 {report.LoadedCount} 个插件，主界面右上角会出现「🧩 插件」菜单。"
                : "没有发现可用插件。";
        }
        catch (Exception ex)
        {
            RefreshPluginPage();
            CustomMessageBox.Error("扫描插件失败：" + ex.Message, "插件");
        }
    }

    private void OnOpenPluginDocs(object sender, RoutedEventArgs e)
        => OpenUrl($"{UpdateChecker.RepositoryUrl}/blob/main/docs/plugin-api.md");

    private void OnOpenPluginTemplate(object sender, RoutedEventArgs e)
        => OpenUrl($"{UpdateChecker.RepositoryUrl}/tree/main/docs/plugin-template");

    // ===================================================================
    //  版本 / 更新
    // ===================================================================

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在连接 GitHub…";

        try
        {
            var result = await UpdateChecker.CheckAsync();

            if (!result.Succeeded)
            {
                UpdateStatusText.Text =
                    $"检查失败：{result.Error}\n可以点右边的「打开 GitHub 主页」自己看看。";
                return;
            }

            if (result.HasUpdate)
            {
                UpdateStatusText.Text = $"发现新版本 v{result.LatestVersion}（当前 v{result.CurrentVersion}）";

                var go = CustomMessageBox.Confirm(
                    $"发现新版本 v{result.LatestVersion}（当前 v{result.CurrentVersion}）。\n\n"
                    + "现在打开 GitHub 下载页吗？下载安装包覆盖安装即可，你的数据会保留。",
                    "有新版本", confirmText: "去下载");
                if (go) OpenUrl(result.ReleaseUrl ?? UpdateChecker.ReleasesUrl);
            }
            else
            {
                UpdateStatusText.Text = string.IsNullOrWhiteSpace(result.LatestVersion)
                    ? "GitHub 上还没有正式发布的版本记录。"
                    : $"已是最新版本（v{result.CurrentVersion}）。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnOpenRepository(object sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.RepositoryUrl);

    /// <summary>用系统默认浏览器打开链接（explorer.exe 比 Process.Start(url) 更稳）。</summary>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start("explorer.exe", url);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error($"无法打开链接，请手动访问：\n{url}\n\n{ex.Message}", "打开链接失败");
        }
    }

    // ===================================================================
    //  其它小交互
    // ===================================================================

    private void OnConcurrencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateConcurrencyText();

    private void UpdateConcurrencyText()
    {
        if (ConcurrencyText == null) return;
        ConcurrencyText.Text = ((int)Math.Round(ConcurrencySlider.Value)).ToString();
    }
}
