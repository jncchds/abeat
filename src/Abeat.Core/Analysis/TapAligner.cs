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
    /// <summary>Latency removed from each appended batch of each run (seconds), in the order they were added.</summary>
    public List<List<double>> AppendedOffsetsSec { get; set; } = [];
    /// <summary>Times (seconds) both runs tapped: the user wants a double there.</summary>
    public List<double> Doubles { get; set; } = [];
}

/// <summary>One tap-along run: its own taps (recorded, or a map's notes when <paramref name="Exact"/>) on a
/// song.egg padded <paramref name="PadSec"/>, plus batches appended later from the playhead (each its own
/// recording, so its own latency).</summary>
public sealed record TapRunData(double[] Taps, double? PadSec = null, bool Exact = false, IReadOnlyList<double[]>? Appended = null);

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
/// </list>
/// Exact runs (a human map's notes turned into taps) skip the latency and the snapping: their times are kept.
/// Batches appended to a run get their own latency (against the onsets and an exact run's notes; a batch too
/// short to tell borrows its run's, the song's other recordings' or <see cref="TypicalLagSec"/>).</summary>
public static class TapAligner
{
    public const string Layer = "taps";
    public const double MinLagSec = -0.06, MaxLagSec = 0.16;
    /// <summary>Latency for an appended batch too short to measure when nothing else in the song tells:
    /// what the browser recordings measured (+60 and +70 ms on two tapped songs).</summary>
    public const double TypicalLagSec = 0.065;
    /// <summary>Appended batches are short: fewer taps than a whole run need to find their latency.</summary>
    const int MinBatchTaps = 8;
    const double HitSec = 0.02, SnapSec = 0.035, DoubleSec = 0.05, SameTapSec = 0.04;

    /// <summary>Reads a song's taps.json: <c>{"runs":[{"taps":[s...],"padSec":p,"exact":false,"appended":[{"taps":[s...]}]}]}</c>.</summary>
    public static List<TapRunData> LoadRuns(string path)
    {
        var runs = new List<TapRunData>();
        if (!File.Exists(path)) return runs;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("runs", out var arr) || arr.ValueKind != JsonValueKind.Array) return runs;
        static double[] Times(JsonElement e) => e.TryGetProperty("taps", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetDouble()).Where(double.IsFinite).Order().ToArray() : [];
        foreach (var r in arr.EnumerateArray())
        {
            double? pad = r.TryGetProperty("padSec", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            var appended = r.TryGetProperty("appended", out var ap) && ap.ValueKind == JsonValueKind.Array
                ? ap.EnumerateArray().Select(Times).Where(b => b.Length > 0).ToArray() : [];
            var run = new TapRunData(Times(r), pad, r.TryGetProperty("exact", out var x) && x.ValueKind == JsonValueKind.True, appended);
            if (run.Taps.Length > 0 || appended.Length > 0) runs.Add(run);
        }
        return runs;
    }

    /// <summary>Adds the aligned taps as layer <see cref="Layer"/> and <see cref="SongAnalysis.Taps"/>
    /// (both removed when there are no taps).</summary>
    public static void Apply(SongAnalysis a, IReadOnlyList<TapRunData> runs)
    {
        a.Layers.Remove(Layer);
        a.Taps = null;
        if (runs.All(r => r.Taps.Length == 0 && (r.Appended?.Count ?? 0) == 0)) return;

        var all = a.Layers.Values.SelectMany(l => l).OrderBy(o => o.T).ToArray();
        var strong = a.Layers.Values.SelectMany(StrongHalf).OrderBy(o => o.T).ToArray();
        var strongT = strong.Select(o => o.T).ToArray();
        var allT = all.Select(o => o.T).ToArray();

        // every part (a run's own taps, each appended batch) on the current padding, with its own latency:
        // appended taps come from another recording session, and an exact run has none at all
        var parts = new List<(int Run, double[] Taps, bool Exact, double Lag, bool Sure)>();
        for (int k = 0; k < runs.Count; k++)
        {
            double pad = a.Audio.PadSec - (runs[k].PadSec ?? a.Audio.PadSec);
            double[] Shift(double[] taps) => [.. taps.Select(t => t + pad).Where(t => t >= 0)];
            var own = Shift(runs[k].Taps);
            // an exact run's notes help find the latency of taps added to it
            var reference = runs[k].Exact ? [.. strongT.Concat(own).Order()] : strongT;
            if (runs[k].Exact) parts.Add((k, own, true, 0, true));
            else { var (lag, sure) = Lag(own, strongT); parts.Add((k, own, false, lag, sure)); }
            foreach (var batch in runs[k].Appended ?? [])
            {
                var taps = Shift(batch);
                var (lag, sure) = Lag(taps, reference, MinBatchTaps);
                parts.Add((k, taps, false, lag, sure));
            }
        }
        // a batch too short to tell takes its run's latency, else the song's other recordings', else a typical one
        var known = parts.Where(p => !p.Exact && p.Sure && p.Taps.Length > 0).Select(p => p.Lag).Order().ToList();
        double fallback = known.Count > 0 ? known[known.Count / 2] : TypicalLagSec;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p.Exact || p.Sure) continue;
            var own = parts.FirstOrDefault(q => q.Run == p.Run && !q.Exact && q.Sure && q.Taps.Length > 0);
            bool isOwn = parts.FindIndex(q => q.Run == p.Run) == i;
            // a run's own unclear recording stays unshifted, as before; unclear appended batches borrow
            if (!isOwn) parts[i] = p with { Lag = own.Taps is { Length: > 0 } ? own.Lag : fallback };
        }

        var info = new TapInfo();
        var points = new List<(double T, int Run, Onset? On, bool Exact)>();
        for (int k = 0; k < runs.Count; k++)
        {
            var mine = parts.Where(p => p.Run == k).ToList();
            info.OffsetsSec.Add(Math.Round(mine[0].Lag, 4));
            info.Confident.Add(mine[0].Sure);
            info.AppendedOffsetsSec.Add([.. mine.Skip(1).Select(p => Math.Round(p.Lag, 4))]);
            foreach (var p in mine)
                foreach (double raw in p.Taps)
                {
                    if (p.Exact)
                    {
                        // an onset at the note only lends its brightness; the time stays the note's
                        points.Add((raw, k, Nearest(all, allT, raw, HitSec) is { } o ? new Onset { T = raw, S = o.S, Br = o.Br } : null, true));
                        continue;
                    }
                    double t = raw - p.Lag;
                    var on = Nearest(strong, strongT, t, SnapSec) ?? Nearest(all, allT, t, HitSec);
                    points.Add((on?.T ?? t, k, on, false));
                }
        }

        // merge: a tap of the other run this close is a double, of the same run a repeat
        points.Sort((x, y) => x.T.CompareTo(y.T));
        var layer = new List<Onset>();
        int lastRun = -1; // run of the last kept tap (-1 after a double)
        bool lastExact = false;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (layer.Count > 0 && p.T - layer[^1].T < SameTapSec && p.Run == lastRun)
            {
                // the same tap twice (an appended one on a note already there): a map note's time wins
                if (p.Exact && !lastExact) { layer[^1] = new Onset { T = p.T, S = layer[^1].S, Br = p.On?.Br ?? layer[^1].Br }; lastExact = true; }
                continue;
            }
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
            lastExact = p.Exact;
        }
        info.Count = layer.Count;
        a.Layers[Layer] = layer;
        a.Taps = info;
    }

    /// <summary>The latency with the most taps within <see cref="HitSec"/> of a reference onset (5 ms steps,
    /// 3-step smoothing); unshifted when the peak is not clearly above the average (lift &lt; 1.15) or the
    /// run is too short to tell.</summary>
    public static (double Lag, bool Confident) Lag(double[] taps, double[] reference, int minTaps = 16)
    {
        if (taps.Length < minTaps || reference.Length < 16) return (0, false);
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
