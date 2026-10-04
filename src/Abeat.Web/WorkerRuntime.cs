using System.Text.Json;

namespace Abeat.Web;

/// <summary>State of the analysis runtime. In the container it is installed in the background on start
/// (docker/provision.py writes ABEAT_RUNTIME_STATUS); without that variable (local runs) the worker in
/// analysis/.venv is used and the runtime always counts as ready.</summary>
public sealed class WorkerRuntime(IConfiguration config)
{
    public sealed record State(string Status, string? Accel = null, string[]? Extras = null, string? Message = null, string? Worker = null);

    readonly string? statusFile = config["ABEAT_RUNTIME_STATUS"];
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public State Current
    {
        get
        {
            if (statusFile == null) return new State("ready");
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statusFile));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var extras = r.TryGetProperty("extras", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null;
                return new State(S("state") ?? "installing", S("accel"), extras, S("message"), S("worker"));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return new State("installing", Message: "preparing the analysis runtime");
            }
        }
    }

    /// <summary>Waits until the runtime is installed; returns the worker executable (null = default).</summary>
    public async Task<string?> WaitReadyAsync(Action<string> log, CancellationToken ct)
    {
        string? last = null;
        while (true)
        {
            var s = Current;
            if (s.Status == "ready") return s.Worker;
            if (s.Status == "failed") throw new InvalidOperationException(s.Message ?? "the analysis runtime could not be installed");
            if (s.Message != last) log($"waiting for the analysis runtime: {last = s.Message}");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
