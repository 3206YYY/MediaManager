namespace MediaManager.Core.Models;

/// <summary>
/// 私密空间里的一个「分区」。
///
/// 为什么不用普通的 <see cref="Category"/>：
/// 分类会出现在主界面侧栏，连名字带数量一起露出来 —— 「分类：前女友 (12)」这种东西
/// 出现在明面上，等于在保险箱门口贴了张清单。所以私密内容有自己独立的一套分区。
/// </summary>
public class VaultSection
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>分区内的文件数（查询时填充，不是表字段）。</summary>
    public int ItemCount { get; set; }
}
