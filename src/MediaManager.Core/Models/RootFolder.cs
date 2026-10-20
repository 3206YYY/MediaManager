namespace MediaManager.Core.Models;

/// <summary>媒体根文件夹。</summary>
public class RootFolder
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>卷序列号（用于移动硬盘盘符变化后恢复路径）。</summary>
    public string? VolumeSerial { get; set; }

    /// <summary>相对卷根的路径。</summary>
    public string? RelativePath { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? LastScanUtc { get; set; }
    public string AddedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
