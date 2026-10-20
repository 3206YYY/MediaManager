using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MediaManager.App.Services;

namespace MediaManager.App.Controls;

/// <summary>
/// 给 <see cref="Image"/> 用的附加属性：<c>ctl:AsyncThumbnail.SourcePath="{Binding ThumbnailPath}"</c>。
///
/// 行为：
/// - 缓存命中 → 直接赋值（UI 线程，零 IO）；
/// - 未命中 → 线程池解码，解码完成后回来赋值；期间这一格留空，由模板里的骨架屏兜底（不阻塞滚动）；
/// - 解码完成时做 0.2 秒淡入，避免「啪」地硬切造成闪烁感；
/// - 容器被虚拟化复用（路径变化）→ 递增令牌，之前那次加载即使完成也会被丢弃，
///   不会出现「滚动后图片串位」。
/// </summary>
public static class AsyncThumbnail
{
    /// <summary>淡入时长。够长才看得出来，够短才不会拖慢滚动手感。</summary>
    private const int FadeMilliseconds = 200;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.RegisterAttached(
        "SourcePath",
        typeof(string),
        typeof(AsyncThumbnail),
        new PropertyMetadata(null, OnSourcePathChanged));

    public static void SetSourcePath(DependencyObject element, string? value)
        => element.SetValue(SourcePathProperty, value);

    public static string? GetSourcePath(DependencyObject element)
        => (string?)element.GetValue(SourcePathProperty);

    /// <summary>
    /// 每次换图递增的令牌。
    ///
    /// 关键点：它必须是一个**普通对象**（不是 DependencyProperty）。
    /// 之前把令牌存在 Image 的依赖属性上，解码线程池回来问「这格还要吗」时调
    /// <c>image.GetValue(...)</c>，跨线程访问 DependencyObject 会抛
    /// 「调用线程无法访问此对象」，异常被 catch 吞掉 → 缩略图永远空白。
    /// 换成普通字段后，任何线程都能安全读，只由 UI 线程写。
    /// </summary>
    private sealed class LoadState
    {
        public int Token;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(LoadState), typeof(AsyncThumbnail), new PropertyMetadata(null));

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        var path = e.NewValue as string;

        var state = (LoadState?)image.GetValue(StateProperty);
        if (state == null)
        {
            state = new LoadState();
            image.SetValue(StateProperty, state);
        }

        // 1) 作废上一轮加载（只有 UI 线程会写这个字段）
        var token = ++state.Token;

        if (string.IsNullOrWhiteSpace(path))
        {
            image.Source = null;
            ShowImmediately(image);
            return;
        }

        // 2) 命中缓存：同步赋值，滚动时几乎看不到加载过程，也就不需要动画
        if (ThumbnailMemoryCache.TryGet(path, out var cached) && cached != null)
        {
            image.Source = cached;
            ShowImmediately(image);
            return;
        }

        // 3) 未命中：先清空（复用容器时避免短暂显示上一张图），再异步补
        image.Source = null;
        _ = LoadAsync(image, state, path, token);
    }

    private static async Task LoadAsync(Image image, LoadState state, string path, int token)
    {
        try
        {
            var bitmap = await ThumbnailMemoryCache
                .GetOrLoadAsync(path, () => Volatile.Read(ref state.Token) == token)
                .ConfigureAwait(true);   // 回到 UI 线程再动控件

            if (bitmap == null) return;
            if (Volatile.Read(ref state.Token) != token) return;   // 已经被复用给别的图

            image.Source = bitmap;
            FadeIn(image);
        }
        catch
        {
            // 单张缩略图失败不影响列表
        }
    }

    /// <summary>立即显示（不发动画），并清掉可能残留的淡入动画。</summary>
    private static void ShowImmediately(Image image)
    {
        image.BeginAnimation(UIElement.OpacityProperty, null);
        image.Opacity = 1;
    }

    /// <summary>
    /// 0.2 秒透明度渐显。
    ///
    /// 为什么要用 Opacity 而不是旋转 Loading：透明度动画只影响合成，不触发
    /// 重新布局/重新光栅化整块缩略图，几十格同时播放也不会掉帧；
    /// 而旋转动画每帧都要重绘矢量，虚拟化列表里是妥妥的性能杀手。
    /// </summary>
    private static void FadeIn(Image image)
    {
        // 基值先置 1：动画结束时 FillBehavior.Stop 会回落到基值，不会留下半透明
        image.BeginAnimation(UIElement.OpacityProperty, null);
        image.Opacity = 1;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMilliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        image.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
