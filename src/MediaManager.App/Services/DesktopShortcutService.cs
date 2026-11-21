using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaManager.App.Services;

/// <summary>桌面图标当前处于什么状态。</summary>
public enum ShortcutState
{
    /// <summary>桌面上没有本程序的快捷方式。</summary>
    Missing,

    /// <summary>有，而且指向的就是当前这个 exe。</summary>
    Current,

    /// <summary>有，但指向的程序已经不在了（换目录、重新解压、旧版本被移走）。</summary>
    Stale,
}

/// <summary>一次检查的结果。<see cref="Path"/> 是查到的 .lnk，<see cref="Target"/> 是它指向的 exe。</summary>
public sealed record ShortcutInfo(ShortcutState State, string? Path, string? Target);

/// <summary>
/// 桌面快捷方式：创建、检查、以及启动时自愈。
/// <para>
/// <b>为什么需要它</b>：开发期每次 <c>publish</c> 都会把旧的发布目录移走再重建，
/// 桌面那个 .lnk 里存的是绝对路径，路径一没，双击就报「找不到项目」。
/// 以前只能手工重建，这里把它做成一个按钮 + 启动时自动修。
/// </para>
/// <para>
/// <b>三条约束（和右键集成一个原则）：</b>
/// </para>
/// <list type="number">
/// <item>只写<b>当前用户桌面</b>（<see cref="Environment.SpecialFolder.DesktopDirectory"/>），
///   不碰公共桌面，不要管理员权限。</item>
/// <item><b>只处理指向本程序的 .lnk</b>：自愈时读到的目标文件名必须是 <c>MediaManager.App.exe</c>，
///   别人的快捷方式一个都不动。不改名、不删除、不移动任何文件。</item>
/// <item>写失败（只读桌面、被安全软件拦）只当这次没成功，<b>绝不影响程序其它功能</b>。</item>
/// </list>
/// </summary>
public static class DesktopShortcutService
{
    /// <summary>默认文件名。故意不加版本号：换版本时覆盖同一个图标，桌面不会越堆越多。</summary>
    public const string FileName = "媒体管理器.lnk";

    private const string ExeFileName = "MediaManager.App.exe";

    /// <summary>快捷方式描述（鼠标悬停时显示）。</summary>
    private const string Description = "媒体管理器 — 本地图片与视频管理（79 种格式）";

    /// <summary>
    /// 当前用户桌面目录。
    /// <para>
    /// 自动化验证时用环境变量 <c>MM_SHORTCUT_DIR</c> 指到临时目录 —— 测试脚本不该往用户真实桌面上写东西。
    /// </para>
    /// </summary>
    public static string DesktopDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("MM_SHORTCUT_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }

    /// <summary>当前程序路径（与右键集成共用同一套取值逻辑）。</summary>
    public static string ExecutablePath => ShellIntegrationService.ExecutablePath;

    /// <summary>安装版的位置（安装包一直装到这里，和 InstallLocation 一致）。</summary>
    public static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MediaManager", ExeFileName);

    /// <summary>
    /// 桌面图标该指向哪个 exe。
    /// <para>
    /// 从开发构建（<c>bin\Debug</c>、<c>bin\Release</c>）跑起来时，自己不是候选 ——
    /// 开发目录会被 <c>dotnet clean</c>、publish 的「移走旧目录」反复搬动，指过去下次清理就废
    /// （用户原话：「你每次改完我桌面图标就用不了了」）。所以候选只有<b>已经发布出去的那几份</b>：
    /// <c>dist\*\MediaManager.App.exe</c> 和安装版，然后<b>按文件版本号挑最高的</b>。
    /// </para>
    /// <para>
    /// <b>为什么必须比版本号，不能「安装版优先」也不能「修改时间最新」</b>：
    /// 曾经优先安装版，而安装目录里躺着 1.3.3（比 dist 里的 1.4.7 还旧），
    /// dist 一旦被清空，点「更新桌面图标」就把图标指回 1.3.3 —— 用户点开发现功能倒退，
    /// 还以为新版本被删了。按版本号挑，最差也只是停在手上那版，不会往回走。
    /// </para>
    /// <para>
    /// 注意：这只在<b>图标已经失效</b>时才会被用到（<see cref="EnsureUpToDate"/> 里
    /// 目标是别的还能用的副本就原样保留），所以用户自己指到哪儿不会被抢走。
    /// 自动化验证用 <c>MM_SHORTCUT_USE_SELF=1</c> 强制指向自身。
    /// </para>
    /// </summary>
    private static string PreferredTarget()
    {
        var exe = ExecutablePath;

        if (Environment.GetEnvironmentVariable("MM_SHORTCUT_USE_SELF") == "1") return exe;
        if (!IsDevBuild(exe)) return exe;

        var candidates = new List<string>(FindPublishedCopies());
        if (File.Exists(InstalledExe)) candidates.Add(InstalledExe);

        var best = candidates
            .Select(p => (Path: p, Version: ReadFileVersion(p), Stamp: SafeWriteTime(p)))
            .OrderByDescending(t => t.Version ?? new Version(0, 0))
            .ThenByDescending(t => t.Stamp)
            .Select(t => t.Path)
            .FirstOrDefault();

        return best ?? (File.Exists(InstalledExe) ? InstalledExe : exe);
    }

    /// <summary>读 exe 自带的文件版本（读不出返回 null）。ProductVersion 带 <c>+git哈希</c>，所以用 FileVersion。</summary>
    private static Version? ReadFileVersion(string path)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(text, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>这个 exe 是不是从 bin\Debug / bin\Release 跑起来的开发构建。</summary>
    private static bool IsDevBuild(string exe)
    {
        var dir = Path.GetDirectoryName(exe) ?? string.Empty;
        return dir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || dir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 往上找 <c>dist\</c>，列出里面所有 MediaManager.App.exe（含子目录）。
    /// 找不到返回空（正式安装版没有 dist 目录，正常）。
    /// </summary>
    private static IEnumerable<string> FindPublishedCopies()
    {
        try
        {
            var dir = Path.GetDirectoryName(ExecutablePath) is { Length: > 0 } d
                ? new DirectoryInfo(d)
                : null;

            for (var i = 0; i < 6 && dir != null; i++)
            {
                var dist = Path.Combine(dir.FullName, "dist");
                if (Directory.Exists(dist))
                {
                    return Directory.EnumerateFiles(dist, ExeFileName, SearchOption.AllDirectories)
                        .Where(File.Exists)
                        .ToList();
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // 没权限 / 路径怪：当没找到
        }

        return Array.Empty<string>();
    }

    /// <summary>默认快捷方式路径（桌面 + <see cref="FileName"/>）。</summary>
    public static string DefaultPath(string? desktopDir = null)
        => Path.Combine(desktopDir ?? DesktopDirectory, FileName);

    /// <summary>
    /// 看看桌面上现在是什么情况：先找同名文件，找不到再扫一遍桌面上其它 .lnk，
    /// 认出「指向本程序但不是这个名字」的（用户可能自己改过名）。
    /// </summary>
    public static ShortcutInfo Inspect(string? desktopDir = null)
    {
        var dir = desktopDir ?? DesktopDirectory;

        try
        {
            var mine = DefaultPath(dir);
            if (File.Exists(mine))
                return Judge(mine);

            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                if (FindOwnShortcut(lnk, out _)) return Judge(lnk);
            }
        }
        catch
        {
            // 桌面目录不存在 / 没权限：当成「没有图标」，不影响别的
        }

        return new ShortcutInfo(ShortcutState.Missing, null, null);
    }

    /// <summary>
    /// 创建（或覆盖）桌面图标，返回 .lnk 路径。已经有同名图标时原地覆盖，不会多出一个「(2)」。
    /// </summary>
    public static string Create(string? desktopDir = null)
    {
        var dir = desktopDir ?? DesktopDirectory;
        Directory.CreateDirectory(dir);

        // 已经有一个「改名过」的本程序图标就沿用它的名字，别在桌面上再添一个
        var path = DefaultPath(dir);
        if (!File.Exists(path))
        {
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                if (FindOwnShortcut(lnk, out _))
                {
                    path = lnk;
                    break;
                }
            }
        }

        Write(path, PreferredTarget());
        return path;
    }

    /// <summary>
    /// 启动时调用：图标存在但指向的程序已经不在了（或指到别的副本），就悄悄改成当前程序。
    /// <para>只修「指向 MediaManager.App.exe」的那些，别的快捷方式一律不碰；失败静默。</para>
    /// </summary>
    public static bool EnsureUpToDate(string? desktopDir = null)
    {
        var dir = desktopDir ?? DesktopDirectory;
        if (!Directory.Exists(dir)) return false;

        var exe = PreferredTarget();
        var fixedAny = false;

        try
        {
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                if (!FindOwnShortcut(lnk, out var target)) continue;
                if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (target != null && File.Exists(target)) continue;   // 另一份还能用的副本，不动

                Write(lnk, exe);
                fixedAny = true;
            }
        }
        catch
        {
            // 只读桌面 / 被拦：算了，用户还能用设置页里的按钮手动重建
        }

        return fixedAny;
    }

    /// <summary>读一个 .lnk 指向的目标（读不到返回 null）。</summary>
    public static string? ReadTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);

            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = sb.ToString().Trim();
            return path.Length == 0 ? null : path;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 内部

    private static ShortcutInfo Judge(string lnkPath)
    {
        var target = ReadTarget(lnkPath);

        // Current 的定义是「指向一个真实存在的本程序」，不是「等于当前正在跑的 exe」。
        // 以前要求两者相等，结果从 bin\Debug 启动时，桌面那个指着 dist 里 1.4.7 的图标
        // 会被判成 Stale，还在设置页写「那儿已经没有程序了」—— 而它双击明明能打开。
        var ok = target != null && File.Exists(target);
        return new ShortcutInfo(ok ? ShortcutState.Current : ShortcutState.Stale, lnkPath, target);
    }

    /// <summary>这个 .lnk 是不是本程序的（按目标文件名判断，不看快捷方式自己的名字）。</summary>
    private static bool FindOwnShortcut(string lnkPath, out string? target)
    {
        target = ReadTarget(lnkPath);
        return target != null
               && string.Equals(Path.GetFileName(target), ExeFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string lnkPath, string exe)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
        // 图标直接用 exe 里那个（0 号），不再依赖 .ico 文件 —— 少一个会丢的文件
        link.SetIconLocation(exe, 0);
        link.SetDescription(Description);
        ((IPersistFile)link).Save(lnkPath, true);

        if (!File.Exists(lnkPath))
            throw new IOException("快捷方式没有写入成功：" + lnkPath);
    }

    // ------------------------------------------------------------------ COM

    /// <summary>Shell 的快捷方式对象（CLSID 固定，Windows 自带，不需要引 COM 库）。</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
                     IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                             int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
