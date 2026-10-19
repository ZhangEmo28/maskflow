namespace MaskFlow.Api.Infrastructure.Security;

public sealed class SamInferenceGate
{
    readonly SemaphoreSlim slots;

    public SamInferenceGate()
    {
        var maxConcurrent = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_SAM_MAX_CONCURRENT"), out var configured) && configured > 0)
        {
            maxConcurrent = configured;
        }

        slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!await slots.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        return new SlotLease(slots);
    }

    // Background work waits for a slot instead of failing; interactive requests use TryAcquireAsync.
    public async Task<IAsyncDisposable> WaitAsync(CancellationToken cancellationToken = default)
    {
        await slots.WaitAsync(cancellationToken);
        return new SlotLease(slots);
    }

    sealed class SlotLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
