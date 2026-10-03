using System.Diagnostics;

namespace Abeat.Core.Analysis;

public sealed record AnalysisOptions(string BeatBackend = "auto", bool Stems = false, double? BpmOverride = null);

/// <summary>Runs the Python analysis worker (analysis/ uv project) as a subprocess.</summary>
public sealed class AnalysisRunner
{
    public string AnalysisProjectDir { get; }

    public AnalysisRunner(string? analysisProjectDir = null)
    {
        AnalysisProjectDir = analysisProjectDir ?? FindAnalysisProject()
            ?? throw new DirectoryNotFoundException(
                "Cannot find the analysis worker (analysis/pyproject.toml). Set ABEAT_ANALYSIS_DIR.");
    }

    public static string? FindAnalysisProject()
    {
        var env = Environment.GetEnvironmentVariable("ABEAT_ANALYSIS_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "pyproject.toml"))) return env;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "analysis");
                if (File.Exists(Path.Combine(candidate, "pyproject.toml"))) return candidate;
            }
        }
        return null;
    }

    public async Task<SongAnalysis> AnalyzeAsync(string audioPath, string workDir, AnalysisOptions options,
        Action<string>? log = null, CancellationToken ct = default)
    {
        string input = IsUrl(audioPath) ? audioPath : Path.GetFullPath(audioPath);
        var args = new List<string> { "analyze", input, "-o", Path.GetFullPath(workDir), "--beats", options.BeatBackend };
        if (options.Stems) args.AddRange(["--stems", "demucs"]);
        if (options.BpmOverride is { } bpm) args.AddRange(["--bpm", bpm.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        await RunWorkerAsync(args, log, ct);
        return SongAnalysis.Load(workDir);
    }

    /// <summary>Inputs the worker downloads itself (YouTube, YouTube Music, ... via yt-dlp).</summary>
    public static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase);

    public Task SynthAsync(string outputWav, Action<string>? log = null, CancellationToken ct = default) =>
        RunWorkerAsync(["synth", Path.GetFullPath(outputWav)], log, ct);

    async Task RunWorkerAsync(IReadOnlyList<string> args, Action<string>? log, CancellationToken ct)
    {
        var psi = BuildStartInfo(args);
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 40) tail.Dequeue();
            }
            log?.Invoke(line);
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"analysis worker failed (exit {proc.ExitCode}):\n{string.Join('\n', tail)}");
    }

    ProcessStartInfo BuildStartInfo(IReadOnlyList<string> args)
    {
        bool windows = OperatingSystem.IsWindows();
        var venvExe = Path.Combine(AnalysisProjectDir, ".venv", windows ? "Scripts" : "bin", windows ? "abeat-analyze.exe" : "abeat-analyze");
        ProcessStartInfo psi;
        if (File.Exists(venvExe))
        {
            psi = new ProcessStartInfo(venvExe);
        }
        else
        {
            psi = new ProcessStartInfo(FindUv());
            foreach (var a in new[] { "run", "--project", AnalysisProjectDir, "abeat-analyze" }) psi.ArgumentList.Add(a);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }

    static string FindUv()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { Path.Combine(home, ".local", "bin", "uv"), Path.Combine(home, ".cargo", "bin", "uv"), Path.Combine(home, ".local", "bin", "uv.exe") })
            if (File.Exists(p)) return p;
        return "uv";
    }
}
