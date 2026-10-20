using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Portable;

/// <summary>便携模式探测器：程序目录存在 portable.marker 文件即进入便携模式。</summary>
public class PortableModeDetector : IPortableModeDetector
{
    private readonly ILogger<PortableModeDetector> _logger;
    private readonly string? _customDataDir;

    public PortableModeDetector(ILogger<PortableModeDetector> logger, string? customDataDir = null)
    {
        _logger = logger;
        _customDataDir = customDataDir;
    }

    private static string _programDir = AppContext.BaseDirectory;
    private static readonly Lazy<bool> _isPortable = new(() =>
    {
        var marker = Path.Combine(_programDir, "portable.marker");
        return File.Exists(marker);
    });

    public string ProgramDirectory => _programDir;

    public bool IsPortable
    {
        get
        {
            // 结果缓存，避免每次访问目录属性时重复探测和打日志
            return _isPortable.Value;
        }
    }

    public string DataDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customDataDir))
                return _customDataDir;

            if (IsPortable)
                return Path.Combine(_programDir, "Data");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaManager");
        }
    }

    public string ThumbnailDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customDataDir))
                return Path.Combine(_customDataDir, "thumbnails");

            return Path.Combine(DataDirectory, "thumbnails");
        }
    }
}
