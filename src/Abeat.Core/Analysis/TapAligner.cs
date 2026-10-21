using System.Text.Json;

namespace Abeat.Core.Analysis;

/// <summary>What the tap-along runs became after alignment (see <see cref="TapAligner"/>).</summary>
public sealed class TapInfo
{
    /// <summary>Latency removed from each run (seconds; positive = the taps came late).</summary>
    public List<double> OffsetsSec { get; set; } = [];
    /// <summary>Whether each run's latency was clear (a hit-rate peak); otherwise it is left unshifted.</summary>
    public List<bool> Confident { get; set; } = [];
    /// <summary>Taps after merging the runs, and how many landed on an onset of the song.</summary>
    public int Count { get; set; }
    public int Snapped { get; set; }
    /// <summary>Times (seconds) both runs tapped: the user wants a double there.</summary>
    public List<double> Doubles { get; set; } = [];
}

/// <summary>Turns tap-along runs (one per hand, recorded in the browser) into the "taps" layer: moments
/// the user wants a note on. The runs are not hand assignments, only two sets of ticks.
/// <list type="number">
/// <item>Each run is moved onto the current song.egg padding (a re-analysis can change it).</item>
/// <item>Its latency is the shift in <see cref="MinLagSec"/>..<see cref="MaxLagSec"/> that puts the most taps
/// within 20 ms of a strong onset (top half by strength within each layer). The hit rate repeats every
/// sixteenth (taps and onsets share the grid), so only latencies a tapper can have are searched; the lag
/// hardly drifts within a run (±10 ms per 30 s on four tapped songs), so one shift per run.</item>
/// <item>Each tap snaps to the nearest strong onset within <see cref="SnapSec"/> (any onset within 20 ms
/// otherwise), else keeps its shifted time: a tap where the music has no onset still wants a note.</item>
/// <item>The runs merge; taps of both runs within <see cref="DoubleSec"/> are one double.</item>
/// </list></summary>
public static class TapAligner
{
    public const string Layer = "taps";
    public const double MinLagSec = -0.06, MaxLagSec = 0.16;
    const double HitSec = 0.02, SnapSec = 0.035, DoubleSec = 0.05, SameTapSec = 0.04;

    /// <summary>Reads a song's taps.json: <c>{"runs":[{"taps":[s...],"padSec":p}]}</c>.</summary>
    public static List<(double[] Taps, double? PadSec)> LoadRuns(string path)
    {
        var runs = new List<(double[], double?)>();
        if (!File.Exists(path)) return runs;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("runs", out var arr) || arr.ValueKind != JsonValueKind.Array) return runs;
        foreach (var r in arr.EnumerateArray())
        {
            var taps = r.TryGetProperty("taps", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetDouble()).Where(double.IsFinite).Order().ToArray() : [];
            double? pad = r.TryGetProperty("padSec", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            if (taps.Length > 0) runs.Add((taps, pad));
        }
        return runs;
    }

    /// <summary>Adds the aligned taps as layer <see cref="Layer"/> and <see cref="SongAnalysis.Taps"/>
    /// (both removed when there are no taps).</summary>
    public static void Apply(SongAnalysis a, IReadOnlyList<(double[] Taps, double? PadSec)> runs)
    {
        a.Layers.Remove(Layer);
        a.Taps = null;
        if (runs.All(r => r.Taps.Length == 0)) return;

        var all = a.Layers.Values.SelectMany(l => l).OrderBy(o => o.T).ToArray();
        var strong = a.Layers.Values.SelectMany(StrongHalf).OrderBy(o => o.T).ToArray();
        var strongT = strong.Select(o => o.T).ToArray();
        var allT = all.Select(o => o.T).ToArray();

        var info = new TapInfo();
        var points = new List<(double T, int Run, Onset? On)>();
        for (int k = 0; k < runs.Count; k++)
        {
            double pad = a.Audio.PadSec - (runs[k].PadSec ?? a.Audio.PadSec);
            var taps = runs[k].Taps.Select(t => t + pad).Where(t => t >= 0).ToArray();
            var (lag, sure) = Lag(taps, strongT);
            info.OffsetsSec.Add(Math.Round(lag, 4));
            info.Confident.Add(sure);
            foreach (double raw in taps)
            {
                double t = raw - lag;
                var on = Nearest(strong, strongT, t, SnapSec) ?? Nearest(all, allT, t, HitSec);
                points.Add((on?.T ?? t, k, on));
            }
        }

        // merge: a tap of the other run this close is a double, of the same run a repeat
        points.Sort((x, y) => x.T.CompareTo(y.T));
        var layer = new List<Onset>();
        int lastRun = -1; // run of the last kept tap (-1 after a double)
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (layer.Count > 0 && p.T - layer[^1].T < SameTapSec && p.Run == lastRun) continue;
            bool both = i + 1 < points.Count && points[i + 1].Run != p.Run && points[i + 1].T - p.T <= DoubleSec;
            double t = p.T;
            var on = p.On;
            if (both)
            {
                var q = points[++i];
                // both snapped: keep the stronger onset; else the snapped one, else the mean
                (t, on) = (p.On, q.On) switch
                {
                    ({ } x, { } y) => x.S >= y.S ? (x.T, x) : (y.T, y),
                    ({ } x, null) => (x.T, x),
                    (null, { } y) => (y.T, y),
                    _ => ((p.T + q.T) / 2, null),
                };
                info.Doubles.Add(t);
            }
            if (on != null) info.Snapped++;
            layer.Add(new Onset { T = t, S = both ? 1 : 0.8, Br = on?.Br ?? 0.5 });
            lastRun = both ? -1 : p.Run;
        }
        info.Count = layer.Count;
        a.Layers[Layer] = layer;
        a.Taps = info;
    }

    /// <summary>The latency with the most taps within <see cref="HitSec"/> of a reference onset (5 ms steps,
    /// 3-step smoothing); unshifted when the peak is not clearly above the average (lift &lt; 1.15) or the
    /// run is too short to tell.</summary>
    public static (double Lag, bool Confident) Lag(double[] taps, double[] reference)
    {
        if (taps.Length < 16 || reference.Length < 16) return (0, false);
        int n = (int)Math.Round((MaxLagSec - MinLagSec) / 0.005) + 1;
        var hit = new double[n];
        for (int i = 0; i < n; i++)
        {
            double lag = MinLagSec + i * 0.005;
            hit[i] = taps.Count(t => Distance(reference, t - lag) < HitSec) / (double)taps.Length;
        }
        var smooth = hit.Select((h, i) => (hit[Math.Max(0, i - 1)] + h + hit[Math.Min(n - 1, i + 1)]) / 3).ToArray();
        int best = Array.IndexOf(smooth, smooth.Max());
        double mean = smooth.Average();
        return mean > 0 && smooth[best] / mean >= 1.15 ? (MinLagSec + best * 0.005, true) : (0, false);
    }

    static IEnumerable<Onset> StrongHalf(List<Onset> layer)
    {
        if (layer.Count == 0) return [];
        double median = layer.Select(o => o.S).Order().ElementAt(layer.Count / 2);
        return layer.Where(o => o.S >= median);
    }

    static double Distance(double[] sorted, double t)
    {
        int i = Array.BinarySearch(sorted, t);
        if (i >= 0) return 0;
        i = ~i;
        double d = double.PositiveInfinity;
        if (i < sorted.Length) d = sorted[i] - t;
        if (i > 0) d = Math.Min(d, t - sorted[i - 1]);
        return d;
    }

    static Onset? Nearest(Onset[] onsets, double[] times, double t, double within)
    {
        int i = Array.BinarySearch(times, t);
        if (i < 0) i = ~i;
        Onset? best = null;
        double bestD = within;
        for (int j = Math.Max(0, i - 1); j <= Math.Min(times.Length - 1, i); j++)
            if (Math.Abs(times[j] - t) <= bestD) { bestD = Math.Abs(times[j] - t); best = onsets[j]; }
        return best;
    }
}
