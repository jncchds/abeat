using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Evaluation;

public enum IssueKind { Reset, VisionBlock, Crossover, HighCost, WallClash, BombHit }

public sealed record FlowIssue(double Beat, Hand Hand, IssueKind Kind, double Cost);

public sealed record FlowReport
{
    public DifficultyName Difficulty { get; init; }
    public int Notes { get; init; }
    public int Bombs { get; init; }
    public int Obstacles { get; init; }
    public double DurationSec { get; init; }
    public double Nps { get; init; }
    /// <summary>Highest notes per second over any 4-second window.</summary>
    public double PeakNps { get; init; }
    public int Resets { get; init; }
    /// <summary>Same-parity swings with a bomb in between (intentional, signalled resets).</summary>
    public int BombResets { get; init; }
    public int VisionBlocks { get; init; }
    public int Crossovers { get; init; }
    /// <summary>Notes inside a wall while it passes (unplayable).</summary>
    public int WallClashes { get; init; }
    /// <summary>Bombs that lie in a saber's swing path.</summary>
    public int BombHits { get; init; }
    public double MeanCost { get; init; }
    /// <summary>0..100, higher = smoother: 100 * exp(-mean physical cost / ScoreScale). The scale is
    /// calibrated so curated human maps from BeatSaver average about 85 (abeat bench).</summary>
    public double FlowScore { get; init; }
    public double LeftShare { get; init; }
    public IReadOnlyList<FlowIssue> Issues { get; init; } = [];

    public override string ToString() =>
        $"{Difficulty,-10} notes {Notes,5}  nps {Nps,5:0.00} (peak {PeakNps,5:0.00})  flow {FlowScore,5:0.0}  " +
        $"resets {Resets,3} (+{BombResets} bomb)  vision {VisionBlocks,3}  cross {Crossovers,3}  walls {Obstacles,3} (clash {WallClashes})  bombs {Bombs,3} (hit {BombHits})  L/R {LeftShare:P0}";
}

/// <summary>Scores a difficulty with the physical part of the swing cost model. Works on any map,
/// so generated maps can be compared with human-made ones.</summary>
public static class FlowAnalyzer
{
    /// <summary>Mean cost of curated human maps was ~1.82 with the current weights; 11 maps that to ~85.</summary>
    public const double ScoreScale = 11;

    public static FlowReport Analyze(DifficultyMap map, double bpm, FlowWeights? weights = null, double minSameHandGap = 0.2)
    {
        var model = new SwingCostModel(weights ?? new FlowWeights());
        double spb = 60.0 / bpm;
        var notes = map.Notes.OrderBy(n => n.Beat).ToList();
        var bombBeats = map.Bombs.Select(b => b.Beat).Order().ToArray();
        var states = new[] { HandState.Initial(Hand.Left), HandState.Initial(Hand.Right) };
        var issues = new List<FlowIssue>();
        double total = 0;
        int resets = 0, bombResets = 0, vision = 0, cross = 0;

        // next directional note per hand, so dots can take the direction that leads into it
        var nextDir = new Dictionary<ColorNote, ColorNote?>(ReferenceEqualityComparer.Instance);
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            ColorNote? next = null;
            foreach (var n in notes.Where(n => n.Hand == hand).Reverse())
            {
                nextDir[n] = next;
                if (n.Direction != CutDirection.Any) next = n;
            }
        }

        int i = 0;
        while (i < notes.Count)
        {
            // notes on the same beat are evaluated against the states before that beat
            int j = i;
            while (j < notes.Count && notes[j].Beat - notes[i].Beat < 1e-3) j++;
            var before = (HandState[])states.Clone();
            // with several notes for one hand on a beat (stacks/sliders), score only the first
            foreach (var group in notes.Skip(i).Take(j - i).GroupBy(n => n.Hand))
            {
                var n = group.OrderByDescending(x => x.Y).First();
                int h = (int)n.Hand;
                double t = n.Beat * spb;
                var s = before[h];
                // sliders / windows: a note right after the previous one of this hand is the same swing
                if (s.Active && t - s.Time < SwingCostModel.SliderGapSec) continue;
                var dir = n.Direction;
                if (dir == CutDirection.Any)
                {
                    // dots: the player picks the direction that leads cleanly into the next note
                    var nx = nextDir[n];
                    if (nx != null && (nx.Beat - n.Beat) * spb < 1.5)
                    {
                        var want = -Swing.Vector(nx.Direction);
                        if (!SwingCostModel.IsReset(n.Hand, s, want) || !s.Active) dir = Swing.FromVector(want);
                    }
                }
                var c = model.Physical(n.Hand, s, before[1 - h], t, n.X, n.Y, dir, minSameHandGap);
                double cost = c.Physical;
                if (c.Reset)
                {
                    bool bomb = s.Active && HasBombBetween(bombBeats, s.Time / spb, n.Beat);
                    if (bomb) { bombResets++; cost -= model.Weights.Reset - model.Weights.SlowReset; }
                    else { resets++; issues.Add(new FlowIssue(n.Beat, n.Hand, IssueKind.Reset, cost)); }
                }
                if (c.VisionBlock) { vision++; issues.Add(new FlowIssue(n.Beat, n.Hand, IssueKind.VisionBlock, cost)); }
                if (c.Crossover) { cross++; issues.Add(new FlowIssue(n.Beat, n.Hand, IssueKind.Crossover, cost)); }
                if (!c.Reset && cost > 8) issues.Add(new FlowIssue(n.Beat, n.Hand, IssueKind.HighCost, cost));
                total += cost;
                states[h] = s.After(t, n.X, n.Y, dir, SwingCostModel.EffectiveSwing(s, dir), n.Hand);
            }
            i = j;
        }

        int wallClashes = 0;
        foreach (var n in notes)
            if (map.Obstacles.Any(o => WallGenerator.Hits(o, n, 0)))
            {
                wallClashes++;
                issues.Add(new FlowIssue(n.Beat, n.Hand, IssueKind.WallClash, 0));
            }
        int bombHits = 0;
        if (map.Bombs.Count > 0)
        {
            var paths = new[] { new SaberPath(notes, Hand.Left, bpm), new SaberPath(notes, Hand.Right, bpm) };
            foreach (var b in map.Bombs)
                for (int h = 0; h < 2; h++)
                    if (paths[h].MinDistance(b.Beat, b.X, b.Y, 0.05) < BombGenerator.HitDistance * 0.7)
                    {
                        bombHits++;
                        issues.Add(new FlowIssue(b.Beat, (Hand)h, IssueKind.BombHit, 0));
                        break;
                    }
        }
        issues.Sort((x, y) => x.Beat.CompareTo(y.Beat));

        double duration = notes.Count > 1 ? (notes[^1].Beat - notes[0].Beat) * spb : 0;
        double mean = notes.Count > 0 ? total / notes.Count : 0;
        return new FlowReport
        {
            Difficulty = map.Difficulty,
            Notes = notes.Count,
            Bombs = map.Bombs.Count,
            Obstacles = map.Obstacles.Count,
            DurationSec = duration,
            Nps = duration > 0 ? notes.Count / duration : 0,
            PeakNps = PeakNps(notes, spb, 4.0),
            Resets = resets,
            BombResets = bombResets,
            VisionBlocks = vision,
            Crossovers = cross,
            WallClashes = wallClashes,
            BombHits = bombHits,
            MeanCost = mean,
            FlowScore = 100 * Math.Exp(-mean / ScoreScale),
            LeftShare = notes.Count > 0 ? notes.Count(n => n.Hand == Hand.Left) / (double)notes.Count : 0,
            Issues = issues,
        };
    }

    static bool HasBombBetween(double[] bombs, double b0, double b1)
    {
        int idx = Array.BinarySearch(bombs, b0 + 1e-4);
        if (idx < 0) idx = ~idx;
        return idx < bombs.Length && bombs[idx] < b1 - 1e-4;
    }

    static double PeakNps(List<ColorNote> notes, double spb, double window)
    {
        double peak = 0;
        int a = 0;
        for (int b = 0; b < notes.Count; b++)
        {
            while ((notes[b].Beat - notes[a].Beat) * spb > window) a++;
            peak = Math.Max(peak, (b - a + 1) / window);
        }
        return peak;
    }
}
