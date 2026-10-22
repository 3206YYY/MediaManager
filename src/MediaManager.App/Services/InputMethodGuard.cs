using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MediaManager.App.Services;

/// <summary>
/// 窗口级输入法隔离：默认把 IME 上下文从窗口上摘掉，只在真正需要打中文的输入框里挂回去。
///
/// 为什么需要这一层：
/// 只写 <c>InputMethod.IsInputMethodEnabled="False"</c> 与 <c>Key.ImeProcessed</c> 归一，
/// 对一部分输入法（尤其第三方中文输入法）不够 —— 它们仍会抢走 R / F / 1-5 这些单键，
/// 快捷键静默失效，还会弹出候选框。
/// 在 Win32 层把 HWND 的 IME 上下文置空（<c>ImmAssociateContext(hwnd, IntPtr.Zero)</c>）后，
/// 输入法收不到这个窗口的按键，按键原样以 <c>Key.R</c> 进来。
///
/// 代价：窗口里所有输入框都打不了中文。所以焦点进入 <see cref="TextBoxBase"/> 时把原上下文
/// 挂回去，离开时再摘掉（GotKeyboardFocus 早于任何按键，时机安全）。
/// </summary>
public sealed class InputMethodGuard
{
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hImc);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmCreateContext();

    // 同一个窗口重复 Attach 时返回同一个实例，避免钩子被装两遍
    private static readonly ConditionalWeakTable<Window, InputMethodGuard> Attached = new();

    private readonly Window _window;
    private IntPtr _hwnd;
    private IntPtr _savedContext;
    private bool _detached;

    private InputMethodGuard(Window window) => _window = window;

    /// <summary>给窗口装上输入法隔离（幂等）。</summary>
    public static InputMethodGuard Attach(Window window)
    {
        if (Attached.TryGetValue(window, out var existing)) return existing;

        var guard = new InputMethodGuard(window);
        Attached.Add(window, guard);

        window.SourceInitialized += (_, _) => guard.Initialize();
        window.Loaded += (_, _) => guard.Initialize();
        window.PreviewGotKeyboardFocus += guard.OnFocusChanged;
        window.PreviewLostKeyboardFocus += guard.OnFocusChanged;
        window.Activated += (_, _) => guard.Sync(guard._window);
        return guard;
    }

    private void Initialize()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        Sync(_window);
    }

    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject node) UpdateFor(node);
    }

    private void Sync(Window window)
    {
        if (Keyboard.FocusedElement is DependencyObject node)
            UpdateFor(node);
        else
            Detach();
    }

    private void UpdateFor(DependencyObject focused)
    {
        if (IsTextInput(focused)) Restore();
        else Detach();
    }

    /// <summary>焦点元素是否在文本输入控件里（TextBox 内部的 TextBoxView 也要认出来）。</summary>
    private static bool IsTextInput(DependencyObject node)
    {
        for (var cur = node; cur != null; cur = GetParent(cur))
        {
            if (cur is TextBoxBase) return true;
            if (cur is Window) return false;
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var vp = VisualTreeHelper.GetParent(node);
            if (vp != null) return vp;
        }
        return LogicalTreeHelper.GetParent(node);
    }

    private void Detach()
    {
        if (_hwnd == IntPtr.Zero || _detached) return;
        try
        {
            _savedContext = ImmAssociateContext(_hwnd, IntPtr.Zero);
            _detached = true;
        }
        catch
        {
            // 摘不掉也不致命：退回到 Key.ImeProcessed 归一那条兜底路径
        }
    }

    private void Restore()
    {
        if (_hwnd == IntPtr.Zero || !_detached) return;
        try
        {
            // ImmAssociateContext 返回 0 表示原本就没有上下文（部分输入法如此），
            // 这时新建一个默认上下文挂回去，否则输入框里打不出中文。
            var ctx = _savedContext != IntPtr.Zero ? _savedContext : ImmCreateContext();
            ImmAssociateContext(_hwnd, ctx);
        }
        catch
        {
            // 恢复失败不致命
        }
        finally
        {
            _detached = false;
        }
    }
}
