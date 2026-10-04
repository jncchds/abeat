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
      --roformer               vocals from BS-RoFormer (cleaner, slow on CPU; worker extra "roformer")
      --vocals flux|notes|lyrics   vocal onsets: spectral flux (default), sung notes (pitch), or
                               lyric syllables (Whisper + forced alignment, worker extra "lyrics")
      --pitched flux|notes     other/bass onsets: spectral flux (default) or notes transcribed by
                               basic-pitch (pitch drives rows, note lengths drive arcs)
      --lyrics <file.txt>      lyrics to align for --vocals lyrics (repeats written out); skips Whisper
      --bpm <x>                override detected BPM (constant tempo)
      --tempo auto|constant|variable   tempo changes for drifting (live) songs; auto = only when one
                               BPM can't follow the song
      --reanalyze              ignore cached analysis
      --modes <l>              extra game modes: onesaber,90,360 (same difficulties as Standard)
      --environment default|pyro   pyro = PyroEnvironment with a v3 group lightshow
      --no-lights, --no-walls, --no-zip
  abeat analyze <audio> [-o <work dir>] [--beats ..] [--no-stems] [--vocals ..] [--bpm x] [--tempo ..]
  abeat check <map folder | zip | Info.dat>   flow report for any map (compare with human maps)
  abeat settings [file.json]                   write default generator settings to edit
  abeat fetch-maps [--count 20] [--per-mapper 2] [-o work/beatsaver]   curated BeatSaver maps (no mods), top-rated + recent
  abeat compare <map.zip|folder> [--settings f]  re-map the map's own song and compare with the human map
  abeat bench [dir] [--settings f]             compare every map zip in dir (default work/beatsaver), write bench.csv
  abeat movement [map | dir] [--csv f] [--write-prior f]   hand movement per difficulty: swing angle changes, saber-tip
                                               travel and strain between consecutive swings; a dir of maps
                                               (default work/beatsaver) also prints per-difficulty tables;
                                               --write-prior learns the generator's movement prior from them
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
        "movement" => Movement(opts),
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
    o.Get("vocals") ?? "flux",
    o.Get("tempo") ?? "auto",
    o.Get("pitched") ?? "flux",
    o.Has("roformer") ? "roformer" : "demucs");

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
    if (o.Get("modes") is { } modes) s = s with { Modes = ParseModes(modes) };
    if (o.Get("environment") is { } env) s = s with { Environment = env.Equals("pyro", StringComparison.OrdinalIgnoreCase) ? "Pyro" : "Default" };

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
    foreach (var d in result.Extra)
    {
        string what = d.Map.Characteristic == Characteristic.OneSaber
            ? $"notes {d.Map.Notes.Count,5}  flow {d.Report.FlowScore,5:0.0}  resets {d.Report.Resets}  strain {d.Report.Movement.StrainP90:0.0} (plays like {d.Report.Movement.MovementDifficulty})"
            : $"rotations {d.Map.Rotations.Count,3} ({d.Map.Rotations.Sum(r => Math.Abs(r.Degrees)):0}° turned)";
        Console.WriteLine($"  {d.Map.Characteristic,-9} {d.Map.Difficulty,-10} {what}");
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
    string drift = !a.TempoMap.IsConstant ? $", variable: {a.TempoMap.Points.Count - 1} tempo changes, {a.TempoMap.Points.Min(p => p.Bpm):0.#}-{a.TempoMap.Points.Max(p => p.Bpm):0.#} BPM"
        : a.Tempo.Stable ? "" : ", UNSTABLE TEMPO: notes may drift";
    Console.WriteLine($"  bpm {a.Tempo.Bpm:0.##} ({a.Tempo.Backend}, fit residual {a.Tempo.ResidualMs:0.0} ms{drift})");
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
        var r = FlowAnalyzer.Analyze(d, map.Tempo, LoadSettings(o).Weights);
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
    if (diffs.Any(d => d.BpmChanges > 0 && d.TempoChanges is null)) { Console.Error.WriteLine($"{input}: skipped (unsupported BPM changes)"); return null; }

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
    var results = diffs.Select(h => MapComparer.Compare(h, human.Tempo,
        gen.Map.Difficulties.First(g => g.Difficulty == h.Difficulty), a.TempoMap, a.Audio.PadSec, analysis: a)).ToList();

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
    Console.WriteLine($"{"difficulty",-11} {"n",3} {"F1",5} {"P",5} {"R",5} {"offset",7} {"nps gen/hum",12} {"flow gen/hum",13} {"resets g/h",11} {"dirΔ",5} {"posΔ",5} {"strain g/h",11} {"travel g/h",11} {"angle g/h",10} {"sharp g/h",10} {"above g/h",10} {"repeat g/h",10}");
    foreach (var g in rows.GroupBy(r => r.c.Difficulty).OrderBy(g => g.Key).Append(rows.GroupBy(_ => (DifficultyName)99).First()))
    {
        var c = g.Select(r => r.c).ToList();
        string name = (int)g.Key == 99 ? "ALL" : g.Key.ToString();
        Console.WriteLine($"{name,-11} {c.Count,3} {c.Average(x => x.F1),5:0.00} {c.Average(x => x.Precision),5:0.00} {c.Average(x => x.Recall),5:0.00} " +
            $"{c.Average(x => x.OffsetMs),5:0} ms {c.Average(x => x.GeneratedNps),5:0.0}/{c.Average(x => x.HumanNps),-6:0.0} " +
            $"{c.Average(x => x.GeneratedFlow),6:0.0}/{c.Average(x => x.HumanFlow),-6:0.0} {c.Average(x => x.GeneratedResets),5:0.0}/{c.Average(x => x.HumanResets),-5:0.0} " +
            $"{c.Average(x => x.DirectionDistance),5:0.00} {c.Average(x => x.PositionDistance),5:0.00} " +
            $"{c.Average(x => x.GeneratedMovement.StrainP90),5:0.0}/{c.Average(x => x.HumanMovement.StrainP90),-5:0.0} " +
            $"{c.Average(x => x.GeneratedMovement.TravelMean),5:0.00}/{c.Average(x => x.HumanMovement.TravelMean),-5:0.00} " +
            $"{c.Average(x => x.GeneratedMovement.AngleMean),4:0}/{c.Average(x => x.HumanMovement.AngleMean),-5:0} " +
            $"{c.Average(x => x.GeneratedMovement.SharpTurns),4:P0}/{c.Average(x => x.HumanMovement.SharpTurns),-5:P0} " +
            $"{c.Average(x => x.GeneratedMovement.AboveLevel),4:P0}/{c.Average(x => x.HumanMovement.AboveLevel),-5:P0} " +
            $"{c.Average(x => x.GeneratedRepetition?.Same ?? 0),4:P0}/{c.Average(x => x.HumanRepetition?.Same ?? 0),-5:P0}");
    }
    var csv = Path.Combine(dir, "bench.csv");
    File.WriteAllLines(csv, rows.Select(r => string.Join(',', r.map, r.c.Difficulty, r.humanBpm, r.c.F1.ToString("0.000"), r.c.Precision.ToString("0.000"),
        r.c.Recall.ToString("0.000"), r.c.OffsetMs.ToString("0.0"), r.c.GeneratedNps.ToString("0.00"), r.c.HumanNps.ToString("0.00"),
        r.c.GeneratedFlow.ToString("0.0"), r.c.HumanFlow.ToString("0.0"), r.c.GeneratedResets, r.c.HumanResets,
        r.c.DirectionDistance.ToString("0.000"), r.c.PositionDistance.ToString("0.000"),
        r.c.GeneratedMovement.StrainP90.ToString("0.00"), r.c.HumanMovement.StrainP90.ToString("0.00"),
        r.c.GeneratedMovement.TravelMean.ToString("0.000"), r.c.HumanMovement.TravelMean.ToString("0.000"),
        r.c.GeneratedMovement.AngleMean.ToString("0.0"), r.c.HumanMovement.AngleMean.ToString("0.0")))
        .Prepend("map,difficulty,human_bpm,f1,precision,recall,offset_ms,gen_nps,human_nps,gen_flow,human_flow,gen_resets,human_resets,dir_dist,pos_dist,gen_strain,human_strain,gen_travel,human_travel,gen_angle,human_angle"));
    Console.WriteLine($"\nper-map rows: {Path.GetFullPath(csv)}");
    return 0;
}

static int Movement(Options o)
{
    string input = o.Positional.FirstOrDefault() ?? Path.Combine("work", "beatsaver");
    var paths = Directory.Exists(input) && !File.Exists(Path.Combine(input, "Info.dat"))
        ? Directory.EnumerateFiles(input, "*.zip").Order().ToList()
        : [input];
    var all = new List<(string map, MovementReport r)>();
    foreach (var path in paths)
    {
        MapSet map;
        try { map = MapReader.Read(path); }
        catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
        if (map.Difficulties.Any(d => d.BpmChanges > 0)) { Console.Error.WriteLine($"{path}: skipped (BPM changes)"); continue; }
        Console.WriteLine($"{map.SongAuthor} - {map.SongName}  [{map.LevelAuthor}]  bpm {map.Bpm}");
        foreach (var d in map.Difficulties.Where(d => d.Notes.Count > 0).OrderBy(d => d.Difficulty))
        {
            var r = MovementAnalyzer.Analyze(d, map.Tempo);
            all.Add((Path.GetFileNameWithoutExtension(path), r));
            Console.WriteLine($"  {r}");
        }
    }
    if (all.Count == 0) return 1;
    if (o.Get("csv") is { } csv)
    {
        File.WriteAllLines(csv, all.SelectMany(a => a.r.Items.Select(m => string.Join(',', a.map, a.r.Difficulty, m.Hand, m.Beat.ToString("0.###"),
            m.GapSec.ToString("0.####"), m.Angle.ToString("0.#"), m.Travel.ToString("0.###"), m.Distance.ToString("0.###"), m.From, m.To,
            m.Strain.ToString("0.###")))).Prepend("map,difficulty,hand,beat,gap_sec,angle,travel,distance,from,to,strain"));
        Console.WriteLine($"moves: {Path.GetFullPath(csv)}");
    }
    if (o.Get("write-prior") is { } prior)
    {
        var json = MovementPrior.Build(all.Select(a => a.r), $"{paths.Count} maps from {input}");
        File.WriteAllText(prior, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"movement prior: {Path.GetFullPath(prior)}");
    }
    if (paths.Count < 2) return 0;

    var byDiff = all.GroupBy(a => a.r.Difficulty).OrderBy(g => g.Key).ToList();
    Console.WriteLine();
    Console.WriteLine("per difficulty (mean over maps; strain = effective swings per second, see MovementAnalyzer.Strain)");
    Console.WriteLine($"{"difficulty",-11} {"maps",4} {"angle",6} {"a p90",6} {"travel",7} {"t p90",6} {"speed50",8} {"speed90",8} {"strain50",9} {"strain90",9} {"strain98",9} {"sharp",6} {"above",6} {"rank",5}");
    foreach (var g in byDiff)
    {
        var r = g.Select(x => x.r).ToList();
        Console.WriteLine($"{g.Key,-11} {r.Count,4} {r.Average(x => x.AngleMean),5:0}° {r.Average(x => x.AngleP90),5:0}° {r.Average(x => x.TravelMean),7:0.00} {r.Average(x => x.TravelP90),6:0.00} " +
            $"{r.Average(x => x.SpeedP50),8:0.00} {r.Average(x => x.SpeedP90),8:0.00} {r.Average(x => x.StrainP50),9:0.00} {r.Average(x => x.StrainP90),9:0.00} {r.Average(x => x.StrainP98),9:0.00} " +
            $"{r.Average(x => x.SharpTurns),6:P0} {r.Average(x => x.AboveLevel),6:P0} {r.Average(x => x.MovementRank),5:0.0}");
    }

    // how often each kind of move (angle x travel) appears per difficulty, pooled over all maps
    double[] travelEdges = [0.25, 0.75, 1.25, 1.75, 2.5, double.PositiveInfinity];
    string[] travelNames = ["<.25", ".25-.75", ".75-1.25", "1.25-1.75", "1.75-2.5", ">2.5"];
    int[] angles = [0, 45, 90, 135, 180];
    Console.WriteLine();
    Console.WriteLine("share of moves by turn angle (rows) and tip travel in cells (columns), and median gap in seconds");
    foreach (var g in byDiff)
    {
        var moves = g.SelectMany(x => x.r.Items).ToList();
        Console.WriteLine($"{g.Key} ({moves.Count} moves)");
        Console.WriteLine($"  {"angle",5} " + string.Join(" ", travelNames.Select(n => $"{n,15}")));
        foreach (int a in angles)
        {
            var row = moves.Where(m => Math.Abs(m.Angle - a) < 22.5).ToList();
            Console.Write($"  {a,4}° ");
            double lo = 0;
            foreach (double hi in travelEdges)
            {
                var cell = row.Where(m => m.Travel >= lo && m.Travel < hi).Select(m => m.GapSec).Order().ToArray();
                Console.Write(cell.Length == 0 ? $"{"",15} " : $"{100.0 * cell.Length / moves.Count,6:0.0}% {MovementAnalyzer.Percentile(cell, 0.5),5:0.00}s ");
                lo = hi;
            }
            Console.WriteLine();
        }
    }
    return 0;
}

static List<string> ParseModes(string list) => [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(m => m.ToLowerInvariant() switch
    {
        "onesaber" or "one" => Characteristic.OneSaber,
        "90" or "90degree" => Characteristic.Degree90,
        "360" or "360degree" => Characteristic.Degree360,
        "all" => throw new ArgumentException("use --modes onesaber,90,360"),
        _ => throw new ArgumentException($"unknown mode '{m}' (onesaber, 90, 360)"),
    })];

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
    static readonly HashSet<string> Flags = ["roformer", "no-stems", "reanalyze", "no-lights", "no-walls", "no-zip", "verbose", "v"];
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
