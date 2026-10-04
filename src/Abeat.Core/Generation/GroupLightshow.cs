using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>v3 group lightshow for the Pyro environment (14 light groups in left/right pairs, as a curated
/// Pyro map uses them). Each pair gets a musical role: ambient section colour, downbeat chases, note
/// flashes per hand, snares, kicks, melody, hats; rotating pairs turn at section changes.</summary>
public static class GroupLightshow
{
    public const string Environment = "PyroEnvironment";

    const int Ambient = 0, Chase = 2, Notes = 4, Snare = 6, Kick = 8, Melody = 10, Hats = 12;
    const int Red = 0, Blue = 1, White = 2;

    public static void Generate(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events)
    {
        var lights = map.GroupLights;
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        double end = map.Notes.Count > 0 ? map.Notes.Max(n => n.Beat) + 2 : 0;
        var labelColor = new Dictionary<string, int>();
        var drums = (a.Layers.GetValueOrDefault("drums") ?? []).Where(o => o.K is { Length: > 0 } && o.S >= 0.3).ToList();
        var downbeats = a.Tempo.Downbeats.Select(a.SecondsToBeat).ToList();

        void Pair(double beat, int first, Func<int, LightBox> box)
        {
            lights.Add(new LightGroupEvent(beat, first, [box(0)]));
            lights.Add(new LightGroupEvent(beat, first + 1, [box(1)]));
        }
        static LightBox Flash(int color, double brightness, double fade, double spread = 0, bool reverse = false, int sections = 1, int part = 0) =>
            new(sections, part, reverse, spread, 0, [new(0, color, brightness), new(fade, color, 0, Transition: 1)]);

        int idx = 0;
        foreach (var sec in a.Sections)
        {
            double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
            if (b0 >= end) break;
            if (!labelColor.TryGetValue(sec.Label, out int color)) labelColor[sec.Label] = color = labelColor.Count % 2 == 0 ? Blue : Red;
            int other = color == Blue ? Red : Blue;
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;

            // ambient wash in the section colour, fading in over a beat
            Pair(b0, Ambient, _ => new LightBox(1, 0, false, 0, 0.3, [new(0, color, 0.1), new(1, color, 0.25 + 0.5 * rel, Transition: 1)]));
            // the rotating pairs swing to a new pose at each section, wider in louder parts
            double swing = (idx % 2 == 0 ? 1 : -1) * (15 + 45 * rel);
            foreach (var (g, axis) in new[] { (Kick, 1), (Melody, 0), (Chase, 1) })
                for (int k = 0; k < 2; k++)
                    map.GroupRotations.Add(new LightRotation(b0, g + k, axis, k == 0 ? swing : -swing, 10 + 20 * rel, 2));
            idx++;

            // downbeat chases running outwards, direction alternating per bar
            if (rel >= 0.4)
            {
                int bar = 0;
                foreach (var db in downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01))
                    Pair(db, Chase, _ => Flash(bar % 2 == 0 ? color : other, 1, 1, spread: 1, reverse: bar++ % 2 == 1));
            }

            // the drummer: kicks pulse the kick pair, snares flash white, hats flicker a quarter of the lights
            double lastHat = double.NegativeInfinity;
            int hatPart = 0;
            foreach (var o in drums)
            {
                double b = Math.Round(a.SecondsToBeat(o.T) * 48) / 48;
                if (b < b0 || b >= b1 || b >= end) continue;
                switch (o.K![0])
                {
                    case 'k' when rel >= 0.3:
                        Pair(b, Kick, _ => Flash(color, 0.5 + 0.5 * o.S, 0.5));
                        break;
                    case 's':
                        Pair(b, Snare, _ => Flash(White, 0.6 + 0.4 * o.S, 0.4));
                        break;
                    case 'h' when rel >= 0.7 && b - lastHat >= 0.25:
                        lastHat = b;
                        int part = hatPart++ % 4;
                        Pair(b, Hats, side => Flash(other, 0.3 + 0.3 * o.S, 0.25, sections: 4, part: side == 0 ? part : 3 - part));
                        break;
                }
            }
        }

        // notes flash their hand's pair half in the saber colour; melody notes light the melody pair
        foreach (var n in map.Notes)
            lights.Add(new LightGroupEvent(n.Beat, Notes + (int)n.Hand, [Flash(n.Hand == Hand.Left ? Red : Blue, 1, 0.5)]));
        foreach (var e in events.Where(e => RhythmSelector.MelodyLayers.Contains(e.Layer)))
        {
            double hold = Math.Clamp(a.SecondsToBeat(e.Time + Math.Max(0.2, e.Sustain)) - e.Beat, 0.25, 4);
            int c = e.Brightness > 0.5 ? Blue : Red;
            Pair(e.Beat, Melody, side => new LightBox(1, 0, side == 1, 0.5, 0.5, [new(0, c, 0.4 + 0.6 * e.Intensity), new(hold, c, 0, Transition: 1)]));
        }

        // everything out after the last note
        for (int g = 0; g < 14; g++)
            lights.Add(new LightGroupEvent(end, g, [new LightBox(1, 0, false, 0, 0, [new(0, Blue, 0)])]));

        // one event per group and beat (later rules win)
        var unique = lights.GroupBy(l => (Math.Round(l.Beat, 4), l.Group)).Select(g => g.Last()).OrderBy(l => l.Beat).ToList();
        lights.Clear();
        lights.AddRange(unique);
    }
}
