using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Generation;
using Abeat.Core.Model;
using Abeat.Core.Packaging;

namespace Abeat.Web;

public static class MapEndpoints
{
    public static string MapDir(SongStore store, string id) => Path.Combine(store.Dir(id), "map");

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/defaults", () => new
        {
            settings = new GeneratorSettings(),
            profiles = Enum.GetValues<DifficultyName>().ToDictionary(d => d.ToString(), DifficultyProfile.Default),
        });

        api.MapGet("/songs", (SongStore store) => store.All);

        api.MapPost("/songs", async (HttpRequest req, SongStore store, AnalysisQueue queue) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest("multipart form expected");
            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Results.BadRequest("no file");
            var options = new AnalysisOptions(
                form["beats"].FirstOrDefault() ?? "auto",
                form["stems"].FirstOrDefault() == "true");
            await using var s = file.OpenReadStream();
            var meta = await store.AddAsync(file.FileName, s, options);
            queue.Enqueue(meta.Id);
            return Results.Ok(meta);
        }).DisableAntiforgery();

        api.MapPost("/songs/url", (UrlRequest req, SongStore store, AnalysisQueue queue) =>
        {
            if (!Uri.TryCreate(req.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.BadRequest("expected an http(s) link, e.g. a YouTube or YouTube Music URL");
            var meta = store.AddUrl(uri.ToString(), new AnalysisOptions(req.Beats ?? "auto", req.Stems));
            queue.Enqueue(meta.Id);
            return Results.Ok(meta);
        });

        api.MapGet("/songs/{id}", (string id, SongStore store) =>
            store.Get(id) is { } m ? Results.Ok(new { meta = m, log = store.Log(id).Snapshot() }) : Results.NotFound());

        api.MapDelete("/songs/{id}", (string id, SongStore store) =>
        {
            store.Delete(id);
            return Results.NoContent();
        });

        api.MapPost("/songs/{id}/reanalyze", (string id, AnalysisOptions options, SongStore store, AnalysisQueue queue) =>
        {
            if (store.Get(id) is not { } m) return Results.NotFound();
            m.Analysis = options;
            m.Status = SongStatus.Queued;
            store.Save(m);
            queue.Enqueue(id);
            return Results.Ok(m);
        });

        api.MapGet("/songs/{id}/analysis", (string id, SongStore store) =>
            store.Analysis(id) is { } a ? Results.Ok(a) : Results.NotFound());

        api.MapGet("/songs/{id}/audio", (string id, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            return Results.File(Path.Combine(a.Directory, a.Audio.File), "audio/ogg", enableRangeProcessing: true);
        });

        api.MapGet("/songs/{id}/cover", (string id, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            return Results.File(Path.Combine(a.Directory, a.Cover), "image/jpeg");
        });

        api.MapGet("/songs/{id}/settings", (string id, SongStore store) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(store.Settings(id)));

        // Generate with the given settings, save them, write the map + zip, and return everything the
        // timeline needs (notes, walls, events, reports with issues).
        api.MapPost("/songs/{id}/generate", async (string id, GeneratorSettings settings, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            var result = await Task.Run(() => MapGenerator.Generate(a, settings));
            store.SaveSettings(id, settings);
            MapPackager.Write(result.Map, a, MapDir(store, id), zip: true);
            return Results.Ok(ToDto(a, result));
        });

        api.MapGet("/songs/{id}/map", (string id, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            var dir = MapDir(store, id);
            if (!File.Exists(Path.Combine(dir, "Info.dat"))) return Results.NotFound();
            var map = Abeat.Core.Formats.MapReader.Read(dir);
            return Results.Ok(new
            {
                difficulties = map.Difficulties.Select(d => DifficultyDto(a, d, FlowAnalyzer.Analyze(d, a.Tempo.Bpm), null)),
            });
        });

        // zip is CORS-enabled so ArcViewer (?url=...) can load it when the server is reachable
        api.MapGet("/songs/{id}/map.zip", (string id, SongStore store, HttpContext ctx) =>
        {
            var m = store.Get(id);
            var zip = MapDir(store, id) + ".zip";
            if (m == null || !File.Exists(zip)) return Results.NotFound();
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            string name = string.IsNullOrWhiteSpace(m.Artist) ? m.Title : $"{m.Artist} - {m.Title}";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Results.File(zip, "application/zip", $"{name}.zip");
        });
    }

    public sealed record UrlRequest(string? Url, string? Beats, bool Stems);

    static object ToDto(SongAnalysis a, GenerationResult r) => new
    {
        difficulties = r.Difficulties.Select(d => DifficultyDto(a, d.Map, d.Report, d.Events)),
    };

    static object DifficultyDto(SongAnalysis a, DifficultyMap d, FlowReport report, IReadOnlyList<RhythmEvent>? events) => new
    {
        name = d.Difficulty.ToString(),
        njs = d.NoteJumpSpeed,
        offset = d.NoteJumpOffset,
        jumpDistance = Math.Round(MapGenerator.JumpDistance(a.Tempo.Bpm, d.NoteJumpSpeed, d.NoteJumpOffset), 2),
        notes = d.Notes.Select(n => new { b = n.Beat, x = n.X, y = n.Y, c = (int)n.Hand, d = (int)n.Direction }),
        bombs = d.Bombs.Select(n => new { b = n.Beat, x = n.X, y = n.Y }),
        walls = d.Obstacles.Select(o => new { b = o.Beat, d = o.Duration, x = o.X, y = o.Y, w = o.Width, h = o.Height }),
        events = events?.Select(e => new { b = e.Beat, s = Math.Round(e.Strength, 3), dbl = e.IsDouble, layer = e.Layer }),
        report = new
        {
            report.Notes, report.Nps, report.PeakNps, report.Resets, report.BombResets, report.VisionBlocks,
            report.Crossovers, report.WallClashes, report.BombHits, report.MeanCost, report.FlowScore, report.LeftShare, lights = d.Lights.Count,
            issues = report.Issues.Select(i => new { b = i.Beat, hand = (int)i.Hand, kind = i.Kind.ToString(), cost = Math.Round(i.Cost, 2) }),
        },
    };
}
