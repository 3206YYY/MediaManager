using System.Windows;
using System.Windows.Controls;
using MediaManager.App.Views.Dialogs;
using MediaManager.Core.Services;
using Serilog;

namespace MediaManager.App.Services;

/// <summary>
/// 启动时问一句「GitHub 上有没有新版本」，有就提示，没有就安静。
/// <para>
/// 界面上什么都不弹是最常见的结果：断网、限流、没新版都当「没事发生」。
/// 真正的原则是**不后台下载、不后台安装、不重启程序**——只告诉用户
/// 「有新版、改了这些、去哪下」，装不装由用户自己决定。
/// </para>
/// </summary>
public static class StartupUpdateCheck
{
    /// <summary>启动后等多久再检查。太早会跟首屏缩略图抢 IO，也会挡住用户刚点开的东西。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    /// <summary>本次运行已经提示过，避免启动检查与其它入口叠着弹两次。</summary>
    private static bool _prompted;

    /// <summary>启动路径调用：读开关 → 延迟 → 检查 → 值当就提示。永不抛异常。</summary>
    public static async Task RunOnStartupAsync(IMediaRepository repo, Window? owner)
    {
        try
        {
            if (_prompted) return;

            var raw = await repo.GetSettingAsync(UpdatePolicy.CheckOnStartupKey);
            if (!UpdatePolicy.ParseBool(raw, UpdatePolicy.DefaultCheckOnStartup))
            {
                Log.Debug("启动更新检查已关闭，跳过");
                return;
            }

            await Task.Delay(StartupDelay);
            if (_prompted) return;

            var result = await UpdateChecker.CheckAsync();
            await repo.SetSettingAsync(UpdatePolicy.LastCheckUtcKey, DateTimeOffset.UtcNow.ToString("o"));

            var skipped = await repo.GetSettingAsync(UpdatePolicy.SkippedVersionKey);
            if (!UpdatePolicy.ShouldNotify(result, skipped))
            {
                Log.Debug("更新检查：无需提示（成功={Ok} 有新版={Has} 跳过={Skipped}）",
                    result.Succeeded, result.HasUpdate, skipped);
                return;
            }

            _prompted = true;
            Log.Information("发现新版本 {Latest}（当前 {Current}），已提示用户",
                result.LatestVersion, result.CurrentVersion);
            await ShowPromptAsync(result, repo, owner);
        }
        catch (Exception ex)
        {
            // 检查更新失败绝不该影响使用，连提示都不给
            Log.Warning(ex, "启动更新检查失败");
        }
    }

    /// <summary>
    /// 弹出「发现新版本」窗口。
    /// 手动检查（设置页 / 托盘）用 <paramref name="repo"/> 传 null 就不会记「跳过这个版本」——
    /// 用户主动问的，他下次再问还得有答案。
    /// </summary>
    public static async Task<UpdateChoice> ShowPromptAsync(
        UpdateCheckResult result, IMediaRepository? repo, Window? owner)
    {
        var app = Application.Current;
        if (app == null) return UpdateChoice.Later;

        if (!app.Dispatcher.CheckAccess())
        {
            return await app.Dispatcher.InvokeAsync(() => ShowPromptAsync(result, repo, owner)).Task.Unwrap();
        }

        // 已经开着一个更新提示就别再开第二个
        if (app.Windows.OfType<UpdateWindow>().Any()) return UpdateChoice.Later;

        var window = new UpdateWindow(result);
        try
        {
            if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
                window.Owner = owner;
        }
        catch
        {
            // 设 Owner 失败不影响显示
        }

        // 主窗口上正悬着的图片提示（ToolTip）是独立顶层窗口，会浮在提示窗上面挡住文字。
        // 显示期间把 Owner 的提示服务关掉——它会顺手收起已经弹出来的那个；
        // 关窗后恢复，用户平时该有的悬停提示一点不受影响。
        var ownerHadToolTips = owner != null && ToolTipService.GetIsEnabled(owner);
        if (ownerHadToolTips) ToolTipService.SetIsEnabled(owner!, false);
        try
        {
            window.ShowDialog();
        }
        finally
        {
            if (ownerHadToolTips) ToolTipService.SetIsEnabled(owner!, true);
        }

        if (window.Choice == UpdateChoice.SkipThisVersion && repo != null)
        {
            var version = UpdatePolicy.NormalizeVersion(result.LatestVersion) ?? result.LatestVersion ?? string.Empty;
            if (version.Length > 0)
            {
                await repo.SetSettingAsync(UpdatePolicy.SkippedVersionKey, version);
                Log.Information("用户跳过版本 {Version}，以后不再提示这个版本", version);
            }
        }

        return window.Choice;
    }
}
