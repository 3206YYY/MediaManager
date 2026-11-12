using MediaManager.Infrastructure.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MediaManager.Tests;

/// <summary>
/// EXIF 面板的取数规则。这里守的是两个坑：
/// 1) 早先版本把 Description 当成键，加上 XAML 只绑了 Key，
///    面板上就只剩「1080:」这种没有名字的行，等于整个面板失效；
/// 2) ComfyUI / SD 生成的 PNG 会把提示词和工作流塞进 tEXt 块，
///    一并列出来既撑爆面板，又等于把人的配方（模型名、LoRA、采样参数）摊给人看。
/// </summary>
public class ExifServiceTests
{
    private static string MakePng(Action<PngMetadata>? configure = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mm_exif_{Guid.NewGuid():N}.png");
        using var img = new Image<Rgba32>(64, 48);
        var meta = img.Metadata.GetPngMetadata();
        configure?.Invoke(meta);
        img.Save(path, new PngEncoder());
        return path;
    }

    [Fact]
    public async Task 键是标签名而不是值_不再出现_1080_1080()
    {
        var path = MakePng();
        try
        {
            var dict = await new ExifService().ReadExifAsync(path);

            Assert.NotEmpty(dict);
            // 键和值相同 = 旧 bug 的典型症状
            Assert.DoesNotContain(dict, kv => kv.Key == kv.Value);
            Assert.Contains(dict, kv => kv.Key.Contains("Width", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(dict, kv => kv.Value.Contains("64"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AI生成的提示词与工作流不进面板()
    {
        var path = MakePng(meta =>
        {
            meta.TextData.Add(new PngTextData(
                "prompt",
                "{\"1\":{\"class_type\":\"UNETLoader\",\"inputs\":{\"unet_name\":\"pawhibitedAnima29b_v10.safetensors\"}}}",
                "", ""));
            meta.TextData.Add(new PngTextData("workflow", "{\"nodes\":[{\"type\":\"KSampler\"}]}", "", ""));
            meta.TextData.Add(new PngTextData(
                "parameters", "masterpiece, best quality, 1girl, solo\nSteps: 28, Sampler: Euler a, CFG: 7", "", ""));
        });
        try
        {
            var dict = await new ExifService().ReadExifAsync(path);

            // 三条 AI 元数据一条都不许露出来
            Assert.DoesNotContain(dict, kv => kv.Key.Contains("prompt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dict, kv => kv.Key.Contains("workflow", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dict, kv => kv.Key.Contains("parameters", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dict, kv => kv.Key.Contains("Textual", StringComparison.OrdinalIgnoreCase));
            // 值里更不许出现模型名 / 采样器
            Assert.DoesNotContain(dict, kv => kv.Value.Contains("safetensors", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dict, kv => kv.Value.Contains("KSampler", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(dict, kv => kv.Value.Contains("Steps:", StringComparison.OrdinalIgnoreCase));

            // 正常的像素信息仍然要在
            Assert.Contains(dict, kv => kv.Key.Contains("Width", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 超长的值被截断而不是整段铺开()
    {
        // 用 JPEG 的 ImageDescription 造长值：PNG 的 tEXt 会被当成 AI 元数据过滤掉，
        // 拿它测截断是测不到的（也正好说明过滤生效）。
        var path = Path.Combine(Path.GetTempPath(), $"mm_exif_{Guid.NewGuid():N}.jpg");
        try
        {
            using (var img = new Image<Rgba32>(64, 48))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.ImageDescription, new string('长', 400));
                img.Metadata.ExifProfile = exif;
                img.Save(path, new JpegEncoder());
            }

            var dict = await new ExifService().ReadExifAsync(path);

            Assert.All(dict.Values, v => Assert.True(v.Length <= 121, "值超过 121 字：" + v.Length));
            Assert.Contains(dict, kv => kv.Value.EndsWith('…'));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task 不存在的文件不抛异常()
    {
        var dict = await new ExifService().ReadExifAsync(
            Path.Combine(Path.GetTempPath(), "mm_not_exist_" + Guid.NewGuid().ToString("N") + ".png"));
        Assert.Empty(dict);
    }
}
