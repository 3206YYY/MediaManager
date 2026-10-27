using System.Windows;
using System.Windows.Input;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 私密空间的密码窗（设置 / 解锁两种形态）。
///
/// <para>
/// 当前版本**没有接线**：私密空间按「不设密码、只做隐藏」的形态发布，
/// 进来点一下即可（见 MainWindow.OpenVaultAsync）。这个窗口连同
/// <see cref="Core.Services.IVaultService.SetupAsync"/> 一起留着，
/// 将来要给用户加一道锁时，把入口改回「先弹这个窗口再进」就能用，不用重写。
/// </para>
///
/// 解锁不关窗重试：密码错了就在原地提示并清空，用户不用重新点一遍导航。
/// </summary>
public partial class VaultPasswordWindow : Window
{
    public enum VaultDialogMode
    {
        /// <summary>首次设置密码（要求输入两遍）。</summary>
        Setup,

        /// <summary>解锁（一次输入，错了原地重试）。</summary>
        Unlock,
    }

    private readonly VaultDialogMode _mode;
    private readonly Func<string, Task<bool>>? _verify;

    /// <summary>设置模式下用户输入的密码；解锁模式不使用。</summary>
    public string Password { get; private set; } = string.Empty;

    public VaultPasswordWindow(VaultDialogMode mode, Func<string, Task<bool>>? verify = null)
    {
        InitializeComponent();

        _mode = mode;
        _verify = verify;

        if (mode == VaultDialogMode.Setup)
        {
            Title = "设置私密空间密码";
            TitleText.Text = "给私密空间设一个密码";
            PasswordBox2.Visibility = Visibility.Visible;
            OkButton.Content = "创建";
            PasswordBox1.Tag = "密码";
            MessageText.Text =
                "密码只在本机校验（PBKDF2 哈希，不存明文），至少 4 位。\n\n"
                + "说明白一点：保险箱**不加密文件**，它只是把文件挪到数据目录下的 Vault 文件夹"
                + "（并设成隐藏），同时从主界面、搜索、文件夹视图里全部隐藏。\n"
                + "好处是——密码忘了也不会丢数据，直接去 Vault 文件夹就能把文件拷出来。";
        }
        else
        {
            Title = "私密空间已上锁";
            TitleText.Text = "输入密码解锁";
            OkButton.Content = "解锁";
            MessageText.Text = "解锁后才能看到保险箱里的内容。";
        }

        Loaded += (_, _) => PasswordBox1.Focus();
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var pwd = PasswordBox1.Password;

        if (_mode == VaultDialogMode.Setup)
        {
            if (pwd.Length < 4)
            {
                ShowError("密码至少 4 位。");
                return;
            }

            if (!string.Equals(pwd, PasswordBox2.Password, StringComparison.Ordinal))
            {
                ShowError("两次输入的密码不一致，请重新输入。");
                PasswordBox2.Clear();
                PasswordBox2.Focus();
                return;
            }

            Password = pwd;
            DialogResult = true;
            return;
        }

        if (string.IsNullOrEmpty(pwd))
        {
            ShowError("请输入密码。");
            return;
        }

        OkButton.IsEnabled = false;
        try
        {
            var ok = _verify != null && await _verify(pwd);
            if (ok)
            {
                DialogResult = true;
                return;
            }

            ShowError("密码不对，再试一次。");
            PasswordBox1.Clear();
            PasswordBox1.Focus();
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
}
