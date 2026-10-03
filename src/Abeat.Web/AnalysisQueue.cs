using System.Threading.Channels;
using Abeat.Core.Analysis;
using Abeat.Core.Generation;
using Abeat.Core.Packaging;

namespace Abeat.Web;

/// <summary>Runs analyses one at a time (they are CPU/RAM heavy), then generates a map with the
/// song's settings so every upload ends with a downloadable map without further clicks.</summary>
public sealed class AnalysisQueue(SongStore store, ILogger<AnalysisQueue> logger) : BackgroundService
{
    readonly Channel<string> queue = Channel.CreateUnbounded<string>();

    public void Enqueue(string id) => queue.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var m in store.All.Where(m => m.Status is SongStatus.Queued or SongStatus.Analyzing or SongStatus.Generating))
            Enqueue(m.Id);

        await foreach (var id in queue.Reader.ReadAllAsync(ct))
        {
            var meta = store.Get(id);
            if (meta == null) continue;
            var log = store.Log(id);
            try
            {
                meta.Status = SongStatus.Analyzing;
                meta.Error = null;
                store.Save(meta);
                log.Add($"analysis started ({meta.Analysis})");
                var runner = new AnalysisRunner();
                var a = await runner.AnalyzeAsync(store.Input(meta), store.WorkDir(id), meta.Analysis, log.Add, ct);
                store.InvalidateAnalysis(id);
                meta.Title = string.IsNullOrWhiteSpace(a.Source.Title) ? meta.Title : a.Source.Title;
                meta.Artist = a.Source.Artist;
                meta.Bpm = a.Tempo.Bpm;
                meta.DurationSec = a.Audio.DurationSec;

                meta.Status = SongStatus.Generating;
                store.Save(meta);
                log.Add("generating map");
                var result = await Task.Run(() => MapGenerator.Generate(a, store.Settings(id)), ct);
                MapPackager.Write(result.Map, a, MapEndpoints.MapDir(store, id), zip: true);
                foreach (var d in result.Difficulties) log.Add(d.Report.ToString());

                meta.Status = SongStatus.Ready;
                store.Save(meta);
                log.Add("ready");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "analysis of {Id} failed", id);
                meta.Status = SongStatus.Failed;
                meta.Error = e.Message;
                store.Save(meta);
                log.Add("FAILED: " + e.Message);
            }
        }
    }
}
