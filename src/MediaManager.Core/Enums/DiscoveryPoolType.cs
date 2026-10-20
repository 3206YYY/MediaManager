namespace MediaManager.Core.Enums;

/// <summary>发现页随机推荐的池子类型。</summary>
public enum DiscoveryPoolType
{
    /// <summary>收藏。</summary>
    Favorite,
    /// <summary>全部。</summary>
    All,
    /// <summary>未分类。</summary>
    Unfiled,
    /// <summary>指定分类或收藏夹（配合 PoolId 使用）。</summary>
    Category,
}
