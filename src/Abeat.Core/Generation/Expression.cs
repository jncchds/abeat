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

    /// <summary>Arcs over held melody notes: a single note held for at least a beat gets an arc to the same
    /// hand's next note (1-4 beats later) when the sound covers a good part of that gap. Hands usually
    /// alternate, so the sound reaching the next melody note covers about half of it. Skipped when the
    /// next swing is a reset (no continuous motion to draw) or a gameplay wall passes in between.</summary>
    public static void AddArcs(DifficultyMap map, IReadOnlyList<RhythmEvent> events, double bpm)
    {
        double spb = 60.0 / bpm;
        var byBeat = events.GroupBy(e => Math.Round(e.Beat, 4)).ToDictionary(g => g.Key, g => g.First());
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var notes = map.Notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat).ToList();
            var state = HandState.Initial(hand);
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                var v = SwingCostModel.EffectiveSwing(state, n.Direction);
                state = state.After(n.Beat * spb, n.X, n.Y, n.Direction, v, hand);
                if (i + 1 >= notes.Count || !byBeat.TryGetValue(Math.Round(n.Beat, 4), out var e) || e.IsDouble) continue;
                var tail = notes[i + 1];
                double gapBeats = tail.Beat - n.Beat, gapSec = gapBeats * spb;
                if (gapBeats < 1 - 1e-6 || gapBeats > 4 + 1e-6 || gapSec < 0.4) continue;
                if (e.Sustain < Math.Max(spb, 0.45 * gapSec)) continue;
                var tv = SwingCostModel.EffectiveSwing(state, tail.Direction);
                if (SwingCostModel.IsReset(hand, state, tv)) continue;
                if (map.Obstacles.Any(o => !IsSideWall(o) && o.Beat < tail.Beat && o.Beat + o.Duration > n.Beat)) continue;
                map.Arcs.Add(new Arc(n.Beat, n.X, n.Y, hand, Swing.FromVector(v), tail.Beat, tail.X, tail.Y, Swing.FromVector(tv)));
            }
        }
        map.Arcs.Sort((a, b) => a.Beat.CompareTo(b.Beat));
    }

    static bool IsSideWall(Obstacle o) => o.Width == 1 && o.X is 0 or 3;
}
