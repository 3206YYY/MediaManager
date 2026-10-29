using System.Windows;
using MediaManager.App.ViewModels;
using MediaManager.App.Views;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Enums;
using MediaManager.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Services;

/// <summary>
/// 「点开一个媒体」的唯一实现：主界面、私密空间、日记附件都走这里。
///
/// 之前这段逻辑在 <c>MainWindow.OpenViewer</c>、<c>VaultWindow.OpenInWindow</c> 里各写了一遍，
/// 日记里的附件干脆没写 —— 点缩略图毫无反应（用户报的「点不开」）。
/// 一份实现放这儿，以后再加入口就不用重新想一遍「什么类型开哪个窗」。
/// </summary>
public static class MediaOpenLauncher
{
    /// <summary>
    /// 打开一项媒体。<paramref name="scope"/> 决定上一张/下一张的范围（一般传当前列表）。
    /// 返回已经 Show 出来的窗口（图片查看器 / 视频播放器），外放类型返回 null —— 调用方
    /// 用它来挂 CopySaved 事件或跟着一起关。
    /// </summary>
    /// <remarks>
    /// 图片 / 视频必须内置（随机跳转、快捷键、编辑器、连播只在内置窗口里成立）；
    /// 音频 / 文档 / 压缩包交给系统默认程序。分流判断只认 <see cref="MediaOpenStrategy"/>。
    /// </remarks>
    public static Window? Open(IReadOnlyList<ThumbnailItemViewModel> scope, ThumbnailItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (MediaOpenStrategy.UseExternalApp(item.Media.MediaType))
        {
            OpenExternal(item.Path);
            return null;
        }

        if (item.IsVideo)
        {
            // 播放列表沿用「同一范围内所有视频」：外放类型不会混进来，拖进来也放不了
            var videos = scope.Where(i => i.IsVideo).Select(i => i.Path).ToList();
            var startIndex = videos.FindIndex(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
            {
                // 范围里没有它（例如日记附件不在当天列表里）：至少能把它单独放出来
                videos.Insert(0, item.Path);
                startIndex = 0;
            }

            // 不设 Owner：WPF/Windows 会把「有主窗口」的窗口跟主窗口一起最小化，
            // 主窗口一最小化视频窗口就被藏掉、只剩透明/黑屏。
            var player = new VideoPlayerWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            player.SetPlaylist(videos, startIndex);
            player.Show();
            player.Activate();
            WindowThemeHelper.ApplyTitleBarDarkMode(player, App.IsDarkTheme);
            return player;
        }

        // 查看器只放图片：混进视频会让「上一张/下一张」切到放不出来的文件
        var images = PickImageScope(scope, item);
        var index = images.FindIndex(i => ReferenceEquals(i, item));
        if (index < 0) index = images.FindIndex(i => i.Id == item.Id && i.Id != 0);

        var viewerVm = new ViewerViewModel(App.Services.GetRequiredService<Core.Services.IMediaRepository>());
        // 同样不设 Owner：查看器要能独立最小化 / 独立存在
        var window = new ViewerWindow(viewerVm) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.SetItems(images, Math.Max(0, index));
        window.Show();
        window.Activate();
        WindowThemeHelper.ApplyTitleBarDarkMode(window, App.IsDarkTheme);
        return window;
    }

    /// <summary>
    /// 算出查看器里「上一张 / 下一张」的范围：范围内的图片，且一定包含 <paramref name="item"/>。
    /// 单独抽出来是因为它有唯一一个容易写错的地方 —— 当前项不在范围里时（日记附件、私密空间
    /// 里被筛掉的项），不能返回一个空列表，否则查看器打开就是白板。
    /// </summary>
    public static List<ThumbnailItemViewModel> PickImageScope(
        IReadOnlyList<ThumbnailItemViewModel> scope, ThumbnailItemViewModel item)
    {
        var images = scope.Where(i => i.Media.MediaType == MediaType.Image).ToList();

        var present = images.Any(i => ReferenceEquals(i, item))
                      || images.Any(i => i.Id != 0 && i.Id == item.Id)
                      || images.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));

        if (!present) images.Add(item);
        return images;
    }

    /// <summary>交给系统默认程序。失败弹提示而不是静默 —— 拔盘、没关联程序都很常见。</summary>
    public static void OpenExternal(string? path)
    {
        if (ShellHelper.TryOpenWithDefaultApp(path, out var error)) return;
        CustomMessageBox.Warn(error ?? "打开失败。", "用系统默认程序打开");
    }
}
