using System.Security.Cryptography;
using System.Text;
using MediaManager.Core.Services;

namespace MediaManager.Infrastructure.Vault;

/// <summary>
/// 保险箱服务：密码校验（PBKDF2）+ 文件在保险箱目录与原始位置之间的搬移。
///
/// 密码只存派生结果，不存明文：
/// <c>pbkdf2-sha256$迭代次数$salt$hash</c>。
/// 迭代次数写在存串里，将来调高迭代（比如 15 万 → 30 万）时，
/// 老密码照样能验证通过，用户不需要重设 —— 验证时按串里记的次数算。
/// </summary>
public sealed class VaultService : IVaultService
{
    /// <summary>密码存库的设置键。</summary>
    public const string CredentialSettingKey = "Vault.Credential";

    /// <summary>自动锁定分钟数（0 = 不自动锁）的设置键。</summary>
    public const string AutoLockMinutesSettingKey = "Vault.AutoLockMinutes";

    /// <summary>密码最短长度。界面提示和服务层校验读同一个常量。</summary>
    public const int MinPasswordLength = VaultPasswordPolicy.MinLength;

    private const int DefaultIterations = 150_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly IMediaRepository _repo;
    private readonly IAppConfigService _config;

    private string? _credential;

    public VaultService(IMediaRepository repo, IAppConfigService config)
    {
        _repo = repo;
        _config = config;
    }

    public string VaultDirectory => Path.Combine(_config.DataDirectory, "Vault");

    public bool IsConfigured => !string.IsNullOrEmpty(_credential);

    public bool IsUnlocked { get; private set; }

    public event EventHandler? Locked;

    /// <summary>
    /// 启动时读一次密码凭据到内存，之后 <see cref="IsConfigured"/> 就是纯内存判断。
    /// 不这么做的话，每次判断都要查一次数据库，而「有没有设过密码」会被界面频繁问。
    /// </summary>
    public async Task InitializeAsync()
    {
        _credential = await _repo.GetSettingAsync(CredentialSettingKey).ConfigureAwait(false);
        IsUnlocked = false;
    }

    /// <summary>
    /// 打开私密空间。
    ///
    /// 三种情况：
    /// <list type="number">
    /// <item>没设密码 → 只保证保险箱目录存在，直接放行（默认形态，绝大多数用户就是这样）；</item>
    /// <item>设了密码且本次已解锁 → 放行；</item>
    /// <item>设了密码但锁着 → 返回 false，由界面弹解锁窗，<b>不是</b>失败。</item>
    /// </list>
    /// 注意这里只会「往上加一道门」，不会因为返回 false 就锁住任何东西 —— 密码忘了解不开，
    /// 文件照样在 <see cref="VaultDirectory"/> 里可以直接拷走，不存在丢失。
    /// </summary>
    public Task<bool> OpenAsync()
    {
        EnsureVaultDirectory();

        if (IsConfigured && !IsUnlocked) return Task.FromResult(false);

        IsUnlocked = true;
        return Task.FromResult(true);
    }

    public async Task<bool> SetupAsync(string password)
    {
        if (IsConfigured) return false;
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength) return false;

        var credential = CreateCredential(password.Trim(), DefaultIterations);
        await _repo.SetSettingAsync(CredentialSettingKey, credential).ConfigureAwait(false);
        _credential = credential;

        EnsureVaultDirectory();
        IsUnlocked = true;
        return true;
    }

    public Task<bool> UnlockAsync(string password)
    {
        if (!IsConfigured) return Task.FromResult(false);

        var ok = Verify(password?.Trim() ?? string.Empty, _credential!);
        if (ok) IsUnlocked = true;
        return Task.FromResult(ok);
    }

    public void Lock()
    {
        if (!IsUnlocked) return;

        IsUnlocked = false;
        Locked?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (!IsConfigured) return false;
        if (!Verify(oldPassword?.Trim() ?? string.Empty, _credential!)) return false;
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinPasswordLength) return false;

        var credential = CreateCredential(newPassword.Trim(), DefaultIterations);
        await _repo.SetSettingAsync(CredentialSettingKey, credential).ConfigureAwait(false);
        _credential = credential;
        return true;
    }

    /// <summary>
    /// 取消密码，回到「无密码隐藏」。
    ///
    /// 写空串而不是删设置项：<c>AppSetting</c> 表的一次「删了又重建」毫无意义，
    /// 而 <see cref="IsConfigured"/> 判断的是「非空」，空串天然等于「没设过」。
    /// 当前这次会话保持已解锁状态 —— 用户刚验证过身份，没必要立刻把他关在外面。
    /// </summary>
    public async Task<bool> RemovePasswordAsync(string currentPassword)
    {
        if (!IsConfigured) return false;
        if (!Verify(currentPassword?.Trim() ?? string.Empty, _credential!)) return false;

        await _repo.SetSettingAsync(CredentialSettingKey, string.Empty).ConfigureAwait(false);
        _credential = null;
        IsUnlocked = true;
        return true;
    }

    public Task<string> MoveIntoVaultAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("要移入私密空间的文件不存在", sourcePath);

        EnsureVaultDirectory();

        var target = UniquePath(VaultDirectory, Path.GetFileName(sourcePath));
        return Task.Run(() =>
        {
            MoveFile(sourcePath, target);
            return target;
        });
    }

    public Task<string?> MoveOutOfVaultAsync(string currentPath, string? originalPath)
    {
        // 没有原路径（老数据 / 从别处直接放进保险箱）就只能留在原地，由调用方提示用户手动处理
        if (string.IsNullOrWhiteSpace(originalPath)) return Task.FromResult<string?>(null);

        var directory = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return Task.FromResult<string?>(null);

        var target = File.Exists(originalPath)
            ? UniquePath(directory, Path.GetFileName(originalPath))
            : originalPath;

        return Task.Run(() =>
        {
            if (!File.Exists(currentPath)) return (string?)null;

            MoveFile(currentPath, target);
            return (string?)target;
        });
    }

    // ---------- 目录与文件 ----------

    /// <summary>
    /// 确保保险箱目录存在，并打上隐藏属性。
    /// 隐藏不是安全措施（资源管理器开「显示隐藏」就能看到），只是让随手翻的人别一眼撞见。
    /// </summary>
    private void EnsureVaultDirectory()
    {
        var dir = VaultDirectory;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            try
            {
                File.SetAttributes(dir, FileAttributes.Hidden | FileAttributes.Directory);
            }
            catch (Exception)
            {
                // 某些网络盘 / 特殊文件系统不支持设隐藏，忽略即可，不影响功能
            }
        }
    }

    /// <summary>同名文件已存在时追加 " (1)"、" (2)"…… 保留扩展名。</summary>
    private static string UniquePath(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            target = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(target)) return target;
        }

        // 极端情况下（同一个文件夹里上万个同名）退回 GUID，保证不会覆盖任何已有文件
        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    /// <summary>
    /// 移动文件。跨盘（数据目录在另一个硬盘 / 移动硬盘上）时 File.Move 会失败，
    /// 退回「复制 + 删源」。顺序很重要：先确认复制成功再删源，中途断电最多多一份副本，不会丢文件。
    /// </summary>
    private static void MoveFile(string source, string target)
    {
        var sameVolume = string.Equals(
            Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            File.Move(source, target);
            return;
        }

        File.Copy(source, target, overwrite: false);
        File.Delete(source);
    }

    // ---------- 密码 ----------

    private static string CreateCredential(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Verify(string password, string credential)
    {
        // 格式：pbkdf2-sha256$iterations$salt$hash
        var parts = credential.Split('$');
        if (parts.Length != 4 || !parts[0].Equals("pbkdf2-sha256", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // 定长时间比较：别让「前几位对得上」的响应更快，把密码一点点试出来
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
