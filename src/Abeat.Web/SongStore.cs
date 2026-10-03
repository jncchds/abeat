using System.Collections.Concurrent;
using System.Text.Json;
using Abeat.Core.Analysis;
using Abeat.Core.Generation;

namespace Abeat.Web;

public enum SongStatus { Queued, Analyzing, Generating, Ready, Failed }

public sealed record SongMeta
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    /// <summary>Set when the song was added by link; the worker downloads it.</summary>
    public string? SourceUrl { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public SongStatus Status { get; set; } = SongStatus.Queued;
    public string? Error { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double? Bpm { get; set; }
    public double? DurationSec { get; set; }
    public AnalysisOptions Analysis { get; set; } = new();
    /// <summary>A human-made map of the same song, kept for comparison (data/songs/{id}/reference).</summary>
    public string? ReferenceMapper { get; set; }
    public string? ReferenceUrl { get; set; }
}

/// <summary>File-based storage: data/songs/{id}/ holds source audio, meta.json, work/ (analysis) and
/// settings.json (last generator settings). Survives container restarts via a volume.</summary>
public sealed class SongStore
{
    public string Root { get; }
    readonly ConcurrentDictionary<string, SongMeta> metas = new();
    readonly ConcurrentDictionary<string, SongAnalysis> analysisCache = new();
    readonly ConcurrentDictionary<string, LogBuffer> logs = new();
    static readonly JsonSerializerOptions Json = GeneratorSettings.JsonOptions;

    public SongStore(IConfiguration config)
    {
        Root = Path.GetFullPath(config["ABEAT_DATA"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "abeat"));
        Directory.CreateDirectory(Path.Combine(Root, "songs"));
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(Root, "songs")))
        {
            var f = Path.Combine(dir, "meta.json");
            if (!File.Exists(f)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<SongMeta>(File.ReadAllText(f), Json)!;
                // jobs interrupted by a restart are re-queued by the queue service
                metas[m.Id] = m;
            }
            catch { /* skip broken entries */ }
        }
    }

    public IEnumerable<SongMeta> All => metas.Values.OrderByDescending(m => m.CreatedUtc);
    public SongMeta? Get(string id) => metas.GetValueOrDefault(id);
    public string Dir(string id) => Path.Combine(Root, "songs", id);
    public string WorkDir(string id) => Path.Combine(Dir(id), "work");
    public string ReferenceDir(string id) => Path.Combine(Dir(id), "reference");
    /// <summary>Uploads keep their original file name: the worker falls back to "Artist - Title" names when tags are missing.</summary>
    public string SourcePath(SongMeta m) => Path.Combine(Dir(m.Id), "source", m.FileName);
    public LogBuffer Log(string id) => logs.GetOrAdd(id, _ => new LogBuffer());

    public async Task<SongMeta> AddAsync(string fileName, Stream content, AnalysisOptions options)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        string safe = Path.GetFileName(fileName);
        foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(safe))) safe = "audio" + Path.GetExtension(safe);
        var meta = new SongMeta { Id = id, FileName = safe, Analysis = options, Title = Path.GetFileNameWithoutExtension(safe) };
        Directory.CreateDirectory(Path.GetDirectoryName(SourcePath(meta))!);
        await using (var f = File.Create(SourcePath(meta))) await content.CopyToAsync(f);
        metas[id] = meta;
        Save(meta);
        return meta;
    }

    public SongMeta AddUrl(string url, AnalysisOptions options)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        var meta = new SongMeta { Id = id, FileName = "", SourceUrl = url, Analysis = options, Title = url };
        Directory.CreateDirectory(Dir(id));
        metas[id] = meta;
        Save(meta);
        return meta;
    }

    /// <summary>What to hand to the worker: the uploaded file, or the URL to download.</summary>
    public string Input(SongMeta m) => m.SourceUrl ?? SourcePath(m);

    /// <summary>Imports an existing analysis work dir and/or a human map folder (with its abeat-work
    /// analysis, as written by `abeat compare/bench`). Without an analysis the song audio is copied
    /// and the caller queues it.</summary>
    public SongMeta Import(string path)
    {
        path = Path.GetFullPath(path);
        bool isMap = File.Exists(Path.Combine(path, "Info.dat")) || File.Exists(Path.Combine(path, "info.dat"));
        string? work = File.Exists(Path.Combine(path, "analysis.json")) ? path
            : File.Exists(Path.Combine(path, "abeat-work", "analysis.json")) ? Path.Combine(path, "abeat-work") : null;
        if (!isMap && work == null) throw new ArgumentException($"{path}: neither a map folder nor an analysis work dir");

        string id = Guid.NewGuid().ToString("N")[..12];
        var human = isMap ? Abeat.Core.Formats.MapReader.Read(path) : null;
        string fileName = human != null ? $"{human.SongAuthor} - {human.SongName}{Path.GetExtension(human.SongFile)}" : "audio.egg";
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        var meta = new SongMeta
        {
            Id = id,
            FileName = fileName,
            Title = human?.SongName ?? Path.GetFileName(path),
            Artist = human?.SongAuthor ?? "",
            ReferenceMapper = human?.LevelAuthor,
            ReferenceUrl = human != null && Path.GetFileName(path) is { Length: > 0 } key && key.All(char.IsAsciiHexDigit)
                ? $"https://beatsaver.com/maps/{key}" : null,
        };
        Directory.CreateDirectory(Dir(id));
        if (work != null)
        {
            CopyDir(work, WorkDir(id), f => !f.Contains($"{Path.DirectorySeparatorChar}download{Path.DirectorySeparatorChar}"));
            var a = Analysis(id)!;
            if (!string.IsNullOrWhiteSpace(a.Source.Title) && human == null) meta.Title = a.Source.Title;
            if (!string.IsNullOrWhiteSpace(a.Source.Artist) && human == null) meta.Artist = a.Source.Artist;
            meta.Bpm = a.Tempo.Bpm;
            meta.DurationSec = a.Audio.DurationSec;
            meta.Status = SongStatus.Ready;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath(meta))!);
            File.Copy(Path.Combine(path, human!.SongFile), SourcePath(meta));
            meta.Status = SongStatus.Queued;
        }
        if (human != null)
        {
            // only the difficulty files: the audio is already in work/ or source/
            Directory.CreateDirectory(ReferenceDir(id));
            foreach (var f in Directory.EnumerateFiles(path, "*.dat"))
                File.Copy(f, Path.Combine(ReferenceDir(id), Path.GetFileName(f)));
            SaveSettings(id, new GeneratorSettings { Difficulties = human.Difficulties.Select(d => d.Difficulty).Distinct().ToList() });
        }
        metas[id] = meta;
        Save(meta);
        return meta;
    }

    static void CopyDir(string from, string to, Func<string, bool> include)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(include))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    public void Save(SongMeta m) => File.WriteAllText(Path.Combine(Dir(m.Id), "meta.json"), JsonSerializer.Serialize(m, Json));

    public void Delete(string id)
    {
        metas.TryRemove(id, out _);
        analysisCache.TryRemove(id, out _);
        logs.TryRemove(id, out _);
        if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), recursive: true);
    }

    public SongAnalysis? Analysis(string id)
    {
        if (analysisCache.TryGetValue(id, out var a)) return a;
        if (!File.Exists(Path.Combine(WorkDir(id), "analysis.json"))) return null;
        return analysisCache[id] = SongAnalysis.Load(WorkDir(id));
    }

    public void InvalidateAnalysis(string id) => analysisCache.TryRemove(id, out _);

    public GeneratorSettings Settings(string id)
    {
        var f = Path.Combine(Dir(id), "settings.json");
        return File.Exists(f) ? GeneratorSettings.Load(f) : new GeneratorSettings();
    }

    public void SaveSettings(string id, GeneratorSettings s) => s.Save(Path.Combine(Dir(id), "settings.json"));
}

public sealed class LogBuffer
{
    readonly Queue<string> lines = new();

    public void Add(string line)
    {
        lock (lines)
        {
            lines.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (lines.Count > 200) lines.Dequeue();
        }
    }

    public string[] Snapshot() { lock (lines) return [.. lines]; }
}
