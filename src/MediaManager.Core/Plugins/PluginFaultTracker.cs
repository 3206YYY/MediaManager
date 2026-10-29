using System;
using System.Collections.Generic;

namespace MediaManager.Core.Plugins;

/// <summary>
/// 插件动作失败的熔断计数器（纯逻辑，无 IO，可单测）。
/// <para>
/// 同一个插件的动作失败累计达到阈值时熔断一次（<see cref="Record"/> 返回 true），
/// 之后继续失败**不再**重复触发——停用动作是 PluginService 的事，这里只管数。
/// 注意计数的是「累计失败」而不是「连续失败」：动作成功没人上报（返回 void），
/// 观测不到成功，所以不做成功清零。
/// 线程安全：动作可能在任意线程被调用。
/// </para>
/// </summary>
public sealed class PluginFaultTracker
{
    private readonly int _threshold;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _faults = new(StringComparer.OrdinalIgnoreCase);

    public PluginFaultTracker(int threshold = 3)
    {
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "阈值至少为 1");
        _threshold = threshold;
    }

    public int Threshold => _threshold;

    /// <summary>记一次失败。只在「正好达到阈值」的那一次返回 true。</summary>
    public bool Record(string pluginName)
    {
        var key = pluginName ?? string.Empty;
        lock (_gate)
        {
            _faults.TryGetValue(key, out var n);
            n++;
            _faults[key] = n;
            return n == _threshold;
        }
    }

    public int Count(string pluginName)
    {
        var key = pluginName ?? string.Empty;
        lock (_gate)
        {
            _faults.TryGetValue(key, out var n);
            return n;
        }
    }

    public void Reset(string pluginName)
    {
        lock (_gate) _faults.Remove(pluginName ?? string.Empty);
    }
}
