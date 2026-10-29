using MediaManager.Core.Plugins;

namespace FaultyPlugin;

/// <summary>
/// 每次执行都抛异常的插件 —— 专门用来验证宿主的熔断逻辑：
/// 前 2 次弹错误框，第 3 次起整组菜单被撤下，不再弹框。
/// </summary>
public sealed class FaultyPlugin : IMediaManagerPlugin
{
    public string Name => "必炸插件";

    public string Version => "1.0.0";

    public string? Author => "验收脚本";

    public string? Description => "实机验证用：动作必定抛异常，预期 3 次后被熔断停用";

    public void Initialize(IPluginContext context)
    {
        context.Log("必炸插件已加载（用于熔断验证）");
        context.AddMenuAction("一定失败", _ => throw new InvalidOperationException("这是验收脚本故意抛的异常"),
            "每次点击都会失败");
    }

    public void Shutdown()
    {
    }
}
