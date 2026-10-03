using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Approximate saber position over time for one hand, in grid units. During a swing the saber
/// moves from the note's entry to its exit; between notes it travels from exit to the next entry.
/// Between two same-parity swings (a reset) the motion is unknown, so the path returns null there.</summary>
public sealed class SaberPath
{
    readonly List<(double beat, Vec2 entry, Vec2 exit, Parity parity)> swings = [];

    public SaberPath(IEnumerable<ColorNote> notes, Hand hand)
    {
        var state = HandState.Initial(hand);
        foreach (var n in notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat))
        {
            if (swings.Count > 0 && Math.Abs(swings[^1].beat - n.Beat) < 1e-3) continue; // stacks: keep first
            var v = SwingCostModel.EffectiveSwing(state, n.Direction);
            var c = new Vec2(n.X, n.Y);
            swings.Add((n.Beat, c - v * SwingCostModel.HalfSwing, c + v * SwingCostModel.HalfSwing, Swing.ParityOf(hand, v)));
            state = state.After(0, n.X, n.Y, n.Direction, v, hand);
        }
    }

    public Vec2? At(double beat)
    {
        int i = swings.FindLastIndex(s => s.beat <= beat);
        if (i < 0 || i == swings.Count - 1)
        {
            // just before the first swing / just after the last one the saber is near the note
            if (i < 0 && swings.Count > 0 && swings[0].beat - beat < 0.25) return swings[0].entry;
            if (i == swings.Count - 1 && i >= 0 && beat - swings[i].beat < 0.25) return swings[i].exit;
            return null;
        }
        var (b0, e0, x0, p0) = swings[i];
        var (b1, e1, _, p1) = swings[i + 1];
        double half = Math.Min(0.2, (b1 - b0) / 4);
        if (beat <= b0 + half) return Lerp(e0, x0, 0.5 + 0.5 * (beat - b0) / half);
        if (beat >= b1 - half) return Lerp(e1, swings[i + 1].exit, 0.5 * (beat - (b1 - half)) / half);
        if (p0 == p1) return null; // reset: the player re-winds outside the grid
        return Lerp(x0, e1, (beat - b0 - half) / (b1 - b0 - 2 * half));
    }

    static Vec2 Lerp(Vec2 a, Vec2 b, double f) => a + (b - a) * Math.Clamp(f, 0, 1);

    /// <summary>Closest the saber comes to a cell within +-window beats.</summary>
    public double MinDistance(double beat, int x, int y, double window = 0.15)
    {
        double best = double.PositiveInfinity;
        for (double t = beat - window; t <= beat + window + 1e-9; t += window / 3)
            if (At(t) is { } p) best = Math.Min(best, (p - new Vec2(x, y)).Length);
        return best;
    }
}

/// <summary>Bombs that signal resets and decorate accents, never in a saber's path.</summary>
public static class BombGenerator
{
    /// <summary>Saber closer than this (grid cells) to a bomb counts as a hit.</summary>
    public const double HitDistance = 0.85;

    public static void Generate(DifficultyMap map, DifficultyProfile p, IReadOnlyList<RhythmEvent> events, GeneratorSettings s, double bpm)
    {
        if (!s.Bombs || p.Name == DifficultyName.Easy) return;
        var paths = new[] { new SaberPath(map.Notes, Hand.Left), new SaberPath(map.Notes, Hand.Right) };
        var bombs = new List<BombNote>();
        double spb = 60.0 / bpm;

        // 1) reset bombs: same-parity swings in a row get a bomb where the natural reversal would cut
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var notes = map.Notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat).ToList();
            var state = HandState.Initial(hand);
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                var v = SwingCostModel.EffectiveSwing(state, n.Direction);
                var next = state.After(n.Beat * spb, n.X, n.Y, n.Direction, v, hand);
                if (i + 1 < notes.Count && notes[i + 1].Beat - n.Beat >= 1)
                {
                    var nn = notes[i + 1];
                    var nv = SwingCostModel.EffectiveSwing(next, nn.Direction);
                    if (Swing.ParityOf(hand, nv) == next.Parity)
                    {
                        // where the natural reversal would cut: one cell against the last swing (above a down-cut)
                        var spot = new Vec2(n.X, n.Y) - v;
                        int bx = Math.Clamp((int)Math.Round(spot.X), 0, 3), by = Math.Clamp((int)Math.Round(spot.Y), 0, 2);
                        if (by == n.Y && bx == n.X) by = Math.Clamp(by + (v.Y < 0 ? 1 : -1), 0, 2);
                        double bb = n.Beat + Math.Min(1, (nn.Beat - n.Beat) / 2);
                        var other = paths[1 - (int)hand];
                        if (other.MinDistance(bb, bx, by) > HitDistance && !(bx == nn.X && by == nn.Y))
                            bombs.Add(new BombNote(bb, bx, by));
                    }
                }
                state = next;
            }
        }

        // 2) accent bombs on strong single-note events, on the idle hand's side away from its path
        if (p.BombRate > 0 && p.Name >= DifficultyName.Hard)
        {
            var rng = new Random(s.Seed * 31 + (int)p.Name);
            foreach (var e in events.Where(e => !e.IsDouble && e.Strength > 0.7))
            {
                if (rng.NextDouble() > p.BombRate) continue;
                var at = map.Notes.FirstOrDefault(n => Math.Abs(n.Beat - e.Beat) < 1e-3);
                if (at == null) continue;
                var idle = at.Hand == Hand.Left ? Hand.Right : Hand.Left;
                int bx = idle == Hand.Right ? 3 : 0;
                foreach (int by in new[] { 2, 0 })
                {
                    if (Safe(bx, by, e.Beat, paths, map.Notes))
                    {
                        bombs.Add(new BombNote(e.Beat, bx, by));
                        break;
                    }
                }
            }
        }

        foreach (var b in bombs.DistinctBy(b => (Math.Round(b.Beat, 3), b.X, b.Y)))
            if (!map.Notes.Any(n => n.X == b.X && n.Y == b.Y && Math.Abs(n.Beat - b.Beat) < 0.5))
                map.Bombs.Add(b);
        map.Bombs.Sort((a, b) => a.Beat.CompareTo(b.Beat));
    }

    static bool Safe(int x, int y, double beat, SaberPath[] paths, IEnumerable<ColorNote> notes) =>
        !(y == 1 && x is 1 or 2)
        && paths.All(p => p.MinDistance(beat, x, y, 0.3) > HitDistance * 1.2)
        && !notes.Any(n => n.X == x && n.Y == y && Math.Abs(n.Beat - beat) < 1);
}
