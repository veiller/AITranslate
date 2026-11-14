namespace AITranslate.Core.Translation.Concurrency;

/// <summary>
/// 全局并发限流器：限制同时进行中的 API 调用数量，避免多页签同时打满接口触发限流。
/// </summary>
public sealed class GlobalRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public GlobalRateLimiter(int maxConcurrent)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));
    }

    /// <summary>
    /// 获取一个并发名额。返回的 <see cref="IDisposable"/> 在 <c>Dispose</c> 时释放名额。
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
