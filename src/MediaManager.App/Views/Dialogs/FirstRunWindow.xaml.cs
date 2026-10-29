using System.Windows;
using System.Windows.Input;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 首次启动的选择窗：侧栏留几项，交给用户自己定。
///
/// <para>
/// 它是**非模态**的（Show 而不是 ShowDialog）：主窗口已经显示出来了，用户可以先把界面
/// 看一圈再决定。窗口不设 Owner —— 项目里所有独立窗口都不设，否则主窗口最小化会把
/// 一起收进任务栏（用户会以为窗口没了）。
/// </para>
///
/// <para>
/// 直接按 Esc 或点不到地方就关掉也允许：等于「不选」，调用方只记「问过了」，
/// 不写 <c>Ui.SimpleMode</c> —— 保持用户原本的设置（新装就是全量）。
/// </para>
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>
    /// 用户的选择：true = 极简（只看图），false = 全量，null = 没选就关了。
    /// </summary>
    public bool? ChooseSimpleMode { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SimpleButton.Focus();
    }

    private void OnChooseSimple(object sender, RoutedEventArgs e) => Complete(true);

    private void OnChooseFull(object sender, RoutedEventArgs e) => Complete(false);

    private void Complete(bool simpleMode)
    {
        ChooseSimpleMode = simpleMode;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    /// <summary>无边框窗口没有标题栏，靠这块空白拖。</summary>
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
