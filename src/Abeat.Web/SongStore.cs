using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Abeat.Core.Analysis;
using Abeat.Core.Generation;

namespace Abeat.Web;

/// <summary>State of the song's current or last job. A song with <see cref="SongMeta.HasAnalysis"/> stays
/// usable (analysis, versions, generating) whatever its job is doing.</summary>
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
    /// <summary>An analysis is in place (the last re-analysis may still be running or have failed).</summary>
    public bool HasAnalysis { get; set; }
    /// <summary>Bumped by every successful analysis, so clients know to reload it.</summary>
    public int AnalysisRevision { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double? Bpm { get; set; }
    public double? DurationSec { get; set; }
    public AnalysisOptions Analysis { get; set; } = new();
    /// <summary>A human-made map of the same song, kept for comparison (data/songs/{id}/reference).</summary>
    public string? ReferenceMapper { get; set; }
    public string? ReferenceUrl { get; set; }
    /// <summary>When the newest saved version was generated; filled in for the song list only.</summary>
    public DateTime? LastGeneratedUtc { get; init; }
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
                m.HasAnalysis = File.Exists(Path.Combine(dir, "work", "analysis.json"));
                metas[m.Id] = m;
            }
            catch { /* skip broken entries */ }
        }
    }

    public IEnumerable<SongMeta> All => metas.Values.OrderByDescending(m => m.CreatedUtc);
    public SongMeta? Get(string id) => metas.GetValueOrDefault(id);
    public string Dir(string id) => Path.Combine(Root, "songs", id);
    public string WorkDir(string id) => Path.Combine(Dir(id), "work");
    /// <summary>Where a re-analysis writes; swapped in for <see cref="WorkDir"/> only when it succeeds.</summary>
    public string NextWorkDir(string id) => Path.Combine(Dir(id), "work.next");

    /// <summary>Fresh <see cref="NextWorkDir"/> seeded with what the worker reuses (separated stems,
    /// downloaded audio and cover), so a re-analysis neither separates nor downloads again.</summary>
    public string PrepareNextWorkDir(string id)
    {
        var next = NextWorkDir(id);
        if (Directory.Exists(next)) Directory.Delete(next, recursive: true);
        Directory.CreateDirectory(next);
        foreach (var sub in new[] { "stems", "download" })
            if (Directory.Exists(Path.Combine(WorkDir(id), sub))) CopyDir(Path.Combine(WorkDir(id), sub), Path.Combine(next, sub), _ => true);
        return next;
    }

    /// <summary>Makes a finished re-analysis the song's analysis.</summary>
    public void CommitNextWorkDir(string id)
    {
        var work = WorkDir(id);
        var old = Path.Combine(Dir(id), "work.old");
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        if (Directory.Exists(work)) Directory.Move(work, old);
        Directory.Move(NextWorkDir(id), work);
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        InvalidateAnalysis(id);
    }

    public void DiscardNextWorkDir(string id)
    {
        try { if (Directory.Exists(NextWorkDir(id))) Directory.Delete(NextWorkDir(id), recursive: true); }
        catch (IOException) { /* a worker may still hold a file; the next run clears it */ }
    }
    public string ReferenceDir(string id) => Path.Combine(Dir(id), "reference");
    /// <summary>Lyrics pasted by the user, aligned instead of a transcription when vocal onsets come from lyrics.</summary>
    public string LyricsPath(string id) => Path.Combine(Dir(id), "lyrics.txt");
    public string? LyricsFile(string id) => File.Exists(LyricsPath(id)) && new FileInfo(LyricsPath(id)).Length > 0 ? LyricsPath(id) : null;
    /// <summary>Beats the user tapped along with the song, to compare with generated note timings.</summary>
    public string TapsPath(string id) => Path.Combine(Dir(id), "taps.json");
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
    public SongMeta Import(string path, AnalysisOptions? options = null)
    {
        path = Path.GetFullPath(path);
        bool isMap = File.Exists(Path.Combine(path, "Info.dat")) || File.Exists(Path.Combine(path, "info.dat"));
        string? work = File.Exists(Path.Combine(path, "analysis.json")) ? path
            : File.Exists(Path.Combine(path, "abeat-work", "analysis.json")) ? Path.Combine(path, "abeat-work") : null;
        if (!isMap && work == null) throw new ArgumentException($"{path}: neither a map folder nor an analysis work dir");

        string id = Guid.NewGuid().ToString("N")[..12];
        var human = isMap ? Abeat.Core.Formats.MapReader.Read(path) : null;
        if (human != null && !Path.GetFullPath(Path.Combine(path, human.SongFile)).StartsWith(path + Path.DirectorySeparatorChar))
            throw new InvalidDataException($"the map's song file ({human.SongFile}) is outside the map");
        if (work == null && !File.Exists(Path.Combine(path, human!.SongFile)))
            throw new InvalidDataException($"the map has no song file ({human.SongFile})");
        string fileName = human != null ? $"{human.SongAuthor} - {human.SongName}{Path.GetExtension(human.SongFile)}" : "audio.egg";
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        var meta = new SongMeta
        {
            Id = id,
            FileName = fileName,
            Title = human?.SongName ?? Path.GetFileName(path),
            Artist = human?.SongAuthor ?? "",
            Analysis = options ?? new(),
            ReferenceMapper = human == null ? null : string.IsNullOrWhiteSpace(human.LevelAuthor) ? "unknown mapper" : human.LevelAuthor,
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
            meta.HasAnalysis = true;
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

    const long MaxUnzipped = 1L << 30;

    /// <summary>A new song from a zipped Beat Saver map (as downloaded from BeatSaver): the map's audio is
    /// analysed like an upload and its difficulties are kept as the human version.</summary>
    public SongMeta ImportMapZip(string zipPath, AnalysisOptions options, string? referenceUrl = null)
    {
        string tmp = Path.Combine(Root, "incoming", Guid.NewGuid().ToString("N"));
        try
        {
            ZipArchive OpenZip()
            {
                try { return ZipFile.OpenRead(zipPath); }
                catch (InvalidDataException) { throw new InvalidDataException("not a zip file"); }
            }
            using (var zip = OpenZip())
            {
                if (zip.Entries.Sum(e => e.Length) > MaxUnzipped) throw new InvalidDataException("the zip unpacks to more than 1 GB");
                zip.ExtractToDirectory(tmp); // refuses entries that would land outside tmp
            }
            var info = Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Equals("Info.dat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length).FirstOrDefault()
                ?? throw new InvalidDataException("no Info.dat in the zip: not a Beat Saver map");
            var meta = Import(Path.GetDirectoryName(info)!, options);
            meta.ReferenceUrl = referenceUrl; // not from the folder name: the temp folder is hex too
            Save(meta);
            return meta;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"not a readable Beat Saver map: {e.Message}", e);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    public string IncomingPath(string name)
    {
        var dir = Path.Combine(Root, "incoming");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}-{name}");
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
