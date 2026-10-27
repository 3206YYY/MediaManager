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
/// v1.5 调整：默认**不设密码**，就是一层「不想被随手翻到」的隐藏（类似 Steam 的隐藏游戏）。
/// 进入不需要任何校验，<see cref="OpenAsync"/> 直接放行；密码相关的接口保留，
/// 将来要给用户加一道锁时，把入口改回「先 <see cref="UnlockAsync"/> 再进去」即可，不用重写存储层。
/// </para>
/// </summary>
public interface IVaultService
{
    /// <summary>保险箱目录的绝对路径。</summary>
    string VaultDirectory { get; }

    /// <summary>是否已经设置过密码（当前版本默认 false，表示走「无密码隐藏」模式）。</summary>
    bool IsConfigured { get; }

    /// <summary>当前是否处于打开状态（仅内存，重启即关）。</summary>
    bool IsUnlocked { get; }

    /// <summary>被关闭（手动关闭 / 超时自动关闭）时触发，UI 据此退回普通视图。</summary>
    event EventHandler? Locked;

    /// <summary>
    /// 打开私密空间。
    /// <para>
    /// 当前版本不设密码：确保保险箱目录存在后直接返回 true。
    /// 将来若启用密码，这里应先校验再放行（未解锁则返回 false）。
    /// </para>
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
