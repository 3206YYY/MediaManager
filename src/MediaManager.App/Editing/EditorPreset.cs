using System.IO;
using System.Text.Json;
using MediaManager.Core.Editing;

namespace MediaManager.App.Editing;

/// <summary>预设里的文字水印样式（字号按画面高度归一化，套到不同尺寸的图上比例不变）。</summary>
public sealed class PresetWatermark
{
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft YaHei";
    /// <summary>字号 / 可见图高度。</summary>
    public double FontSizeRatio { get; set; } = 0.055;
    public string ColorHex { get; set; } = "#FFFFFF";
    public double Opacity { get; set; } = 0.55;
    public string? OutlineHex { get; set; }
    public double OutlineWidth { get; set; } = 0.07;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public double X { get; set; } = 0.55;
    public double Y { get; set; } = 0.82;
    public double Angle { get; set; }
}

/// <summary>
/// 一套「我的预设」：调色参数 + 可选的一条文字水印。
/// 只保存可以跨图复用的东西（位置、字号都是相对值），不保存裁剪/标注等单图内容。
/// </summary>
public sealed class EditorPreset
{
    public string Name { get; set; } = "";
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public EditorFilter Filter { get; set; } = EditorFilter.None;
    public int FilterStrength { get; set; } = 100;
    public PresetWatermark? Watermark { get; set; }

    public ColorAdjustments Adjustments => new(Brightness, Contrast, Saturation);
}

/// <summary>预设的本地存储：数据目录下的一个 JSON 文件。</summary>
public sealed class EditorPresetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public EditorPresetStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "editor-presets.json");
    }

    public List<EditorPreset> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<EditorPreset>();

            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<EditorPreset>>(json, Options);
            return list ?? new List<EditorPreset>();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取编辑预设失败");
            return new List<EditorPreset>();
        }
    }

    public void Save(IEnumerable<EditorPreset> presets)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(_path, JsonSerializer.Serialize(presets, Options));
    }
}
