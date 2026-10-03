using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abeat.Core.Analysis;

/// <summary>Mirror of analysis.json produced by the Python worker. All times are seconds in song.egg,
/// where tempo-grid beat 0 is at t = 0 (so beat = t * bpm / 60).</summary>
public sealed class SongAnalysis
{
    public int SchemaVersion { get; set; }
    public SourceInfo Source { get; set; } = new();
    public AudioInfo Audio { get; set; } = new();
    public TempoInfo Tempo { get; set; } = new();
    public EnergyCurve Energy { get; set; } = new();
    public List<Section> Sections { get; set; } = [];
    public string LayerSource { get; set; } = "bands";
    public Dictionary<string, List<Onset>> Layers { get; set; } = [];
    public string Cover { get; set; } = "cover.jpg";
    public PreviewInfo Preview { get; set; } = new();

    /// <summary>Directory containing analysis.json, song.egg and cover.</summary>
    [JsonIgnore] public string Directory { get; set; } = "";

    public double SecondsToBeat(double t) => t * Tempo.Bpm / 60.0;
    public double BeatToSeconds(double b) => b * 60.0 / Tempo.Bpm;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static SongAnalysis Load(string dirOrFile)
    {
        string file = File.Exists(dirOrFile) ? dirOrFile : Path.Combine(dirOrFile, "analysis.json");
        var a = JsonSerializer.Deserialize<SongAnalysis>(File.ReadAllText(file), Options)
                ?? throw new InvalidDataException($"Cannot parse {file}");
        a.Directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return a;
    }

    public double EnergyAt(double t) => Energy.At(t);

    public Section SectionAt(double t) =>
        Sections.FirstOrDefault(s => t >= s.Start && t < s.End) ?? Sections.LastOrDefault() ?? new Section { End = Audio.DurationSec };

    /// <summary>Beat index (integer grid beats) of the first downbeat modulo 4.</summary>
    public int DownbeatPhase => Tempo.Downbeats.Count == 0 ? 0 : ((int)Math.Round(SecondsToBeat(Tempo.Downbeats[0])) % 4 + 4) % 4;
}

public sealed class SourceInfo
{
    public string Path { get; set; } = "";
    /// <summary>Page the audio was downloaded from, when the input was a URL.</summary>
    public string? Url { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
}

public sealed class AudioInfo
{
    public string File { get; set; } = "song.egg";
    public double DurationSec { get; set; }
    public int SampleRate { get; set; }
    public double PadSec { get; set; }
}

public sealed class TempoInfo
{
    public double Bpm { get; set; } = 120;
    public double FirstBeatSec { get; set; }
    public double ResidualMs { get; set; }
    public double MaxDevMs { get; set; }
    public bool Stable { get; set; } = true;
    public string Backend { get; set; } = "";
    public List<double> Beats { get; set; } = [];
    public List<double> Downbeats { get; set; } = [];
}

public sealed class EnergyCurve
{
    public double HopSec { get; set; } = 0.1;
    public List<double> Values { get; set; } = [];

    public double At(double t)
    {
        if (Values.Count == 0) return 0.5;
        double i = t / HopSec;
        int a = Math.Clamp((int)Math.Floor(i), 0, Values.Count - 1);
        int b = Math.Min(a + 1, Values.Count - 1);
        double f = Math.Clamp(i - a, 0, 1);
        return Values[a] * (1 - f) + Values[b] * f;
    }
}

public sealed class Section
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Label { get; set; } = "A";
    public double Energy { get; set; } = 0.5;
}

public sealed class Onset
{
    /// <summary>Time in seconds.</summary>
    public double T { get; set; }
    /// <summary>Strength 0..1.</summary>
    public double S { get; set; }
    /// <summary>Brightness 0..1 (log spectral centroid), a rough stand-in for pitch height.</summary>
    public double Br { get; set; }
}

public sealed class PreviewInfo
{
    public double StartSec { get; set; }
    public double DurationSec { get; set; } = 12;
}
