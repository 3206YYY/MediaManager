using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MediaManager.App.Services;

/// <summary>外壳相关的小工具：打开目录、统计目录占用、格式化大小。</summary>
public static class ShellHelper
{
    /// <summary>用资源管理器打开目录（目录不存在则尝试创建，再失败则抛出让调用方提示）。</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("目录为空", nameof(path));

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 少数环境 UseShellExecute 打开目录失败，退回 explorer.exe
            Process.Start("explorer.exe", $"\"{path}\"");
        }
    }

    /// <summary>
    /// 用 Windows 默认关联程序打开文件（等价在资源管理器里双击）。
    /// <para>
    /// 音频 / 文档 / 压缩包走这条路：这些格式的「专业工具」用户自己机器上就有，
    /// 内置一个半成品播放器 / 文本预览反而占体积、还要为每种编码和换行符踩坑。
    /// </para>
    /// <para>
    /// 失败不抛异常，把原因写进 <paramref name="error"/> 交给调用方弹提示 —— 拔了盘、
    /// 文件被移走、系统里没有这类文件的关联程序，都是很常见的情况。
    /// </para>
    /// </summary>
    public static bool TryOpenWithDefaultApp(string? path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "文件路径为空。";
            return false;
        }

        if (!File.Exists(path))
        {
            var root = Path.GetPathRoot(path);
            error = !string.IsNullOrEmpty(root) && !Directory.Exists(root)
                ? $"设备已断开（可能是移动硬盘 / U 盘被拔出）：\n{path}"
                : $"文件不在原位，可能已被移动或删除：\n{path}";
            return false;
        }

        try
        {
            // UseShellExecute=true 才会走 shell 关联（Windows 按扩展名找默认程序）。
            // 返回的 Process 不需要等，Dispose 掉只是为了不留句柄。
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1155 = ERROR_NO_ASSOCIATION（没有关联程序），1223 = 用户在 UAC 弹窗点了取消
            error = ex.NativeErrorCode == 1223
                ? "已取消（系统权限弹窗被关闭）。"
                : $"系统里没有为这类文件配置默认程序。\n可以右键「打开所在位置」，再选一个程序打开。\n\n（{path}）";
            Serilog.Log.Information(ex, "外部打开失败（无关联程序）：{Path}", path);
            return false;
        }
        catch (Exception ex)
        {
            error = $"调用系统默认程序失败：{ex.Message}";
            Serilog.Log.Warning(ex, "外部打开失败：{Path}", path);
            return false;
        }
    }

    /// <summary>统计目录内文件数量与总字节数（跳过无权限/被占用的文件，不抛异常）。</summary>
    public static (long Bytes, int Files) MeasureFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return (0, 0);

        long bytes = 0;
        int files = 0;

        var stack = new Stack<string>();
        stack.Push(dir);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch { /* 单个文件读不到就跳过 */ }
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                    stack.Push(sub);
            }
            catch { /* 无权限 / 设备被拔出的目录直接跳过 */ }
        }

        return (bytes, files);
    }

    /// <summary>把字节数格式化成易读的 B / KB / MB / GB。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";

        const double kb = 1024d;
        double value = bytes;
        if (value < kb) return $"{value:0} B";
        value /= kb;
        if (value < kb) return $"{value:0} KB";
        value /= kb;
        if (value < kb) return $"{value:0.0} MB";
        value /= kb;
        return $"{value:0.00} GB";
    }
}
