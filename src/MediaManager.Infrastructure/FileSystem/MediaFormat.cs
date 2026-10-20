using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>媒体文件扩展名识别。</summary>
public static class MediaFormat
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".avif",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts",
    };

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => Videos.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);

    public static MediaType? GetMediaType(string path)
    {
        if (IsImage(path)) return MediaType.Image;
        if (IsVideo(path)) return MediaType.Video;
        return null;
    }
}
