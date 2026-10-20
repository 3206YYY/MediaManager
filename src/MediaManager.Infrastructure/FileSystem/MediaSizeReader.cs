using SixLabors.ImageSharp;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>
/// 只读文件头拿图片宽高（不整图解码，几十微秒级）。
/// 早先扫描入库时完全没写 Width/Height，导致状态栏和详情面板永远显示「未知」。
/// </summary>
public static class MediaSizeReader
{
    /// <summary>尝试读取图片尺寸；失败（损坏/非图片/不支持格式）返回 false。</summary>
    public static bool TryReadImageSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            var info = Image.Identify(path);
            if (info == null) return false;

            width = info.Width;
            height = info.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            // 损坏文件、无权限、设备离线都走这里，交给调用方跳过
            return false;
        }
    }
}
