namespace MediaManager.Core.Services;

/// <summary>
/// 私密空间密码的硬性规则（长度这类「界面要显示、服务层要校验」的常量）。
///
/// 单独拎出来是为了让界面提示和服务层校验读同一个数 —— 否则哪天把门槛从 4 改成 6，
/// 界面还写着「至少 4 位」，用户输 5 位被拒，看着像 bug。
/// </summary>
public static class VaultPasswordPolicy
{
    /// <summary>密码最短长度。本地软件，别把门槛定得让人烦。</summary>
    public const int MinLength = 4;
}
