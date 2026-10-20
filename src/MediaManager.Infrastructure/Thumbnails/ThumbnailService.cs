using System.Threading.Channels;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MediaManager.Infrastructure.Thumbnails;

/// <summary>缩略图服务：Channel 队列 + 4 worker，图片用 ImageSharp，视频用 FFmpeg。</summary>
public class ThumbnailService : IThumbnailService, IDisposable
{
    private readonly IMediaRepository _repo;
    private readonly IAppConfigService _config;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly Channel<MediaItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private const int MaxLongEdge = 320;

    public ThumbnailService(IMediaRepository repo, IAppConfigService config, ILogger<ThumbnailService> logger)
    {
        _repo = repo;
        _config = config;
        _logger = logger;
        _channel = Channel.CreateBounded<MediaItem>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });

        Directory.CreateDirectory(config.ThumbnailDirectory);

        var workerCount = Math.Clamp(config.Concurrency, 1, 8);
        for (int i = 0; i < workerCount; i++)
            _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token)));
    }

    public event Action<long, string>? ThumbnailReady;

    public void Enqueue(MediaItem item)
    {
        _channel.Writer.TryWrite(item);
    }

    public string? GetCachedPath(MediaItem item)
    {
        var path = Path.Combine(_config.ThumbnailDirectory, $"{item.Id}_{item.QuickHash ?? "0"}.jpg");
        return File.Exists(path) ? path : null;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    await GenerateAsync(item, ct);
                }
                catch (Exception ex)
                {
                    // 设备被拔出属于正常场景：只记调试日志，避免刷屏；等重新扫描时会重建
                    if (MediaManager.Core.Diagnostics.StorageErrors.IsDeviceUnavailable(ex))
                        _logger.LogDebug("缩略图跳过（设备已断开）：{Path}", item.Path);
                    else
                        _logger.LogWarning(ex, "缩略图生成失败：{Path}", item.Path);
                }
            }
        }
    }

    private async Task GenerateAsync(MediaItem item, CancellationToken ct)
    {
        var output = Path.Combine(_config.ThumbnailDirectory, $"{item.Id}_{item.QuickHash ?? "0"}.jpg");
        if (File.Exists(output)) return;

        if (!File.Exists(item.Path)) return;

        bool ok = item.MediaType == Core.Enums.MediaType.Image
            ? await GenerateImageAsync(item.Path, output, ct)
            : await GenerateVideoAsync(item.Path, output, ct);

        if (ok && File.Exists(output))
        {
            item.ThumbnailPath = output;
            await _repo.UpdateMediaAsync(item);
            ThumbnailReady?.Invoke(item.Id, output);
        }
    }

    private async Task<bool> GenerateImageAsync(string input, string output, CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(input, ct).ConfigureAwait(false);
            image.Mutate(x => x.AutoOrient());
            var ratio = (double)MaxLongEdge / Math.Max(image.Width, image.Height);
            var w = Math.Max(1, (int)(image.Width * ratio));
            var h = Math.Max(1, (int)(image.Height * ratio));
            image.Mutate(x => x.Resize(w, h));
            await image.SaveAsJpegAsync(output, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // ImageSharp 无法解码（AVIF/HEIC/伪装成 .png 的 WebP 等）→ 回退 FFmpeg
            _logger.LogDebug(ex, "ImageSharp 解码失败，尝试 FFmpeg 回退：{Input}", input);
            return await GenerateWithFfmpegAsync(input, output, ct).ConfigureAwait(false);
        }
    }

    /// <summary>用 FFmpeg 解码生成缩略图（AVIF/HEIC/WebP/视频通用回退）。</summary>
    private async Task<bool> GenerateWithFfmpegAsync(string input, string output, CancellationToken ct)
    {
        var ffmpeg = _config.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            return false;

        // 图片用 scale 限制长边，视频截取第 1 秒帧
        var args = $"-y -i \"{input}\" -vf \"scale='min({MaxLongEdge},iw)':-2\" -frames:v 1 -q:v 3 \"{output}\"";
        return await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false);
    }

    private async Task<bool> GenerateVideoAsync(string input, string output, CancellationToken ct)
    {
        var ffmpeg = _config.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            // 只提示一次，避免刷屏
            if (_ffmpegMissingLogged != true)
            {
                _logger.LogWarning("未配置 FFmpeg，视频/特殊格式图片缩略图将被跳过；请下载 ffmpeg.exe 放入 tools/ffmpeg/ 目录");
                _ffmpegMissingLogged = true;
            }
            return false;
        }

        var args = $"-y -ss 00:00:01 -i \"{input}\" -vf \"thumbnail,scale={MaxLongEdge}:-1\" -frames:v 1 -q:v 3 \"{output}\"";
        return await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false);
    }

    private bool? _ffmpegMissingLogged;

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<long> CleanCacheAsync(CancellationToken ct = default)
    {
        long removed = 0;
        var dir = _config.ThumbnailDirectory;
        if (!Directory.Exists(dir)) return Task.FromResult(0L);

        foreach (var file in Directory.EnumerateFiles(dir, "*.jpg"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Delete(file);
                removed++;
            }
            catch { /* ignore */ }
        }
        return Task.FromResult(removed);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
