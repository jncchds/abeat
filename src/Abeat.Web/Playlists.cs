using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abeat.Core.Generation;

namespace Abeat.Web;

public sealed record PlaylistEntry(string SongId, string Version, DateTime AddedUtc);

public sealed record Playlist
{
    public required string Id { get; init; }
    public string Title { get; set; } = "ABeat playlist";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public List<PlaylistEntry> Entries { get; init; } = [];
}

/// <summary>Playlists of generated versions, stored as data/playlists/{id}.json, exported as one zip of map
/// folders plus a .bplist for BSManager's map and playlist import (its one-click links only resolve maps
/// that are on BeatSaver, which generated maps are not).</summary>
public sealed class PlaylistStore
{
    readonly string root;
    readonly ConcurrentDictionary<string, Playlist> lists = new();
    readonly Lock gate = new();
    static readonly JsonSerializerOptions Json = GeneratorSettings.JsonOptions;

    public PlaylistStore(SongStore songs)
    {
        root = Path.Combine(songs.Root, "playlists");
        Directory.CreateDirectory(root);
        foreach (var f in Directory.EnumerateFiles(root, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<Playlist>(File.ReadAllText(f), Json) is { } p) lists[p.Id] = p; }
            catch { /* skip broken files */ }
        }
    }

    public IEnumerable<Playlist> All => lists.Values.OrderBy(p => p.CreatedUtc);
    public Playlist? Get(string id) => lists.GetValueOrDefault(id);

    public Playlist Create(string? title)
    {
        var p = new Playlist { Id = Guid.NewGuid().ToString("N")[..10], Title = Clean(title) ?? "ABeat playlist" };
        Save(p);
        return p;
    }

    /// <summary>Runs a change under a lock and saves the playlist.</summary>
    public Playlist? Update(string id, Action<Playlist> change)
    {
        lock (gate)
        {
            if (!lists.TryGetValue(id, out var p)) return null;
            change(p);
            Save(p);
            return p;
        }
    }

    public bool Delete(string id)
    {
        if (!lists.TryRemove(id, out _)) return false;
        File.Delete(Path.Combine(root, id + ".json"));
        return true;
    }

    void Save(Playlist p)
    {
        lists[p.Id] = p;
        File.WriteAllText(Path.Combine(root, p.Id + ".json"), JsonSerializer.Serialize(p, Json));
    }

    public static string? Clean(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(100, title.Trim().Length)];

    /// <summary>An entry resolved against the songs and their generations (null parts when deleted).</summary>
    public sealed record Resolved(PlaylistEntry Entry, SongMeta? Song, GenerationMeta? Generation, int Number);

    public static Resolved Resolve(SongStore songs, PlaylistEntry e)
    {
        var song = songs.Get(e.SongId);
        if (song == null) return new(e, null, null, 0);
        var gens = Generations.List(songs, e.SongId);
        int i = gens.FindIndex(g => g.Id == e.Version);
        // generations are numbered oldest first, as in the UI
        return i < 0 ? new(e, song, null, 0) : new(e, song, gens[i], gens.Count - i);
    }

    /// <summary>Zip with one folder per available entry and the playlist's .bplist at the root.</summary>
    public void WriteZip(Playlist p, SongStore songs, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bplistSongs = new JsonArray();
        string? image = null;
        foreach (var r in p.Entries.Select(e => Resolve(songs, e)))
        {
            if (r.Song == null || r.Generation == null) continue;
            var dir = Generations.MapDir(songs, r.Song.Id, r.Generation.Id);
            if (!File.Exists(Path.Combine(dir, "Info.dat"))) continue;

            var files = ReadMap(dir, $"ABeat #{r.Number}", out var info, out string hash);
            string folder = Unique(used, SafeName($"{(string.IsNullOrWhiteSpace(r.Song.Artist) ? "" : r.Song.Artist + " - ")}{r.Song.Title} (ABeat #{r.Number})"));
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry($"{folder}/{name}", CompressionLevel.Fastest);
                using var s = entry.Open();
                s.Write(bytes);
            }
            bplistSongs.Add(new JsonObject
            {
                ["hash"] = hash,
                ["songName"] = info["_songName"]?.GetValue<string>() ?? r.Song.Title,
                ["levelAuthorName"] = info["_levelAuthorName"]?.GetValue<string>() ?? "",
            });
            if (image == null && files.FirstOrDefault(f => f.Name == (info["_coverImageFilename"]?.GetValue<string>() ?? "cover.jpg")) is { Bytes: { } cover })
                image = "data:image/jpeg;base64," + Convert.ToBase64String(cover);
        }

        var bplist = zip.CreateEntry($"{SafeName(p.Title)}.bplist", CompressionLevel.Fastest);
        using (var s = bplist.Open()) s.Write(Encoding.UTF8.GetBytes(Bplist(p, bplistSongs, image)));
    }

    /// <summary>The .bplist alone (songs referenced by level hash; install the maps from the zip first).</summary>
    public string BplistOnly(Playlist p, SongStore songs)
    {
        var arr = new JsonArray();
        string? image = null;
        foreach (var r in p.Entries.Select(e => Resolve(songs, e)))
        {
            if (r.Song == null || r.Generation == null) continue;
            var dir = Generations.MapDir(songs, r.Song.Id, r.Generation.Id);
            if (!File.Exists(Path.Combine(dir, "Info.dat"))) continue;
            var files = ReadMap(dir, $"ABeat #{r.Number}", out var info, out string hash);
            arr.Add(new JsonObject { ["hash"] = hash, ["songName"] = info["_songName"]?.GetValue<string>() ?? r.Song.Title, ["levelAuthorName"] = info["_levelAuthorName"]?.GetValue<string>() ?? "" });
            if (image == null && files.FirstOrDefault(f => f.Name == (info["_coverImageFilename"]?.GetValue<string>() ?? "cover.jpg")) is { Bytes: { } cover })
                image = "data:image/jpeg;base64," + Convert.ToBase64String(cover);
        }
        return Bplist(p, arr, image);
    }

    static string Bplist(Playlist p, JsonArray songsArr, string? image)
    {
        var o = new JsonObject
        {
            ["playlistTitle"] = p.Title,
            ["playlistAuthor"] = "ABeat by CHDS",
            ["playlistDescription"] = "Generated with ABeat",
            ["songs"] = songsArr,
        };
        if (image != null) o["image"] = image;
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The map's files with Info.dat's sub name set to the version label (so several versions of a
    /// song can be told apart in game), plus its Beat Saber level hash: SHA-1 over Info.dat followed by the
    /// difficulty files in Info.dat order.</summary>
    static List<(string Name, byte[] Bytes)> ReadMap(string dir, string subName, out JsonObject info, out string hash)
    {
        info = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "Info.dat")))!.AsObject();
        info["_songSubName"] = subName;
        var infoBytes = Encoding.UTF8.GetBytes(info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var files = new List<(string, byte[])> { ("Info.dat", infoBytes) };
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(infoBytes);
        var diffFiles = (info["_difficultyBeatmapSets"]?.AsArray() ?? [])
            .SelectMany(set => set?["_difficultyBeatmaps"]?.AsArray() ?? [])
            .Select(d => d?["_beatmapFilename"]?.GetValue<string>())
            .OfType<string>().ToList();
        foreach (var name in diffFiles)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            sha.AppendData(File.ReadAllBytes(path));
        }
        hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (!name.Equals("Info.dat", StringComparison.OrdinalIgnoreCase)) files.Add((name, File.ReadAllBytes(f)));
        }
        return files;
    }

    static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])) s = s.Replace(c, '_');
        return s.Trim().TrimEnd('.');
    }

    static string Unique(HashSet<string> used, string name)
    {
        string n = name;
        for (int i = 2; !used.Add(n); i++) n = $"{name} {i}";
        return n;
    }
}
