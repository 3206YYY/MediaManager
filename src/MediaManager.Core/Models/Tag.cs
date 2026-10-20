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

/// <summary>分类。</summary>
public class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>收藏夹（收藏的独立分区，与「分类」两套体系）。</summary>
public class Collection
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
