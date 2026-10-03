namespace Abeat.Web;

public static class PlaylistEndpoints
{
    public sealed record TitleRequest(string? Title);
    public sealed record EntryRequest(string SongId, string Version);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/playlists");

        api.MapGet("", (PlaylistStore lists) => lists.All.Select(p => new { p.Id, p.Title, p.CreatedUtc, count = p.Entries.Count }));

        api.MapPost("", (TitleRequest req, PlaylistStore lists) => Results.Ok(Summary(lists.Create(req.Title))));

        api.MapGet("/{id}", (string id, PlaylistStore lists, SongStore songs) =>
            lists.Get(id) is { } p ? Results.Ok(Details(p, songs)) : Results.NotFound());

        api.MapPatch("/{id}", (string id, TitleRequest req, PlaylistStore lists) =>
            lists.Update(id, p => p.Title = PlaylistStore.Clean(req.Title) ?? p.Title) is { } p ? Results.Ok(Summary(p)) : Results.NotFound());

        api.MapDelete("/{id}", (string id, PlaylistStore lists) => lists.Delete(id) ? Results.NoContent() : Results.NotFound());

        // generated versions only: human reference maps are not ours to redistribute (and are on BeatSaver)
        api.MapPost("/{id}/entries", (string id, EntryRequest req, PlaylistStore lists, SongStore songs) =>
        {
            if (songs.Get(req.SongId) == null || Generations.Get(songs, req.SongId, req.Version) == null)
                return Results.BadRequest("unknown song version");
            return lists.Update(id, p =>
            {
                if (!p.Entries.Any(e => e.SongId == req.SongId && e.Version == req.Version))
                    p.Entries.Add(new PlaylistEntry(req.SongId, req.Version, DateTime.UtcNow));
            }) is { } pl ? Results.Ok(Summary(pl)) : Results.NotFound();
        });

        api.MapDelete("/{id}/entries/{index:int}", (string id, int index, PlaylistStore lists) =>
            lists.Update(id, p => { if (index >= 0 && index < p.Entries.Count) p.Entries.RemoveAt(index); }) is { } pl
                ? Results.Ok(Summary(pl)) : Results.NotFound());

        api.MapGet("/{id}/download.zip", (string id, PlaylistStore lists, SongStore songs, HttpContext ctx) =>
        {
            if (lists.Get(id) is not { } p) return Results.NotFound();
            var tmp = Path.GetTempFileName();
            using (var f = File.Create(tmp)) lists.WriteZip(p, songs, f);
            ctx.Response.RegisterForDispose(new TempFile(tmp));
            return Results.File(File.OpenRead(tmp), "application/zip", FileName(p.Title, ".zip"));
        });

        api.MapGet("/{id}/playlist.bplist", (string id, PlaylistStore lists, SongStore songs) =>
            lists.Get(id) is { } p
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(lists.BplistOnly(p, songs)), "application/json", FileName(p.Title, ".bplist"))
                : Results.NotFound());
    }

    static object Summary(Playlist p) => new { p.Id, p.Title, p.CreatedUtc, count = p.Entries.Count };

    static object Details(Playlist p, SongStore songs) => new
    {
        p.Id,
        p.Title,
        p.CreatedUtc,
        entries = p.Entries.Select((e, i) =>
        {
            var r = PlaylistStore.Resolve(songs, e);
            return new
            {
                index = i,
                e.SongId,
                e.Version,
                e.AddedUtc,
                title = r.Song?.Title ?? "(deleted song)",
                artist = r.Song?.Artist ?? "",
                number = r.Number,
                appVersion = r.Generation?.AppVersion,
                createdUtc = r.Generation?.CreatedUtc,
                difficulties = r.Generation?.Difficulties ?? [],
                missing = r.Generation == null,
            };
        }),
    };

    static string FileName(string title, string ext)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
        return title + ext;
    }

    sealed class TempFile(string path) : IDisposable
    {
        public void Dispose() { try { File.Delete(path); } catch { /* best effort */ } }
    }
}
