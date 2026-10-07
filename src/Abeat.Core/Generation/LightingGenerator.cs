using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Rule-based lightshow on classic event types: sections set the palette and motion, downbeats
/// pulse the back lasers, notes flash the side lasers in their hand's colour, accents hit the centre.
/// With labelled drum hits (Demucs drum stem) the drummer plays the lights: kicks pulse the back lasers,
/// snares flash the centre, hats flicker the rings in loud sections. Build-ups spin the rings faster and
/// faster with rising laser speeds; drops flash white, zoom the rings and switch on the boost colours.
/// Environments with extra light channels (6/7, 10/11) get hats and melody on them, and the ones with
/// moving set pieces (16-19) raise them on drops and lower them in quiet parts.</summary>
public static class LightingGenerator
{
    const int Back = 0, Ring = 1, LeftLaser = 2, RightLaser = 3, Center = 4, RingSpin = 8, RingZoom = 9, LeftSpeed = 12, RightSpeed = 13;
    const int ExtraLeft = 6, ExtraRight = 7, Extra2Left = 10, Extra2Right = 11;

    public static void Generate(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events, EnvironmentInfo? env = null)
    {
        env ??= EnvironmentCatalog.Default;
        var lights = map.Lights;
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        var labelColor = new Dictionary<string, bool>();
        var drums = (a.Layers.GetValueOrDefault("drums") ?? []).Where(o => o.K is { Length: > 0 }).ToList();
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

            if (drums.Count > 0)
            {
                DrumLights(a, lights, drums, b0, b1, red, rel);
                foreach (var db in a.Tempo.Downbeats.Select(a.SecondsToBeat).Where(b => b > b0 + 0.01 && b < b1 - 0.01).Where((_, i) => i % 2 == 1))
                    if (high) lights.Add(new LightEvent(db, RingSpin, 0));
            }
            // downbeat pulses on the back lasers in energetic sections, ring spins every 2 bars
            else if (rel >= 0.5)
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

        var dropTimes = SongShape.Drops(a);
        BuildsAndDrops(a, map, dropTimes, lastNoteBeat);
        // group environments give these basic types other meanings
        if (env.System == LightingSystem.Classic) Extras(a, map, events, env, maxEnergy, lastNoteBeat);

        // fade everything out after the last note
        double end = lastNoteBeat + 2;
        foreach (int t in new[] { Back, Ring, LeftLaser, RightLaser, Center, ExtraLeft, ExtraRight, Extra2Left, Extra2Right }.Where(t => t <= Center || env.System == LightingSystem.Classic && env.Has(t)))
            lights.Add(new LightEvent(end, t, LightValue.Off));
        map.Boosts.Add(new BoostEvent(end, false));

        // one event per type and beat: later rules (drops, build-ups) override the section and note ones
        var unique = lights.GroupBy(l => (Math.Round(l.Beat, 4), l.Type)).Select(g => g.Last()).OrderBy(l => l.Beat).ToList();
        lights.Clear();
        lights.AddRange(unique);
        var boosts = map.Boosts.GroupBy(b => Math.Round(b.Beat, 4)).Select(g => g.Last()).OrderBy(b => b.Beat).ToList();
        map.Boosts.Clear();
        map.Boosts.AddRange(boosts);
    }

    /// <summary>Build-ups wind the stage up: ring spins on every bar, then every beat, then every half
    /// beat, laser speeds climbing to the maximum and the back lasers strobing; the drop hits with a white
    /// flash on every light, a ring zoom and the boost colours for the loud part that follows.</summary>
    static void BuildsAndDrops(SongAnalysis a, DifficultyMap map, List<double> dropTimes, double lastNoteBeat)
    {
        var lights = map.Lights;
        foreach (var bu in SongShape.BuildUps(a, dropTimes))
        {
            if (bu.Drop > lastNoteBeat) break;
            double len = bu.Drop - bu.Start;
            for (double b = bu.Start; b < bu.Drop - 0.01;)
            {
                double prog = (b - bu.Start) / len;
                double step = prog < 0.5 ? 4 : prog < 0.75 ? 1 : 0.5;
                lights.Add(new LightEvent(b, RingSpin, 0));
                if (prog >= 0.5) lights.Add(new LightEvent(b, Back, LightValue.Flash(Math.Floor(b) % 2 == 0), 0.4 + 0.6 * prog));
                b += step;
            }
            for (int i = 0; i < 4; i++)
            {
                double b = bu.Start + len * i / 4;
                int speed = 2 + 2 * i;
                lights.Add(new LightEvent(b, LeftSpeed, speed));
                lights.Add(new LightEvent(b, RightSpeed, speed));
            }
            map.Boosts.Add(new BoostEvent(bu.Start, false));
        }
        foreach (double d in dropTimes.Select(t => Math.Round(a.SecondsToBeat(t))).Where(d => d <= lastNoteBeat))
        {
            foreach (int t in new[] { Back, Ring, LeftLaser, RightLaser, Center })
                lights.Add(new LightEvent(d, t, LightValue.WhiteFlash, 1.2));
            lights.Add(new LightEvent(d, RingZoom, 0));
            lights.Add(new LightEvent(d, RingSpin, 0));
            map.Boosts.Add(new BoostEvent(d, true));
        }
    }

    /// <summary>Environment-specific channels: the extra laser pairs flicker with the hats (6/7) and hold the
    /// melody (10/11); set pieces (16-19) move up at loud section starts and drops and down in quiet sections.</summary>
    static void Extras(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events, EnvironmentInfo env, double maxEnergy, double lastNoteBeat)
    {
        var lights = map.Lights;
        if (env.Has(ExtraLeft) && env.Has(ExtraRight))
        {
            var hats = (a.Layers.GetValueOrDefault("drums") ?? []).Where(o => o.K?.Contains('h') == true && o.S >= 0.35).ToList();
            var src = hats.Count > 0 ? hats.Select(o => (a.SecondsToBeat(o.T), o.S)) : events.Where(e => e.Strength > 0.6).Select(e => (e.Beat, e.Strength));
            double last = double.NegativeInfinity;
            int side = 0;
            foreach (var (beat, str) in src)
            {
                double b = Math.Round(beat * 48) / 48;
                if (b > lastNoteBeat || b - last < 0.5) continue;
                double rel = maxEnergy > 0 ? a.SectionAt(a.BeatToSeconds(b)).Energy / maxEnergy : 0.5;
                if (rel < 0.5) continue;
                last = b;
                bool red = side++ % 2 == 0;
                lights.Add(new LightEvent(b, red ? ExtraLeft : ExtraRight, LightValue.Flash(red), 0.5 + 0.5 * str));
            }
        }
        if (env.Has(Extra2Left) && env.Has(Extra2Right))
        {
            var melody = events.Where(e => RhythmSelector.MelodyLayers.Contains(e.Layer) && e.Beat <= lastNoteBeat).ToList();
            var pitch = SongShape.RelativePitch(melody);
            foreach (var e in melody)
            {
                bool red = pitch(e) <= 0;
                lights.Add(new LightEvent(e.Beat, red ? Extra2Left : Extra2Right, LightValue.Fade(red), 0.4 + 0.6 * e.Intensity));
            }
        }
        var pieces = Enumerable.Range(16, 4).Where(env.Has).ToList();
        if (pieces.Count == 0) return;
        // small value ranges select which pieces move (Interscope's cars: the even type lowers, the odd one
        // raises); larger ones are positions (Gaga's towers: low when quiet, typical when loud, top on drops)
        bool selector = pieces.All(t => env.ValueRange(t).Hi <= 3);
        int Typical(int t) => env.BasicValues.TryGetValue(t, out var v) && v.Length >= 3 ? v[2] : env.ValueRange(t).Hi;
        void Pieces(double beat, int level) // 0 quiet, 1 loud, 2 drop
        {
            foreach (int t in pieces)
            {
                if (selector) { if ((t % 2 == 1) == (level > 0)) lights.Add(new LightEvent(beat, t, Typical(t))); }
                else lights.Add(new LightEvent(beat, t, level == 0 ? env.ValueRange(t).Lo : level == 1 ? Typical(t) : env.ValueRange(t).Hi));
            }
        }
        foreach (var sec in a.Sections)
        {
            double b0 = Math.Round(a.SecondsToBeat(sec.Start));
            if (b0 > lastNoteBeat) break;
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
            if (rel >= 0.75) Pieces(b0, 1);
            else if (rel < 0.45) Pieces(b0, 0);
        }
        foreach (double d in SongShape.Drops(a).Select(t => Math.Round(a.SecondsToBeat(t))).Where(d => d <= lastNoteBeat))
            Pieces(d, 2);
    }

    /// <summary>Drum hits inside one section: kicks pulse the back lasers (alternating colours per bar
    /// half), snares flash the centre in the other colour, hats flicker the rings dimly when the section
    /// is loud. Weak hits are skipped and each light fires at most every quarter beat.</summary>
    static void DrumLights(SongAnalysis a, List<LightEvent> lights, List<Onset> drums, double b0, double b1, bool red, double rel)
    {
        var last = new Dictionary<int, double>();
        bool Free(int type, double beat)
        {
            if (last.TryGetValue(type, out double prev) && beat - prev < 0.25) return false;
            last[type] = beat;
            return true;
        }
        foreach (var o in drums)
        {
            double b = Math.Round(a.SecondsToBeat(o.T) * 48) / 48;
            if (b < b0 || b >= b1) continue;
            switch (o.K![0])
            {
                case 'k' when o.S >= 0.3 && rel >= 0.35 && Free(Back, b):
                    lights.Add(new LightEvent(b, Back, LightValue.Flash(Math.Floor(b / 2) % 2 == 0 ? red : !red), 0.6 + 0.4 * o.S));
                    break;
                case 's' when o.S >= 0.3 && Free(Center, b):
                    lights.Add(new LightEvent(b, Center, LightValue.Flash(!red), 0.5 + 0.5 * o.S));
                    break;
                case 'h' when o.S >= 0.35 && rel >= 0.7 && Free(Ring, b):
                    lights.Add(new LightEvent(b, Ring, LightValue.Flash(!red), 0.25 + 0.3 * o.S)); // the rings keep their section colour
                    break;
            }
        }
    }
}
