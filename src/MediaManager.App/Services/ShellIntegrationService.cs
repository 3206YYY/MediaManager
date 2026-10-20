using System.IO;
using Microsoft.Win32;

namespace MediaManager.App.Services;

/// <summary>
/// 资源管理器右键集成：在 HKCU 下注册「用媒体管理器打开」，无需管理员权限。
/// 只写当前用户，卸载/关闭开关时干净删除，不污染系统。
/// </summary>
public static class ShellIntegrationService
{
    private const string VerbKeyName = "MediaManagerOpen";
    private const string MarkerKey = @"Software\MediaManager";
    private const string MenuText = "用媒体管理器打开";

    /// <summary>支持的扩展名（与 MediaFormat 的图片集合保持一致）。</summary>
    private static readonly string[] Extensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".avif",
    };

    /// <summary>当前可执行文件路径（发布后即安装目录里的 exe）。</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            return Path.Combine(AppContext.BaseDirectory, "MediaManager.App.exe");
        }
    }

    /// <summary>当前用户是否已启用（以注册表实际状态为准，避免设置项与实际不一致）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var ext = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\SystemFileAssociations\.jpg\shell\{VerbKeyName}\command");
            if (ext?.GetValue(null) is string cmd && cmd.Contains("MediaManager", StringComparison.OrdinalIgnoreCase))
                return true;

            // .jpg 之外也可能只注册成功一部分，兜底看标记
            using var marker = Registry.CurrentUser.OpenSubKey(MarkerKey);
            return marker?.GetValue("ShellIntegration") is int v && v == 1 && ext != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>注册右键菜单（幂等：重复调用只会覆盖成当前 exe 路径）。</summary>
    public static void Enable()
    {
        var exe = ExecutablePath;
        foreach (var ext in Extensions)
        {
            using var root = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{VerbKeyName}");
            root.SetValue(null, MenuText);
            root.SetValue("Icon", $"\"{exe}\",0");
            // 允许一次选中多个文件时由资源管理器批量传给程序
            root.SetValue("MultiSelectModel", "Player");

            using var cmd = root.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        using var marker = Registry.CurrentUser.CreateSubKey(MarkerKey);
        marker.SetValue("ShellIntegration", 1, RegistryValueKind.DWord);
    }

    /// <summary>移除右键菜单。</summary>
    public static void Disable()
    {
        foreach (var ext in Extensions)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell", writable: true);
                key?.DeleteSubKeyTree(VerbKeyName, throwOnMissingSubKey: false);
            }
            catch
            {
                // 单个扩展名删不掉不影响其他扩展名
            }
        }

        try
        {
            using var marker = Registry.CurrentUser.OpenSubKey(MarkerKey, writable: true);
            marker?.SetValue("ShellIntegration", 0, RegistryValueKind.DWord);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>启动时调用：若用户开启过但 exe 路径变了（换目录/便携版拷走），自动修正。</summary>
    public static void EnsureRegistered()
    {
        try
        {
            if (!IsEnabled()) return;
            if (!IsCommandUpToDate(out _)) Enable();
        }
        catch
        {
            // 注册表不可写（受限账户）时静默失败，不影响主功能
        }
    }

    private static bool IsCommandUpToDate(out string? current)
    {
        current = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\SystemFileAssociations\.jpg\shell\{VerbKeyName}\command");
            current = key?.GetValue(null) as string;
            return current != null &&
                   current.Contains($"\"{ExecutablePath}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
