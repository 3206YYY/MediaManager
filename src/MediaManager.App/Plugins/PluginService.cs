using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaManager.App.Services;
using MediaManager.Core.Plugins;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.Plugins;

/// <summary>
/// 插件宿主：扫描 <c>{数据目录}\plugins</c>，把每个子目录当成一个插件加载。
/// <para>
/// 设计原则（很重要，改之前先读）：
/// <list type="bullet">
/// <item>单个插件加载失败**只影响它自己**：记一条错误，其它插件照常跑，主程序绝不因此启动失败。</item>
/// <item>插件只拿到 <see cref="IPluginContext"/>，碰不到主窗口 / 数据库 / 内部服务。
/// 这样主程序随意重构内部实现都不会把民间插件全废掉。</item>
/// <item>用独立的 <see cref="AssemblyLoadContext"/>，插件的依赖 dll 不会和主程序抢版本。</item>
/// <item>暂不支持卸载/热重载（AssemblyLoadContext 卸载有坑），改插件后重启程序即可。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PluginService
{
    private const string ManifestFileName = "plugin.json";

    private readonly IAppConfigService _config;
    private readonly ILogger<PluginService> _logger;
    private readonly object _gate = new();

    private readonly List<PluginInfo> _plugins = new();
    private readonly List<PluginAction> _actions = new();
    private readonly HashSet<string> _loadedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IMediaManagerPlugin> _instances = new();

    public PluginService(IAppConfigService config, ILogger<PluginService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>插件根目录：<c>{数据目录}\plugins</c>。</summary>
    public string PluginDirectory => Path.Combine(_config.DataDirectory, "plugins");

    /// <summary>已注册的菜单动作（主界面据此生成「🧩 插件」菜单）。</summary>
    public IReadOnlyList<PluginAction> Actions
    {
        get { lock (_gate) return _actions.ToArray(); }
    }

    /// <summary>最近一次扫描结果。</summary>
    public PluginLoadReport LastReport { get; private set; } = new() { Directory = string.Empty };

    /// <summary>插件列表或动作列表变化。</summary>
    public event EventHandler? Changed;

    /// <summary>插件请求给用户显示一句提示（主窗口负责呈现）。</summary>
    public event EventHandler<string>? NotificationRequested;

    /// <summary>当前程序版本（如 <c>1.2.5</c>）。</summary>
    public static string AppVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// 扫描并加载插件。可以重复调用：已经加载过的 dll 会跳过（改插件需重启程序）。
    /// </summary>
    public Task<PluginLoadReport> ReloadAsync() => Task.Run(LoadCore);

    private PluginLoadReport LoadCore()
    {
        var dir = PluginDirectory;

        try
        {
            // 目录不存在就建一个空目录：用户能在设置页点「打开插件目录」直接看到该放哪儿
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建插件目录失败：{Dir}", dir);
            return Finish(new PluginLoadReport { Directory = dir, DirectoryMissing = true });
        }

        var candidates = new List<PluginCandidate>();

        try
        {
            // 1) 每个子目录 = 一个插件（推荐用法）
            foreach (var sub in Directory.EnumerateDirectories(dir))
                candidates.AddRange(DiscoverInFolder(sub));

            // 2) 直接丢在 plugins 根目录下的 dll 也认（最省事的用法）
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                candidates.Add(new PluginCandidate(Path.GetFileNameWithoutExtension(dll), dll, Path.GetDirectoryName(dll)!));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举插件目录失败：{Dir}", dir);
        }

        foreach (var candidate in candidates) LoadOne(candidate);

        return Finish(new PluginLoadReport
        {
            Directory = dir,
            Plugins = Snapshot(),
        });
    }

    private PluginLoadReport Finish(PluginLoadReport report)
    {
        LastReport = report;
        NotifyChanged();

        if (report.Plugins.Count == 0)
            _logger.LogInformation("插件：未安装插件（目录 {Dir}）", report.Directory);
        else
            _logger.LogInformation("插件：{Ok} 个加载成功，{Bad} 个失败（目录 {Dir}）",
                report.LoadedCount, report.FailedCount, report.Directory);

        return report;
    }

    private IReadOnlyList<PluginInfo> Snapshot()
    {
        lock (_gate) return _plugins.ToArray();
    }

    /// <summary>一个插件目录里：优先按 plugin.json 的 entry，其次取同名的 dll，最后取第一个 dll。</summary>
    private static IEnumerable<PluginCandidate> DiscoverInFolder(string folder)
    {
        var id = Path.GetFileName(folder);
        var manifest = Path.Combine(folder, ManifestFileName);
        string? entry = null;

        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("entry", out var e) && e.ValueKind == JsonValueKind.String)
                    entry = e.GetString();

                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    id = n.GetString() ?? id;
            }
            catch
            {
                // manifest 写坏了不应该让插件目录整个消失：退回「找 dll」的兜底逻辑
            }
        }

        if (!string.IsNullOrWhiteSpace(entry))
        {
            var path = Path.Combine(folder, entry);
            if (File.Exists(path))
            {
                yield return new PluginCandidate(id, path, folder, manifest);
                yield break;
            }
        }

        var sameName = Path.Combine(folder, Path.GetFileName(folder) + ".dll");
        if (File.Exists(sameName))
        {
            yield return new PluginCandidate(id, sameName, folder, manifest);
            yield break;
        }

        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return new PluginCandidate(id, dll, folder, manifest);
            yield break;
        }
    }

    private void LoadOne(PluginCandidate candidate)
    {
        lock (_gate)
        {
            if (_loadedFiles.Contains(candidate.DllPath)) return;
            _loadedFiles.Add(candidate.DllPath);
        }

        var displayName = candidate.DisplayName;

        try
        {
            var context = new PluginLoadContext(candidate.DllPath, candidate.Folder);
            var assembly = context.LoadFromAssemblyPath(candidate.DllPath);

            var types = assembly.GetTypes()
                .Where(t => typeof(IMediaManagerPlugin).IsAssignableFrom(t)
                            && t is { IsInterface: false, IsAbstract: false })
                .ToArray();

            if (types.Length == 0)
            {
                // 只是依赖 dll，不是插件本体 —— 静默跳过，不往设置页里塞噪音
                return;
            }

            foreach (var type in types)
            {
                var plugin = (IMediaManagerPlugin?)Activator.CreateInstance(type);
                if (plugin == null) continue;

                var host = new PluginHost(this, plugin.Name, candidate.Folder);
                plugin.Initialize(host);

                lock (_gate)
                {
                    _instances.Add(plugin);
                    var info = new PluginInfo
                    {
                        Name = string.IsNullOrWhiteSpace(plugin.Name) ? displayName : plugin.Name,
                        Version = string.IsNullOrWhiteSpace(plugin.Version) ? "—" : plugin.Version,
                        Author = plugin.Author,
                        Description = plugin.Description,
                        FilePath = candidate.DllPath,
                        IsLoaded = true,
                        ActionCount = host.ActionCount,
                    };
                    _plugins.RemoveAll(p => string.Equals(p.FilePath, candidate.DllPath, StringComparison.OrdinalIgnoreCase));
                    _plugins.Add(info);
                }

                _logger.LogInformation("插件已加载：{Name} {Version}（{File}）",
                    plugin.Name, plugin.Version, candidate.DllPath);
            }
        }
        catch (Exception ex)
        {
            // 插件写错不能影响主程序：记错误 + 设置页里显示一条红色的失败记录
            _logger.LogError(ex, "插件加载失败：{File}", candidate.DllPath);
            lock (_gate)
            {
                _plugins.RemoveAll(p => string.Equals(p.FilePath, candidate.DllPath, StringComparison.OrdinalIgnoreCase));
                _plugins.Add(new PluginInfo
                {
                    Name = displayName,
                    FilePath = candidate.DllPath,
                    IsLoaded = false,
                    Error = Flatten(ex),
                });
            }
        }
    }

    /// <summary>异常只留最有信息量的一句，不要把一个巨大的堆栈塞进设置页。</summary>
    private static string Flatten(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        var msg = inner.Message;
        return string.IsNullOrWhiteSpace(msg) ? ex.GetType().Name : msg;
    }

    internal void RegisterAction(PluginAction action)
    {
        lock (_gate) _actions.Add(action);
    }

    internal void RaiseNotification(string message)
    {
        NotificationRequested?.Invoke(this, message);
    }

    private void NotifyChanged()
    {
        // 加载在后台线程完成：事件订阅方（WPF）自己负责切回 UI 线程
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "插件状态变化通知失败"); }
    }

    /// <summary>退出前给插件一个收尾机会（异常一律吞掉，不能拖慢退出）。</summary>
    public void ShutdownAll()
    {
        lock (_gate)
        {
            foreach (var plugin in _instances)
            {
                try { plugin.Shutdown(); }
                catch (Exception ex) { _logger.LogWarning(ex, "插件退出失败：{Name}", plugin.Name); }
            }
            _instances.Clear();
        }
    }

    private sealed record PluginCandidate(string DisplayName, string DllPath, string Folder, string? ManifestPath = null);

    /// <summary>插件上下文实现：转发到宿主，顺便统计这个插件注册了几个动作。</summary>
    private sealed class PluginHost : IPluginContext
    {
        private readonly PluginService _owner;
        private readonly string _pluginName;

        public PluginHost(PluginService owner, string pluginName, string pluginFolder)
        {
            _owner = owner;
            _pluginName = string.IsNullOrWhiteSpace(pluginName) ? "未命名插件" : pluginName;
            PluginDirectory = pluginFolder;
        }

        public string DataDirectory => _owner._config.DataDirectory;

        public string PluginDirectory { get; }

        public string AppVersion => PluginService.AppVersion;

        public int ActionCount { get; private set; }

        public void AddMenuAction(string title, Action<PluginSelection> handler, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(title) || handler == null) return;

            _owner.RegisterAction(new PluginAction(_pluginName, title.Trim(), description, selection =>
            {
                try
                {
                    handler(selection);
                }
                catch (Exception ex)
                {
                    // 插件动作抛异常 → 提示用户，主程序继续活着
                    _owner._logger.LogError(ex, "插件动作执行失败：{Plugin} / {Title}", _pluginName, title);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        Views.Dialogs.CustomMessageBox.Error(
                            $"插件「{_pluginName}」执行「{title}」时出错：\n\n{Flatten(ex)}",
                            "插件出错")));
                }
            }));

            ActionCount++;
        }

        public void Log(string message)
            => _owner._logger.LogInformation("[插件 {Plugin}] {Message}", _pluginName, message);

        public void Notify(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _owner.RaiseNotification($"{_pluginName}：{message}");
        }
    }
}

/// <summary>
/// 每个插件一个加载上下文：插件的私有依赖从它自己目录解析，
/// 但 MediaManager.* 一律复用主程序已加载的版本，否则接口类型对不上（InvalidCastException）。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _folder;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginPath, string folder) : base($"Plugin:{Path.GetFileName(pluginPath)}")
    {
        _folder = folder;
        try { _resolver = new AssemblyDependencyResolver(pluginPath); } catch { _resolver = null; }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        // 主程序自己的程序集必须共享，不能各加载一份（否则 IMediaManagerPlugin 变成两个不同类型）
        if (name != null && (name.StartsWith("MediaManager.", StringComparison.OrdinalIgnoreCase)
                             || name is "MediaManager.Core" or "MediaManager.App" or "MediaManager.Infrastructure"))
            return null;

        // 插件目录里自带的依赖优先
        if (name != null)
        {
            var local = Path.Combine(_folder, name + ".dll");
            if (File.Exists(local)) return LoadFromAssemblyPath(local);
        }

        var resolved = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (resolved != null && File.Exists(resolved)) return LoadFromAssemblyPath(resolved);

        // 其余交给默认上下文（.NET 运行时自带的那批）
        return null;
    }
}
