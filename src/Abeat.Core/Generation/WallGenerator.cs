using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Walls placed only where they can't conflict with notes:
/// <list type="bullet">
/// <item>crouch walls (full width, top) in the gap right before a big energy jump (a drop)</item>
/// <item>dodge walls (one centre lane, full height) alternating sides in other note-free gaps</item>
/// <item>decorative side walls in the outer lanes during calm sections</item>
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

        foreach (var o in gameplay.Concat(SideWalls(a, notes, p)))
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

    /// <summary>A note inside the wall's cells while the wall passes (plus a small margin) is unplayable.</summary>
    public static bool Clashes(Obstacle o, IEnumerable<ColorNote> notes) => notes.Any(n => Hits(o, n, 0.25));

    public static bool Hits(Obstacle o, ColorNote n, double margin) =>
        n.Beat >= o.Beat - margin && n.Beat <= o.Beat + o.Duration + margin
        && n.X >= o.X && n.X < o.X + o.Width
        && n.Y >= o.Y && n.Y < o.Y + o.Height;

    static bool Overlaps(Obstacle a, Obstacle b) =>
        a.Beat < b.Beat + b.Duration && b.Beat < a.Beat + a.Duration && a.X < b.X + b.Width && b.X < a.X + a.Width;
}
