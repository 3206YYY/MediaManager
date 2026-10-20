using System.Windows;
using System.Windows.Controls;
using MediaManager.App.Services;

namespace MediaManager.App.Controls;

/// <summary>
/// 给 <see cref="Image"/> 用的附加属性：<c>ctl:AsyncThumbnail.SourcePath="{Binding ThumbnailPath}"</c>。
///
/// 行为：
/// - 缓存命中 → 直接赋值（UI 线程，零 IO）；
/// - 未命中 → 线程池解码，解码完成后回来赋值；期间这一格是空的（不阻塞滚动）；
/// - 容器被虚拟化复用（路径变化）→ 递增令牌，之前那次加载即使完成也会被丢弃，
///   不会出现「滚动后图片串位」。
/// </summary>
public static class AsyncThumbnail
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.RegisterAttached(
        "SourcePath",
        typeof(string),
        typeof(AsyncThumbnail),
        new PropertyMetadata(null, OnSourcePathChanged));

    public static void SetSourcePath(DependencyObject element, string? value)
        => element.SetValue(SourcePathProperty, value);

    public static string? GetSourcePath(DependencyObject element)
        => (string?)element.GetValue(SourcePathProperty);

    /// <summary>每次换图递增；用来判断异步结果是否已经过期。</summary>
    private static readonly DependencyProperty LoadTokenProperty = DependencyProperty.RegisterAttached(
        "LoadToken", typeof(int), typeof(AsyncThumbnail), new PropertyMetadata(0));

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        var path = e.NewValue as string;

        // 1) 作废上一轮加载
        var token = (int)(image.GetValue(LoadTokenProperty) ?? 0) + 1;
        image.SetValue(LoadTokenProperty, token);

        if (string.IsNullOrWhiteSpace(path))
        {
            image.Source = null;
            return;
        }

        // 2) 命中缓存：同步赋值，滚动时几乎看不到加载过程
        if (ThumbnailMemoryCache.TryGet(path, out var cached))
        {
            image.Source = cached;
            return;
        }

        // 3) 未命中：先清空（复用容器时避免短暂显示上一张图），再异步补
        image.Source = null;
        _ = LoadAsync(image, path, token);
    }

    private static async Task LoadAsync(Image image, string path, int token)
    {
        try
        {
            var bitmap = await ThumbnailMemoryCache
                .GetOrLoadAsync(path, () => IsStillCurrent(image, token))
                .ConfigureAwait(true);   // 回到 UI 线程再动控件

            if (bitmap == null || !IsStillCurrent(image, token)) return;
            image.Source = bitmap;
        }
        catch
        {
            // 单张缩略图失败不影响列表
        }
    }

    private static bool IsStillCurrent(Image image, int token)
        => (int)(image.GetValue(LoadTokenProperty) ?? 0) == token;
}
