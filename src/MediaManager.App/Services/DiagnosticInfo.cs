using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaManager.App.Services;

/// <summary>
/// 一份「用户能直接贴出去的」诊断信息。
///
/// 起因：程序报错时弹窗只写「请把 logs 目录下的日志发给开发者」—— 用户既不知道 logs 在哪，
/// 也不知道该说什么，最后只能留一句「打不开，好卡」。而这类软件最怕的就是
/// 「看不见的报错」变成「猜来猜去的对线」。
///
/// 两条硬规则：
/// 1. <b>路径里的用户名要抹掉</b>。诊断信息大概率会被贴到公开论坛，
///    带着 `C:\Users\张三\...` 一起出去是隐私泄漏，而排查根本不需要知道用户名。
/// 2. <b>不读任何内容</b>。只统计条目数、报路径，不去读用户的照片、日记、备注。
/// </summary>
public static class DiagnosticInfo
{
    /// <summary>采集到的原始事实（拆出来是为了不碰环境也能测排版）。</summary>
    public sealed record Snapshot(
        string AppVersion,
        string OsDescription,
        string RuntimeDescription,
        string Architecture,
        string DataMode,
        string DataDirectory,
        string LogDirectory,
        long MediaItemCount,
        string UserName);

    /// <summary>把快照排成可直接粘贴的文本。<paramref name="generatedAt"/> 由调用方给，便于测试复现。</summary>
    public static string Compose(Snapshot snapshot, DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();
        sb.AppendLine("媒体管理器 诊断信息（可以直接贴到 Issue / 群里）");
        sb.AppendLine("生成时间：" + generatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("版本：" + Safe(snapshot.AppVersion));
        sb.AppendLine("系统：" + Safe(snapshot.OsDescription));
        sb.AppendLine("运行时：" + Safe(snapshot.RuntimeDescription));
        sb.AppendLine("架构：" + Safe(snapshot.Architecture));
        sb.AppendLine("数据模式：" + Safe(snapshot.DataMode));
        sb.AppendLine("数据目录：" + RedactPath(snapshot.DataDirectory, snapshot.UserName));
        sb.AppendLine("日志目录：" + RedactPath(snapshot.LogDirectory, snapshot.UserName));
        sb.Append("库内媒体条目：" + snapshot.MediaItemCount);
        return sb.ToString();
    }

    /// <summary>
    /// 抹掉路径里的用户名（只抹用户名，盘符和目录结构留着 —— 排查需要它）。
    /// 用户名是空的时候原样返回，不做猜测。
    /// </summary>
    public static string RedactPath(string? path, string? userName)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (string.IsNullOrWhiteSpace(userName)) return path;

        // 整段替换（不区分大小写）：比按 `\Users\` 前缀匹配可靠 —— 用户可能把数据目录
        // 放到 D:\我的东西\张三\ 这种地方，一样会带出名字
        var redacted = path.Replace(userName, "<用户>", StringComparison.OrdinalIgnoreCase);
        return redacted;
    }

    /// <summary>从当前进程环境采集（只在界面里调用，方便单测的入口是 <see cref="Compose"/>）。</summary>
    public static Snapshot Capture(
        string appVersion, string dataDirectory, string logDirectory, long mediaItemCount)
    {
        var portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));

        return new Snapshot(
            AppVersion: appVersion,
            OsDescription: RuntimeInformation.OSDescription,
            RuntimeDescription: RuntimeInformation.FrameworkDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            DataMode: portable ? "便携（数据在程序目录 Data\\）" : "安装（数据在 %LocalAppData%\\MediaManager）",
            DataDirectory: dataDirectory,
            LogDirectory: logDirectory,
            MediaItemCount: mediaItemCount,
            UserName: Environment.UserName);
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "（未知）" : value;
}
