using MediaManager.Core.Services;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace MediaManager.Infrastructure.Media;

/// <summary>EXIF 元数据读取实现（基于 MetadataExtractor）。带小容量缓存，重复选中同一张不再重复读盘。</summary>
public class ExifService : IExifService
{
    /// <summary>最近读过的 EXIF（key = 路径 + 修改时间，文件变了会自动失效）。</summary>
    private const int CacheCapacity = 256;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();

    public Task<Dictionary<string, string>> ReadExifAsync(string path)
    {
        return Task.Run(() => ReadExifCached(path));
    }

    private static Dictionary<string, string> ReadExifCached(string path)
    {
        string key = path;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) key = $"{path}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { /* 取不到时间就退化为按路径缓存 */ }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var result = ReadExif(path);

        lock (CacheGate)
        {
            if (Cache.Count >= CacheCapacity && CacheOrder.Count > 0)
                Cache.Remove(CacheOrder.Dequeue());

            Cache[key] = result;
            CacheOrder.Enqueue(key);
            return result;
        }
    }

    /// <summary>单个值最多显示多少字（超长的多半是 JSON / 二进制串，截断而不是整段铺开）。</summary>
    private const int MaxValueChars = 120;

    /// <summary>
    /// AI 生成图写在 PNG tEXt 块里的关键词。ComfyUI 是 prompt / workflow，
    /// Stable Diffusion WebUI 是 parameters。
    /// </summary>
    private static readonly string[] AiMetaKeys = { "prompt", "workflow", "parameters" };

    private static Dictionary<string, string> ReadExif(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var items = new List<(int Rank, string Name, string Value)>();
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    // 之前把 Description 当成了键（XAML 又只绑 Key），面板上就只剩
                    // 「1080:」「1920:」这种没头没尾的行，等于 EXIF 面板本身失效。
                    var name = tag.Name;
                    var value = tag.Description;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
                    if (IsNoise(name, value)) continue;

                    if (value.Length > MaxValueChars)
                        value = value[..MaxValueChars].TrimEnd() + "…";

                    items.Add((Rank(name), name, value));
                }
            }

            // 尺寸、位深这些一眼要看的排前面；相机型号/拍摄时间其次；其余垫后。
            foreach (var it in items.OrderBy(x => x.Rank))
                result.TryAdd(it.Name, it.Value);
        }
        catch
        {
            // 无 EXIF 或读取失败
        }
        return result;
    }

    private static int Rank(string name)
    {
        if (name.Contains("Width", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Height", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bit Depth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Color Type", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Make", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Model", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Date", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("MIME", StringComparison.OrdinalIgnoreCase)
            || name.Contains("File", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Format", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Compression", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    /// <summary>
    /// 过滤掉对用户没意义、或者根本不该显示的标签。
    /// <para>
    /// 后者是真事：ComfyUI 生成的 PNG 会在 tEXt 块里塞进完整的提示词和节点工作流
    /// （每张几 KB 的 JSON，含模型文件名、LoRA、采样参数）。列出来既撑爆面板，
    /// 又等于把别人的配方摊开给所有人看 —— 截图一发出去就收不回来了。
    /// </para>
    /// </summary>
    private static bool IsNoise(string name, string value)
    {
        if (name.Contains("Textual Data", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("XMP", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var key in AiMetaKeys)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
                return true;

        // 值本身就是一坨 JSON / 数组的，一律不显示（正常 EXIF 不会有这种值）
        var head = value.TrimStart();
        if (value.Length > 12 && (head.StartsWith('{') || head.StartsWith('[')))
            return true;

        return false;
    }

    /// <summary>提取关键 EXIF 字段（相机、拍摄时间、光圈等）。</summary>
    public static Dictionary<string, string> ReadKeyExif(string path)
    {
        var keys = new Dictionary<string, string>();
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            if (ifd0 != null)
            {
                Add(keys, "相机", ifd0, ExifDirectoryBase.TagMake);
                Add(keys, "型号", ifd0, ExifDirectoryBase.TagModel);
                Add(keys, "拍摄时间", ifd0, ExifDirectoryBase.TagDateTime);
            }
            if (subIfd != null)
            {
                Add(keys, "光圈", subIfd, ExifDirectoryBase.TagFNumber);
                Add(keys, "快门", subIfd, ExifDirectoryBase.TagExposureTime);
                Add(keys, "ISO", subIfd, ExifDirectoryBase.TagIsoEquivalent);
                Add(keys, "焦距", subIfd, ExifDirectoryBase.TagFocalLength);
                Add(keys, "镜头", subIfd, ExifDirectoryBase.TagLensModel);
            }
        }
        catch { /* ignore */ }
        return keys;
    }

    private static void Add(Dictionary<string, string> dict, string label, MetadataExtractor.Directory dir, int tagId)
    {
        var desc = dir.GetDescription(tagId);
        if (desc != null)
            dict[label] = desc;
    }
}
