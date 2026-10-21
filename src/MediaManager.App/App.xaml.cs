using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Windows;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Services;
using MediaManager.Infrastructure;
using MediaManager.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MediaManager.App;

/// <summary>应用入口。启动策略：主窗口先出画面，数据/备份/注册表全部后台补。</summary>
public partial class App : Application
{
    private IServiceProvider? _provider;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    /// <summary>后台任务的取消令牌：退出时统一取消，避免关窗后还在读盘。</summary>
    private readonly CancellationTokenSource _bgCts = new();

    /// <summary>上一次弹「未处理异常」提示的时间，用于防刷屏。</summary>
    private static DateTime _lastErrorDialogUtc = DateTime.MinValue;

    public static IServiceProvider Services => ((App)Current)._provider!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallExceptionHandlers();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                System.IO.Path.Combine(ResolveLogDirectory(), "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("入口开始：进程启动后 {Ms:F0} ms（含 CLR/WPF 初始化）",
            (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);

        // ---- 依赖注入 ----
        // 不用 Host.CreateDefaultBuilder：它会去探测 appsettings.json / 环境变量、
        // 挂控制台日志和托管生命周期，对一个纯 WPF 客户端是纯开销（实测多花约 200ms）。
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(Log.Logger, dispose: false));
        services.AddInfrastructure();
        services.AddSingleton<Services.DeletedItemsService>();
        services.AddSingleton<Plugins.PluginService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<ViewerViewModel>();
        services.AddTransient<DuplicateViewModel>();
        services.AddSingleton<MainWindow>();
        _provider = services.BuildServiceProvider();

        Log.Debug("依赖注入就绪：进程启动后 {Ms:F0} ms", ElapsedMs());

        // ---- 主题：读缓存文件，绝不在这里碰数据库 ----
        // 保证窗口第一帧就是正确配色，同时不把「打开 SQLite」这一步拉进关键路径。
        var detector = _provider.GetRequiredService<IPortableModeDetector>();
        ApplyTheme(ReadCachedTheme(detector), persist: false);

        // 「跟随系统」时，Windows 改深浅色后界面要跟着走（只在偏好为 System 时生效）
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // ---- 主窗口立刻显示 ----
        _mainWindow = _provider.GetRequiredService<MainWindow>();
        Log.Debug("主窗口构建完成：进程启动后 {Ms:F0} ms", ElapsedMs());
        MainWindow = _mainWindow;
        _mainWindow.Show();

        var startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        Log.Information("主窗口已显示：进程启动后 {Ms:F0} ms", startupMs);

        // ---- 其余全部丢到后台，不占用「开窗」这条关键路径 ----
        _ = BootstrapAsync(_mainWindow, e.Args);
    }

    /// <summary>进程启动至今的毫秒数（用于分段计时，排查启动瓶颈）。</summary>
    private static double ElapsedMs()
        => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    /// <summary>
    /// 日志目录必须可写。用户若在安装时选「为所有用户安装」，程序会落到 Program Files，
    /// 那里普通权限写不进去——日志初始化失败会连带拖垮整个启动。
    /// 所以日志跟随数据目录：便携模式 Data\logs，安装模式 %LocalAppData%\MediaManager\logs。
    /// </summary>
    private static string ResolveLogDirectory()
    {
        try
        {
            var programDir = AppContext.BaseDirectory;
            var dir = File.Exists(System.IO.Path.Combine(programDir, "portable.marker"))
                ? System.IO.Path.Combine(programDir, "Data", "logs")
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MediaManager", "logs");

            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            // 极端情况（连数据目录都建不出来）：退回临时目录，保证程序仍然能起来
            return System.IO.Path.GetTempPath();
        }
    }

    /// <summary>后台引导：数据库初始化 → 主题校验 → 数据加载 → 托盘 → 备份循环。</summary>
    private async Task BootstrapAsync(MainWindow mainWindow, string[] args)
    {
        var provider = _provider!;
        try
        {
            // 1) 数据库初始化（建表/PRAGMA/迁移）。全新安装或大版本升级时这里最慢，
            //    必须离开 UI 线程，否则用户看到的就是「点了没反应」。
            var init = provider.GetRequiredService<DatabaseInitializer>();
            await Task.Run(init.Initialize);

            // 2) 主题校准：用户可能在设置里改过，缓存文件与数据库不一致时纠正
            await SyncThemeFromDatabaseAsync(provider);

            // 3) 托盘图标（必须在 UI 线程创建）
            _tray = new TrayIconService(
                showMainWindow: () => mainWindow.Dispatcher.Invoke(ShowMainWindow),
                exitApplication: () => mainWindow.Dispatcher.Invoke(ShutdownApp));
            mainWindow.TrayService = _tray;

            // 4) 加载数据（内部把 SQLite 查询放到线程池，UI 线程只做集合填充）
            await provider.GetRequiredService<MainViewModel>().InitializeAsync();

            LogMemory("数据加载完成");

            // 启动期产生了大量一次性临时对象（DTO/查询字符串/字体资源），
            // 这里做一次紧凑回收，把碎片内存直接还给系统，避免带着几十 MB 垃圾跑一整天。
            await Task.Run(() =>
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            });
            LogMemory("首次回收后");

            // 5) 右键集成路径修正 + 命令行打开文件：都不影响主界面可用
            await Task.Run(ShellIntegrationService.EnsureRegistered);
            OpenFileFromCommandLine(args, mainWindow);

            // 5.5) 插件：后台加载，慢插件不会挡住开窗；加载失败只影响它自己
            _ = provider.GetRequiredService<Plugins.PluginService>().ReloadAsync();

            // 6) 补齐历史数据缺失的图片宽高（老库全是空值，界面一直显示「未知」）
            _ = Task.Run(async () =>
            {
                try
                {
                    await provider.GetRequiredService<IScanService>().BackfillSizesAsync(_bgCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warning(ex, "补齐图片尺寸失败"); }
            });

            // 7) 备份 / 清理 / 定时备份循环
            _ = BackupLoopAsync(provider);

            // 7) 启动稳定后再打一条内存日志（此时首屏缩略图已经解码完）
            await Task.Delay(2500);
            LogMemory("启动稳定后");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动后台任务失败");
            try
            {
                CustomMessageBox.Warn(
                    "部分初始化没有完成，但主界面仍可正常使用。\n\n"
                    + "如果图片列表是空的，请稍后点右上角「刷新」重试；反复出现请把 logs 目录的日志发给开发者。\n\n"
                    + "详细信息：" + ex.Message,
                    "媒体管理器");
            }
            catch { /* 弹窗失败则忽略 */ }
        }
    }

    /// <summary>启动备份 + 按设置周期自动备份（后台循环）。</summary>
    private async Task BackupLoopAsync(IServiceProvider provider)
    {
        var backup = provider.GetRequiredService<IDatabaseBackupService>();
        var repo = provider.GetRequiredService<IMediaRepository>();

        // 启动时清理超过 30 天的「最近删除」记录（回收站文件交给系统策略处理）
        try
        {
            await Task.Run(() => provider.GetRequiredService<Services.DeletedItemsService>()
                .PurgeOldRecordsAsync(30));
        }
        catch (Exception ex) { Log.Warning(ex, "清理最近删除记录失败"); }

        // 启动时先备份一次（保留原有行为）
        try { await Task.Run(backup.BackupAsync); }
        catch (Exception ex) { Log.Warning(ex, "启动备份失败"); }

        while (true)
        {
            try
            {
                var s = await repo.GetSettingAsync("AutoBackupIntervalMinutes");
                int intervalMinutes = int.TryParse(s, out var v) ? v : 0;

                if (intervalMinutes <= 0)
                {
                    // 未开启自动备份：每分钟轮询一次设置
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes));
                await Task.Run(backup.BackupAsync);
                Log.Information("自动备份完成（间隔 {Interval} 分钟）", intervalMinutes);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "自动备份循环异常");
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
    }

    /// <summary>把内存占用写进日志，便于用户反馈「卡/占内存」时定位。</summary>
    private static void LogMemory(string stage)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            var cache = MediaManager.App.Services.ThumbnailMemoryCache.CurrentBytes / 1024.0 / 1024.0;
            var cacheCount = MediaManager.App.Services.ThumbnailMemoryCache.Count;
            Log.Information(
                "{Stage}：工作集 {WorkingSet:F0} MB，托管堆 {Managed:F0} MB，缩略图内存缓存 {Cache:F1} MB / {Count} 张",
                stage,
                p.WorkingSet64 / 1024.0 / 1024.0,
                GC.GetTotalMemory(false) / 1024.0 / 1024.0,
                cache,
                cacheCount);
        }
        catch { /* 统计失败不影响功能 */ }
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "未处理的 UI 异常");
            args.Handled = true;
            ShowUnhandledError(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log.Error(args.ExceptionObject as Exception, "未处理的 AppDomain 异常");
            // 进程即将终止，尽力给用户一个说明（不再尝试恢复）
            try
            {
                CustomMessageBox.Error(
                    "程序遇到严重错误，即将关闭。\n\n请重新打开程序；如反复出现，请把 logs 目录下的日志发给开发者。",
                    "媒体管理器");
            }
            catch { /* 已无法弹窗则忽略 */ }
        };
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }

    /// <summary>解析命令行里的全部可打开文件（多选时合并成播放列表）。</summary>
    private static void OpenFileFromCommandLine(string[] args, MainWindow mainWindow)
    {
        if (args.Length == 0) return;

        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("-")) continue;
            var candidate = arg.Trim('"');
            if (System.IO.File.Exists(candidate) && !paths.Contains(candidate)) paths.Add(candidate);
        }

        if (paths.Count == 0)
        {
            Log.Debug("命令行参数里没有可打开的文件：{Args}", string.Join(" | ", args));
            return;
        }

        Log.Debug("命令行打开文件 x{Count}：{First}", paths.Count, paths[0]);

        mainWindow.Dispatcher.BeginInvoke(
            new Action(() => mainWindow.OpenStandaloneFiles(paths)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- 主题 ----------

    private const string ThemeFileName = "ui.theme";
    public const string ThemeModeSystem = "System";
    public const string ThemeModeLight = "Light";
    public const string ThemeModeDark = "Dark";

    /// <summary>用户选择的主题偏好（System / Light / Dark），不等同于「当前实际深浅」。</summary>
    public static string ThemePreference { get; private set; } = ThemeModeSystem;

    private static string ThemeCachePath(IPortableModeDetector detector)
        => System.IO.Path.Combine(detector.DataDirectory, ThemeFileName);

    /// <summary>把任意存值规整成三种主题偏好之一（无法识别时按「跟随系统」）。</summary>
    private static string NormalizeThemeMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeModeLight,
            "dark" => ThemeModeDark,
            _ => ThemeModeSystem,
        };

    /// <summary>读取上次保存的主题偏好（只读一个小文本文件，微秒级）。</summary>
    private static string ReadCachedTheme(IPortableModeDetector detector)
    {
        try
        {
            var file = ThemeCachePath(detector);
            if (!File.Exists(file)) return ThemeModeSystem;   // 全新安装：跟随系统
            return NormalizeThemeMode(File.ReadAllText(file));
        }
        catch
        {
            return ThemeModeSystem;
        }
    }

    /// <summary>把主题偏好写进缓存文件，供下次启动秒读。</summary>
    private static void WriteCachedTheme(string mode)
    {
        try
        {
            var detector = Services.GetRequiredService<IPortableModeDetector>();
            var file = ThemeCachePath(detector);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var text = NormalizeThemeMode(mode);
            if (File.Exists(file) && File.ReadAllText(file).Trim() == text) return;   // 避免每次都重写磁盘
            File.WriteAllText(file, text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "写入主题缓存失败");
        }
    }

    /// <summary>读 Windows「应用模式」：AppsUseLightTheme=0 为深色、1 为浅色。</summary>
    private static bool ResolveSystemIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ResolveIsDark(string mode) => NormalizeThemeMode(mode) switch
    {
        ThemeModeDark => true,
        ThemeModeLight => false,
        _ => ResolveSystemIsDark(),
    };

    /// <summary>按偏好应用主题（System 会实时读系统设置），可选择是否写缓存。</summary>
    private static void ApplyThemePreference(string mode, bool persistCache)
    {
        try
        {
            ThemePreference = NormalizeThemeMode(mode);
            ApplyThemeNow(ResolveIsDark(ThemePreference));
            if (persistCache) WriteCachedTheme(ThemePreference);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "应用主题失败");
        }
    }

    /// <summary>启动时按缓存应用主题；随后在后台用数据库里的真实设置校准一次。</summary>
    private static void ApplyTheme(string mode, bool persist)
        => ApplyThemePreference(mode, persist);

    private static async Task SyncThemeFromDatabaseAsync(IServiceProvider provider)
    {
        try
        {
            var repo = provider.GetRequiredService<IMediaRepository>();
            var theme = await Task.Run(() => repo.GetSettingAsync("Theme"));
            var mode = NormalizeThemeMode(theme);
            if (mode != ThemePreference || ResolveIsDark(mode) != IsDarkTheme)
            {
                ApplyThemePreference(mode, persistCache: true);
                Log.Information("已按数据库设置切换主题：{Mode}", mode);
            }
            else
            {
                // 数据库与缓存一致，补齐缓存文件（老版本用户第一次升级时没有这个文件）
                WriteCachedTheme(mode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "校准主题失败");
        }
    }

    /// <summary>当前是否深色主题（供窗口标题栏等非资源字典处使用）。</summary>
    public static bool IsDarkTheme { get; private set; }

    /// <summary>立即切换主题（isDark=true 切深色，false 切浅色）。可在启动时或设置保存后调用。</summary>
    public static void ApplyThemeNow(bool isDark)
    {
        IsDarkTheme = isDark;
        var source = isDark ? "Resources/Themes/Dark.xaml" : "Resources/Themes/Light.xaml";
        // MergedDictionaries[0] 是 App.xaml 里硬编码的主题字典，替换即可
        Application.Current.Resources.MergedDictionaries[0] =
            new System.Windows.ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        // 同步所有已打开窗口的标题栏深浅（Win10/11 沉浸式深色标题栏）
        foreach (Window w in Application.Current.Windows)
            WindowThemeHelper.ApplyTitleBarDarkMode(w, isDark);

        // 同步内存配置：否则设置窗口会用启动时的旧主题初始化下拉框，
        // 用户再保存任何其他设置时又把旧主题写回数据库（表现为「主题自己变回去」）。
        try
        {
            if (Current is App app &&
                app._provider?.GetService(typeof(IAppConfigService)) is IAppConfigService cfg)
            {
                // 这里同步的是「偏好」，不是实际深浅：否则设置页会把 System 当成具体主题存回去。
                cfg.UpdateTheme(ThemePreference switch
                {
                    ThemeModeDark => MediaManager.Core.Enums.AppTheme.Dark,
                    ThemeModeLight => MediaManager.Core.Enums.AppTheme.Light,
                    _ => MediaManager.Core.Enums.AppTheme.System,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "同步主题配置失败（不影响界面）");
        }

        WriteCachedTheme(ThemePreference);
    }

    /// <summary>系统主题变化时，仅当偏好为「跟随系统」才重刷界面。</summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (ThemePreference != ThemeModeSystem) return;
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General
            && e.Category != Microsoft.Win32.UserPreferenceCategory.VisualStyle) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var isDark = ResolveSystemIsDark();
            if (isDark == IsDarkTheme) return;
            ApplyThemeNow(isDark);
            Log.Information("系统主题变化，已跟随切换：{Theme}", isDark ? "深色" : "浅色");
        }));
    }

    /// <summary>
    /// 运行时切换主题并持久化（主界面快捷开关用）。
    /// 只调 <see cref="ApplyThemeNow"/> 的话，数据库里还是旧值，重启就变回去。
    /// </summary>
    public static Task SetThemeAsync(bool isDark)
        => SetThemePreferenceAsync(isDark ? ThemeModeDark : ThemeModeLight);

    /// <summary>保存主题偏好（跟随系统 / 浅色 / 深色）并立即生效、落库。</summary>
    public static async Task SetThemePreferenceAsync(string mode)
    {
        var normalized = NormalizeThemeMode(mode);
        ApplyThemePreference(normalized, persistCache: true);

        try
        {
            await Services.GetRequiredService<IMediaRepository>()
                .SetSettingAsync("Theme", normalized);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存主题设置失败");
        }
    }

    /// <summary>
    /// 未处理异常的可见提示：不再静默吞掉，而是告诉用户发生了什么、还能不能继续用。
    /// 3 秒内只提示一次，避免连环异常把窗口刷爆。
    /// </summary>
    private static void ShowUnhandledError(Exception ex)
    {
        try
        {
            if ((DateTime.UtcNow - _lastErrorDialogUtc).TotalSeconds < 3) return;
            _lastErrorDialogUtc = DateTime.UtcNow;

            string message;
            if (StorageErrors.IsDeviceUnavailable(ex))
            {
                message = StorageErrors.FriendlyMessage(ex)
                          + "\n\n程序会继续运行，其他操作不受影响。重新插好设备后，再执行刚才的操作即可。";
            }
            else
            {
                message = "刚才的操作出现了意外错误，已记录到日志。\n\n"
                          + "程序会继续运行；如果反复出现，请把 logs 目录下的日志发给开发者。\n\n"
                          + "详细信息：" + ex.Message;
            }

            CustomMessageBox.Warn(message, "媒体管理器");
        }
        catch
        {
            // 弹窗本身失败（例如应用正在退出）则忽略，日志已经记录
        }
    }

    /// <summary>从托盘恢复主窗口。</summary>
    private void ShowMainWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>真正退出应用（托盘「退出」菜单）。</summary>
    private void ShutdownApp()
    {
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _tray?.Dispose();
            _tray = null;

            if (_provider != null)
            {
                // 给插件一个收尾机会（异常已在内部吞掉，不会拖慢退出）
                try { _provider.GetRequiredService<Plugins.PluginService>().ShutdownAll(); }
                catch (Exception ex) { Log.Warning(ex, "插件退出清理失败"); }

                // 关闭前备份一次（涵盖本次会话的所有修改）
                try
                {
                    await _provider.GetRequiredService<IDatabaseBackupService>().BackupAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "退出备份失败");
                }

                if (_provider is IDisposable d) d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出清理失败");
        }
        finally
        {
            try { _bgCts.Cancel(); } catch { }
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
