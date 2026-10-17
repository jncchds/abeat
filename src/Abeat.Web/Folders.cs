using System.Text.Json;
using Abeat.Core.Generation;

namespace Abeat.Web;

/// <summary>A folder of the song list. Folders nest at most two levels: a top-level folder may hold
/// sub-folders, a sub-folder only songs.</summary>
public sealed record Folder
{
    public required string Id { get; init; }
    public string Name { get; set; } = "New folder";
    public string? ParentId { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Song-list folders, stored together as data/folders.json; a song names its folder in
/// <see cref="SongMeta.FolderId"/> (none: shown at the top level).</summary>
public sealed class FolderStore
{
    public const int MaxDepth = 2;

    readonly string file;
    readonly SongStore songs;
    readonly Dictionary<string, Folder> folders = [];
    readonly Lock gate = new();
    static readonly JsonSerializerOptions Json = GeneratorSettings.JsonOptions;

    public FolderStore(SongStore songs)
    {
        this.songs = songs;
        file = Path.Combine(songs.Root, "folders.json");
        try
        {
            if (File.Exists(file))
                foreach (var f in JsonSerializer.Deserialize<List<Folder>>(File.ReadAllText(file), Json) ?? [])
                    folders[f.Id] = f;
        }
        catch { /* a broken file: start without folders */ }
        // a song left in a folder that no longer exists goes back to the top level
        foreach (var m in songs.All.Where(m => m.FolderId != null && !folders.ContainsKey(m.FolderId)).ToList())
        {
            m.FolderId = null;
            songs.Save(m);
        }
    }

    public List<Folder> All { get { lock (gate) return [.. folders.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)]; } }
    public Folder? Get(string id) { lock (gate) return folders.GetValueOrDefault(id); }

    /// <summary>1 for a top-level folder, 2 for a sub-folder.</summary>
    int Depth(Folder f) => f.ParentId != null && folders.TryGetValue(f.ParentId, out var p) ? 1 + Depth(p) : 1;
    bool HasSubfolders(string id) => folders.Values.Any(f => f.ParentId == id);

    /// <summary>Why a folder (with or without sub-folders) cannot go under <paramref name="parentId"/>, or null.</summary>
    string? CheckParent(string? id, string? parentId)
    {
        if (parentId == null) return null;
        if (!folders.TryGetValue(parentId, out var parent)) return "unknown parent folder";
        if (parentId == id) return "a folder cannot contain itself";
        if (Depth(parent) >= MaxDepth) return $"folders nest at most {MaxDepth} levels";
        if (id != null && HasSubfolders(id)) return "a folder with sub-folders must stay at the top level";
        return null;
    }

    public (Folder? Folder, string? Error) Create(string? name, string? parentId)
    {
        lock (gate)
        {
            if (CheckParent(null, parentId) is { } error) return (null, error);
            var f = new Folder { Id = Guid.NewGuid().ToString("N")[..10], Name = Clean(name) ?? "New folder", ParentId = parentId };
            folders[f.Id] = f;
            Save();
            return (f, null);
        }
    }

    /// <summary>Renames and/or moves a folder; <paramref name="move"/> false leaves the parent as it is.</summary>
    public (Folder? Folder, string? Error) Update(string id, string? name, bool move, string? parentId)
    {
        lock (gate)
        {
            if (!folders.TryGetValue(id, out var f)) return (null, null);
            if (move && CheckParent(id, parentId) is { } error) return (null, error);
            if (Clean(name) is { } n) f.Name = n;
            if (move) f.ParentId = parentId;
            Save();
            return (f, null);
        }
    }

    /// <summary>Removes a folder; its songs and sub-folders move up to its parent (or the top level).</summary>
    public bool Delete(string id)
    {
        lock (gate)
        {
            if (!folders.Remove(id, out var gone)) return false;
            foreach (var sub in folders.Values.Where(f => f.ParentId == id)) sub.ParentId = gone.ParentId;
            foreach (var m in songs.All.Where(m => m.FolderId == id))
            {
                m.FolderId = gone.ParentId;
                songs.Save(m);
            }
            Save();
            return true;
        }
    }

    public bool MoveSong(SongMeta song, string? folderId)
    {
        if (folderId != null && Get(folderId) == null) return false;
        song.FolderId = folderId;
        songs.Save(song);
        return true;
    }

    void Save() => File.WriteAllText(file, JsonSerializer.Serialize(folders.Values.OrderBy(f => f.CreatedUtc), Json));

    static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(100, name.Trim().Length)];
}

public static class FolderEndpoints
{
    public sealed record FolderRequest(string? Name, string? ParentId);
    public sealed record SongFolderRequest(string? FolderId);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/folders", (FolderStore folders) => folders.All);

        api.MapPost("/folders", (FolderRequest req, FolderStore folders) =>
            folders.Create(req.Name, string.IsNullOrEmpty(req.ParentId) ? null : req.ParentId) is var (f, error) && f != null
                ? Results.Ok(f) : Results.BadRequest(error));

        // a body with "parentId" moves the folder (null: to the top level); without it only renames
        api.MapPatch("/folders/{id}", async (string id, HttpRequest http, FolderStore folders) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Body);
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest("JSON object expected");
            string? Str(string key) => body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool move = body.TryGetProperty("parentId", out _);
            var parent = Str("parentId");
            var (f, error) = folders.Update(id, Str("name"), move, string.IsNullOrEmpty(parent) ? null : parent);
            return f != null ? Results.Ok(f) : error != null ? Results.BadRequest(error) : Results.NotFound();
        });

        api.MapDelete("/folders/{id}", (string id, FolderStore folders) => folders.Delete(id) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/songs/{id}/folder", (string id, SongFolderRequest req, SongStore songs, FolderStore folders) =>
        {
            if (songs.Get(id) is not { } m) return Results.NotFound();
            return folders.MoveSong(m, string.IsNullOrEmpty(req.FolderId) ? null : req.FolderId) ? Results.Ok(m) : Results.BadRequest("unknown folder");
        });
    }
}
