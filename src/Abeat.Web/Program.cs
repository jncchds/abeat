using System.Text.Json.Serialization;
using Abeat.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

const long MaxUpload = 300L * 1024 * 1024;
// ABEAT_HTTPS_PORT adds an https listener with a self-signed LAN certificate (for ArcViewer, see
// LocalHttps); explicit listeners replace --urls, so the http port is then ABEAT_HTTP_PORT.
int? httpsPort = int.TryParse(builder.Configuration["ABEAT_HTTPS_PORT"], out var hp) ? hp : null;
// the port browsers reach the https listener on (a container publishes it under another number)
int? publicHttpsPort = httpsPort is null ? null
    : int.TryParse(builder.Configuration["ABEAT_HTTPS_PUBLIC_PORT"], out var pp) ? pp : httpsPort;
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = MaxUpload;
    if (httpsPort is { } port)
    {
        var data = builder.Configuration["ABEAT_DATA"] is { } d ? Path.GetFullPath(d)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "abeat");
        var cert = LocalHttps.LoadOrCreate(data);
        k.ListenAnyIP(int.TryParse(builder.Configuration["ABEAT_HTTP_PORT"], out var p) ? p : 5080);
        k.ListenAnyIP(port, o => o.UseHttps(cert));
    }
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUpload);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddSingleton<SongStore>();
builder.Services.AddSingleton<PlaylistStore>();
builder.Services.AddSingleton<WorkerRuntime>();
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());

var app = builder.Build();
// index.html must revalidate (phones otherwise keep showing an old build after an update); the
// hashed files under /assets never change, so they can be cached for good
var staticFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=31536000, immutable",
};
app.UseDefaultFiles();
app.UseStaticFiles(staticFiles);

// ArcViewer (GPL-3.0, github.com/AllPoland/ArcViewer), fetched by scripts/fetch-arcviewer.sh and served
// from the same origin, so it can open map zips without HTTPS or CORS workarounds
// (in the container it is fetched into /runtime/arcviewer after the server starts, so its presence is
// checked per request)
var arcViewerDir = builder.Configuration["ABEAT_ARCVIEWER_DIR"] ?? Path.Combine(app.Environment.ContentRootPath, "arcviewer");
bool ArcViewer() => File.Exists(Path.Combine(arcViewerDir, "index.html"));
if (ArcViewer() || builder.Configuration["ABEAT_ARCVIEWER_DIR"] != null)
{
    Directory.CreateDirectory(arcViewerDir);
    var types = new FileExtensionContentTypeProvider();
    types.Mappings[".data"] = "application/octet-stream";
    var arcFiles = new PhysicalFileProvider(Path.GetFullPath(arcViewerDir));
    // its asset paths are relative: /arcviewer must become /arcviewer/ (a mapped route would also match
    // /arcviewer/ and keep the static files from serving it)
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.Value == "/arcviewer") ctx.Response.Redirect("/arcviewer/" + ctx.Request.QueryString);
        else await next();
    });
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = arcFiles, RequestPath = "/arcviewer" });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = arcFiles,
        RequestPath = "/arcviewer",
        ContentTypeProvider = types,
        // same file names in every ArcViewer release, so no "immutable" here
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
            ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=604800",
    });
}
// routing after the static files, so "/arcviewer/" is rewritten to its index.html instead of being
// matched by the SPA fallback first
app.UseRouting();
MapEndpoints.Map(app);
PlaylistEndpoints.Map(app);
app.MapGet("/healthz", () => "ok");
app.MapGet("/api/config", (WorkerRuntime runtime) => new { httpsPort = publicHttpsPort, arcViewer = ArcViewer(), runtime = runtime.Current });
// client-side routes (/songs/{id}) are served by the React app
app.MapFallbackToFile("index.html", staticFiles);

app.Run();
