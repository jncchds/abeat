using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Rule-based lightshow on classic event types: sections set the palette and motion, downbeats
/// pulse the back lasers, notes flash the side lasers in their hand's colour, accents hit the centre.</summary>
public static class LightingGenerator
{
    const int Back = 0, Ring = 1, LeftLaser = 2, RightLaser = 3, Center = 4, RingSpin = 8, RingZoom = 9, LeftSpeed = 12, RightSpeed = 13;

    public static void Generate(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events)
    {
        var lights = map.Lights;
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        var labelColor = new Dictionary<string, bool>();
        double lastNoteBeat = map.Notes.Count > 0 ? map.Notes.Max(n => n.Beat) : 0;

        foreach (var (sec, idx) in a.Sections.Select((s, i) => (s, i)))
        {
            double b0 = a.SecondsToBeat(sec.Start), b1 = a.SecondsToBeat(sec.End);
            if (b0 > lastNoteBeat + 4) break;
            // repeated parts get the same colour scheme
            if (!labelColor.TryGetValue(sec.Label, out bool red)) labelColor[sec.Label] = red = labelColor.Count % 2 == 1;
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
            bool high = rel > 0.85;

            lights.Add(new LightEvent(b0, RingZoom, 0));
            lights.Add(new LightEvent(b0, RingSpin, 0));
            lights.Add(new LightEvent(b0, Center, LightValue.Flash(!red)));
            lights.Add(new LightEvent(b0, Back, rel < 0.4 ? LightValue.Fade(red) : LightValue.On(red), rel < 0.4 ? 0.6 : 1));
            lights.Add(new LightEvent(b0, Ring, rel < 0.5 ? LightValue.Off : LightValue.On(!red), 0.7));
            int speed = (int)Math.Round(1 + rel * 7);
            lights.Add(new LightEvent(b0, LeftSpeed, speed));
            lights.Add(new LightEvent(b0, RightSpeed, speed));
            map.Boosts.Add(new BoostEvent(b0, high));

            // downbeat pulses on the back lasers in energetic sections, ring spins every 2 bars
            if (rel >= 0.5)
            {
                int bar = 0;
                foreach (var db in a.Tempo.Downbeats)
                {
                    double b = a.SecondsToBeat(db);
                    if (b <= b0 + 0.01 || b >= b1 - 0.01) continue;
                    lights.Add(new LightEvent(b, Back, LightValue.Flash(bar % 2 == 0 ? red : !red)));
                    if (high && bar % 2 == 1) lights.Add(new LightEvent(b, RingSpin, 0));
                    bar++;
                }
            }
        }

        foreach (var n in map.Notes)
        {
            bool red = n.Hand == Hand.Left;
            lights.Add(new LightEvent(n.Beat, red ? LeftLaser : RightLaser, LightValue.Flash(red)));
        }
        foreach (var e in events.Where(e => e.IsDouble || e.Strength > 0.9))
            lights.Add(new LightEvent(e.Beat, Center, LightValue.Flash(e.Beat % 2 < 1)));

        // fade everything out after the last note
        double end = lastNoteBeat + 2;
        foreach (int t in new[] { Back, Ring, LeftLaser, RightLaser, Center })
            lights.Add(new LightEvent(end, t, LightValue.Off));
        map.Boosts.Add(new BoostEvent(end, false));

        // flashes on both laser types at the same beat are fine, but drop exact duplicates
        var unique = lights.DistinctBy(l => (Math.Round(l.Beat, 4), l.Type, l.Value)).OrderBy(l => l.Beat).ToList();
        lights.Clear();
        lights.AddRange(unique);
    }
}
