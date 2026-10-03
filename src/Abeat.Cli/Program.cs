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
abeat - automatic Beat Saber map generator

usage:
  abeat generate <audio file | analysis dir> [options]
      -o, --out <dir>          output folder (default: ./out/<Artist - Title>)
      -d, --difficulties <l>   comma list: easy,normal,hard,expert,expertplus|all (default: expert,expertplus)
      --density <x>            note density multiplier (default 1.0)
      --seed <n>               variation seed (default 1)
      --beam <n>               beam width (default 64)
      --settings <file.json>   load generator settings (see `abeat settings`)
      --work <dir>             analysis work dir (default: ./work/<file name>)
      --beats auto|librosa|beat_this   beat tracker (default auto)
      --stems                  separate stems with demucs (slow on CPU, better rhythm choices)
      --bpm <x>                override detected BPM
      --reanalyze              ignore cached analysis
      --no-lights, --no-walls, --no-zip
  abeat analyze <audio> [-o <work dir>] [--beats ..] [--stems] [--bpm x]
  abeat check <map folder | zip | Info.dat>   flow report for any map (compare with human maps)
  abeat settings [file.json]                   write default generator settings to edit
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
    if (!File.Exists(input)) throw new FileNotFoundException($"not found: {input}");

    string work = o.Get("work") ?? Path.Combine("work", Path.GetFileNameWithoutExtension(input));
    if (!o.Has("reanalyze") && File.Exists(Path.Combine(work, "analysis.json")))
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
    var sw = Stopwatch.StartNew();
    var a = await runner.AnalyzeAsync(input, work, AnalysisOpts(o), line => Console.Error.WriteLine(line));
    Console.Error.WriteLine($"analysis took {sw.Elapsed.TotalSeconds:0.0}s");
    return a;
}

static AnalysisOptions AnalysisOpts(Options o) => new(
    o.Get("beats") ?? "auto",
    o.Has("stems"),
    o.Get("bpm") is { } b ? double.Parse(b, CultureInfo.InvariantCulture) : null);

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
        var r = FlowAnalyzer.Analyze(d, map.Bpm);
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
    static readonly HashSet<string> Flags = ["stems", "reanalyze", "no-lights", "no-walls", "no-zip", "verbose", "v"];
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
