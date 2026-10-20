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
