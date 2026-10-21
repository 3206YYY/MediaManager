using MediaManager.Core.Plugins;

namespace HelloPlugin;

/// <summary>
/// 最小可用插件：往主界面「🧩 插件」菜单里加一项，点一下告诉用户选了什么。
/// 复制这个文件改个名字就能开始写自己的插件。
/// </summary>
public sealed class HelloPlugin : IMediaManagerPlugin
{
    public string Name => "示例插件";

    public string Version => "1.0.0";

    public string? Author => "你的名字";

    public string? Description => "演示插件接口：统计选中文件、把路径写进日志、在状态栏打招呼";

    public void Initialize(IPluginContext context)
    {
        context.Log($"示例插件已加载，主程序版本 {context.AppVersion}");

        context.AddMenuAction("统计选中项", selection =>
        {
            if (selection.IsEmpty)
            {
                context.Notify("先在列表里选中一张图片或一个视频");
                return;
            }

            context.Log($"选中 {selection.Paths.Count} 个文件，主文件 = {selection.PrimaryPath}");
            context.Notify($"已选中 {selection.Paths.Count} 个文件：{selection.PrimaryFileName}");
        }, "只读，不会修改你的文件");
    }

    public void Shutdown()
    {
        // 有需要保存的插件状态，在这里落盘
    }
}
