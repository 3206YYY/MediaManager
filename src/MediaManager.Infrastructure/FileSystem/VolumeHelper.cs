using System.Runtime.InteropServices;
using System.Text;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>Windows 卷序列号读取（用于移动硬盘盘符变化恢复）。</summary>
public static class VolumeHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string? lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    /// <summary>获取卷序列号（8 位十六进制字符串），失败返回 null。</summary>
    public static string? GetVolumeSerial(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return null;

            var sb = new StringBuilder(261);
            if (GetVolumeInformation(root, sb, sb.Capacity, out uint serial, out _, out _, null, 0))
            {
                return serial.ToString("X8");
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>计算路径相对卷根目录的相对路径。</summary>
    public static string GetRelativePath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;
        return fullPath.Substring(root.Length).TrimStart('\\', '/');
    }

    /// <summary>在所有就绪的驱动器中查找匹配卷序列号的分区。</summary>
    public static string? FindDriveBySerial(string volumeSerial)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var serial = GetVolumeSerial(drive.RootDirectory.FullName);
            if (string.Equals(serial, volumeSerial, StringComparison.OrdinalIgnoreCase))
                return drive.RootDirectory.FullName;
        }
        return null;
    }
}
