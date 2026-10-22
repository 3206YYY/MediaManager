using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MediaManager.Core.Input;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.App.Services;

/// <summary>单个快捷键的注册结果（失败原因直接可以显示给用户）。</summary>
public sealed record HotkeyRegistration(string ActionId, HotkeyGesture Gesture, bool Success, string? Error);

/// <summary>
/// 全局快捷键服务。
///
/// 用一个独立的消息窗口（<see cref="HwndSource"/>）接收 <c>WM_HOTKEY</c>，
/// 而不是挂在主窗口上：主窗口关到托盘后 <c>Hide()</c> 仍然保有句柄，
/// 但换成「关闭即退出」或窗口重建时，挂在主窗口上就会丢注册。
/// 独立窗口的生命周期只跟服务本身绑定，行为最稳定。
///
/// 所有调用（Apply / Suspend / Resume / Dispose）都必须在 UI 线程上执行。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;      // 按住不放不会连发
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int WsExToolWindow = 0x00000080; // 不进 Alt-Tab / 任务栏

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly ILogger<HotkeyService> _logger;

    /// <summary>系统热键 id → 动作 id（注册成功后才有映射）。</summary>
    private readonly Dictionary<int, string> _idToAction = new();

    /// <summary>用户期望的配置（含未注册成功的），Suspend 后 Resume 靠它重放。</summary>
    private readonly Dictionary<string, HotkeyGesture> _desired = new();

    /// <summary>当前真正注册成功的配置。</summary>
    private readonly Dictionary<string, HotkeyGesture> _active = new();

    private HwndSource? _sink;
    private int _nextId = 0xB100;
    private bool _suspended;
    private bool _disposed;

    public HotkeyService(ILogger<HotkeyService> logger) => _logger = logger;

    /// <summary>快捷键被按下（参数为动作 id）。始终在 UI 线程触发。</summary>
    public event Action<string>? Pressed;

    /// <summary>当前真正生效的快捷键（设置页用它显示「已生效 / 被占用」）。</summary>
    public IReadOnlyDictionary<string, HotkeyGesture> Active => _active;

    /// <summary>按配置重新注册（先全部注销，再逐个注册，避免自己跟自己抢）。</summary>
    public IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyDictionary<string, HotkeyGesture> gestures)
    {
        _desired.Clear();
        foreach (var (action, gesture) in gestures) _desired[action] = gesture;
        return Reapply();
    }

    /// <summary>从数据库读配置并注册；从没配置过的动作用默认值。</summary>
    public async Task<IReadOnlyList<HotkeyRegistration>> LoadAsync(IMediaRepository repo)
    {
        // 读库放到线程池：SQLite 的「异步」其实在调用线程同步跑完
        var map = await Task.Run(async () =>
        {
            var result = new Dictionary<string, HotkeyGesture>();
            foreach (var def in HotkeyActions.All)
            {
                var raw = await repo.GetSettingAsync(HotkeyActions.SettingKey(def.Id));

                // null = 从没配置过 → 用默认值；空串 = 用户主动清空 → 保持未设置
                if (raw == null)
                {
                    result[def.Id] = HotkeyGesture.TryParse(def.Default, out var fallback)
                        ? fallback
                        : HotkeyGesture.None;
                    continue;
                }

                result[def.Id] = HotkeyGesture.TryParse(raw, out var gesture) ? gesture : HotkeyGesture.None;
            }
            return result;
        });

        // 建消息窗口必须在 UI 线程，不能在 Task.Run 里做
        return Apply(map);
    }

    /// <summary>
    /// 临时注销全部快捷键。设置页录键时必须先调：否则用户按的正是已注册的组合，
    /// 会被自己的全局热键抢走（窗口就在这时被隐藏，看起来像「一按就崩」）。
    /// </summary>
    public void Suspend()
    {
        if (_disposed) return;
        _suspended = true;
        UnregisterAll();
    }

    /// <summary>录键结束，按原配置恢复。</summary>
    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;
        Reapply();
    }

    private IReadOnlyList<HotkeyRegistration> Reapply()
    {
        UnregisterAll();

        var results = new List<HotkeyRegistration>();
        if (_disposed || _suspended) return results;

        foreach (var (action, gesture) in _desired)
        {
            if (gesture.IsEmpty) continue;
            var (ok, error) = TryRegister(action, gesture);
            results.Add(new HotkeyRegistration(action, gesture, ok, error));
        }

        return results;
    }

    private (bool Ok, string? Error) TryRegister(string action, HotkeyGesture gesture)
    {
        EnsureSink();
        if (_sink == null) return (false, "无法创建快捷键消息窗口");

        if (!Enum.TryParse<Key>(gesture.Key, ignoreCase: true, out var key) || key == Key.None)
            return (false, "不支持的按键：" + gesture.Key);

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return (false, "不支持的按键：" + gesture.Key);

        var id = _nextId++;
        if (!RegisterHotKey(_sink.Handle, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            _logger.LogInformation("注册快捷键 {Action}={Gesture} 失败，错误码 {Code}",
                action, gesture.ToStorageValue(), code);

            return (false, code == ErrorHotkeyAlreadyRegistered
                ? "已被其它程序占用"
                : $"系统拒绝注册（错误码 {code}）");
        }

        _idToAction[id] = action;
        _active[action] = gesture;
        return (true, null);
    }

    private void UnregisterAll()
    {
        if (_sink != null)
        {
            foreach (var id in _idToAction.Keys)
            {
                UnregisterHotKey(_sink.Handle, id);
            }
        }
        _idToAction.Clear();
        _active.Clear();
    }

    /// <summary>惰性创建消息窗口：没人配快捷键时一个句柄都不占。</summary>
    private void EnsureSink()
    {
        if (_sink != null || _disposed) return;

        try
        {
            var parameters = new HwndSourceParameters("MediaManagerHotkeySink")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,              // 不带 WS_VISIBLE：是个纯消息窗口，肉眼看不见
                ExtendedWindowStyle = WsExToolWindow,
            };

            _sink = new HwndSource(parameters);
            _sink.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建快捷键消息窗口失败，全局快捷键不可用");
            _sink = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        var id = wParam.ToInt32();
        if (!_idToAction.TryGetValue(id, out var action)) return IntPtr.Zero;

        handled = true;

        // 回调里绝不能让异常逃出去：它会顺着消息循环把整个进程带走
        try
        {
            Pressed?.Invoke(action);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "执行全局快捷键 {Action} 失败", action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        if (_sink != null)
        {
            _sink.RemoveHook(WndProc);
            _sink.Dispose();
            _sink = null;
        }
    }
}
