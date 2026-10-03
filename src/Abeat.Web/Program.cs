using System.Text.Json.Serialization;
using Abeat.Web;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

const long MaxUpload = 300L * 1024 * 1024;
// ABEAT_HTTPS_PORT adds an https listener with a self-signed LAN certificate (for ArcViewer, see
// LocalHttps); explicit listeners replace --urls, so the http port is then ABEAT_HTTP_PORT.
int? httpsPort = int.TryParse(builder.Configuration["ABEAT_HTTPS_PORT"], out var hp) ? hp : null;
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
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
MapEndpoints.Map(app);
app.MapGet("/healthz", () => "ok");
app.MapGet("/api/config", () => new { httpsPort });
// client-side routes (/songs/{id}) are served by the React app
app.MapFallbackToFile("index.html");

app.Run();
