namespace RagKit.Internal;

/// <summary>
/// Single gate for all concurrency throttling. Holds two semaphores — one for LLM
/// calls (across both tiers), one for ingestion — configured once from
/// <see cref="RagOptions"/> and shared by every component that needs them.
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly SemaphoreSlim _llm;
    private readonly SemaphoreSlim _ingest;

    public ConcurrencyGate(int maxLlm, int maxIngest)
    {
        _llm = new SemaphoreSlim(Math.Max(1, maxLlm));
        _ingest = new SemaphoreSlim(Math.Max(1, maxIngest));
    }

    /// <summary>
    /// Enter the LLM gate (classification, routing, guardrail, tier-1 answer —
    /// every <c>CompleteAsync</c> / <c>NextAsync</c> / <c>StreamAsync</c> call).
    /// Returns a releaser that MUST be disposed after the LLM call completes.
    /// </summary>
    public async Task<IDisposable> EnterLlmAsync(CancellationToken ct = default)
    {
        await _llm.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_llm);
    }

    /// <summary>
    /// Enter the ingestion gate. Returns a releaser that MUST be disposed after
    /// the ingestion operation completes.
    /// </summary>
    public async Task<IDisposable> EnterIngestAsync(CancellationToken ct = default)
    {
        await _ingest.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_ingest);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }
}
