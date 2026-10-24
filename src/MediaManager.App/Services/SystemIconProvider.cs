using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaManager.App.Services;

/// <summary>
/// 读取 Windows 系统文件图标（和资源管理器一致）。
/// 图片仍可由调用方优先使用真缩略图，这里负责文件夹 / 非图片文件 / 缩略图失败时的兜底。
/// </summary>
public static class SystemIconProvider
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    private const int SHIL_LARGE = 0;        // 32px
    private const int SHIL_EXTRALARGE = 2;   // 48px
    private const int SHIL_JUMBO = 4;        // 256px（Vista+）

    private const int ILD_TRANSPARENT = 0x1;

    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList? ppv);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>系统文件夹图标。</summary>
    public static ImageSource? GetFolderIcon() => GetCached("::folder",
        () => LoadIcon("C:\\", FileAttributes.Directory, useFileAttributes: true));

    /// <summary>按真实文件路径取系统文件图标（按扩展名缓存）。</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        var ext = Path.GetExtension(path);
        var key = "ext:" + ext;
        return GetCached(key, () => LoadIcon(path, 0, useFileAttributes: false));
    }

    private static ImageSource? GetCached(string key, Func<BitmapSource?> factory)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        BitmapSource? result;
        try { result = factory(); }
        catch { result = null; }

        lock (Cache) Cache[key] = result;
        return result;
    }

    private static BitmapSource? LoadIcon(string path, FileAttributes attributes, bool useFileAttributes)
    {
        // 1) 先拿系统图标索引，再取大尺寸（256px → 48px），比 32px 清晰
        var shfi = new SHFILEINFO();
        var flags = SHGFI_SYSICONINDEX | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        var result = SHGetFileInfo(path, (uint)attributes, ref shfi,
            (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

        if (result != IntPtr.Zero && shfi.iIcon >= 0)
        {
            if (TryGetImageListIcon(SHIL_JUMBO, shfi.iIcon, out var big)) return big;
            if (TryGetImageListIcon(SHIL_EXTRALARGE, shfi.iIcon, out var mid)) return mid;
        }

        // 2) 兜底：32px 图标
        var shfi2 = new SHFILEINFO();
        var flags2 = SHGFI_ICON | SHGFI_LARGEICON | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
        var result2 = SHGetFileInfo(path, (uint)attributes, ref shfi2,
            (uint)Marshal.SizeOf<SHFILEINFO>(), flags2);
        if (result2 == IntPtr.Zero || shfi2.hIcon == IntPtr.Zero) return null;

        var source = Imaging.CreateBitmapSourceFromHIcon(
            shfi2.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        DestroyIcon(shfi2.hIcon);
        return source;
    }

    private static bool TryGetImageListIcon(int imageListType, int index, out BitmapSource? bitmap)
    {
        bitmap = null;
        try
        {
            var iid = IID_IImageList;
            if (SHGetImageList(imageListType, ref iid, out var list) != 0 || list == null)
                return false;

            if (list.GetIcon(index, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                return false;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            DestroyIcon(hIcon);
            bitmap = source;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
