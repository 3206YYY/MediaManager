using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>重复媒体检测服务接口。</summary>
public interface IDuplicateService
{
    /// <summary>查找重复媒体（按大小分组 + 内容哈希），返回重复组列表。</summary>
    Task<List<DuplicateGroup>> FindDuplicatesAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
