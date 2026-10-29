namespace MediaManager.Core.Plugins;

/// <summary>
/// 主界面当前选中的媒体（0~N 个文件）。
/// 插件拿到的永远是「此刻用户选了什么」，不要缓存它。
/// </summary>
public sealed class PluginSelection
{
    public PluginSelection(IReadOnlyList<string> paths, string? primaryPath = null)
    {
        Paths = paths ?? Array.Empty<string>();
        PrimaryPath = primaryPath ?? (Paths.Count > 0 ? Paths[0] : null);
    }

    public static PluginSelection Empty { get; } = new(Array.Empty<string>());

    /// <summary>选中的所有文件完整路径（已去重，按用户选择顺序）。</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>主文件（通常是刚点开的那一个）。</summary>
    public string? PrimaryPath { get; }

    public bool IsEmpty => Paths.Count == 0;

    public string? PrimaryFileName
        => string.IsNullOrEmpty(PrimaryPath) ? null : System.IO.Path.GetFileName(PrimaryPath);
}

/// <summary>
/// 插件注册到主界面的一个菜单动作。
/// </summary>
public sealed class PluginAction
{
    public PluginAction(string pluginName, string title, string? description, Action<PluginSelection> handler)
    {
        PluginName = pluginName;
        Title = title;
        Description = description;
        Handler = handler;
    }

    /// <summary>注册这个动作的插件名（用于日志与出错提示）。</summary>
    public string PluginName { get; }

    /// <summary>菜单项文字。</summary>
    public string Title { get; }

    /// <summary>菜单项说明（可为空）。</summary>
    public string? Description { get; }

    /// <summary>点击后执行。异常会被插件宿主捕获并提示，不会崩主程序。</summary>
    public Action<PluginSelection> Handler { get; }
}

/// <summary>
/// 插件运行期能用的全部能力。目前只开放「注册菜单动作」+ 读写自己的数据目录 + 日志，
/// 故意不暴露主窗口 / 数据库 / 内部服务：插件崩了不能拖垮主程序，升级也不会因为内部实现变化而全废。
/// </summary>
public interface IPluginContext
{
    /// <summary>程序数据目录（可读可写，适合存插件自己的配置）。</summary>
    string DataDirectory { get; }

    /// <summary>本插件所在目录（放自己的 dll、图标、模板）。</summary>
    string PluginDirectory { get; }

    /// <summary>主程序版本号，例如 <c>1.2.5</c>。</summary>
    string AppVersion { get; }

    /// <summary>在主界面「🧩 插件」菜单里加一项。</summary>
    /// <param name="title">菜单文字，建议 2~6 个字。</param>
    /// <param name="handler">点击时执行；抛异常会被捕获并弹提示。</param>
    /// <param name="description">一句话说明（可选，作为菜单提示）。</param>
    void AddMenuAction(string title, Action<PluginSelection> handler, string? description = null);

    /// <summary>把日志写进主程序日志文件（前缀会自动带上插件名）。</summary>
    void Log(string message);

    /// <summary>给用户一个轻提示（右下角 toast）。</summary>
    void Notify(string message);
}

/// <summary>
/// 插件入口。实现这个接口的类会在启动时被找到并实例化（需要一个无参构造函数）。
/// </summary>
public interface IMediaManagerPlugin
{
    /// <summary>显示名（设置页会列出来）。</summary>
    string Name { get; }

    /// <summary>版本号，例如 <c>1.0.0</c>。</summary>
    string Version { get; }

    /// <summary>作者。</summary>
    string? Author { get; }

    /// <summary>一句话说明这个插件干什么。</summary>
    string? Description { get; }

    /// <summary>加载后调用一次。在这里注册菜单动作、初始化自己的状态。</summary>
    void Initialize(IPluginContext context);

    /// <summary>程序退出前调用（可留空）。</summary>
    void Shutdown();
}

/// <summary>设置页展示用：一个插件的加载结果。</summary>
public sealed class PluginInfo
{
    public required string Name { get; init; }
    public string Version { get; init; } = "—";
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>插件 dll 路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>是否加载成功。</summary>
    public bool IsLoaded { get; init; }

    /// <summary>
    /// 加载本身成功，但菜单动作连续执行失败，被宿主熔断停用（重启程序后重置）。
    /// 与 <see cref="IsLoaded"/> 不冲突：插件还在进程里，只是菜单项被撤了。
    /// </summary>
    public bool DisabledByFailures { get; init; }

    /// <summary>失败原因（加载成功且未熔断时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>这个插件注册了几个菜单动作。</summary>
    public int ActionCount { get; init; }
}

/// <summary>一次扫描/加载的结果汇总。</summary>
public sealed class PluginLoadReport
{
    public IReadOnlyList<PluginInfo> Plugins { get; init; } = Array.Empty<PluginInfo>();

    /// <summary>扫描的目录。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>目录不存在时为 true（第一次用还没建，不算错误）。</summary>
    public bool DirectoryMissing { get; init; }

    public int LoadedCount => Plugins.Count(p => p.IsLoaded);
    public int FailedCount => Plugins.Count(p => !p.IsLoaded);
}
