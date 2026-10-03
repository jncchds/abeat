using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abeat.Core.Evaluation;

/// <summary>Minimal BeatSaver API client for fetching well-rated human maps to compare against.</summary>
public sealed class BeatSaverClient : IDisposable
{
    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.beatsaver.com/"), Timeout = TimeSpan.FromSeconds(60) };

    public BeatSaverClient()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ABeat/0.1 (+https://github.com/jncchds/abeat)");
    }

    public sealed record MapInfo(string Id, string Name, double Bpm, double Duration, string Mapper, double Score, string DownloadUrl, double MaxNps);

    /// <summary>Curated Standard maps that need no mods (no Noodle / Mapping Extensions, no AI maps) and
    /// have an Expert or Expert+ difficulty with a playable density. Alternates between the highest-rated
    /// and the most recently curated lists, with at most <paramref name="perMapper"/> maps per mapper, so the
    /// sample (and the style prior learned from it) is not dominated by one mapper or one era.</summary>
    public async Task<List<MapInfo>> CuratedAsync(int count, int perMapper = 2, double minNps = 2.5, double maxNps = 9, CancellationToken ct = default)
    {
        var result = new List<MapInfo>();
        var seen = new HashSet<string>();
        var mappers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] sorts = ["Rating", "Curated"];
        for (int page = 0; page < 40 && result.Count < count; page++)
        foreach (var sort in sorts)
        {
            if (result.Count >= count) break;
            var resp = await http.GetFromJsonAsync<SearchResponse>($"search/text/{page}?sortOrder={sort}&curated=true", Json, ct);
            if (resp?.Docs is not { Count: > 0 } docs) continue;
            foreach (var d in docs)
            {
                if (!seen.Add(d.Id)) continue;
                if (mappers.GetValueOrDefault(d.Metadata.LevelAuthorName) >= perMapper) continue;
                var v = d.Versions.FirstOrDefault();
                if (v == null || d.Automapper || d.DeclaredAi is not (null or "None")) continue;
                var std = v.Diffs.Where(x => x.Characteristic == "Standard").ToList();
                if (std.Any(x => x.Ne || x.Me)) continue;
                var top = std.Where(x => x.Difficulty is "Expert" or "ExpertPlus").ToList();
                if (top.Count == 0) continue;
                double nps = top.Max(x => x.Nps);
                if (nps < minNps || nps > maxNps) continue;
                result.Add(new MapInfo(d.Id, d.Name, d.Metadata.Bpm, d.Metadata.Duration, d.Metadata.LevelAuthorName, d.Stats.Score, v.DownloadUrl, nps));
                mappers[d.Metadata.LevelAuthorName] = mappers.GetValueOrDefault(d.Metadata.LevelAuthorName) + 1;
                if (result.Count >= count) break;
            }
            await Task.Delay(300, ct); // be polite to the API
        }
        return result;
    }

    public async Task DownloadAsync(MapInfo map, string path, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(map.DownloadUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var f = File.Create(path);
        await resp.Content.CopyToAsync(f, ct);
    }

    public void Dispose() => http.Dispose();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed record SearchResponse(List<Doc> Docs);
    sealed record Doc(string Id, string Name, Meta Metadata, Stats Stats, List<Version> Versions, bool Automapper, string? DeclaredAi);
    sealed record Meta(double Bpm, double Duration, string LevelAuthorName);
    sealed record Stats(double Score);
    sealed record Version(string DownloadUrl, List<Diff> Diffs);
    sealed record Diff(string Characteristic, string Difficulty, double Nps, bool Ne, bool Me, [property: JsonPropertyName("chroma")] bool Chroma);
}
