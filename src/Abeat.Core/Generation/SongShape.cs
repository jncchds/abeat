using Abeat.Core.Analysis;

namespace Abeat.Core.Generation;

/// <summary>A build-up: the stretch before a drop where the music winds up (beats).</summary>
public sealed record BuildUp(double Start, double Drop);

/// <summary>Song-level structure and character used by the lights and the environment choice.</summary>
public static class SongShape
{
    /// <summary>Downbeats (seconds) where the energy jumps into a loud part (a drop), one per 16 beats at
    /// most, in time order.</summary>
    public static List<double> Drops(SongAnalysis a)
    {
        double Mean(double t0, double t1)
        {
            double sum = 0; int n = 0;
            for (double x = Math.Max(0, t0); x <= t1; x += a.Energy.HopSec) { sum += a.EnergyAt(x); n++; }
            return n > 0 ? sum / n : 0;
        }
        var found = new List<(double t, double jump)>();
        foreach (var d in a.Tempo.Downbeats)
        {
            if (d < 4) continue;
            double after = Mean(d + 0.05, d + 2), jump = after - Mean(d - 2, d - 0.15);
            if (jump >= 0.3 && after >= 0.6) found.Add((d, jump));
        }
        var picked = new List<double>();
        foreach (var (t, _) in found.OrderByDescending(f => f.jump))
            if (picked.All(x => Math.Abs(a.SecondsToBeat(x) - a.SecondsToBeat(t)) >= 16)) picked.Add(t);
        picked.Sort();
        return picked;
    }

    /// <summary>The wind-up before each drop: from the start of the section the drop ends (or 16 beats
    /// before it, whichever is later) to the drop, at least 4 beats long.</summary>
    public static List<BuildUp> BuildUps(SongAnalysis a, IReadOnlyList<double> drops)
    {
        var result = new List<BuildUp>();
        double prev = double.NegativeInfinity;
        foreach (var t in drops)
        {
            double drop = Math.Round(a.SecondsToBeat(t));
            var sec = a.Sections.LastOrDefault(s => s.Start < t - 0.5);
            double start = Math.Max(drop - 16, Math.Max(prev + 4, sec == null ? 0 : Math.Round(a.SecondsToBeat(sec.Start))));
            if (drop - start >= 4) result.Add(new BuildUp(start, drop));
            prev = drop;
        }
        return result;
    }

    /// <summary>Where the song sits on the axes of <see cref="EnvironmentCharacter"/>: drive from how often
    /// a kick lands on the beat in the loud parts (four-on-the-floor vs. a band's backbeat), darkness from
    /// the average onset brightness, intensity from tempo, drum density and how much of the song is loud.</summary>
    public static EnvironmentCharacter Character(SongAnalysis a)
    {
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        bool Loud(double t) => a.Sections.Count == 0 || a.SectionAt(t).Energy >= 0.6 * maxEnergy;
        double duration = Math.Max(1, a.Audio.DurationSec);

        var drums = a.Layers.GetValueOrDefault("drums");
        var kicks = (drums ?? []).Where(o => o.K?.Contains('k') == true).Select(o => o.T).Order().ToArray();
        var loudBeats = GridBeats(a).Select(a.BeatToSeconds).Where(Loud).ToList();
        double drive = 0.5;
        if (kicks.Length > 0 && loudBeats.Count > 16)
        {
            double share = loudBeats.Count(b => Near(kicks, b, 0.07)) / (double)loudBeats.Count;
            drive = Math.Clamp((share - 0.45) / 0.45, 0, 1);
        }

        var all = a.Layers.Values.SelectMany(l => l).ToList();
        double brightness = all.Count > 0 ? all.Average(o => o.Br) : 0.72;
        double darkness = Math.Clamp((0.8 - brightness) / 0.16, 0, 1);

        double loudShare = a.Sections.Count == 0 ? 0.5
            : a.Sections.Where(s => s.Energy >= 0.7 * maxEnergy).Sum(s => s.End - s.Start) / duration;
        double drumRate = drums != null ? drums.Count / duration : all.Count / duration / 6;
        double intensity = 0.4 * Math.Clamp((a.Tempo.Bpm - 80) / 100, 0, 1) + 0.3 * Math.Clamp(loudShare, 0, 1)
            + 0.3 * Math.Clamp((drumRate - 2) / 4, 0, 1);
        return new EnvironmentCharacter(drive, intensity, darkness, a.Tempo.Bpm);
    }

    /// <summary>Maps an event's brightness to -1..1 within the 10th..90th percentile of the given events,
    /// so "higher than usual" / "lower than usual" works for songs whose sounds are all bright or dark.</summary>
    public static Func<RhythmEvent, double> RelativePitch(IReadOnlyList<RhythmEvent> events)
    {
        if (events.Count == 0) return _ => 0;
        var sorted = events.Select(e => e.Brightness).Order().ToArray();
        double lo = sorted[(int)(0.1 * (sorted.Length - 1))], hi = sorted[(int)(0.9 * (sorted.Length - 1))];
        double mid = (lo + hi) / 2, half = Math.Max(1e-3, (hi - lo) / 2);
        return e => Math.Clamp((e.Brightness - mid) / half, -1, 1);
    }

    /// <summary>Whole grid beats from 0 to the end of the song.</summary>
    public static IEnumerable<double> GridBeats(SongAnalysis a)
    {
        double last = a.SecondsToBeat(a.Audio.DurationSec);
        for (int b = 0; b <= last; b++) yield return b;
    }

    static bool Near(double[] sorted, double t, double tol)
    {
        int i = Array.BinarySearch(sorted, t - tol);
        if (i < 0) i = ~i;
        return i < sorted.Length && sorted[i] <= t + tol;
    }

    /// <summary>The environment that suits the song best: closest character (tempo compared up to
    /// doubling/halving), with group-lighting environments preferred for their richer lightshows. The seed
    /// nudges near-ties, so other seeds can land on another fitting environment.</summary>
    public static EnvironmentInfo PickEnvironment(SongAnalysis a, int seed) =>
        RankEnvironments(Character(a), seed).First().Env;

    public static IEnumerable<(EnvironmentInfo Env, double Score)> RankEnvironments(EnvironmentCharacter song, int seed) =>
        EnvironmentCatalog.All
            .Where(e => e.System == LightingSystem.Groups || e.SurveyMaps > 0 || e.Id == "DefaultEnvironment")
            .Select(e => (e, Score(song, e) + Jitter(seed, e.Id)))
            .OrderByDescending(x => x.Item2);

    static double Score(EnvironmentCharacter s, EnvironmentInfo e)
    {
        var c = e.Character;
        double bpm = new[] { s.Bpm, s.Bpm / 2, s.Bpm * 2 }.Min(b => Math.Abs(b - c.Bpm)) / 40;
        double d = 1.2 * Math.Abs(s.Drive - c.Drive) + 1.0 * Math.Abs(s.Intensity - c.Intensity)
            + 0.8 * Math.Abs(s.Darkness - c.Darkness) + 0.5 * Math.Min(bpm, 1.5);
        return -d + (e.System == LightingSystem.Groups ? 0.25 : 0);
    }

    static double Jitter(int seed, string id)
    {
        uint h = 2166136261;
        foreach (char ch in id) h = (h ^ ch) * 16777619;
        h = (h ^ (uint)seed) * 16777619;
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h % 1000) / 1000.0 * 0.12;
    }
}
