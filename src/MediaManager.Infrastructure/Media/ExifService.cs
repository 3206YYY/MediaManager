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

    private static Dictionary<string, string> ReadExif(string path)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    var key = tag.Description ?? tag.Name;
                    var value = tag.Description;
                    if (tag.Description != null)
                        result.TryAdd(key, tag.Description);
                }
            }
        }
        catch
        {
            // 无 EXIF 或读取失败
        }
        return result;
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
