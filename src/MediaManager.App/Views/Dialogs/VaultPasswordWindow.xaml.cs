using System.Windows;
using System.Windows.Input;
using MediaManager.Core.Services;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 私密空间的密码窗（设置 / 解锁 / 修改 / 取消 四种形态）。
///
/// <para>
/// 设计前提：**密码是可选功能，代码永远不主动弹这个窗去催用户设**。
/// 只有用户自己点了私密空间顶栏的「🔑 密码」才会走到 Setup；
/// 只有用户自己设过密码，进私密空间时才会走到 Unlock。
/// </para>
///
/// 校验错了不关窗：原地提示并清空，用户不用重新点一遍导航。
/// </summary>
public partial class VaultPasswordWindow : Window
{
    public enum VaultDialogMode
    {
        /// <summary>首次设置密码（输入两遍）。</summary>
        Setup,

        /// <summary>解锁（一次输入，错了原地重试）。</summary>
        Unlock,

        /// <summary>修改密码（原密码 + 新密码两遍）。</summary>
        Change,

        /// <summary>取消密码（验证原密码后回到无密码状态）。</summary>
        Remove,
    }

    private readonly VaultDialogMode _mode;

    /// <summary>单密码校验（Unlock / Remove 用）。</summary>
    private readonly Func<string, Task<bool>>? _verify;

    /// <summary>改密码校验（Change 用），参数是（原密码, 新密码）。</summary>
    private readonly Func<string, string, Task<bool>>? _change;

    /// <summary>设置模式下用户输入的新密码；其余模式不使用。</summary>
    public string Password { get; private set; } = string.Empty;

    public VaultPasswordWindow(
        VaultDialogMode mode,
        Func<string, Task<bool>>? verify = null,
        Func<string, string, Task<bool>>? change = null)
    {
        InitializeComponent();

        _mode = mode;
        _verify = verify;
        _change = change;

        switch (mode)
        {
            case VaultDialogMode.Setup:
                Title = "设置私密空间密码";
                TitleText.Text = "给私密空间设一个密码";
                MainLabel.Text = "密码";
                ConfirmRow.Visibility = Visibility.Visible;
                ConfirmLabel.Text = "再输一次";
                OkButton.Content = "创建";
                MessageText.Text =
                    "密码只在本机校验（PBKDF2-SHA256 加盐哈希，不存明文），至少 4 位。\n\n"
                    + "说实话：这是一道「防顺手」的门，不是加密保险箱。"
                    + "挡得住别人坐在你电脑前随手点开；挡不住能操作这台电脑文件的人 —— "
                    + "凭据就存在同一个数据库里，删掉 Vault.Credential 那一行即失效，"
                    + "文件本身也不加密，就在数据目录的 Vault 文件夹里。"
                    + "真要防取证，请用 VeraCrypt 这类真加密工具。\n\n"
                    + "不设也完全可以：点「取消」即可，私密空间照旧能用，只是谁点都能打开。";
                break;

            case VaultDialogMode.Unlock:
                Title = "私密空间已上锁";
                TitleText.Text = "输入密码解锁";
                MainLabel.Text = "密码";
                OkButton.Content = "解锁";
                MessageText.Text =
                    "解锁后才能看到保险箱里的内容。\n\n"
                    + "忘了密码也不用慌：文件没加密，去「设置 → 数据目录 → 打开数据目录」，"
                    + "里面的 Vault 文件夹能直接拷出来。";
                break;

            case VaultDialogMode.Change:
                Title = "修改私密空间密码";
                TitleText.Text = "改一个密码";
                OldRow.Visibility = Visibility.Visible;
                MainLabel.Text = "新密码";
                ConfirmRow.Visibility = Visibility.Visible;
                ConfirmLabel.Text = "再输一次新密码";
                OkButton.Content = "保存";
                MessageText.Text = "先输原密码，再输两遍新密码（至少 4 位）。";
                break;

            case VaultDialogMode.Remove:
                Title = "取消私密空间密码";
                TitleText.Text = "取消密码";
                OldRow.Visibility = Visibility.Visible;
                MainRow.Visibility = Visibility.Collapsed;
                OkButton.Content = "取消密码";
                MessageText.Text =
                    "验证当前密码后取消。取消之后私密空间照旧可用，"
                    + "只是不再需要输密码（文件仍然藏在 Vault 目录里，不会回到主界面）。";
                break;
        }

        Loaded += (_, _) =>
        {
            if (mode is VaultDialogMode.Change or VaultDialogMode.Remove)
                OldPasswordBox.Focus();
            else
                PasswordBox1.Focus();
        };
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case VaultDialogMode.Setup:
            case VaultDialogMode.Change:
                await HandleWithNewPasswordAsync();
                return;

            case VaultDialogMode.Unlock:
                await HandleVerifyOnlyAsync(PasswordBox1.Password);
                return;

            case VaultDialogMode.Remove:
                await HandleVerifyOnlyAsync(OldPasswordBox.Password);
                return;
        }
    }

    /// <summary>Setup / Change：校验两遍新密码，Change 还要先过原密码。</summary>
    private async Task HandleWithNewPasswordAsync()
    {
        var newPassword = PasswordBox1.Password;

        if (newPassword.Length < VaultPasswordPolicy.MinLength)
        {
            ShowError($"密码至少 {VaultPasswordPolicy.MinLength} 位。");
            PasswordBox1.Focus();
            return;
        }

        if (!string.Equals(newPassword, PasswordBox2.Password, StringComparison.Ordinal))
        {
            ShowError("两次输入的密码不一致，请重新输入。");
            PasswordBox2.Clear();
            PasswordBox2.Focus();
            return;
        }

        // 首次设置不需要问原密码：新密码本身就够，直接交给服务层落库
        if (_mode == VaultDialogMode.Setup)
        {
            OkButton.IsEnabled = false;
            try
            {
                if (_verify != null && await _verify(newPassword))
                {
                    Password = newPassword;
                    DialogResult = true;
                    return;
                }

                ShowError("没能设置成功（可能已经设过密码了）。");
            }
            catch (Exception ex)
            {
                ShowError("设置失败：" + ex.Message);
            }
            finally
            {
                OkButton.IsEnabled = true;
            }
            return;
        }

        var oldPassword = OldPasswordBox.Password;
        if (string.IsNullOrEmpty(oldPassword))
        {
            ShowError("请输入原密码。");
            OldPasswordBox.Focus();
            return;
        }

        OkButton.IsEnabled = false;
        try
        {
            if (_change != null && await _change(oldPassword, newPassword))
            {
                Password = newPassword;
                DialogResult = true;
                return;
            }

            ShowError("原密码不对，再试一次。");
            OldPasswordBox.Clear();
            OldPasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ShowError("修改失败：" + ex.Message);
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    /// <summary>Unlock / Remove：只有一次密码输入，交给回调判断。</summary>
    private async Task HandleVerifyOnlyAsync(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            ShowError("请输入密码。");
            return;
        }

        OkButton.IsEnabled = false;
        try
        {
            var ok = _verify != null && await _verify(password);
            if (ok)
            {
                DialogResult = true;
                return;
            }

            ShowError("密码不对，再试一次。");
            PasswordBox1.Clear();
            OldPasswordBox.Clear();
            (_mode == VaultDialogMode.Remove ? OldPasswordBox : PasswordBox1).Focus();
        }
        catch (Exception ex)
        {
            ShowError("校验失败：" + ex.Message);
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    // ===================================================================
    //  对外入口：四个静态方法包掉 模式 + 文案 + 回调 + Owner
    // ===================================================================
    //  调用方只需要关心「成没成」，不用重复拼回调，也不会漏掉某个模式的状态同步。

    /// <summary>进私密空间前解锁。返回 false = 用户取消（此时**不要**进）。</summary>
    public static bool TryUnlock(IVaultService vault)
        => Show(new VaultPasswordWindow(VaultDialogMode.Unlock, vault.UnlockAsync));

    /// <summary>设置密码。返回 false = 用户取消（私密空间照旧无密码可用）。</summary>
    public static bool TrySetup(IVaultService vault)
        => Show(new VaultPasswordWindow(VaultDialogMode.Setup, verify: vault.SetupAsync));

    /// <summary>修改密码。返回 false = 取消或原密码不对。</summary>
    public static bool TryChange(IVaultService vault)
        => Show(new VaultPasswordWindow(VaultDialogMode.Change, change: vault.ChangePasswordAsync));

    /// <summary>取消密码。返回 false = 取消或密码不对。</summary>
    public static bool TryRemove(IVaultService vault)
        => Show(new VaultPasswordWindow(VaultDialogMode.Remove, vault.RemovePasswordAsync));

    /// <summary>
    /// 设 Owner 后模态显示。Owner 取当前激活窗口 —— 从私密窗口点进来的就压私密窗口上，
    /// 从主界面进私密空间时压主界面；和 <see cref="CustomMessageBox"/> 一个套路。
    /// </summary>
    private static bool Show(VaultPasswordWindow window)
    {
        var owner = ResolveOwner();
        if (owner != null && owner != window && owner.IsVisible)
            window.Owner = owner;

        return window.ShowDialog() == true;
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app == null) return null;

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        return active ?? (app.MainWindow is { IsVisible: true } mw ? mw : null);
    }
}
