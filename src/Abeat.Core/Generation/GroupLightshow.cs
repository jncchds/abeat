using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>v3 group lightshow for any group-lighting environment, laid out from how curated maps use its
/// light groups (<see cref="EnvironmentCatalog"/>). Neighbouring look-alike groups pair up as left/right units;
/// units get musical roles by prominence (kicks, ambient section wash, note flashes per hand, snares,
/// melody, downbeat chases, hats) and groups that turn or move get choreography that follows the song:
/// slow drifts in quiet parts, swings on downbeats in loud parts, lights folding together over a
/// build-up and bursting open on the drop, and a group tracing the melody's contour. Groups mappers switch
/// on far less often than the rest (Pyro's fire) go off on the big moments only, and the environment's
/// basic light types (Pyro's runway and projectors) wash along with the sections.</summary>
public static class GroupLightshow
{
    const int Red = 0, Blue = 1, White = 2;

    enum Role { Kick, Ambient, Notes, Snare, Melody, Chase, Hats }

    /// <summary>One or two groups (left, right) lit together.</summary>
    sealed record Unit(LightGroupInfo[] Groups)
    {
        public double Color => Groups.Max(g => g.Color);
        public int Lights => Groups.Max(g => g.Lights);
        public double? Rate => Groups.All(g => g.Rate > 0) ? Groups.Max(g => g.Rate!.Value) : null;
    }

    public static void Generate(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events, EnvironmentInfo env)
    {
        var lit = env.Groups.Where(g => g.Color >= 0.3).ToList();
        if (lit.Count == 0) lit = env.Groups.Where(g => g.Color > 0).ToList();
        if (lit.Count == 0) return;
        // groups switched on under a quarter as often as the typical lit group (Pyro's fire jets) are set
        // pieces, not lights: they take no role and go off on the big moments only
        var rates = lit.Where(g => g.Rate > 0).Select(g => g.Rate!.Value).Order().ToList();
        var accents = rates.Count >= 4 ? lit.Where(g => g.Rate < 0.25 * rates[rates.Count / 2]).ToList() : [];
        if (accents.Count < lit.Count) lit = lit.Except(accents).ToList();
        else accents = [];
        var units = Pair(lit).OrderByDescending(u => u.Color).ThenByDescending(u => u.Lights).ToList();
        var roles = AssignRoles(units);
        Unit? For(Role r) => roles.GetValueOrDefault(r);

        var lights = map.GroupLights;
        double maxEnergy = a.Sections.Count > 0 ? a.Sections.Max(s => s.Energy) : 1;
        double end = map.Notes.Count > 0 ? map.Notes.Max(n => n.Beat) + 2 : 0;
        var labelColor = new Dictionary<string, int>();
        var drums = (a.Layers.GetValueOrDefault("drums") ?? []).Where(o => o.K is { Length: > 0 } && o.S >= 0.3).ToList();
        var downbeats = a.Tempo.Downbeats.Select(a.SecondsToBeat).ToList();
        var dropTimes = SongShape.Drops(a);
        var drops = dropTimes.Select(t => Math.Round(a.SecondsToBeat(t))).ToList();
        var builds = SongShape.BuildUps(a, dropTimes);
        bool InBuild(double b) => builds.Any(x => b >= x.Start && b < x.Drop);

        void Light(Unit? u, double beat, Func<int, LightBox> box)
        {
            if (u == null || beat >= end) return;
            for (int k = 0; k < u.Groups.Length; k++) lights.Add(new LightGroupEvent(beat, u.Groups[k].Id, [box(k)]));
        }
        static LightBox Flash(int color, double brightness, double fade, double spread = 0, bool reverse = false, int sections = 1, int part = 0, int strobe = 0) =>
            new(sections, part, reverse, spread, 0, [new(0, color, brightness, Strobe: strobe), new(fade, color, 0, Transition: 1)]);

        foreach (var sec in a.Sections)
        {
            double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
            if (b0 >= end) break;
            if (!labelColor.TryGetValue(sec.Label, out int color)) labelColor[sec.Label] = color = labelColor.Count % 2 == 0 ? Blue : Red;
            int other = color == Blue ? Red : Blue;
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;

            // ambient wash in the section colour: fades in, and breathes every two bars in quiet parts
            Light(For(Role.Ambient), b0, _ => new LightBox(1, 0, false, 0, 0.3, [new(0, color, 0.1), new(1, color, 0.25 + 0.5 * rel, Transition: 1)]));
            if (rel < 0.45)
                foreach (var db in downbeats.Where(b => b > b0 + 0.01 && b < b1 - 0.01).Where((_, i) => i % 2 == 1))
                    Light(For(Role.Ambient), db, _ => new LightBox(1, 0, false, 2, 0.4, [new(0, color, 0.15, Transition: 1), new(4, color, 0.4, Transition: 1)]));

            // downbeat chases running outwards, direction alternating per bar
            if (rel >= 0.4)
            {
                int bar = 0;
                foreach (var db in downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01 && !InBuild(b)))
                    Light(For(Role.Chase), db, _ => Flash(bar % 2 == 0 ? color : other, 1, 1, spread: 1, reverse: bar++ % 2 == 1));
            }

            // the drummer: kicks pulse the kick unit, snares flash white, hats flicker a quarter of the lights
            if (drums.Count > 0)
            {
                double lastHat = double.NegativeInfinity;
                int hatPart = 0;
                foreach (var o in drums)
                {
                    double b = Math.Round(a.SecondsToBeat(o.T) * 48) / 48;
                    if (b < b0 || b >= b1) continue;
                    switch (o.K![0])
                    {
                        case 'k' when rel >= 0.3:
                            Light(For(Role.Kick), b, _ => Flash(color, 0.5 + 0.5 * o.S, 0.5));
                            break;
                        case 's':
                            Light(For(Role.Snare), b, _ => Flash(White, 0.6 + 0.4 * o.S, 0.4));
                            break;
                        case 'h' when rel >= 0.7 && b - lastHat >= 0.25:
                            lastHat = b;
                            int part = hatPart++ % 4;
                            Light(For(Role.Hats), b, side => Flash(other, 0.3 + 0.3 * o.S, 0.25, sections: 4, part: side == 0 ? part : 3 - part));
                            break;
                    }
                }
            }
            else if (rel >= 0.3)
            {
                // no drum stem: pulse the beats instead, accenting the downbeats
                foreach (var beat in SongShape.GridBeats(a).Where(b => b >= b0 && b < b1))
                {
                    bool down = downbeats.Any(d => Math.Abs(d - beat) < 0.05);
                    Light(For(Role.Kick), beat, _ => Flash(color, down ? 1 : 0.6, 0.5));
                }
            }
        }

        // build-ups: chases speed up (bar, half bar, beat, half beat) and brighten, the ambient wash
        // strobes faster towards the drop
        foreach (var bu in builds)
        {
            double len = bu.Drop - bu.Start;
            int i = 0;
            for (double b = bu.Start; b < bu.Drop - 0.01; i++)
            {
                double prog = (b - bu.Start) / len;
                double step = prog < 0.5 ? 2 : prog < 0.75 ? 1 : 0.5;
                if (len < 8) step /= 2;
                int c = i % 2 == 0 ? Blue : Red;
                Light(For(Role.Chase), b, side => Flash(c, 0.5 + 0.7 * prog, step, spread: step, reverse: (i + side) % 2 == 1));
                b += step;
            }
            Light(For(Role.Ambient), bu.Start, _ => new LightBox(1, 0, false, 0, 0, [new(0, White, 0.1), new(len, White, 1, Transition: 1, Strobe: 4)]));
        }
        // drops: everything flashes white and settles into the section colour
        foreach (var d in drops)
        {
            int c = a.Sections.Count > 0 ? labelColor.GetValueOrDefault(a.SectionAt(a.BeatToSeconds(d) + 0.1).Label, Blue) : Blue;
            foreach (var u in units)
                Light(u, d, _ => new LightBox(1, 0, false, 0.5, 0, [new(0, White, 1.5), new(2, c, 0.6, Transition: 1)]));
        }

        // notes flash their hand's half (or their hand's group of a pair) in the saber colour
        if (For(Role.Notes) is { } notes)
            foreach (var n in map.Notes)
            {
                int c = n.Hand == Hand.Left ? Red : Blue;
                if (notes.Groups.Length == 2) lights.Add(new LightGroupEvent(n.Beat, notes.Groups[(int)n.Hand].Id, [Flash(c, 1, 0.5)]));
                else lights.Add(new LightGroupEvent(n.Beat, notes.Groups[0].Id, [Flash(c, 1, 0.5, sections: 2, part: (int)n.Hand)]));
            }
        var melody = events.Where(e => RhythmSelector.MelodyLayers.Contains(e.Layer)).ToList();
        var pitch = SongShape.RelativePitch(melody);
        foreach (var e in melody)
        {
            double hold = Math.Clamp(a.SecondsToBeat(e.Time + Math.Max(0.2, e.Sustain)) - e.Beat, 0.25, 4);
            int c = pitch(e) > 0 ? Blue : Red;
            Light(For(Role.Melody), e.Beat, side => new LightBox(1, 0, side == 1, 0.5, 0.5, [new(0, c, 0.4 + 0.6 * e.Intensity), new(hold, c, 0, Transition: 1)]));
        }

        Motion(a, map, env, end, maxEnergy, downbeats, drops, builds, melody);
        Accents(a, map, Pair(accents), end, maxEnergy, downbeats, drops);
        Steps(a, map, env, end, maxEnergy, downbeats, drops, builds);
        BasicLights(a, map, env, end, maxEnergy, drops, labelColor);

        // environment special events (40-43: set-piece animations) go off on the drops, with the value
        // curated maps use most
        foreach (int t in Enumerable.Range(40, 4).Where(env.Has))
        {
            int v = env.BasicValues.TryGetValue(t, out var vals) && vals.Length >= 3 ? vals[2] : 0;
            foreach (var d in drops.Where(d => d < end)) map.Lights.Add(new LightEvent(d, t, v));
        }

        // everything out after the last note
        foreach (var g in lit.Concat(accents))
            lights.Add(new LightGroupEvent(end, g.Id, [new LightBox(1, 0, false, 0, 0, [new(0, Blue, 0)])]));

        // one event per group and beat (later rules win)
        var unique = lights.GroupBy(l => (Math.Round(l.Beat, 4), l.Group)).Select(g => g.Last()).OrderBy(l => l.Beat).ToList();
        lights.Clear();
        lights.AddRange(unique);
    }

    /// <summary>Set-piece groups (<paramref name="units"/>, e.g. Pyro's fire) burst as often as curated maps
    /// fire them (<see cref="LightGroupInfo.Rate"/> of the song's beats): on the drops first, then the starts
    /// of loud sections and the downbeats of the loudest ones, at least a bar apart. Each burst is a
    /// switch on and, a beat later, off (fire effects start on any lit event and end on a dark one).</summary>
    static void Accents(SongAnalysis a, DifficultyMap map, List<Unit> units, double end, double maxEnergy,
        List<double> downbeats, List<double> drops)
    {
        if (units.Count == 0 || end <= 0) return;
        double rate = units.SelectMany(u => u.Groups).Select(g => g.Rate ?? 0).Order().ElementAt(units.Sum(u => u.Groups.Length) / 2);
        int target = Math.Max(drops.Count, (int)Math.Round(rate * end));
        var candidates = drops.Select(d => (Beat: d, Rank: 0, Energy: 1.0)).ToList();
        foreach (var sec in a.Sections)
        {
            double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
            double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
            if (rel < 0.6) continue;
            var bars = downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01).ToList();
            if (bars.Count > 0) candidates.Add((bars[0], 1, rel));
            candidates.AddRange(bars.Skip(1).Select((b, i) => (b, rel >= 0.8 && i % 2 == 1 ? 2 : 3, rel)));
        }
        var chosen = new List<double>();
        foreach (var c in candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.Energy).ThenBy(c => c.Beat))
        {
            if (chosen.Count >= target) break;
            if (c.Beat < end - 1 && chosen.All(b => Math.Abs(b - c.Beat) >= 4)) chosen.Add(c.Beat);
        }
        foreach (var b in chosen.Order())
            foreach (var g in units.SelectMany(u => u.Groups))
                map.GroupLights.Add(new LightGroupEvent(b, g.Id, [new LightBox(1, 0, false, 0, 0, [new(0, Red, 1), new(1, Red, 0)])]));
    }

    /// <summary>Step types (<see cref="EnvironmentInfo.Steps"/>, The Second's ring spacing) as curated maps pulse
    /// them: quiet sections rest at the low step, louder ones alternate high and low from the downbeats about as
    /// often as the maps change the type (twice as often in the loudest sections), build-ups step down from
    /// high to low on the beats and the drops jump to high; low after the last note.</summary>
    static void Steps(SongAnalysis a, DifficultyMap map, EnvironmentInfo env, double end, double maxEnergy,
        List<double> downbeats, List<double> drops, List<BuildUp> builds)
    {
        foreach (var t in env.Steps ?? [])
        {
            void Add(double beat, int value) { if (beat <= end) map.EnvironmentLights.Add(new LightEvent(beat, t.Type, value)); }
            bool InBuild(double b) => builds.Any(x => b >= x.Start && b < x.Drop);
            double step = Math.Pow(2, Math.Clamp(Math.Round(Math.Log2(1 / Math.Max(t.Rate, 1e-3))), -1, 4));
            Add(0, t.Low);
            foreach (var sec in a.Sections)
            {
                double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = Math.Min(a.SecondsToBeat(sec.End), end);
                if (b0 >= end) break;
                double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
                if (rel < 0.45) { Add(b0, t.Low); continue; }
                double s = rel >= 0.8 ? step / 2 : step;
                double first = downbeats.FirstOrDefault(b => b >= b0 - 0.01 && b < b1, b0);
                int k = 0;
                for (double b = first; b < b1 - 0.01; b += s)
                    if (!InBuild(b)) Add(b, k++ % 2 == 0 ? t.High : t.Low);
            }
            foreach (var bu in builds)
            {
                // from high down to low by the drop, one step at a time (at most one per beat)
                int n = Math.Max(1, Math.Min((int)Math.Floor(bu.Drop - bu.Start), Math.Abs(t.High - t.Low) + 1));
                for (int i = 0; i < n; i++)
                    Add(bu.Start + i * (bu.Drop - bu.Start) / n, (int)Math.Round(t.High + (t.Low - t.High) * (double)i / Math.Max(1, n - 1)));
            }
            foreach (var d in drops) Add(d, t.High);
            Add(end, t.Low);
        }
    }

    /// <summary>The environment's basic light types (<see cref="EnvironmentInfo.BasicLights"/>) as curated maps
    /// light them next to the groups: one event per step (as often as the maps fire the type, half as often in
    /// quiet sections), each section starting "on" in its colour and the following events switching on or
    /// fading ("transition") to the other colour or white in the learned proportions, brighter with energy;
    /// white on the drops, off after the last note. Mirrored types get identical events.</summary>
    static void BasicLights(SongAnalysis a, DifficultyMap map, EnvironmentInfo env, double end, double maxEnergy,
        List<double> drops, Dictionary<string, int> labelColor)
    {
        var types = env.BasicLights ?? [];
        foreach (var t in types)
        {
            bool paired = t.Mirror is int m && types.Any(x => x.Type == m);
            if (paired && t.Mirror < t.Type) continue; // written with its partner
            int[] ids = paired ? [t.Type, t.Mirror!.Value] : [t.Type];
            void Add(double beat, int value, double f)
            {
                if (beat > end) return;
                foreach (int id in ids) map.EnvironmentLights.Add(new LightEvent(beat, id, value, Math.Round(f, 2)));
            }
            // a power of two between half a beat and 32 beats, as often as curated maps fire the type
            double step = Math.Pow(2, Math.Clamp(Math.Round(Math.Log2(1 / Math.Max(t.Rate, 1e-3))), -1, 5));
            double fades = t.On + t.Transition > 0 ? t.Transition / (t.On + t.Transition) : 0;
            double accFade = 0, accWhite = 0;
            foreach (var sec in a.Sections)
            {
                double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = Math.Min(a.SecondsToBeat(sec.End), end);
                if (b0 >= end) break;
                int color = labelColor.GetValueOrDefault(sec.Label, Blue);
                double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
                double s = rel < 0.45 ? step * 2 : step;
                int k = 0;
                for (double b = b0; b < b1 - 0.01; b += s, k++)
                {
                    accWhite += t.White;
                    bool white = accWhite >= 1;
                    if (white) accWhite--;
                    int c = white ? White : k % 2 == 0 ? color : 1 - color;
                    int on = c == Red ? LightValue.RedOn : c == Blue ? LightValue.BlueOn : LightValue.WhiteOn;
                    if (k == 0) { Add(b, color == Red ? LightValue.RedOn : LightValue.BlueOn, 0.4 + 0.6 * rel); continue; }
                    accFade += fades;
                    bool fade = accFade >= 1;
                    if (fade) accFade--;
                    // a transition value fades from the previous event to this colour
                    Add(b, fade ? on + 3 : on, fade ? 0.2 + 0.5 * rel : 0.4 + 0.6 * rel);
                }
            }
            foreach (var d in drops) Add(d, LightValue.WhiteOn, 1.2);
            Add(end, LightValue.Off, 0);
        }
        // one event per type and beat (later rules win)
        var unique = map.EnvironmentLights.GroupBy(l => (Math.Round(l.Beat, 4), l.Type)).Select(g => g.Last()).OrderBy(l => l.Beat).ToList();
        map.EnvironmentLights.Clear();
        map.EnvironmentLights.AddRange(unique);
    }

    /// <summary>Rotation and translation choreography for every group curated maps move, between the poses
    /// mappers use for that group (turns and rails on every axis mappers use) (<see cref="LightGroupInfo.RotationPoses"/>, <see cref="LightGroupInfo.TranslationValues"/>):
    /// every moving group starts at its home pose (the most used one), quiet sections drift to a nearby
    /// pose and back, louder ones swing between learned poses on downbeats (further from home with energy, as
    /// often as curated maps turn the group),
    /// over a build-up the lights return home and they burst out to their widest pose on the drop. Mirror
    /// partners get identical events (the game mirrors the other side). With two or more rotating units the
    /// most often turned one traces the melody instead: higher sounds turn it further from home.</summary>
    static void Motion(SongAnalysis a, DifficultyMap map, EnvironmentInfo env, double end, double maxEnergy,
        List<double> downbeats, List<double> drops, List<BuildUp> builds, List<RhythmEvent> melody)
    {
        var rotating = MotionUnits(env.Groups.Where(g => g.Rotation >= 0.3 && g.RotationAxes.Length > 0))
            .OrderByDescending(u => u.Groups[0].Rotation).ThenBy(u => u.Groups[0].Id).ToList();
        var moving = MotionUnits(env.Groups.Where(g => g.Translation >= 0.3 && g.TranslationAxes.Length > 0))
            .OrderByDescending(u => u.Groups[0].Translation).ThenBy(u => u.Groups[0].Id).ToList();
        // the tracer is the unit curated maps turn most often (Lizzo's top lasers, not its rarely lit end lasers)
        var tracer = rotating.Count >= 2 && melody.Count >= 16 ? rotating.MaxBy(u => u.Groups[0].RotationRate ?? 0) : null;
        double barBeats = downbeats.Count >= 2 ? downbeats.Zip(downbeats.Skip(1), (x, y) => y - x).Order().ElementAt((downbeats.Count - 1) / 2) : 4;
        var pitch = SongShape.RelativePitch(melody);
        bool InBuild(double b) => builds.Any(x => b >= x.Start && b < x.Drop + 0.01);
        // swings and pumps take as long as curated maps' eased moves in this environment
        double turn = Math.Clamp(env.RotationEase ?? 1, 0.25, 4), push = Math.Clamp(env.TranslationEase ?? 1, 0.25, 4);
        double start = a.Sections.Count > 0 ? Math.Max(0, Math.Round(a.SecondsToBeat(a.Sections[0].Start))) : 0;

        foreach (var (u, ui) in rotating.Select((u, i) => (u, i)))
        {
            var g = u.Groups[0];
            int axis = g.RotationAxes[0];
            var p = RotationPoses.Of(g.RotationPoses, g.RotationRange);
            // further axes mappers turn the group about (Weave's lights also turn about y), between their own
            // learned poses: they join every other swing and follow the builds and drops
            var extra = g.RotationAxes.Skip(1).Where(ax => g.PosesOn(ax) is { Length: > 0 })
                .Select(ax => (ax, p: RotationPoses.Of(g.PosesOn(ax), g.RotationRange))).ToList();
            // neighbouring units swing in opposite phase so their beams cross
            bool flip = ui % 2 == 1;
            void Turn(double beat, int ax, double deg, double spread, double dur, double wave)
            {
                if (beat >= end) return;
                double norm = ((deg % 360) + 360) % 360;
                foreach (var x in u.Groups) map.GroupRotations.Add(new LightRotation(beat, x.Id, ax, norm, spread, dur, BeatSpread: wave));
            }
            void Rot(double beat, double deg, double spread, double dur, double wave = 0) => Turn(beat, axis, deg, spread, dur, wave);
            void Extra(double beat, Func<RotationPoses, double> to, double dur, double wave = 0)
            {
                foreach (var (ax, q) in extra) Turn(beat, ax, to(q), 0, dur, wave);
            }
            Rot(start, p.Home, 0, 0);
            Extra(start, q => q.Home, 0);
            if (u == tracer)
            {
                double last = double.NegativeInfinity;
                // no more often than about twice the rate curated maps turn it
                double gap = g.RotationRate > 0 ? Math.Clamp(0.5 / g.RotationRate.Value, 0.5, 4) : 0.5;
                foreach (var e in melody.Where(e => e.Beat - last >= gap && !InBuild(e.Beat)))
                {
                    last = e.Beat;
                    Rot(e.Beat, p.Toward(pitch(e)), p.Reach * 0.15, 0.5);
                }
            }
            else
                foreach (var sec in a.Sections)
                {
                    double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
                    if (b0 >= end) break;
                    double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
                    var bars = downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01 && !InBuild(b)).ToList();
                    if (rel < 0.45)
                    {
                        // slow drift: a long eased turn to the nearest pose and back, every four bars
                        for (int i = 0; i < bars.Count; i += 4)
                            Rot(bars[i], (i / 4 % 2 == 0) != flip ? p.Near : p.Home, 0, 16);
                        Extra(b0, q => q.Home, 4);
                    }
                    else
                    {
                        // swings on downbeats (as often as curated maps turn the group when loud, half as often
                        // otherwise; every bar / every other bar when unknown) between a learned pose and its
                        // counterpart, further from home and fanned wider with energy
                        int every = (g.RotationRate > 0 ? (int)Math.Pow(2, Math.Clamp(Math.Round(Math.Log2(1 / (g.RotationRate.Value * barBeats))), 0, 3)) : 1)
                            * (rel >= 0.8 ? 1 : 2);
                        var (out1, out2) = p.Swing(0.3 + 0.7 * rel);
                        // groups curated maps mostly leave at home (EDM's circle rotations) come back home between swings
                        double[] seq = g.HomeShare >= 0.5 ? [out1, p.Home, out2, p.Home] : [out1, out2];
                        double fan = Math.Abs(Angle(out1 - p.Home)) * 0.3 * rel;
                        for (int i = 0; i < bars.Count; i += every)
                        {
                            int k = i / every;
                            Rot(bars[i], seq[(k + (flip ? seq.Length / 2 : 0)) % seq.Length], fan, turn, 0.5);
                            if (k % 2 == 0)
                                Extra(bars[i], q =>
                                {
                                    var (x1, x2) = q.Swing(0.3 + 0.7 * rel);
                                    double[] xs = g.HomeShare >= 0.5 ? [x1, q.Home, x2, q.Home] : [x1, x2];
                                    return xs[(k / 2 + (flip ? xs.Length / 2 : 0)) % xs.Length];
                                }, turn, 0.5);
                        }
                    }
                }
            foreach (var bu in builds)
            {
                Rot(bu.Start, p.Home, 0, bu.Drop - bu.Start - 0.25); // gather at home over the build
                Extra(bu.Start, q => q.Home, bu.Drop - bu.Start - 0.25);
            }
            foreach (var d in drops)
            {
                Rot(d, p.Wide, p.Reach * 0.5, 0.5, 0.25); // burst out to the widest pose on the drop
                Extra(d, q => q.Wide, 0.5, 0.25);
            }
            Rot(end - 2, p.Home, 0, 2); // settle home after the last note
            Extra(end - 2, q => q.Home, 2);
        }

        foreach (var u in moving)
        {
            var g = u.Groups[0];
            // every axis mappers move the group on, the main one first: home = the median distance, the far
            // side = the percentile further from it. Mappers set the axes together (Lattice's cube clusters sit
            // out, down and forward), so all axes go home at the start, follow the drops and builds, and the
            // others join the main axis' bar pumps at half size.
            double range = g.TranslationRange > 0.01 ? g.TranslationRange : 1;
            var axes = g.TranslationAxes.Select((axis, i) => (axis, v: g.TranslationValues?.GetValueOrDefault(axis) is { Length: 3 } tv ? tv : i == 0 ? [0, 0, range] : null))
                .Where(x => x.v != null).Select(x =>
                {
                    double home = x.v![1];
                    double lo = Math.Clamp(x.v[0], home - range, home + range), hi = Math.Clamp(x.v[2], home - range, home + range);
                    double far = Math.Abs(hi - home) >= Math.Abs(lo - home) ? hi : lo;
                    return (x.axis, home, far, near: far == hi ? lo : hi);
                }).ToList();
            void Move(double beat, Func<(int axis, double home, double far, double near), int, double> to, double spread, double dur, double wave = 0)
            {
                if (beat >= end) return;
                foreach (var (ax, i) in axes.Select((x, i) => (x, i)))
                {
                    double d = to(ax, i), sp = spread * (ax.far - ax.home) * (i == 0 ? 1 : 0.5);
                    foreach (var x in u.Groups) map.GroupTranslations.Add(new LightTranslation(beat, x.Id, ax.axis, d, sp, dur, wave));
                }
            }
            Move(start, (x, _) => x.home, 0, 0);
            foreach (var sec in a.Sections)
            {
                double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
                if (b0 >= end) break;
                double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
                if (rel < 0.45) { Move(b0, (x, _) => x.home, 0, 4); continue; }
                // pump with the bars: out towards the far side on the downbeat, partly back on the next
                var bars = downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01 && !InBuild(b)).ToList();
                for (int i = 0; i < bars.Count; i++)
                {
                    double k = rel * (i % 2 == 0 ? 1 : 0.4);
                    Move(bars[i], (x, ai) => x.home + (x.far - x.home) * k * (ai == 0 ? 1 : 0.5), 0.3 * rel, push, 0.25);
                }
            }
            foreach (var bu in builds)
                Move(bu.Start, (x, _) => x.home + (x.near - x.home) * 0.5, 0, bu.Drop - bu.Start - 0.25); // pull in over the build
            foreach (var d in drops)
                Move(d, (x, _) => x.far, 0.5, 0.5, 0.5); // and burst out on the drop
            Move(end - 2, (x, _) => x.home, 0, 2);
        }

        // one motion per group and beat (later rules win)
        var rot = map.GroupRotations.GroupBy(r => (Math.Round(r.Beat, 4), r.Group, r.Axis)).Select(x => x.Last()).OrderBy(r => r.Beat).ToList();
        map.GroupRotations.Clear();
        map.GroupRotations.AddRange(rot);
        var tr = map.GroupTranslations.GroupBy(t => (Math.Round(t.Beat, 4), t.Group, t.Axis)).Select(x => x.Last()).OrderBy(t => t.Beat).ToList();
        map.GroupTranslations.Clear();
        map.GroupTranslations.AddRange(tr);
    }

    /// <summary>Signed angle -180..180.</summary>
    static double Angle(double deg) => ((deg % 360) + 540) % 360 - 180;

    /// <summary>The angles a group turns between, from the poses mappers use (fallback: ±range/2 about 0).</summary>
    sealed record RotationPoses(double Home, double[] Others)
    {
        public static RotationPoses Of(int[]? learned, double range)
        {
            var poses = learned is { Length: > 0 } rp ? rp.Select(x => (double)x).ToArray() : [];
            if (poses.Length == 0)
            {
                double r = range >= 5 ? Math.Min(range, 90) : 30;
                return new(0, [r / 2, -r / 2]);
            }
            double home = poses[0];
            // the common poses (first 5) within 135° of home: further turns read as a flip, not a sweep
            var others = poses.Skip(1).Take(5).Where(x => Math.Abs(Angle(x - home)) <= 135).ToArray();
            return new(home, others.Length > 0 ? others : [home + 45, home - 45]);
        }

        double Delta(double x) => Angle(x - Home);

        /// <summary>Largest turn from home among the poses.</summary>
        public double Reach => Others.Max(x => Math.Abs(Delta(x)));

        /// <summary>The pose closest to home.</summary>
        public double Near => Others.MinBy(x => Math.Abs(Delta(x)));

        /// <summary>The pose furthest from home.</summary>
        public double Wide => Others.MaxBy(x => Math.Abs(Delta(x)));

        /// <summary>The pose that turns about <paramref name="share"/> of <see cref="Reach"/> from home, and its
        /// counterpart: the pose nearest the mirrored turn (home when there is none within 30°).</summary>
        public (double, double) Swing(double share)
        {
            double want = share * Reach;
            double a = Others.MinBy(x => Math.Abs(Math.Abs(Delta(x)) - want));
            double mirror = Home - Delta(a);
            double b = Others.Where(x => x != a).DefaultIfEmpty(Home).MinBy(x => Math.Abs(Angle(x - mirror)));
            return (a, Math.Abs(Angle(b - mirror)) <= 30 ? b : Home);
        }

        /// <summary>A turn for <paramref name="t"/> in -1..1: 0 is home, ±1 the furthest pose on that side
        /// (the widest pose either way when the group only turns to one side).</summary>
        public double Toward(double t)
        {
            var side = Others.Where(x => Math.Sign(Delta(x)) == Math.Sign(t)).ToArray();
            double reach = side.Length > 0 ? side.Max(x => Math.Abs(Delta(x))) * Math.Sign(t) : Delta(Wide);
            return Home + Math.Abs(t) * reach;
        }
    }

    /// <summary>Groups that move together: mirror partners (the lower id first), every other group alone.</summary>
    static List<Unit> MotionUnits(IEnumerable<LightGroupInfo> groups)
    {
        var list = groups.ToList();
        var ids = list.Select(g => g.Id).ToHashSet();
        var units = new List<Unit>();
        foreach (var g in list.OrderBy(g => g.Id))
        {
            if (g.Mirror is int m && ids.Contains(m))
            {
                if (m < g.Id) continue;
                units.Add(new Unit([g, list.First(h => h.Id == m)]));
            }
            else units.Add(new Unit([g]));
        }
        return units;
    }

    /// <summary>Left/right pairs: mirror partners learned from curated maps, otherwise neighbouring groups
    /// (consecutive ids) that curated maps use alike.</summary>
    static List<Unit> Pair(List<LightGroupInfo> groups)
    {
        var units = new List<Unit>();
        var byId = groups.ToDictionary(g => g.Id);
        var taken = new HashSet<int>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (!taken.Add(g.Id)) continue;
            if (g.Mirror is int m && byId.TryGetValue(m, out var partner) && taken.Add(m))
                units.Add(new Unit(g.Id < m ? [g, partner] : [partner, g]));
            else if (g.Mirror == null && i + 1 < groups.Count && groups[i + 1] is var h && h.Id == g.Id + 1 && h.Mirror == null && Alike(g, h) && taken.Add(h.Id))
                units.Add(new Unit([g, h]));
            else units.Add(new Unit([g]));
        }
        return units;
    }

    static bool Alike(LightGroupInfo g, LightGroupInfo h) =>
        Math.Abs(g.Color - h.Color) <= 0.25
        && (g.Rotation >= 0.3) == (h.Rotation >= 0.3)
        && (g.Translation >= 0.3) == (h.Translation >= 0.3)
        && (g.Lights == 0 || h.Lights == 0 || Math.Max(g.Lights, h.Lights) <= 2 * Math.Min(g.Lights, h.Lights));

    /// <summary>The most prominent units take the roles; among them the busier roles (notes, kicks, melody,
    /// snares, chases, hats, then the ambient wash) go to the groups curated maps switch on more often (when
    /// every rate is known; else in order of prominence). With fewer units than roles the later roles share a
    /// unit: snares flash the kick unit, melody the ambient one, hats the chase (or notes) one.</summary>
    static Dictionary<Role, Unit> AssignRoles(List<Unit> units)
    {
        var order = new[] { Role.Kick, Role.Ambient, Role.Notes, Role.Snare, Role.Melody, Role.Chase, Role.Hats };
        var busy = new[] { Role.Notes, Role.Kick, Role.Melody, Role.Snare, Role.Chase, Role.Hats, Role.Ambient };
        var roles = new Dictionary<Role, Unit>();
        int n = Math.Min(order.Length, units.Count);
        if (units.Take(n).All(u => u.Rate != null))
        {
            var present = busy.Where(r => Array.IndexOf(order, r) < n).ToList();
            var byRate = units.Take(n).OrderByDescending(u => u.Rate).ToList();
            for (int i = 0; i < n; i++) roles[present[i]] = byRate[i];
        }
        else
            for (int i = 0; i < n; i++) roles[order[i]] = units[i];
        // more units than roles: the rest join in as extra chases (cycling) so no group stays dark
        var fallback = new (Role Role, Role[] From)[]
        {
            (Role.Ambient, [Role.Kick]), (Role.Notes, [Role.Kick]), (Role.Snare, [Role.Kick]), (Role.Melody, [Role.Ambient]),
            (Role.Chase, [Role.Ambient]), (Role.Hats, [Role.Chase, Role.Notes]),
        };
        foreach (var (role, from) in fallback)
            if (!roles.ContainsKey(role) && from.Select(roles.GetValueOrDefault).FirstOrDefault(u => u != null) is { } u)
                roles[role] = u;
        if (units.Count > order.Length)
            roles[Role.Chase] = new Unit([.. roles[Role.Chase].Groups, .. units.Skip(order.Length).SelectMany(u => u.Groups)]);
        return roles;
    }
}
