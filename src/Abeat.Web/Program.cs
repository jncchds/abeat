using System.Text.Json.Serialization;
using Abeat.Web;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

const long MaxUpload = 300L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUpload);
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
// client-side routes (/songs/{id}) are served by the React app
app.MapFallbackToFile("index.html");

app.Run();
