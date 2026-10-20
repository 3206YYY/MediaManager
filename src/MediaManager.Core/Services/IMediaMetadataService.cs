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
}
