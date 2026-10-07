using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Walls placed only where they can't conflict with notes:
/// <list type="bullet">
/// <item>crouch walls (full width, top) in the gap right before a big energy jump (a drop)</item>
/// <item>dodge walls (one centre lane, full height) alternating sides in other note-free gaps</item>
/// <item>decorative side walls in the outer lanes during calm sections</item>
/// <item>rhythm walls: short pieces in the upper half of an outer lane on the strongest kicks and
/// snares of louder sections, as curated mappers draw the beat along the edges</item>
/// </list>
/// Every wall is checked against all notes: no note may sit in a wall's cells while it passes.</summary>
public static class WallGenerator
{
    public static void Generate(SongAnalysis a, DifficultyMap map, DifficultyProfile p, GeneratorSettings s)
    {
        if (!s.Walls) return;
        var notes = map.Notes.OrderBy(n => n.Beat).ToList();
        bool allowDodge = p.Name >= DifficultyName.Normal && s.DodgeWalls;
        bool allowCrouch = p.Name >= DifficultyName.Hard && s.CrouchWalls;
        double minGap = p.Name >= DifficultyName.Hard ? 2.5 : 3.5; // beats without notes needed for a gameplay wall

        var gameplay = new List<Obstacle>();
        bool leftSide = true;
        for (int i = 0; i + 1 < notes.Count; i++)
        {
            double g0 = notes[i].Beat, g1 = notes[i + 1].Beat;
            if (g1 - g0 < minGap) continue;
            // leave room to finish the last swing and to get back before the next note
            double start = Math.Ceiling((g0 + 1.0) * 2) / 2, end = g1 - 1.25;
            if (end - start < 1) continue;

            double before = a.EnergyAt(a.BeatToSeconds(g0)), after = a.EnergyAt(a.BeatToSeconds(g1 + 1));
            if (allowCrouch && after - before > 0.25)
            {
                double b = Math.Max(start, end - 1);
                gameplay.Add(new Obstacle(b, Math.Min(1, end - b), 0, 2, 4, 3));
                continue;
            }
            if (!allowDodge) continue;
            // one dodge wall per 2 beats of gap, at most 4, alternating sides
            for (double b = start; b + 1 <= end && gameplay.Count(o => o.Beat >= start) < 4; b += 2)
            {
                gameplay.Add(new Obstacle(b, 1, leftSide ? 1 : 2, 0, 1, 5));
                leftSide = !leftSide;
            }
        }

        var decor = SideWalls(a, notes, p);
        if (s.RhythmWalls && notes.Count > 0) decor = decor.Concat(RhythmWalls(a, p, notes[0].Beat, notes[^1].Beat));
        foreach (var o in gameplay.Concat(decor))
            if (!Clashes(o, notes) && !map.Obstacles.Any(x => Overlaps(x, o)))
                map.Obstacles.Add(o);
        map.Obstacles.Sort((x, y) => x.Beat.CompareTo(y.Beat));
    }

    static IEnumerable<Obstacle> SideWalls(SongAnalysis a, List<ColorNote> notes, DifficultyProfile p)
    {
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(x => x.Energy) : 1;
        foreach (var sec in a.Sections)
        {
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 1;
            if (rel > 0.55) continue; // only calm sections
            double b0 = a.SecondsToBeat(sec.Start), b1 = a.SecondsToBeat(sec.End);
            for (double b = Math.Ceiling(b0 / 4) * 4; b + 6 <= b1; b += 8)
                foreach (int lane in new[] { 0, 3 })
                    yield return new Obstacle(b, 6, lane, 0, 1, 5);
        }
    }

    /// <summary>Rhythm walls: 1/8-beat walls in the top row and above of lane 0 or 3 (alternating), on the
    /// strongest kick and snare hits (above the song's median drum strength), picked strongest first at least
    /// 1.2 s apart in sections at >= 80 % of the loudest section's energy and 2.4 s from 55 % (Easy: 2.4 / 4.8 s).
    /// Curated maps: side-lane walls in 80-93 % of maps, a median 22-51 a minute, mostly 1/8 beat long at
    /// y 2 / height 3 (clear of bottom and middle-row notes), half on a note and half between notes.</summary>
    static IEnumerable<Obstacle> RhythmWalls(SongAnalysis a, DifficultyProfile p, double firstBeat, double lastBeat)
    {
        var hits = a.Layers.TryGetValue("drums", out var drums)
            ? drums.Where(o => o.K is { } k && (k.Contains('k') || k.Contains('s'))).ToList()
            : a.Layers.GetValueOrDefault("low") ?? [];
        if (hits.Count == 0 || a.Sections.Count == 0) yield break;
        double minStrength = hits.Select(o => o.S).Order().ElementAt(hits.Count / 2);
        double maxEnergy = a.Sections.Max(x => x.Energy);
        double t0 = a.BeatToSeconds(firstBeat), t1 = a.BeatToSeconds(lastBeat);
        var picked = new List<double>();
        foreach (var sec in a.Sections)
        {
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 1;
            if (rel < 0.55) continue;
            double spacing = (rel >= 0.8 ? 1.2 : 2.4) * (p.Name == DifficultyName.Easy ? 2 : 1);
            var chosen = new List<double>();
            foreach (var h in hits.Where(o => o.T >= Math.Max(sec.Start, t0) && o.T < Math.Min(sec.End, t1) && o.S >= minStrength)
                .OrderByDescending(o => o.S + (o.K?.Contains('s') == true ? 0.1 : 0)))
                if (chosen.All(t => Math.Abs(t - h.T) >= spacing)) chosen.Add(h.T);
            picked.AddRange(chosen);
        }
        bool left = true;
        foreach (double t in picked.Order())
        {
            yield return new Obstacle(a.SecondsToBeat(t), 0.125, left ? 0 : 3, 2, 1, 3);
            left = !left;
        }
    }

    /// <summary>A note inside the wall's cells while the wall passes (plus a small margin) is unplayable.</summary>
    public static bool Clashes(Obstacle o, IEnumerable<ColorNote> notes) => notes.Any(n => Hits(o, n, 0.25));

    public static bool Hits(Obstacle o, ColorNote n, double margin) => Inside(o, n.Beat, n.X, n.Y, margin);

    public static bool Inside(Obstacle o, double beat, int x, int y, double margin) =>
        beat >= o.Beat - margin && beat <= o.Beat + o.Duration + margin
        && x >= o.X && x < o.X + o.Width
        && y >= o.Y && y < o.Y + o.Height;

    static bool Overlaps(Obstacle a, Obstacle b) =>
        a.Beat < b.Beat + b.Duration && b.Beat < a.Beat + a.Duration && a.X < b.X + b.Width && b.X < a.X + a.Width;
}
