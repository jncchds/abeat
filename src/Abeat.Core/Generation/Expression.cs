using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Touches on a planned map that show how the music moves rather than where it hits: cut
/// angles that lean with the melody and arcs over held notes. Neither changes hands, cells or cut
/// directions, so flow and parity stay exactly as planned.</summary>
public static class Expression
{
    /// <summary>Pitch change (brightness units) between melody notes that counts as a rising/falling line.</summary>
    const double SlopeThreshold = 0.06;

    /// <summary>Single melody notes that continue a pitch line (two steps the same way) lean with it: a rising line tilts vertical cuts like "/"
    /// and lifts the end of horizontal cuts, a falling line the other way. 15°, or 30° for big leaps on
    /// Expert and up. Diagonals, dots and doubles are left alone. Offsets are counter-clockwise.</summary>
    public static void ApplyAngleOffsets(DifficultyMap map, IReadOnlyList<RhythmEvent> events, DifficultyProfile p)
    {
        if (p.Name < DifficultyName.Normal) return;
        var index = map.Notes.Select((n, i) => (n, i)).GroupBy(x => Math.Round(x.n.Beat, 4)).ToDictionary(g => g.Key, g => g.ToList());
        var lastSlope = new Dictionary<string, double>();
        foreach (var e in events)
        {
            if (e.PitchSlope == 0) continue;
            // a line, not jitter: the previous step of this layer moved the same way
            bool line = lastSlope.TryGetValue(e.Layer, out double prev) && Math.Sign(prev) == Math.Sign(e.PitchSlope) && Math.Abs(prev) >= SlopeThreshold / 2;
            lastSlope[e.Layer] = e.PitchSlope;
            if (!line || e.IsDouble || Math.Abs(e.PitchSlope) < SlopeThreshold) continue;
            if (!index.TryGetValue(Math.Round(e.Beat, 4), out var at) || at.Count != 1) continue;
            var (n, i) = at[0];
            int size = Math.Abs(e.PitchSlope) > 0.2 && p.Name >= DifficultyName.Expert ? 30 : 15;
            int rising = e.PitchSlope > 0 ? 1 : -1;
            int ccw = n.Direction switch
            {
                CutDirection.Up or CutDirection.Down => -rising, // "/" is a clockwise lean
                CutDirection.Right => rising,
                CutDirection.Left => -rising,
                _ => 0,
            };
            if (ccw != 0) map.Notes[i] = n with { AngleOffset = ccw * size };
        }
    }

    /// <summary>Arcs per same-hand gap of 0.3-2 s, by difficulty: the median of curated maps that use arcs
    /// (about half of the Easy-Hard maps use none). Curated arcs span a median 0.43 s and a third of them
    /// start on a double.</summary>
    static double ArcShare(DifficultyName d) => d switch
    {
        DifficultyName.Easy => 0.06,
        DifficultyName.Normal or DifficultyName.Hard => 0.075,
        _ => 0.09,
    };

    const double MinArcGapSec = 0.3, MaxArcGapSec = 2.0;

    /// <summary>Melody sound covering at least this share of the gap before an arc may be drawn over it.</summary>
    const double MinArcCover = 0.3;

    /// <summary>Arcs over held melody notes: of all notes followed by the same hand's next note 0.3-2 s
    /// later, the ones whose sound covers the most of that gap get an arc to it, up to
    /// <see cref="ArcShare"/> of those gaps. Skipped when the next swing is a reset (no continuous motion
    /// to draw) or a gameplay wall passes in between.</summary>
    public static void AddArcs(DifficultyMap map, IReadOnlyList<RhythmEvent> events, TempoMap tempo)
    {
        var byBeat = events.GroupBy(e => Math.Round(e.Beat, 4)).ToDictionary(g => g.Key, g => g.First());
        var candidates = new List<(double cover, Arc arc)>();
        int gaps = 0;
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var notes = map.Notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat).ToList();
            var state = HandState.Initial(hand);
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                var v = SwingCostModel.EffectiveSwing(state, n.Direction);
                state = state.After(tempo.BeatToSeconds(n.Beat), n.X, n.Y, n.Direction, v, hand);
                if (i + 1 >= notes.Count) continue;
                var tail = notes[i + 1];
                double gapSec = tempo.Seconds(n.Beat, tail.Beat);
                if (gapSec < MinArcGapSec || gapSec > MaxArcGapSec) continue;
                gaps++;
                if (!byBeat.TryGetValue(Math.Round(n.Beat, 4), out var e)) continue;
                double cover = Math.Min(1, e.Sustain / gapSec);
                if (cover < MinArcCover) continue;
                var tv = SwingCostModel.EffectiveSwing(state, tail.Direction);
                if (SwingCostModel.IsReset(hand, state, tv)) continue;
                if (map.Obstacles.Any(o => !IsSideWall(o) && o.Beat < tail.Beat && o.Beat + o.Duration > n.Beat)) continue;
                candidates.Add((cover + 0.01 * e.Intensity, new Arc(n.Beat, n.X, n.Y, hand, Swing.FromVector(v), tail.Beat, tail.X, tail.Y, Swing.FromVector(tv))));
            }
        }
        int quota = (int)Math.Round(ArcShare(map.Difficulty) * gaps);
        map.Arcs.AddRange(candidates.OrderByDescending(c => c.cover).ThenBy(c => c.arc.Beat).Take(quota).Select(c => c.arc));
        map.Arcs.Sort((a, b) => a.Beat.CompareTo(b.Beat));
    }

    static bool IsSideWall(Obstacle o) => o.Width == 1 && o.X is 0 or 3;

    /// <summary>Chains on notes followed by a fast run in the music (a drum roll, flam or stutter, see
    /// <see cref="RhythmEvent.BurstCount"/>): the head is the planned note and the links continue along its
    /// cut, as human mappers use them, briefly (at most 1/4 beat) with room after. Needs a free path in the
    /// cut direction, a same-hand gap of a beat after it, and no other object on the links' cells.</summary>
    public static void AddChains(DifficultyMap map, IReadOnlyList<RhythmEvent> events, TempoMap tempo, DifficultyProfile p)
    {
        if (p.Name < DifficultyName.Hard) return;
        // seconds between chains: an ornament, not a pattern. Curated maps average 0.5 / 1.0 / 1.6 chains a minute
        // on Hard / Expert / Expert+ (four in five use none)
        double minSpacing = p.Name switch { DifficultyName.Hard => 120, DifficultyName.Expert => 50, _ => 30 };
        var arcEnds = map.Arcs.SelectMany(a => new[] { (Math.Round(a.Beat, 4), a.Hand), (Math.Round(a.TailBeat, 4), a.Hand) }).ToHashSet();
        double lastChain = double.NegativeInfinity;
        foreach (var e in events)
        {
            if (e.BurstCount < 2 || e.BurstSec < 0.06 || e.Intensity < 0.5 || e.Time - lastChain < minSpacing) continue;
            double tailBeat = e.Beat + Math.Clamp(Math.Round(e.BurstSec / tempo.SecPerBeat(e.Beat) * 16) / 16, 0.0625, 0.25);
            var heads = map.Notes.Where(n => Math.Abs(n.Beat - e.Beat) < 1e-4 && n.Direction != CutDirection.Any).ToList();
            var added = new List<Chain>();
            foreach (var n in heads)
            {
                if (arcEnds.Contains((Math.Round(n.Beat, 4), n.Hand))) continue;
                var next = map.Notes.FirstOrDefault(x => x.Hand == n.Hand && x.Beat > n.Beat + 1e-4);
                if (next != null && ((next.Beat - n.Beat) < 1 - 1e-6 || tempo.Seconds(n.Beat, next.Beat) < 0.45)) continue;
                if (ChainFor(map, n, tailBeat) is { } c) added.Add(c);
            }
            // a double gets chains on both notes or none, so the pair stays symmetric
            if (added.Count == 0 || added.Count < heads.Count && e.IsDouble) continue;
            map.Chains.AddRange(added);
            lastChain = e.Time;
        }
    }

    /// <summary>Chain from a note along its cut: two cells when they fit, else one; null when the links
    /// would leave the grid or share a cell with another object nearby in time.</summary>
    static Chain? ChainFor(DifficultyMap map, ColorNote n, double tailBeat)
    {
        var v = Swing.Vector(n.Direction);
        int dx = Math.Sign(Math.Round(v.X, 3)), dy = Math.Sign(Math.Round(v.Y, 3));
        for (int len = 2; len >= 1; len--)
        {
            int tx = n.X + dx * len, ty = n.Y + dy * len;
            if (tx is < 0 or > 3 || ty is < 0 or > 2) continue;
            var cells = Enumerable.Range(1, len).Select(k => (x: n.X + dx * k, y: n.Y + dy * k)).ToList();
            double t0 = n.Beat - 0.25, t1 = tailBeat + 0.5;
            bool blocked = map.Notes.Any(o => o != n && o.Beat > t0 && o.Beat < t1 && cells.Contains((o.X, o.Y)))
                || map.Bombs.Any(b => b.Beat > t0 && b.Beat < t1 && cells.Contains((b.X, b.Y)))
                || map.Obstacles.Any(o => o.Beat < tailBeat && o.Beat + o.Duration > n.Beat
                    && cells.Any(c => c.x >= o.X && c.x < o.X + o.Width && c.y >= o.Y && c.y < o.Y + o.Height));
            if (blocked) continue;
            return new Chain(n.Beat, n.X, n.Y, n.Hand, n.Direction, tailBeat, tx, ty, 3 * len + 1);
        }
        return null;
    }
}
