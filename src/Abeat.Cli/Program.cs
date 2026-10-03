using System.Diagnostics;
using System.Globalization;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;
using Abeat.Core.Packaging;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
ABeat by CHDS - automatic Beat Saber map generator

usage:
  abeat generate <audio file | YouTube URL | analysis dir> [options]
      -o, --out <dir>          output folder (default: ./out/<Artist - Title>)
      -d, --difficulties <l>   comma list: easy,normal,hard,expert,expertplus|all (default: expert,expertplus)
      --density <x>            note density multiplier (default 1.0)
      --seed <n>               variation seed (default 1)
      --beam <n>               beam width (default 64)
      --settings <file.json>   load generator settings (see `abeat settings`)
      --work <dir>             analysis work dir (default: ./work/<file name>)
      --beats auto|librosa|beat_this   beat tracker (default auto)
      --no-stems               skip Demucs stem separation (faster; vocals not isolated)
      --vocals flux|notes|lyrics   vocal onsets: spectral flux (default), sung notes (pitch), or
                               lyric syllables (Whisper + forced alignment, worker extra "lyrics")
      --lyrics <file.txt>      lyrics to align for --vocals lyrics (repeats written out); skips Whisper
      --bpm <x>                override detected BPM
      --reanalyze              ignore cached analysis
      --no-lights, --no-walls, --no-zip
  abeat analyze <audio> [-o <work dir>] [--beats ..] [--no-stems] [--vocals ..] [--bpm x]
  abeat check <map folder | zip | Info.dat>   flow report for any map (compare with human maps)
  abeat settings [file.json]                   write default generator settings to edit
  abeat fetch-maps [--count 20] [--per-mapper 2] [-o work/beatsaver]   curated BeatSaver maps (no mods), top-rated + recent
  abeat compare <map.zip|folder> [--settings f]  re-map the map's own song and compare with the human map
  abeat bench [dir] [--settings f]             compare every map zip in dir (default work/beatsaver), write bench.csv
  abeat synth <out.wav>                        synthetic test track (128 BPM)
""";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

try
{
    var opts = Options.Parse(args.Skip(1));
    return args[0] switch
    {
        "generate" => await Generate(opts),
        "analyze" => await Analyze(opts),
        "check" => Check(opts),
        "settings" => WriteSettings(opts),
        "synth" => await Synth(opts),
        "fetch-maps" => await FetchMaps(opts),
        "compare" => await CompareOne(opts),
        "bench" => await Bench(opts),
        _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
    };
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
{
    return Fail(e.Message);
}

static int Fail(string msg)
{
    Console.Error.WriteLine($"error: {msg}");
    return 1;
}

static async Task<SongAnalysis> GetAnalysis(Options o)
{
    string input = o.Positional.FirstOrDefault() ?? throw new ArgumentException("missing input");
    if (Directory.Exists(input) && File.Exists(Path.Combine(input, "analysis.json")))
        return SongAnalysis.Load(input);
    bool url = AnalysisRunner.IsUrl(input);
    if (!url && !File.Exists(input)) throw new FileNotFoundException($"not found: {input}");

    string work = o.Get("work") ?? Path.Combine("work", url ? UrlWorkName(input) : Path.GetFileNameWithoutExtension(input));
    if (url && !o.Has("reanalyze") && File.Exists(Path.Combine(work, "analysis.json")))
    {
        Console.Error.WriteLine($"using cached analysis in {work} (--reanalyze to redo)");
        return SongAnalysis.Load(work);
    }
    if (!url && !o.Has("reanalyze") && File.Exists(Path.Combine(work, "analysis.json")))
    {
        var cached = SongAnalysis.Load(work);
        if (Path.GetFullPath(cached.Source.Path) == Path.GetFullPath(input)
            && File.GetLastWriteTimeUtc(Path.Combine(work, "analysis.json")) > File.GetLastWriteTimeUtc(input))
        {
            Console.Error.WriteLine($"using cached analysis in {work} (--reanalyze to redo)");
            return cached;
        }
    }
    var runner = new AnalysisRunner();
    string? lyricsFile = o.Get("lyrics");
    var sw = Stopwatch.StartNew();
    var a = await runner.AnalyzeAsync(input, work, AnalysisOpts(o), line => Console.Error.WriteLine(line), lyricsFile: lyricsFile);
    Console.Error.WriteLine($"analysis took {sw.Elapsed.TotalSeconds:0.0}s");
    return a;
}

static string UrlWorkName(string url)
{
    var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]v=([\w-]{6,})|youtu\.be/([\w-]{6,})");
    if (m.Success) return "yt-" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    return "url-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..10].ToLowerInvariant();
}

static AnalysisOptions AnalysisOpts(Options o) => new(
    o.Get("beats") ?? "auto",
    !o.Has("no-stems"),
    o.Get("bpm") is { } b ? double.Parse(b, CultureInfo.InvariantCulture) : null,
    o.Get("vocals") ?? "flux");

static async Task<int> Generate(Options o)
{
    var a = await GetAnalysis(o);
    var s = o.Get("settings") is { } sf ? GeneratorSettings.Load(sf) : new GeneratorSettings();
    if (o.Get("difficulties") is { } dl) s = s with { Difficulties = ParseDifficulties(dl) };
    if (o.Get("density") is { } den) s = s with { Density = double.Parse(den, CultureInfo.InvariantCulture) };
    if (o.Get("seed") is { } seed) s = s with { Seed = int.Parse(seed) };
    if (o.Get("beam") is { } beam) s = s with { BeamWidth = int.Parse(beam) };
    if (o.Has("no-lights")) s = s with { Lights = false };
    if (o.Has("no-walls")) s = s with { Walls = false };

    PrintAnalysisSummary(a);
    var sw = Stopwatch.StartNew();
    var result = MapGenerator.Generate(a, s);
    Console.WriteLine($"generated in {sw.Elapsed.TotalSeconds:0.00}s");
    foreach (var d in result.Difficulties)
    {
        double jd = MapGenerator.JumpDistance(a.Tempo.Bpm, d.Map.NoteJumpSpeed, d.Map.NoteJumpOffset);
        Console.WriteLine($"  {d.Report}  njs {d.Map.NoteJumpSpeed} jd {jd:0.0}");
        var layers = d.Events.GroupBy(e => e.Layer).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {100.0 * g.Count() / d.Events.Count:0}%");
        Console.WriteLine($"             notes led by: {string.Join(", ", layers)}  (hand roles kept {d.HandRoleShare:P0})");
    }

    string outDir = o.Get("out") ?? Path.Combine("out", MapPackager.FolderName(result.Map));
    string written = MapPackager.Write(result.Map, a, outDir, zip: !o.Has("no-zip"));
    Console.WriteLine($"map folder: {Path.GetFullPath(outDir)}");
    if (written != outDir) Console.WriteLine($"zip:        {Path.GetFullPath(written)}  (drag into https://allpoland.github.io/ArcViewer/ to preview)");
    return 0;
}

static async Task<int> Analyze(Options o)
{
    var a = await GetAnalysis(o.With("reanalyze"));
    PrintAnalysisSummary(a);
    return 0;
}

static void PrintAnalysisSummary(SongAnalysis a)
{
    Console.WriteLine($"{a.Source.Artist} - {a.Source.Title}");
    Console.WriteLine($"  bpm {a.Tempo.Bpm:0.##} ({a.Tempo.Backend}, fit residual {a.Tempo.ResidualMs:0.0} ms{(a.Tempo.Stable ? "" : ", UNSTABLE TEMPO: notes may drift")})");
    Console.WriteLine($"  duration {a.Audio.DurationSec:0.0}s, pad {a.Audio.PadSec:0.000}s, layers [{string.Join(", ", a.Layers.Select(kv => $"{kv.Key}:{kv.Value.Count}"))}] ({a.LayerSource})");
    Console.WriteLine($"  sections: {string.Join(" ", a.Sections.Select(s => $"{s.Label}@{s.Start:0}s/{s.Energy:0.00}"))}");
}

static int Check(Options o)
{
    string input = o.Positional.FirstOrDefault() ?? throw new ArgumentException("missing map path");
    var map = MapReader.Read(input);
    Console.WriteLine($"{map.SongAuthor} - {map.SongName}  [{map.LevelAuthor}]  bpm {map.Bpm}");
    foreach (var d in map.Difficulties.OrderBy(d => d.Difficulty))
    {
        var r = FlowAnalyzer.Analyze(d, map.Bpm, LoadSettings(o).Weights);
        Console.WriteLine($"  {r}");
        if (o.Has("verbose"))
            foreach (var i in r.Issues.Take(40))
                Console.WriteLine($"      beat {i.Beat,8:0.###}  {i.Hand,-5} {i.Kind,-11} cost {i.Cost:0.0}");
    }
    return 0;
}

static int WriteSettings(Options o)
{
    string path = o.Positional.FirstOrDefault() ?? "abeat.settings.json";
    var s = new GeneratorSettings
    {
        ProfileOverrides = Enum.GetValues<DifficultyName>().ToDictionary(d => d, DifficultyProfile.Default),
    };
    s.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

static async Task<int> Synth(Options o)
{
    string path = o.Positional.FirstOrDefault() ?? "synth128.wav";
    await new AnalysisRunner().SynthAsync(path, line => Console.Error.WriteLine(line));
    return 0;
}

static async Task<int> FetchMaps(Options o)
{
    int count = int.Parse(o.Get("count") ?? "20");
    string dir = o.Get("out") ?? Path.Combine("work", "beatsaver");
    Directory.CreateDirectory(dir);
    using var bs = new BeatSaverClient();
    var maps = await bs.CuratedAsync(count, int.Parse(o.Get("per-mapper") ?? "2"));
    foreach (var m in maps)
    {
        string path = Path.Combine(dir, $"{m.Id}.zip");
        if (!File.Exists(path)) await bs.DownloadAsync(m, path);
        Console.WriteLine($"{m.Id,-6} {m.Bpm,6:0.##} bpm  nps {m.MaxNps,4:0.0}  score {m.Score:0.000}  {m.Name} [{m.Mapper}]");
    }
    Console.WriteLine($"{maps.Count} maps in {Path.GetFullPath(dir)} (personal use only; do not redistribute)");
    return 0;
}

/// <summary>Extracts the map, analyzes its audio (cached), generates the same difficulties and compares.</summary>
static async Task<(MapSet human, List<Comparison> results)?> Compare(string input, GeneratorSettings s, bool quiet)
{
    string dir = input;
    if (File.Exists(input) && input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input));
        if (!Directory.Exists(dir)) System.IO.Compression.ZipFile.ExtractToDirectory(input, dir);
    }
    var human = MapReader.Read(dir);
    var diffs = human.Difficulties.Where(d => d.Notes.Count > 0).ToList();
    if (diffs.Count == 0) { Console.Error.WriteLine($"{input}: no Standard difficulties"); return null; }
    if (diffs.Any(d => d.BpmChanges > 0)) { Console.Error.WriteLine($"{input}: skipped (BPM changes)"); return null; }

    string song = Path.Combine(dir, human.SongFile);
    string work = Path.Combine(dir, "abeat-work");
    SongAnalysis a;
    if (File.Exists(Path.Combine(work, "analysis.json"))) a = SongAnalysis.Load(work);
    else
    {
        Console.Error.WriteLine($"analyzing {human.SongAuthor} - {human.SongName} ...");
        a = await new AnalysisRunner().AnalyzeAsync(song, work, new AnalysisOptions(), quiet ? null : line => Console.Error.WriteLine(line));
    }
    var gen = MapGenerator.Generate(a, s with { Difficulties = diffs.Select(d => d.Difficulty).ToList() });
    var results = diffs.Select(h => MapComparer.Compare(h, human.Bpm,
        gen.Map.Difficulties.First(g => g.Difficulty == h.Difficulty), a.Tempo.Bpm, a.Audio.PadSec)).ToList();

    double ratio = a.Tempo.Bpm / human.Bpm;
    string bpmNote = Math.Abs(ratio - 1) < 0.005 ? "match" : Math.Abs(ratio - 2) < 0.01 || Math.Abs(ratio - 0.5) < 0.005 ? "octave" : $"x{ratio:0.###}";
    Console.WriteLine($"{human.SongAuthor} - {human.SongName} [{human.LevelAuthor}]  bpm {a.Tempo.Bpm:0.##} vs {human.Bpm:0.##} ({bpmNote})");
    foreach (var r in results) Console.WriteLine($"  {r}");
    return (human, results);
}

static GeneratorSettings LoadSettings(Options o) => o.Get("settings") is { } sf ? GeneratorSettings.Load(sf) : new GeneratorSettings();

static async Task<int> CompareOne(Options o)
{
    string input = o.Positional.FirstOrDefault() ?? throw new ArgumentException("missing map path");
    return await Compare(input, LoadSettings(o), quiet: false) is null ? 1 : 0;
}

static async Task<int> Bench(Options o)
{
    string dir = o.Positional.FirstOrDefault() ?? Path.Combine("work", "beatsaver");
    var s = LoadSettings(o);
    var rows = new List<(string map, double humanBpm, Comparison c)>();
    foreach (var zip in Directory.EnumerateFiles(dir, "*.zip").Order())
    {
        try
        {
            if (await Compare(zip, s, quiet: true) is { } r)
                rows.AddRange(r.results.Select(c => (Path.GetFileNameWithoutExtension(zip), r.human.Bpm, c)));
        }
        catch (Exception e) { Console.Error.WriteLine($"{zip}: {e.Message}"); }
    }
    if (rows.Count == 0) return 1;

    Console.WriteLine();
    Console.WriteLine($"{"difficulty",-11} {"n",3} {"F1",5} {"P",5} {"R",5} {"offset",7} {"nps gen/hum",12} {"flow gen/hum",13} {"resets g/h",11} {"dirΔ",5} {"posΔ",5}");
    foreach (var g in rows.GroupBy(r => r.c.Difficulty).OrderBy(g => g.Key).Append(rows.GroupBy(_ => (DifficultyName)99).First()))
    {
        var c = g.Select(r => r.c).ToList();
        string name = (int)g.Key == 99 ? "ALL" : g.Key.ToString();
        Console.WriteLine($"{name,-11} {c.Count,3} {c.Average(x => x.F1),5:0.00} {c.Average(x => x.Precision),5:0.00} {c.Average(x => x.Recall),5:0.00} " +
            $"{c.Average(x => x.OffsetMs),5:0} ms {c.Average(x => x.GeneratedNps),5:0.0}/{c.Average(x => x.HumanNps),-6:0.0} " +
            $"{c.Average(x => x.GeneratedFlow),6:0.0}/{c.Average(x => x.HumanFlow),-6:0.0} {c.Average(x => x.GeneratedResets),5:0.0}/{c.Average(x => x.HumanResets),-5:0.0} " +
            $"{c.Average(x => x.DirectionDistance),5:0.00} {c.Average(x => x.PositionDistance),5:0.00}");
    }
    var csv = Path.Combine(dir, "bench.csv");
    File.WriteAllLines(csv, rows.Select(r => string.Join(',', r.map, r.c.Difficulty, r.humanBpm, r.c.F1.ToString("0.000"), r.c.Precision.ToString("0.000"),
        r.c.Recall.ToString("0.000"), r.c.OffsetMs.ToString("0.0"), r.c.GeneratedNps.ToString("0.00"), r.c.HumanNps.ToString("0.00"),
        r.c.GeneratedFlow.ToString("0.0"), r.c.HumanFlow.ToString("0.0"), r.c.GeneratedResets, r.c.HumanResets,
        r.c.DirectionDistance.ToString("0.000"), r.c.PositionDistance.ToString("0.000")))
        .Prepend("map,difficulty,human_bpm,f1,precision,recall,offset_ms,gen_nps,human_nps,gen_flow,human_flow,gen_resets,human_resets,dir_dist,pos_dist"));
    Console.WriteLine($"\nper-map rows: {Path.GetFullPath(csv)}");
    return 0;
}

static List<DifficultyName> ParseDifficulties(string list)
{
    if (list.Equals("all", StringComparison.OrdinalIgnoreCase)) return [.. Enum.GetValues<DifficultyName>()];
    return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant() switch
    {
        "e" or "easy" => DifficultyName.Easy,
        "n" or "normal" => DifficultyName.Normal,
        "h" or "hard" => DifficultyName.Hard,
        "x" or "ex" or "expert" => DifficultyName.Expert,
        "x+" or "ex+" or "e+" or "expertplus" or "expert+" => DifficultyName.ExpertPlus,
        _ => throw new ArgumentException($"unknown difficulty '{x}'"),
    }).ToList();
}

sealed class Options
{
    static readonly HashSet<string> Flags = ["no-stems", "reanalyze", "no-lights", "no-walls", "no-zip", "verbose", "v"];
    static readonly Dictionary<string, string> Short = new() { ["o"] = "out", ["d"] = "difficulties", ["v"] = "verbose" };

    public List<string> Positional { get; } = [];
    readonly Dictionary<string, string?> named = [];

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.StartsWith('-') && a.Length > 1)
            {
                string key = a.TrimStart('-');
                if (Short.TryGetValue(key, out var longKey)) key = longKey;
                if (Flags.Contains(key)) o.named[key] = null;
                else if (i + 1 < list.Count) o.named[key] = list[++i];
                else throw new ArgumentException($"option {a} needs a value");
            }
            else o.Positional.Add(a);
        }
        return o;
    }

    public bool Has(string k) => named.ContainsKey(k);
    public string? Get(string k) => named.GetValueOrDefault(k);
    public Options With(string flag) { named[flag] = null; return this; }
}
