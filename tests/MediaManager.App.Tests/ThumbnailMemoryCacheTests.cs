using System.IO;
using MediaManager.App.Services;

namespace MediaManager.App.Tests;

/// <summary>
/// 缩略图内存缓存的行为测试。
/// 注意 <see cref="ThumbnailMemoryCache"/> 是静态共享状态，本类内 xunit 串行执行，
/// 每个用例自行 Clear/恢复预算，不依赖执行顺序。
/// </summary>
public class ThumbnailMemoryCacheTests : IDisposable
{
    /// <summary>1×1 PNG（红点）。解码结果 4 字节，不干扰别的用例。</summary>
    private const string Png1x1 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly string _dir;
    private readonly long _originalBudget;

    public ThumbnailMemoryCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mmc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _originalBudget = ThumbnailMemoryCache.BudgetBytes;
        ThumbnailMemoryCache.Clear();
    }

    public void Dispose()
    {
        ThumbnailMemoryCache.Clear();
        ThumbnailMemoryCache.BudgetBytes = _originalBudget;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录，删不掉就算了 */ }
    }

    private string WritePng(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(Png1x1));
        return path;
    }

    [Fact]
    public void TryGet_未命中返回false()
    {
        Assert.False(ThumbnailMemoryCache.TryGet(@"C:\nope\a.png", out var image));
        Assert.Null(image);
    }

    [Fact]
    public async Task 解码后命中且只占一份()
    {
        var path = WritePng("a.png");

        var decoded = await ThumbnailMemoryCache.GetOrLoadAsync(path);
        Assert.NotNull(decoded);

        Assert.True(ThumbnailMemoryCache.TryGet(path, out var hit));
        Assert.Same(decoded, hit);
        Assert.Equal(1, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task 同一路径的并发请求共享同一次解码()
    {
        // 虚拟化容器回收时，同一格很容易被两处同时请求；各自解码一份就是双倍内存
        var path = WritePng("shared.png");

        var t1 = ThumbnailMemoryCache.GetOrLoadAsync(path);
        var t2 = ThumbnailMemoryCache.GetOrLoadAsync(path);
        var r1 = await t1;
        var r2 = await t2;

        Assert.NotNull(r1);
        Assert.Same(r1, r2);
        Assert.Equal(1, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task 文件不存在返回null()
    {
        var result = await ThumbnailMemoryCache.GetOrLoadAsync(Path.Combine(_dir, "missing.png"));
        Assert.Null(result);
        Assert.Equal(0, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task 空白路径直接返回null不进队列()
    {
        Assert.Null(await ThumbnailMemoryCache.GetOrLoadAsync(""));
        Assert.Null(await ThumbnailMemoryCache.GetOrLoadAsync("   "));
        Assert.Equal(0, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task 滚动划走的格子被丢弃_零解码成本()
    {
        // stillWanted=false 模拟「格子已经被虚拟化划走」：必须在解码前丢掉，返回 null 且不进缓存
        var path = WritePng("gone.png");

        var result = await ThumbnailMemoryCache.GetOrLoadAsync(path, stillWanted: () => false);

        Assert.Null(result);
        Assert.Equal(0, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task 谓词抛异常按还要处理()
    {
        // 谓词坏掉不该让格子永远空白：宁可多解一张
        var path = WritePng("broken_pred.png");

        var result = await ThumbnailMemoryCache.GetOrLoadAsync(path, stillWanted: () => throw new InvalidOperationException());

        Assert.NotNull(result);
        Assert.Equal(1, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public async Task Remove_移除后不再命中()
    {
        var path = WritePng("rm.png");
        await ThumbnailMemoryCache.GetOrLoadAsync(path);
        Assert.Equal(1, ThumbnailMemoryCache.Count);

        ThumbnailMemoryCache.Remove(path);

        Assert.Equal(0, ThumbnailMemoryCache.Count);
        Assert.False(ThumbnailMemoryCache.TryGet(path, out _));
    }

    [Fact]
    public void Remove_空白路径或不存在路径都不炸()
    {
        ThumbnailMemoryCache.Remove("");
        ThumbnailMemoryCache.Remove(@"C:\nope\never.png");
        Assert.Equal(0, ThumbnailMemoryCache.Count);
    }

    [Fact]
    public void 预算下限16MB_防止把缓存挤没()
    {
        // 缩略图缓存是性能根基：预算被设成几 KB 会让网格全程白屏，所以有硬下限
        try
        {
            ThumbnailMemoryCache.BudgetBytes = 1;
            Assert.Equal(16L * 1024 * 1024, ThumbnailMemoryCache.BudgetBytes);
        }
        finally
        {
            ThumbnailMemoryCache.BudgetBytes = _originalBudget;
        }
    }

    [Fact]
    public async Task 统计计数随命中未命中增长()
    {
        var (h0, m0, _) = ThumbnailMemoryCache.Stats;

        var path = WritePng("stats.png");
        await ThumbnailMemoryCache.GetOrLoadAsync(path);          // 未命中 → 解码
        ThumbnailMemoryCache.TryGet(path, out _);                 // 命中

        var (h1, m1, _) = ThumbnailMemoryCache.Stats;
        Assert.True(h1 > h0, $"命中计数应增长: {h0} -> {h1}");
        Assert.True(m1 > m0, $"未命中计数应增长: {m0} -> {m1}");
    }
}
