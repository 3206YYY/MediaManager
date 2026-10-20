using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>EXIF 元数据读取接口。</summary>
public interface IExifService
{
    Task<Dictionary<string, string>> ReadExifAsync(string path);
}

/// <summary>便携模式探测器。</summary>
public interface IPortableModeDetector
{
    bool IsPortable { get; }
    string ProgramDirectory { get; }
    string DataDirectory { get; }
    string ThumbnailDirectory { get; }
}

/// <summary>应用配置服务。</summary>
public interface IAppConfigService
{
    Task EnsureLoadedAsync();
    string DataDirectory { get; }
    string ThumbnailDirectory { get; }
    string? FfmpegPath { get; }
    int Concurrency { get; }
    Enums.AppTheme Theme { get; }
    Enums.AppLanguage Language { get; }
    Enums.DeleteBehavior DeleteBehavior { get; }

    /// <summary>
    /// 主题被运行时切换后同步内存中的配置值。
    /// 不调用它会造成「设置窗口里显示的还是启动时的旧主题」，
    /// 用户再保存其他设置时又把旧主题写回数据库，表现为主题被莫名还原。
    /// </summary>
    void UpdateTheme(Enums.AppTheme theme);

    /// <summary>
    /// 重新从数据库读取全部配置。
    /// 设置窗口保存后必须调用，否则内存里一直是启动时的快照：
    /// 用户「改 A 保存、再改 B 保存」时，B 那次会把 A 的旧值写回库。
    /// </summary>
    Task ReloadAsync();
}
