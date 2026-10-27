using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using MediaManager.App.Services;
using MediaManager.App.ViewModels;
using MediaManager.App.Views;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Diagnostics;
using MediaManager.Core.Services;
using MediaManager.Infrastructure;
using MediaManager.Infrastructure.Database;
using MediaManager.Infrastructure.FileSystem;
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
    private Services.HotkeyService? _hotkeys;
    private MainWindow? _mainWindow;

    /// <summary>单实例互斥量：防止两个进程同时写同一个 SQLite 库。</summary>
    private Mutex? _instanceMutex;
    private const string InstanceMutexName = @"Local\MediaManager.SingleInstance";

    /// <summary>单实例文件转发管道：第二个进程把「右键打开的文件」交给已运行实例。</summary>
    private static readonly string OpenFilePipeName =
        "MediaManager.OpenFile." + SanitizePipePart(Environment.UserName);

    private static string SanitizePipePart(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length > 0 ? new string(chars) : "default";
    }

    /// <summary>后台任务的取消令牌：退出时统一取消，避免关窗后还在读盘。</summary>
    private readonly CancellationTokenSource _bgCts = new();

    /// <summary>上一次弹「未处理异常」提示的时间，用于防刷屏。</summary>
    private static DateTime _lastErrorDialogUtc = DateTime.MinValue;

    public static IServiceProvider Services => ((App)Current)._provider!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例：第二个进程不再启动，只把已有主窗口叫到前台 ----
        // 多开会让两个进程同时写同一个 SQLite 库（数据不同步，严重时锁死）。
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                ForwardArgsToRunningInstance(e.Args);
                ActivateRunningInstance();
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            // 互斥量创建失败（权限 / 系统异常）不该阻止软件启动，退化为允许多开
            Log.Warning(ex, "单实例检测失败，按普通启动继续");
            _instanceMutex = null;
        }

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
        services.AddSingleton<Services.HotkeyService>();
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

        // 监听第二个实例转发的文件路径（右键「用媒体管理器打开」时只激活窗口、不开文件）
        StartPipeServer(_mainWindow);

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

            // 2.5) 私密空间：把密码凭据读进内存（之后「有没有设过密码」是纯内存判断）
            var vault = provider.GetRequiredService<IVaultService>();
            await vault.InitializeAsync();

            // 保险箱目录必须从扫描范围里排除：否则用户把数据目录加进库时，
            // Vault 里的文件会被重新入库成普通项，等于在网格里公开
            ScanService.SetExcludedDirectories(new[] { vault.VaultDirectory });

            // 3) 托盘图标（必须在 UI 线程创建）
            _tray = new TrayIconService(
                showMainWindow: () => mainWindow.Dispatcher.Invoke(ShowMainWindow),
                exitApplication: () => mainWindow.Dispatcher.Invoke(ShutdownApp),
                dataDirectory: provider.GetRequiredService<IPortableModeDetector>().DataDirectory);
            mainWindow.TrayService = _tray;

            // 4) 加载数据（内部把 SQLite 查询放到线程池，UI 线程只做集合填充）
            await provider.GetRequiredService<MainViewModel>().InitializeAsync();

            LogMemory("数据加载完成");

            // 不在启动关键路径上做 full blocking GC：GC.Collect(2, Forced, blocking, compacting)
            // 会让整个进程暂停，正好压在缩略图解码和用户第一次交互上。
            // 启动期的一次性临时对象交给运行时自然回收即可。

            // 5) 右键集成路径修正 + 命令行打开文件：都不影响主界面可用
            await Task.Run(ShellIntegrationService.EnsureRegistered);
            OpenFileFromCommandLine(args, mainWindow);

            // 5.5) 插件：后台加载，慢插件不会挡住开窗；加载失败只影响它自己
            _ = provider.GetRequiredService<Plugins.PluginService>().ReloadAsync();

            // 5.6) 全局快捷键：必须在 UI 线程注册（要建消息窗口），失败也不影响其它功能
            try
            {
                _hotkeys = provider.GetRequiredService<Services.HotkeyService>();
                _hotkeys.Pressed += action => mainWindow.Dispatcher.BeginInvoke(() => HandleHotkey(action));

                var results = await _hotkeys.LoadAsync(provider.GetRequiredService<IMediaRepository>());
                var failed = results.Where(r => !r.Success).ToList();
                if (failed.Count > 0)
                {
                    // 注册失败不弹窗（开机就弹窗最烦人），但必须在状态栏说清楚，
                    // 否则用户只会觉得「我设了快捷键怎么按都没反应」。
                    var names = string.Join("、", failed.Select(f =>
                        MediaManager.App.Services.HotkeyActions.All.FirstOrDefault(a => a.Id == f.ActionId)?.Title ?? f.ActionId));
                    provider.GetRequiredService<MainViewModel>().StatusText =
                        $"全局快捷键未生效：{names}（可能被其它程序占用），可在 设置 → 快捷键 换一个";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "注册全局快捷键失败");
            }

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
                if (_bgCts.IsCancellationRequested) break;

                var s = await repo.GetSettingAsync("AutoBackupIntervalMinutes");
                int intervalMinutes = int.TryParse(s, out var v) ? v : 0;

                if (intervalMinutes <= 0)
                {
                    // 未开启自动备份：每分钟轮询一次设置
                    await Task.Delay(TimeSpan.FromMinutes(1), _bgCts.Token);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), _bgCts.Token);
                await Task.Run(backup.BackupAsync);
                Log.Information("自动备份完成（间隔 {Interval} 分钟）", intervalMinutes);
            }
            catch (OperationCanceledException)
            {
                // 恢复数据 / 退出时会取消：直接收工，绝不能在覆盖库的窗口期里再去备份
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "自动备份循环异常");
                try { await Task.Delay(TimeSpan.FromMinutes(1), _bgCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ===================================================================
    //  历史快照恢复（时光机）
    // ===================================================================

    /// <summary>恢复数据期间置位：退出时不能再备份一次，否则会把刚恢复的快照轮换掉。</summary>
    private bool _suppressExitBackup;

    /// <summary>
    /// 恢复数据前把所有会碰数据库/磁盘的后台工作停下来：
    /// 扫描、缩略图队列、定时备份、尺寸补齐。调用方随后才允许覆盖 library.db。
    /// </summary>
    public static async Task PrepareForDatabaseRestoreAsync(Action<string>? report = null)
    {
        if (Current is not App app || app._provider == null) return;

        report?.Invoke("停止后台扫描与缩略图任务…");
        try
        {
            app._provider.GetRequiredService<MainViewModel>().CancelBackgroundWork();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "取消界面后台任务失败（继续）");
        }

        // 停掉定时备份循环 + 尺寸补齐
        try { app._bgCts.Cancel(); } catch { /* 已取消 */ }

        // 缩略图队列：恢复完就重启程序，这里直接连 worker 一起停掉最稳
        try
        {
            if (app._provider.GetService(typeof(IThumbnailService)) is IDisposable thumb)
                thumb.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止缩略图服务失败（继续）");
        }

        report?.Invoke("等待后台任务收尾…");
        await Task.Delay(400);
    }

    /// <summary>
    /// 恢复完成后重启自己：不重建 DataContext（那太容易出 bug），直接新起一个进程再退掉当前进程。
    /// </summary>
    public static void RestartApplication()
    {
        if (Current is App app) app._suppressExitBackup = true;

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                Log.Information("数据恢复完成，正在重启程序");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动重启失败，请手动重新打开程序");
        }

        Current?.Shutdown();
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

        // 托盘右键菜单是 WinForms 的，不吃资源字典 → 每次切主题都要重新上色
        if (Current is App trayOwner) trayOwner._tray?.ApplyTheme();

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

    /// <summary>
    /// 全局快捷键的分发。窗口隐藏时也能按下，所以每一条都先保证窗口可见。
    /// </summary>
    private void HandleHotkey(string action)
    {
        try
        {
            // 记一行：用户反馈「按了没反应」时，先看日志就知道是没触发还是触发了没执行
            Log.Information("全局快捷键触发：{Action}", action);

            switch (action)
            {
                case MediaManager.App.Services.HotkeyActions.ShowWindow:
                    ToggleMainWindow();
                    break;

                case MediaManager.App.Services.HotkeyActions.Discover:
                    ShowMainWindow();
                    _ = _provider!.GetRequiredService<MainViewModel>().SelectViewCommand.ExecuteAsync("discovery");
                    break;

                case MediaManager.App.Services.HotkeyActions.RandomMedia:
                    ShowMainWindow();
                    _mainWindow?.OpenRandomMedia();
                    break;

                case MediaManager.App.Services.HotkeyActions.Settings:
                    ShowMainWindow();
                    _mainWindow?.OpenSettingsDialog();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "执行全局快捷键 {Action} 失败", action);
        }
    }

    /// <summary>
    /// 显示 / 隐藏主窗口。
    /// 只有「窗口在前台」时才隐藏；窗口在后面被别的程序挡着时，第一下应该是把它调到前面，
    /// 而不是直接藏起来 —— 那会让人以为快捷键坏了。
    /// </summary>
    private void ToggleMainWindow()
    {
        if (MainWindow == null) return;

        if (MainWindow.IsVisible && MainWindow.WindowState != WindowState.Minimized && MainWindow.IsActive)
        {
            MainWindow.Hide();
            return;
        }

        ShowMainWindow();
    }

    /// <summary>真正退出应用（托盘「退出」菜单）。</summary>
    private void ShutdownApp()
    {
        Shutdown();
    }

    /// <summary>把已经运行的实例激活并置顶（第二个进程启动时调用）。</summary>
    private static void ActivateRunningInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            // 对方可能还在启动（MainWindowHandle 暂时为 0），最多等 3 秒
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var other = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

                if (other != null)
                {
                    ShowWindow(other.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(other.MainWindowHandle);
                    return;
                }

                Thread.Sleep(150);
            }
        }
        catch
        {
            // 激活失败也不影响「不启动第二个实例」这一核心目标
        }
    }

    /// <summary>把命令行里的文件路径转发给已运行的实例（右键「用媒体管理器打开」）。</summary>
    private static void ForwardArgsToRunningInstance(string[] args)
    {
        var files = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        if (files.Length == 0) return;

        try
        {
            using var client = new NamedPipeClientStream(".", OpenFilePipeName, PipeDirection.Out);
            client.Connect(1500); // 已运行实例可能刚启动，管道稍后才就绪
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', files));
            writer.Flush();
        }
        catch
        {
            // 管道没起 / 权限受限：退化为只激活窗口，用户仍可手动打开
        }
    }

    /// <summary>已运行实例监听管道：收到第二个实例转发的文件路径后直接打开。</summary>
    private static void StartPipeServer(MainWindow mainWindow)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = CreateOpenFilePipeServer();
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server);

                    // 限读 64KB：管道没有大小上限时，本机任意进程都能发超长数据把内存吃满
                    var payload = await ReadLimitedAsync(reader, 64 * 1024);
                    var paths = payload
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(System.IO.File.Exists)
                        .Where(IsAllowedOpenPath)
                        .Take(32)
                        .ToArray();
                    if (paths.Length == 0) continue;

                    _ = mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        mainWindow.Show();
                        mainWindow.WindowState = WindowState.Normal;
                        mainWindow.Activate();
                        mainWindow.OpenStandaloneFiles(paths);
                    }));
                }
                catch
                {
                    // 管道被上一轮占用 / 关闭时异常：稍等再试，避免空转
                    await Task.Delay(500);
                }
            }
        });
    }

    /// <summary>
    /// 创建只允许当前用户访问的命名管道：防止同机其它账户/进程借它让程序打开文件。
    /// </summary>
    private static NamedPipeServerStream CreateOpenFilePipeServer()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User != null)
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));

                return NamedPipeServerStreamAcl.Create(
                    OpenFilePipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            }
        }
        catch
        {
            // 取不到 SID 时不能传空 PipeSecurity（空 ACL = 谁都连不上），走下面的默认 ACL
        }

        // 默认 ACL 兜底；仍有 64KB 限读 + 绝对路径/类型校验
        return new NamedPipeServerStream(
            OpenFilePipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>限量读取管道内容（按字符数），避免异常/恶意长数据把内存吃满。</summary>
    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[maxChars];
        var total = 0;
        while (total < maxChars)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(total, maxChars - total));
            if (n == 0) break;
            total += n;
        }
        return new string(buffer, 0, total);
    }

    /// <summary>只接受「绝对路径 + 受支持类型」的文件，防止本机其它进程借管道打开任意文件。</summary>
    private static bool IsAllowedOpenPath(string path)
    {
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return false;
            return MediaManager.Infrastructure.FileSystem.MediaFormat.GetMediaTypeForOpen(path) != null;
        }
        catch
        {
            return false;
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _tray?.Dispose();
            _tray = null;

            // 注销全局热键再退出，避免系统里残留占用（换版本重开后注册不上）
            try { _hotkeys?.Dispose(); } catch (Exception ex) { Log.Warning(ex, "注销全局快捷键失败"); }
            _hotkeys = null;

            if (_provider != null)
            {
                // 给插件一个收尾机会（异常已在内部吞掉，不会拖慢退出）
                try { _provider.GetRequiredService<Plugins.PluginService>().ShutdownAll(); }
                catch (Exception ex) { Log.Warning(ex, "插件退出清理失败"); }

                // 关闭前备份一次（涵盖本次会话的所有修改）。
                // 恢复快照后重启时会置位 _suppressExitBackup：那时库刚被换掉，
                // 再备份一次会立刻轮换 bak.1..7，用户刚看到的「时间节点」全被挪位。
                if (!_suppressExitBackup)
                {
                    try
                    {
                        await _provider.GetRequiredService<IDatabaseBackupService>().BackupAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "退出备份失败");
                    }
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
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            try { _instanceMutex?.Dispose(); } catch { }
            _instanceMutex = null;
            try { _bgCts.Cancel(); } catch { }
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
