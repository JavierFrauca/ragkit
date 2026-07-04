using System.Collections.Concurrent;
using System.Threading.Channels;
using RagKit;

namespace RagKit.Dashboard;

/// <summary>Lifecycle of a background <see cref="RagClient.IngestFolderAsync"/> run.</summary>
internal enum IngestRunStatus { Running, Completed, Failed }

/// <summary>One tracked ingest run: its live status plus a channel of results as
/// <see cref="RagClient.IngestFolderAsync"/> produces them. <see cref="Status"/> is
/// set to its final value before the channel is completed, so a reader that drains
/// the channel to the end always observes the final status, never <see
/// cref="IngestRunStatus.Running"/>.</summary>
internal sealed class IngestRun
{
    public Channel<IngestResult> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<IngestResult>();
    public IngestRunStatus Status { get; set; } = IngestRunStatus.Running;
    public string? Error { get; set; }
}

/// <summary>
/// Tracks in-progress and finished ingest runs so <c>GET api/ingest/{runId}/stream</c>
/// can stream a run's results independently of the request that started it — a run
/// keeps producing results in the background even if nobody is listening yet.
/// In-memory and process-lifetime only (deliberately, like the rest of the dashboard's
/// state): runs are never evicted, so a process that starts many ingests without
/// restarting accumulates completed runs in memory — acceptable for a maintenance
/// panel, not for a long-lived queue system.
/// </summary>
internal sealed class IngestRunTracker
{
    private readonly ConcurrentDictionary<Guid, IngestRun> _runs = new();

    public Guid Start(RagClient rag, string path, string? domain, bool recursive)
    {
        var id = Guid.NewGuid();
        var run = new IngestRun();
        _runs[id] = run;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in rag.IngestFolderAsync(path, domain, recursive))
                    await run.Channel.Writer.WriteAsync(result).ConfigureAwait(false);
                run.Status = IngestRunStatus.Completed;
            }
            catch (Exception ex)
            {
                run.Status = IngestRunStatus.Failed;
                run.Error = ex.Message;
            }
            finally
            {
                run.Channel.Writer.Complete();
            }
        });

        return id;
    }

    public bool TryGet(Guid id, out IngestRun run) => _runs.TryGetValue(id, out run!);
}
