using System.Reflection;
using System.Text.Json;
using Abeat.Core.Analysis;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;
using Abeat.Core.Packaging;

namespace Abeat.Web;

/// <summary>One saved generation of a song: data/songs/{id}/generations/{genId}/ holds generation.json,
/// settings.json, the map folder and its zip.</summary>
public sealed record GenerationMeta
{
    public required string Id { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Made by auto-regenerate: the next auto-regenerate replaces it instead of appending.</summary>
    public bool Draft { get; init; }
    public string AppVersion { get; init; } = "";
    /// <summary>Grid the map was written on; older generations are re-timed if the song was re-analyzed.</summary>
    public double Bpm { get; init; }
    public double PadSec { get; init; }
    public List<string> Difficulties { get; init; } = [];
}

/// <summary>Generation history of a song. The newest generation is what "the map" (download, ArcViewer)
/// means when no generation is named.</summary>
public static class Generations
{
    public static readonly string AppVersion =
        typeof(MapGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
    static readonly JsonSerializerOptions Json = GeneratorSettings.JsonOptions;

    static string Root(SongStore store, string id) => Path.Combine(store.Dir(id), "generations");
    public static string Dir(SongStore store, string id, string gen) => Path.Combine(Root(store, id), gen);
    public static string MapDir(SongStore store, string id, string gen) => Path.Combine(Dir(store, id, gen), "map");

    static bool ValidId(string gen) => gen.Length > 0 && gen.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Newest first. Moves a pre-history single map (songs/{id}/map) into the history once.</summary>
    public static List<GenerationMeta> List(SongStore store, string id)
    {
        MigrateLegacy(store, id);
        var root = Root(store, id);
        if (!Directory.Exists(root)) return [];
        var list = new List<GenerationMeta>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var f = Path.Combine(dir, "generation.json");
            if (!File.Exists(f)) continue;
            try { list.Add(JsonSerializer.Deserialize<GenerationMeta>(File.ReadAllText(f), Json)!); }
            catch { /* skip broken entries */ }
        }
        return [.. list.OrderByDescending(g => g.CreatedUtc)];
    }

    public static GenerationMeta? Get(SongStore store, string id, string gen) =>
        ValidId(gen) ? List(store, id).FirstOrDefault(g => g.Id == gen) : null;

    public static GenerationMeta? Latest(SongStore store, string id) => List(store, id).FirstOrDefault();

    /// <summary>Saves a generation. A draft replaces the newest generation if that one is a draft too,
    /// so auto-regenerate while tweaking settings does not flood the history.</summary>
    public static GenerationMeta Save(SongStore store, string id, SongAnalysis a, GeneratorSettings s, GenerationResult r, bool draft)
    {
        if (draft && Latest(store, id) is { Draft: true } old) Delete(store, id, old.Id);
        var now = DateTime.UtcNow;
        var meta = new GenerationMeta
        {
            Id = $"{now:yyyyMMdd-HHmmss-fff}",
            CreatedUtc = now,
            Draft = draft,
            AppVersion = AppVersion,
            Bpm = a.Tempo.Bpm,
            PadSec = a.Audio.PadSec,
            Difficulties = [.. r.Difficulties.Select(d => d.Map.Difficulty.ToString())],
        };
        var dir = Dir(store, id, meta.Id);
        Directory.CreateDirectory(dir);
        MapPackager.Write(r.Map, a, MapDir(store, id, meta.Id), zip: true);
        s.Save(Path.Combine(dir, "settings.json"));
        File.WriteAllText(Path.Combine(dir, "generation.json"), JsonSerializer.Serialize(meta, Json));
        return meta;
    }

    public static bool Delete(SongStore store, string id, string gen)
    {
        if (!ValidId(gen)) return false;
        var dir = Dir(store, id, gen);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    public static GeneratorSettings? Settings(SongStore store, string id, string gen)
    {
        var f = Path.Combine(Dir(store, id, gen), "settings.json");
        return ValidId(gen) && File.Exists(f) ? GeneratorSettings.Load(f) : null;
    }

    public static string? Zip(SongStore store, string id, string gen) =>
        ValidId(gen) && File.Exists(MapDir(store, id, gen) + ".zip") ? MapDir(store, id, gen) + ".zip" : null;

    /// <summary>The generation's difficulties on the song's current beat grid.</summary>
    public static List<DifficultyMap> Read(SongStore store, string id, GenerationMeta g, SongAnalysis a)
    {
        var set = MapReader.Read(MapDir(store, id, g.Id));
        return [.. set.Difficulties.Where(d => d.Notes.Count > 0).Select(d => OnGrid(d, set.Bpm, g.PadSec, a))];
    }

    /// <summary>Re-times a map written at <paramref name="bpm"/> with <paramref name="padSec"/> of padding in
    /// front of the original audio (0 for human maps) onto the analysis grid.</summary>
    public static DifficultyMap OnGrid(DifficultyMap m, double bpm, double padSec, SongAnalysis a)
    {
        if (Math.Abs(bpm - a.Tempo.Bpm) < 1e-9 && Math.Abs(padSec - a.Audio.PadSec) < 1e-6) return m;
        double B(double beat) => a.SecondsToBeat(beat * 60 / bpm - padSec + a.Audio.PadSec);
        double D(double dur) => dur * a.Tempo.Bpm / bpm;
        return new DifficultyMap
        {
            Difficulty = m.Difficulty,
            NoteJumpSpeed = m.NoteJumpSpeed,
            NoteJumpOffset = m.NoteJumpOffset,
            Notes = [.. m.Notes.Select(n => n with { Beat = B(n.Beat) })],
            Bombs = [.. m.Bombs.Select(n => n with { Beat = B(n.Beat) })],
            Arcs = [.. m.Arcs.Select(x => x with { Beat = B(x.Beat), TailBeat = B(x.TailBeat) })],
            Chains = [.. m.Chains.Select(x => x with { Beat = B(x.Beat), TailBeat = B(x.TailBeat) })],
            Obstacles = [.. m.Obstacles.Select(o => o with { Beat = B(o.Beat), Duration = D(o.Duration) })],
            Lights = [.. m.Lights.Select(l => l with { Beat = B(l.Beat) })],
        };
    }

    static void MigrateLegacy(SongStore store, string id)
    {
        var legacy = Path.Combine(store.Dir(id), "map");
        if (!File.Exists(Path.Combine(legacy, "Info.dat")) || store.Analysis(id) is not { } a) return;
        var created = File.GetLastWriteTimeUtc(Path.Combine(legacy, "Info.dat"));
        var set = MapReader.Read(legacy);
        var meta = new GenerationMeta
        {
            Id = $"{created:yyyyMMdd-HHmmss-fff}",
            CreatedUtc = created,
            AppVersion = "older",
            Bpm = set.Bpm,
            PadSec = a.Audio.PadSec, // written with the analysis of that time; assume it is unchanged
            Difficulties = [.. set.Difficulties.Where(d => d.Notes.Count > 0).Select(d => d.Difficulty.ToString())],
        };
        var dir = Dir(store, id, meta.Id);
        Directory.CreateDirectory(dir);
        Directory.Move(legacy, Path.Combine(dir, "map"));
        if (File.Exists(legacy + ".zip")) File.Move(legacy + ".zip", Path.Combine(dir, "map.zip"));
        store.Settings(id).Save(Path.Combine(dir, "settings.json"));
        File.WriteAllText(Path.Combine(dir, "generation.json"), JsonSerializer.Serialize(meta, Json));
    }
}
