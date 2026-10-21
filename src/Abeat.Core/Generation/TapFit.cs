using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>What <see cref="TapFit.Fit"/> found in the taps and the limits it set.</summary>
public sealed record TapFitResult
{
    /// <summary>Taps the fit looked at (after the lead-in).</summary>
    public int Taps { get; init; }
    /// <summary>Shortest time between two taps, and between every other tap (one hand when the hands alternate).</summary>
    public double MinGapSec { get; init; }
    public double MinHandGapSec { get; init; }
    /// <summary>Notes per second the busiest tapped bar needs (doubles count as one moment).</summary>
    public double BusiestBarNps { get; init; }
    /// <summary>New limits per difficulty: the five speed and density limits of every difficulty.</summary>
    public Dictionary<DifficultyName, ProfileOverride> Overrides { get; init; } = [];
}

/// <summary>Speed and density limits that let Expert+ give every tap its note: max notes/s high enough
/// for the busiest tapped bar (<see cref="RhythmSelector"/> grants a bar a note per tap up to it), the any-hand
/// gap no longer than the closest two taps, the hand gap no longer than every other tap (the hands
/// alternate through a run) and the burst gap no longer than the hand gap. Limits only loosen, never
/// tighten. The other difficulties scale by the same ratio to their built-in values as Expert+, so their
/// spacing to Expert+ stays the same.</summary>
public static class TapFit
{
    /// <summary>Null when the analysis has no taps.</summary>
    public static TapFitResult? Fit(SongAnalysis a, GeneratorSettings s)
    {
        if (!a.Layers.TryGetValue(TapAligner.Layer, out var layer)) return null;
        var t = layer.Select(o => o.T).Where(x => x >= s.LeadInSec).Order().ToArray();
        if (t.Length == 0) return null;

        double minGap = double.PositiveInfinity, minHand = double.PositiveInfinity;
        for (int i = 1; i < t.Length; i++)
        {
            // taps this close share a grid slot and become one note whatever the limits
            if (t[i] - t[i - 1] > 0.02) minGap = Math.Min(minGap, t[i] - t[i - 1]);
            if (i >= 2 && t[i] - t[i - 2] > 0.02) minHand = Math.Min(minHand, t[i] - t[i - 2]);
        }

        // bars as RhythmSelector counts them: four beats from the downbeat phase
        var top = DifficultyProfile.Default(DifficultyName.ExpertPlus);
        double busiest = 0;
        foreach (var bar in t.GroupBy(x => (int)Math.Floor((Math.Round(a.SecondsToBeat(x) * 4) / 4 - a.DownbeatPhase) / 4)))
        {
            double b0 = a.BeatToSeconds(bar.Key * 4.0 + a.DownbeatPhase), b1 = a.BeatToSeconds((bar.Key + 1) * 4.0 + a.DownbeatPhase);
            if (b1 > b0) busiest = Math.Max(busiest, bar.Count() / (b1 - b0));
        }

        // RhythmSelector's tapped-bar ceiling is floor(MaxNps * bar / (1 + DoubleRate)); a little headroom for rounding
        double maxNps = Math.Max(top.MaxNps, Math.Ceiling(busiest * (1 + top.DoubleRate) * 1.02 * 10) / 10);
        double gap = Math.Min(top.MinGapSec, Floor(minGap - 0.003));
        double hand = Math.Min(top.MinSameHandGapSec, Floor(minHand - 0.003));
        double burst = Math.Min(top.BurstGapSec, hand);

        double rNps = maxNps / top.MaxNps, rGap = gap / top.MinGapSec, rHand = hand / top.MinSameHandGapSec, rBurst = burst / top.BurstGapSec;
        var overrides = new Dictionary<DifficultyName, ProfileOverride>();
        foreach (var d in Enum.GetValues<DifficultyName>())
        {
            var p = DifficultyProfile.Default(d);
            double h = Math.Max(MinSec, Math.Round(p.MinSameHandGapSec * rHand, 3));
            overrides[d] = new ProfileOverride
            {
                BaseNps = p.BaseNps,
                MaxNps = Math.Round(p.MaxNps * rNps, 1),
                MinGapSec = Math.Max(MinSec, Math.Round(p.MinGapSec * rGap, 3)),
                MinSameHandGapSec = h,
                BurstGapSec = Math.Min(h, Math.Max(MinSec, Math.Round(p.BurstGapSec * rBurst, 3))),
            };
        }
        return new TapFitResult
        {
            Taps = t.Length,
            MinGapSec = double.IsFinite(minGap) ? Math.Round(minGap, 3) : 0,
            MinHandGapSec = double.IsFinite(minHand) ? Math.Round(minHand, 3) : 0,
            BusiestBarNps = Math.Round(busiest, 2),
            Overrides = overrides,
        };
    }

    /// <summary>Shortest gap the fit sets: below it notes come faster than anyone can cut them.</summary>
    const double MinSec = 0.04;

    static double Floor(double sec) => double.IsFinite(sec) ? Math.Max(MinSec, Math.Floor(sec * 200) / 200) : double.PositiveInfinity;
}
