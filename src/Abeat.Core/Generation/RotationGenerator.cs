using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Lane rotations for the 90 and 360 degree modes, on top of a finished Standard map. The track
/// turns on the first note of a phrase (every 2 bars, 4 on Easy/Normal, and at every section start),
/// 15° per turn or 30° in loud parts. 90°: the heading stays within ±45° and turns back at the edge.
/// 360°: it keeps turning one way through a section and may reverse at the next one. Turns land on
/// notes ("early" rotation, so that note already comes from the new direction) after a gap of at
/// least half a second, so a fast stream is never bent mid-run.</summary>
public static class RotationGenerator
{
    public static void Generate(SongAnalysis a, DifficultyMap map, bool full, int seed)
    {
        if (map.Notes.Count == 0) return;
        double every = map.Difficulty <= DifficultyName.Normal ? 16 : 8;
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        var sectionStarts = a.Sections.Select(s => Math.Round(a.SecondsToBeat(s.Start))).ToHashSet();
        var noteBeats = map.Notes.Select(n => n.Beat).Distinct().Order().ToList();
        var rng = new Random(seed);
        double heading = 0, lastTurn = double.NegativeInfinity;
        int dir = rng.Next(2) == 0 ? 1 : -1;
        string? lastLabel = null;

        for (int i = 1; i < noteBeats.Count; i++)
        {
            double b = noteBeats[i];
            var section = a.SectionAt(a.BeatToSeconds(b));
            bool newSection = section.Label != lastLabel && sectionStarts.Any(s => Math.Abs(s - b) <= 2);
            if (!newSection && b - lastTurn < every) continue;
            if (a.TempoMap.Seconds(noteBeats[i - 1], b) < 0.5) continue; // only after a breath
            if (newSection)
            {
                lastLabel = section.Label;
                if (full && rng.NextDouble() < 0.4) dir = -dir;
            }
            double step = section.Energy / maxEnergy > 0.8 ? 30 : 15;
            if (!full && Math.Abs(heading + dir * step) > 45) dir = -dir;
            heading += dir * step;
            map.Rotations.Add(new RotationEvent(b, dir * step));
            lastTurn = b;
        }
    }
}
