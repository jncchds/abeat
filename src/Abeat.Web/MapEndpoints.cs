using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Generation;
using Abeat.Core.Model;
using Abeat.Core.Packaging;

namespace Abeat.Web;

public static class MapEndpoints
{
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
                form["stems"].FirstOrDefault() == "true",
                VocalOnsets: VocalOption(form["vocals"].FirstOrDefault()));
            await using var s = file.OpenReadStream();
            var meta = await store.AddAsync(file.FileName, s, options);
            queue.Enqueue(meta.Id);
            return Results.Ok(meta);
        }).DisableAntiforgery();

        api.MapPost("/songs/url", (UrlRequest req, SongStore store, AnalysisQueue queue) =>
        {
            if (!Uri.TryCreate(req.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.BadRequest("expected an http(s) link, e.g. a YouTube or YouTube Music URL");
            var meta = store.AddUrl(uri.ToString(), new AnalysisOptions(req.Beats ?? "auto", req.Stems, VocalOnsets: VocalOption(req.Vocals)));
            queue.Enqueue(meta.Id);
            return Results.Ok(meta);
        });

        // bulk import of local analyses / human maps; only from this machine (it reads server paths)
        api.MapPost("/admin/import", async (ImportRequest req, HttpContext ctx, SongStore store, AnalysisQueue queue) =>
        {
            if (ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return Results.Forbid();
            var meta = store.Import(req.Path);
            if (meta.Status == SongStatus.Queued) queue.Enqueue(meta.Id);
            else
            {
                var a = store.Analysis(meta.Id)!;
                var settings = store.Settings(meta.Id);
                var result = await Task.Run(() => MapGenerator.Generate(a, settings));
                Generations.Save(store, meta.Id, a, settings, result, draft: false);
            }
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
            options = options with { VocalOnsets = VocalOption(options.VocalOnsets) };
            Generations.List(store, id); // moves a legacy map into the history while its grid still matches
            m.Analysis = options;
            m.Status = SongStatus.Queued;
            store.Save(m);
            queue.Enqueue(id);
            return Results.Ok(m);
        });

        api.MapGet("/songs/{id}/lyrics", (string id, SongStore store) =>
            store.Get(id) is null ? Results.NotFound()
            : Results.Ok(new { text = store.LyricsFile(id) is { } f ? File.ReadAllText(f) : "" }));

        api.MapPut("/songs/{id}/lyrics", (string id, LyricsRequest req, SongStore store) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            File.WriteAllText(store.LyricsPath(id), req.Text ?? "");
            return Results.NoContent();
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

        // debug: separated stems kept by the worker, and the raw analysis
        api.MapGet("/songs/{id}/stems", (string id, SongStore store) =>
        {
            var dir = Path.Combine(store.WorkDir(id), "stems");
            return Directory.Exists(dir)
                ? Results.Ok(Directory.EnumerateFiles(dir).Select(f => new { name = Path.GetFileNameWithoutExtension(f), file = Path.GetFileName(f), bytes = new FileInfo(f).Length }).OrderBy(x => x.name))
                : Results.Ok(Array.Empty<object>());
        });

        api.MapGet("/songs/{id}/stems/{file}", (string id, string file, SongStore store) =>
        {
            if (file != Path.GetFileName(file)) return Results.BadRequest();
            var path = Path.Combine(store.WorkDir(id), "stems", file);
            if (!File.Exists(path) || store.Get(id) is not { } m) return Results.NotFound();
            string name = $"{(string.IsNullOrWhiteSpace(m.Artist) ? "" : m.Artist + " - ")}{m.Title} [{Path.GetFileNameWithoutExtension(file)}]{Path.GetExtension(file)}";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Results.File(path, "audio/flac", name, enableRangeProcessing: true);
        });

        api.MapGet("/songs/{id}/analysis.json", (string id, SongStore store) =>
        {
            var path = Path.Combine(store.WorkDir(id), "analysis.json");
            return File.Exists(path) ? Results.File(path, "application/json", $"{id}-analysis.json") : Results.NotFound();
        });

        api.MapGet("/songs/{id}/settings", (string id, SongStore store) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(store.Settings(id)));

        // Generate with the given settings and save them as the song's settings. Appends a new version;
        // draft=true (auto-regenerate) replaces the newest version if it is a draft too.
        api.MapPost("/songs/{id}/generate", async (string id, GeneratorSettings settings, bool? draft, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            var result = await Task.Run(() => MapGenerator.Generate(a, settings));
            store.SaveSettings(id, settings);
            var gen = Generations.Save(store, id, a, settings, result, draft ?? false);
            return Results.Ok(new
            {
                version = VersionDto(store, id, a, gen, HumanDifficulties(store, id, a)),
                difficulties = result.Difficulties.Select(d => DifficultyDto(a, d.Map, d.Report, d.Events)),
            });
        });

        // Versions: every saved generation (newest first) plus "human" for an imported reference map.
        api.MapGet("/songs/{id}/versions", (string id, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null) return Results.NotFound();
            var human = HumanDifficulties(store, id, a);
            var list = new List<object>();
            if (human != null && store.Get(id) is { } m)
                list.Add(new { id = HumanId, kind = "human", label = $"Human · {m.ReferenceMapper}", difficulties = human.Select(d => d.Difficulty.ToString()) });
            list.AddRange(Generations.List(store, id).Select(g => VersionDto(store, id, a, g, human)));
            return Results.Ok(list);
        });

        api.MapGet("/songs/{id}/versions/{version}", (string id, string version, SongStore store) =>
        {
            var a = store.Analysis(id);
            if (a == null || ReadVersion(store, id, version, a) is not { } maps) return Results.NotFound();
            return Results.Ok(new { difficulties = maps.Select(d => DifficultyDto(a, d, FlowAnalyzer.Analyze(d, a.Tempo.Bpm), null)) });
        });

        api.MapDelete("/songs/{id}/versions/{version}", (string id, string version, SongStore store) =>
            Generations.Delete(store, id, version) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/songs/{id}/versions/{version}/settings", (string id, string version, SongStore store) =>
            Generations.Settings(store, id, version) is { } s ? Results.Ok(s) : Results.NotFound());

        // Any two (version, difficulty) pairs; B is treated as the reference for precision/recall.
        api.MapGet("/songs/{id}/compare", (string id, string a, string ad, string b, string bd, SongStore store) =>
        {
            var an = store.Analysis(id);
            if (an == null) return Results.NotFound();
            var ma = ReadVersion(store, id, a, an)?.FirstOrDefault(d => d.Difficulty.ToString() == ad);
            var mb = ReadVersion(store, id, b, an)?.FirstOrDefault(d => d.Difficulty.ToString() == bd);
            if (ma == null || mb == null) return Results.NotFound();
            return Results.Ok(ComparisonDto(MapComparer.Compare(mb, an.Tempo.Bpm, ma, an.Tempo.Bpm, 0)));
        });

        // ArcViewer (a public https page) fetches the zip directly with ?noProxy=true. Browsers send a
        // private-network preflight before a public site may read from a LAN address.
        api.MapMethods("/songs/{id}/map.zip", ["OPTIONS"], (HttpContext ctx) =>
        {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            ctx.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
            ctx.Response.Headers.AccessControlAllowHeaders = "*";
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            return Results.NoContent();
        });

        api.MapGet("/songs/{id}/map.zip", (string id, string? version, SongStore store, HttpContext ctx) =>
        {
            var m = store.Get(id);
            var zip = (version ?? Generations.Latest(store, id)?.Id) is { } gen ? Generations.Zip(store, id, gen) : null;
            if (m == null || zip == null) return Results.NotFound();
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            string name = string.IsNullOrWhiteSpace(m.Artist) ? m.Title : $"{m.Artist} - {m.Title}";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Results.File(zip, "application/zip", $"{name}.zip");
        });
    }

    public sealed record UrlRequest(string? Url, string? Beats, bool Stems, string? Vocals);

    static string VocalOption(string? v) => v is "notes" or "lyrics" ? v : "flux";
    public sealed record ImportRequest(string Path);
    public sealed record LyricsRequest(string? Text);

    const string HumanId = "human";

    /// <summary>The imported human map's playable difficulties on the analysis grid, or null.</summary>
    static List<DifficultyMap>? HumanDifficulties(SongStore store, string id, SongAnalysis a)
    {
        var dir = store.ReferenceDir(id);
        if (!Directory.Exists(dir)) return null;
        var human = Abeat.Core.Formats.MapReader.Read(dir);
        return [.. human.Difficulties.Where(d => d.Notes.Count > 0).Select(d => Generations.OnGrid(d, human.Bpm, 0, a))];
    }

    static List<DifficultyMap>? ReadVersion(SongStore store, string id, string version, SongAnalysis a) =>
        version == HumanId ? HumanDifficulties(store, id, a)
        : Generations.Get(store, id, version) is { } g ? Generations.Read(store, id, g, a) : null;

    /// <summary>A generation for the versions list, with note-timing F1 against the human map per difficulty.</summary>
    static object VersionDto(SongStore store, string id, SongAnalysis a, GenerationMeta g, List<DifficultyMap>? human)
    {
        Dictionary<string, double>? vsHuman = null;
        if (human != null)
        {
            vsHuman = [];
            foreach (var d in Generations.Read(store, id, g, a))
                if (human.FirstOrDefault(h => h.Difficulty == d.Difficulty) is { } h)
                    vsHuman[d.Difficulty.ToString()] = Math.Round(MapComparer.Compare(h, a.Tempo.Bpm, d, a.Tempo.Bpm, 0).F1, 3);
        }
        return new { g.Id, kind = "abeat", g.CreatedUtc, g.Draft, g.AppVersion, g.Difficulties, vsHuman };
    }

    static object ComparisonDto(Comparison c) => new
    {
        f1 = c.F1, c.Precision, c.Recall, c.OffsetMs, c.DirectionDistance, c.PositionDistance,
        aDoubles = c.GeneratedDoubles, bDoubles = c.HumanDoubles, aNps = c.GeneratedNps, bNps = c.HumanNps,
    };

    static object DifficultyDto(SongAnalysis a, DifficultyMap d, FlowReport report, IReadOnlyList<RhythmEvent>? events) => new
    {
        name = d.Difficulty.ToString(),
        njs = d.NoteJumpSpeed,
        offset = d.NoteJumpOffset,
        jumpDistance = Math.Round(MapGenerator.JumpDistance(a.Tempo.Bpm, d.NoteJumpSpeed, d.NoteJumpOffset), 2),
        notes = d.Notes.Select(n => new { b = n.Beat, x = n.X, y = n.Y, c = (int)n.Hand, d = (int)n.Direction, a = n.AngleOffset }),
        arcs = d.Arcs.Select(x => new { b = x.Beat, x = x.X, y = x.Y, c = (int)x.Hand, tb = x.TailBeat, tx = x.TailX, ty = x.TailY }),
        bombs = d.Bombs.Select(n => new { b = n.Beat, x = n.X, y = n.Y }),
        walls = d.Obstacles.Select(o => new { b = o.Beat, d = o.Duration, x = o.X, y = o.Y, w = o.Width, h = o.Height }),
        events = events?.Select(e => new { b = e.Beat, s = Math.Round(e.Strength, 3), dbl = e.IsDouble, layer = e.Layer }),
        report = new
        {
            report.Notes, report.Nps, report.PeakNps, report.Resets, report.BombResets, report.VisionBlocks,
            report.Crossovers, report.HandClashes, report.WallClashes, report.BombHits, report.MeanCost, report.FlowScore, report.LeftShare, lights = d.Lights.Count,
            movement = new
            {
                strain = Math.Round(report.Movement.StrainP90, 2), rank = Math.Round(report.Movement.MovementRank, 1),
                playsLike = report.Movement.MovementDifficulty.ToString(), spikes = report.StrainSpikes,
                angle = Math.Round(report.Movement.AngleMean, 1), travel = Math.Round(report.Movement.TravelMean, 2),
                sharpTurns = Math.Round(report.Movement.SharpTurns, 3),
            },
            issues = report.Issues.Select(i => new { b = i.Beat, hand = (int)i.Hand, kind = i.Kind.ToString(), cost = Math.Round(i.Cost, 2) }),
        },
    };
}
