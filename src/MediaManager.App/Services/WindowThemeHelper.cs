using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MediaManager.App.Services;

/// <summary>窗口标题栏深色/浅色切换（Windows 10/11 沉浸式深色标题栏）。</summary>
internal static class WindowThemeHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);

    // Win10 1809 用 19，1903 及以后用 20
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    /// <summary>把指定窗口的标题栏切换为深色或浅色。</summary>
    public static void ApplyTitleBarDarkMode(Window window, bool isDark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int value = isDark ? 1 : 0;
        // 先尝试属性 20（Win10 1903+），失败回退 19（Win10 1809）；更老系统静默忽略
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref value, sizeof(int));
        }
    }
}
