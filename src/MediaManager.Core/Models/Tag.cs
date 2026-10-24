namespace MediaManager.Core.Models;

/// <summary>标签。</summary>
public class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>
/// 分类。v1.3.2 起是唯一的归类维度 —— 原来的「收藏夹（Collection）」已并入这里，
/// 迁移后每个旧收藏夹都变成同名的顶级分类（见 DatabaseInitializer.MigrateCollectionsIntoCategories）。
/// </summary>
public class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
