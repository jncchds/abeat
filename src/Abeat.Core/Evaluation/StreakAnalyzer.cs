using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Evaluation;

/// <param name="Known4">Share of a hand's notes (with three earlier notes since its last pause) that complete a
/// streak of 4+ notes from the difficulty's vocabulary.</param>
/// <param name="Known8">The same for streaks of 8+ notes.</param>
/// <param name="Long">Share of all hand notes inside a run of 8+ notes where every 4 in a row is a known streak:
/// long streaks built from human pieces.</param>
/// <param name="Pair4">Share of two-hand 4-windows (4 note moments in a row since the last pause, both hands in
/// them) that are two-hand streaks from the vocabulary.</param>
/// <param name="PairWindows">How many two-hand 4-windows the map has.</param>
public sealed record StreakReport(double Known4, double Known8, double Long, int Notes, double Pair4 = 0, int PairWindows = 0);

/// <summary>How much of a map's per-hand note sequence is made of the streak vocabulary
/// (<see cref="StylePrior.Streaks"/>), read the way scripts/style_prior.py learns it: one figure per hand
/// per beat (a stack's lowest note), streaks broken by pauses over <see cref="MovementAnalyzer.MaxGapSec"/>.</summary>
public static class StreakAnalyzer
{
    public static StreakReport? Analyze(DifficultyMap map, TempoMap tempo)
    {
        if (StylePrior.For(map.Difficulty) is not { Streaks: not null } prior) return null;
        int eligible4 = 0, known4 = 0, eligible8 = 0, known8 = 0, total = 0, inLong = 0;
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var seq = map.Notes.Where(n => n.Hand == hand).GroupBy(n => Math.Round(n.Beat, 3)).OrderBy(g => g.Key)
                .Select(g => (T: tempo.BeatToSeconds(g.Key), N: g.OrderBy(n => n.X).ThenBy(n => n.Y).First())).ToList();
            var run = new List<int>();
            var completes4 = new List<bool>();
            void Flush()
            {
                // runs of notes completing a known 4-streak: k in a row cover k + 3 notes
                for (int i = 0; i < completes4.Count;)
                {
                    if (!completes4[i]) { i++; continue; }
                    int j = i;
                    while (j < completes4.Count && completes4[j]) j++;
                    if (j - i + 3 >= 8) inLong += j - i + 3;
                    i = j;
                }
                run.Clear();
                completes4.Clear();
            }
            double last = double.NegativeInfinity;
            foreach (var (t, n) in seq)
            {
                if (t - last > MovementAnalyzer.MaxGapSec) Flush();
                last = t;
                total++;
                run.Add(StylePrior.Figure(n.X, n.Y, n.Direction));
                int longest = 0;
                foreach (int len in StylePrior.StreakLengths)
                {
                    if (run.Count < len || !prior.HasStreak(hand, len, StylePrior.StreakHash(run.Skip(run.Count - len)))) break;
                    longest = len;
                }
                if (run.Count >= 4) { eligible4++; if (longest >= 4) known4++; completes4.Add(longest >= 4); }
                if (run.Count >= 8) { eligible8++; if (longest >= 8) known8++; }
            }
            Flush();
        }
        var (pairs, pairKnown) = Pairs(map, tempo, prior);
        return new StreakReport(eligible4 > 0 ? (double)known4 / eligible4 : 0, eligible8 > 0 ? (double)known8 / eligible8 : 0,
            total > 0 ? (double)inLong / total : 0, total, pairs > 0 ? (double)pairKnown / pairs : 0, pairs);
    }

    /// <summary>Two-hand 4-windows and how many are known two-hand streaks: one token per note moment (a hand's
    /// first note by x then y; both hands = a double), runs broken by pauses between moments.</summary>
    static (int windows, int known) Pairs(DifficultyMap map, TempoMap tempo, StylePrior prior)
    {
        if (prior.PairStreaks is null) return (0, 0);
        int windows = 0, known = 0;
        var run = new List<int>();
        double last = double.NegativeInfinity;
        foreach (var g in map.Notes.GroupBy(n => Math.Round(n.Beat, 3)).OrderBy(g => g.Key))
        {
            double t = tempo.BeatToSeconds(g.Key);
            if (t - last > MovementAnalyzer.MaxGapSec) run.Clear();
            last = t;
            int? Fig(Hand h) => g.Where(n => n.Hand == h).OrderBy(n => n.X).ThenBy(n => n.Y).Select(n => (int?)StylePrior.Figure(n.X, n.Y, n.Direction)).FirstOrDefault();
            var (l, r) = (Fig(Hand.Left), Fig(Hand.Right));
            run.Add(l is { } lf && r is { } rf ? StylePrior.PairDoubleToken(lf, rf) : l is { } lo ? StylePrior.PairToken(Hand.Left, lo) : StylePrior.PairToken(Hand.Right, r!.Value));
            if (run.Count < StylePrior.PairLength) continue;
            var window = run.GetRange(run.Count - StylePrior.PairLength, StylePrior.PairLength);
            if (!StylePrior.BothHands(window.ToArray())) continue;
            windows++;
            if (prior.HasPairStreak(StylePrior.StreakHash(window))) known++;
        }
        return (windows, known);
    }
}
