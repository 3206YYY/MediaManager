namespace MediaManager.Core.Services;

/// <summary>
/// 私密空间（保险箱）。
///
/// 设计取舍：**移动 + 隐藏，不加密**。
/// 移入时把文件挪到 <c>{数据目录}/Vault</c>（目录设隐藏属性），库里记 <c>IsPrivate=1</c>，
/// 主网格 / 搜索 / 文件夹视图全部看不到。没有加密，所以：
/// <list type="bullet">
/// <item>密码忘了不会丢数据 —— 文件就在 Vault 目录里，可以直接拷走；</item>
/// <item>但懂行的人翻到这个目录仍然能打开。防的是家人、同事随手翻，不是取证。</item>
/// </list>
/// 打开保险箱里的图片/视频不需要解密，直接读原文件，性能没有代价。
///
/// <para>
/// v1.4.1 调整：**密码由用户自己决定设不设**，默认不设。
/// <list type="bullet">
/// <item>没设密码：一层「不想被随手翻到」的隐藏，类似 Steam 的隐藏游戏，点一下就进；</item>
/// <item>设了密码：进门前要输密码，关窗即重新上锁。</item>
/// </list>
/// 代码不会主动催用户设密码 —— 入口只在私密空间窗口的顶栏，用户自己去找、自己去点。
/// 密码忘了不会丢数据（文件没加密，去 Vault 目录直接拷）。
/// </para>
/// </summary>
public interface IVaultService
{
    /// <summary>保险箱目录的绝对路径。</summary>
    string VaultDirectory { get; }

    /// <summary>是否已经设置过密码（false = 走「无密码隐藏」模式，默认如此）。</summary>
    bool IsConfigured { get; }

    /// <summary>当前是否处于打开状态（仅内存，重启即关）。</summary>
    bool IsUnlocked { get; }

    /// <summary>被关闭（手动关闭 / 超时自动关闭）时触发，UI 据此退回普通视图。</summary>
    event EventHandler? Locked;

    /// <summary>
    /// 打开私密空间，返回是否**已经**可以进去。
    /// <list type="bullet">
    /// <item>没设密码 → 确保目录存在，直接返回 true（默认形态）；</item>
    /// <item>设了密码且已解锁 → 返回 true；</item>
    /// <item>设了密码但锁着 → 返回 false，由界面弹解锁窗（这不是失败，只是「先解锁再进」）。</item>
    /// </list>
    /// 只是「先解锁再进」。
    /// </summary>
    Task<bool> OpenAsync();

    /// <summary>启动时调一次：把密码凭据读进内存，之后 <see cref="IsConfigured"/> 是纯内存判断。</summary>
    Task InitializeAsync();

    /// <summary>首次设置密码并创建保险箱目录。密码长度不足或已设置过则返回 false。</summary>
    Task<bool> SetupAsync(string password);

    /// <summary>校验密码并解锁。</summary>
    Task<bool> UnlockAsync(string password);

    /// <summary>关闭私密空间（不清空任何数据，也不动文件）。</summary>
    void Lock();


    /// <summary>修改密码；旧密码不正确返回 false。</summary>
    Task<bool> ChangePasswordAsync(string oldPassword, string newPassword);

    /// <summary>
    /// 取消密码（回到「无密码隐藏」）：需验证当前密码，验证不过返回 false。
    /// 只删掉凭据设置项，不碰任何文件 —— 私密内容仍在，仍然要密码之外的身份才能看到入口。
    /// </summary>
    Task<bool> RemovePasswordAsync(string currentPassword);

    /// <summary>
    /// 把文件挪进保险箱，返回新的绝对路径。
    /// 只负责挪文件，数据库标记由调用方在挪成功后写入。
    /// </summary>
    Task<string> MoveIntoVaultAsync(string sourcePath);

    /// <summary>
    /// 把文件从保险箱挪回原位，返回还原后的绝对路径。
    /// 原目录已不存在 / 已被占用时返回 null（文件仍留在保险箱里，不会丢）。
    /// </summary>
    Task<string?> MoveOutOfVaultAsync(string currentPath, string? originalPath);
}
