using System.Collections.Concurrent;
using System.Threading.Channels;
using Abeat.Core.Analysis;
using Abeat.Core.Generation;

namespace Abeat.Web;

/// <summary>Runs analyses one at a time (they are CPU/RAM heavy), then generates a map with the
/// song's settings so every upload ends with a downloadable map without further clicks. A re-analysis
/// writes to a separate work dir that replaces the song's analysis only when it succeeds, so the song,
/// its analysis and all its versions stay usable while it runs and after it fails. Jobs can be cancelled.</summary>
public sealed class AnalysisQueue(SongStore store, WorkerRuntime runtime, ILogger<AnalysisQueue> logger) : BackgroundService
{
    readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    readonly ConcurrentDictionary<string, bool> cancelled = new();

    public void Enqueue(string id)
    {
        cancelled.TryRemove(id, out _);
        queue.Writer.TryWrite(id);
    }

    /// <summary>Stops a song's queued or running job (the worker process is killed).</summary>
    public bool Cancel(string id)
    {
        if (store.Get(id) is not { Status: SongStatus.Queued or SongStatus.Analyzing or SongStatus.Generating }) return false;
        cancelled[id] = true;
        if (running.TryGetValue(id, out var cts)) cts.Cancel();
        else Finish(id, store.Get(id)!, "cancelled");
        return true;
    }

    void Finish(string id, SongMeta meta, string? error)
    {
        meta.Status = error == null ? SongStatus.Ready : SongStatus.Failed;
        meta.Error = error;
        store.Save(meta);
        store.Log(id).Add(error == null ? "ready" : error == "cancelled" ? "cancelled" : "FAILED: " + error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var m in store.All.Where(m => m.Status is SongStatus.Queued or SongStatus.Analyzing or SongStatus.Generating))
            Enqueue(m.Id);

        await foreach (var id in queue.Reader.ReadAllAsync(ct))
        {
            var meta = store.Get(id);
            if (meta == null || cancelled.TryRemove(id, out _)) continue;
            var log = store.Log(id);
            using var job = CancellationTokenSource.CreateLinkedTokenSource(ct);
            running[id] = job;
            try
            {
                meta.Status = SongStatus.Analyzing;
                meta.Error = null;
                store.Save(meta);
                log.Add($"analysis started ({meta.Analysis})");
                var worker = await runtime.WaitReadyAsync(log.Add, job.Token);
                var next = store.PrepareNextWorkDir(id);
                var a = await new AnalysisRunner(workerExe: worker).AnalyzeAsync(store.Input(meta), next, meta.Analysis, log.Add, job.Token, store.LyricsFile(id));
                store.CommitNextWorkDir(id);
                a = store.Analysis(id)!;
                meta.Title = string.IsNullOrWhiteSpace(a.Source.Title) ? meta.Title : a.Source.Title;
                meta.Artist = a.Source.Artist;
                meta.Bpm = a.Tempo.Bpm;
                meta.DurationSec = a.Audio.DurationSec;
                meta.HasAnalysis = true;
                meta.AnalysisRevision++;

                meta.Status = SongStatus.Generating;
                store.Save(meta);
                log.Add("generating map");
                var settings = store.Settings(id);
                var result = await Task.Run(() => MapGenerator.Generate(a, settings), job.Token);
                Generations.Save(store, id, a, settings, result, draft: false);
                foreach (var d in result.Difficulties) log.Add(d.Report.ToString());
                foreach (var d in result.Extra) log.Add($"{d.Map.Characteristic} {d.Map.Difficulty}: {d.Map.Notes.Count} notes, {d.Map.Rotations.Count} rotations");
                Finish(id, meta, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                store.DiscardNextWorkDir(id);
                Finish(id, meta, "cancelled");
            }
            catch (Exception e)
            {
                logger.LogError(e, "analysis of {Id} failed", id);
                store.DiscardNextWorkDir(id);
                Finish(id, meta, e.Message);
            }
            finally
            {
                running.TryRemove(id, out _);
            }
        }
    }
}
