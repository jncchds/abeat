using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>v3 group lightshow for any group-lighting environment, laid out from how curated maps use its
/// light groups (<see cref="EnvironmentCatalog"/>). Neighbouring look-alike groups pair up as left/right units;
/// units get musical roles by prominence (kicks, ambient section wash, note flashes per hand, snares,
/// melody, downbeat chases, hats) and groups that turn or move get choreography that follows the song:
/// slow drifts in quiet parts, swings on downbeats in loud parts, lights folding together over a
/// build-up and bursting open on the drop, and a group tracing the melody's contour.</summary>
public static class GroupLightshow
{
    const int Red = 0, Blue = 1, White = 2;

    enum Role { Kick, Ambient, Notes, Snare, Melody, Chase, Hats }

    /// <summary>One or two groups (left, right) lit together.</summary>
    sealed record Unit(LightGroupInfo[] Groups)
    {
        public double Color => Groups.Max(g => g.Color);
        public int Lights => Groups.Max(g => g.Lights);
    }

    public static void Generate(SongAnalysis a, DifficultyMap map, IReadOnlyList<RhythmEvent> events, EnvironmentInfo env)
    {
        var lit = env.Groups.Where(g => g.Color >= 0.3).ToList();
        if (lit.Count == 0) lit = env.Groups.Where(g => g.Color > 0).ToList();
        if (lit.Count == 0) return;
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

        // environment special events (40-43: set-piece animations) go off on the drops, with the value
        // curated maps use most
        foreach (int t in Enumerable.Range(40, 4).Where(env.Has))
        {
            int v = env.BasicValues.TryGetValue(t, out var vals) && vals.Length >= 3 ? vals[2] : 0;
            foreach (var d in drops.Where(d => d < end)) map.Lights.Add(new LightEvent(d, t, v));
        }

        // everything out after the last note
        foreach (var g in lit)
            lights.Add(new LightGroupEvent(end, g.Id, [new LightBox(1, 0, false, 0, 0, [new(0, Blue, 0)])]));

        // one event per group and beat (later rules win)
        var unique = lights.GroupBy(l => (Math.Round(l.Beat, 4), l.Group)).Select(g => g.Last()).OrderBy(l => l.Beat).ToList();
        lights.Clear();
        lights.AddRange(unique);
    }

    /// <summary>Rotation and translation choreography for every group curated maps move. Quiet sections
    /// drift slowly; louder ones swing on downbeats, wider and fanned out with energy; over a build-up the
    /// lights fold together (fan closing, rails pulling in) and burst open on the drop. With two or more
    /// rotating groups the most-used one traces the melody instead: higher sounds tilt it further.</summary>
    static void Motion(SongAnalysis a, DifficultyMap map, EnvironmentInfo env, double end, double maxEnergy,
        List<double> downbeats, List<double> drops, List<BuildUp> builds, List<RhythmEvent> melody)
    {
        var rotating = env.Groups.Where(g => g.Rotation >= 0.3 && g.RotationAxes.Length > 0).OrderByDescending(g => g.Rotation).ToList();
        var moving = env.Groups.Where(g => g.Translation >= 0.3 && g.TranslationAxes.Length > 0).OrderByDescending(g => g.Translation).ToList();
        var tracer = rotating.Count >= 2 && melody.Count >= 16 ? rotating[0] : null;
        var pitch = SongShape.RelativePitch(melody);
        bool InBuild(double b) => builds.Any(x => b >= x.Start && b < x.Drop + 0.01);

        foreach (var (g, gi) in rotating.Select((g, i) => (g, i)))
        {
            int axis = g.RotationAxes[0];
            double range = g.RotationRange >= 5 ? Math.Min(g.RotationRange, 90) : 30;
            int dir = gi % 2 == 0 ? 1 : -1; // neighbouring groups swing in mirror
            void Rot(double beat, double deg, double spread, double dur, double wave = 0)
            {
                if (beat < end) map.GroupRotations.Add(new LightRotation(beat, g.Id, axis, dir * deg, dir * spread, dur, BeatSpread: wave));
            }
            if (g == tracer)
            {
                double last = double.NegativeInfinity;
                foreach (var e in melody.Where(e => e.Beat - last >= 0.5 && !InBuild(e.Beat)))
                {
                    last = e.Beat;
                    Rot(e.Beat, pitch(e) * range, range * 0.2, 0.5);
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
                        // slow drift: a long eased turn every four bars
                        for (int i = 0; i < bars.Count; i += 4)
                            Rot(bars[i], (i / 4 % 2 == 0 ? 0.35 : -0.35) * range, 0.3 * range, 16, 0);
                    }
                    else
                    {
                        // swings on downbeats (every bar when loud, every other bar otherwise), fanning wider with energy
                        int every = rel >= 0.8 ? 1 : 2;
                        double amp = range * (0.4 + 0.6 * rel);
                        for (int i = 0; i < bars.Count; i += every)
                            Rot(bars[i], (i / every % 2 == 0 ? 1 : -1) * amp, amp * rel, Math.Min(4, 2 * every), 0.5);
                    }
                }
            foreach (var bu in builds)
                Rot(bu.Start, 0, 0, bu.Drop - bu.Start - 0.25); // fold together over the build
            foreach (var d in drops)
                Rot(d, range, 2 * range, 0.5, 0.25); // burst open on the drop
        }

        foreach (var (g, gi) in moving.Select((g, i) => (g, i)))
        {
            int axis = g.TranslationAxes[0];
            double range = g.TranslationRange > 0.01 ? g.TranslationRange : 1;
            void Move(double beat, double dist, double spread, double dur, double wave = 0)
            {
                if (beat < end) map.GroupTranslations.Add(new LightTranslation(beat, g.Id, axis, dist, spread, dur, wave));
            }
            foreach (var sec in a.Sections)
            {
                double b0 = Math.Round(a.SecondsToBeat(sec.Start)), b1 = a.SecondsToBeat(sec.End);
                if (b0 >= end) break;
                double rel = maxEnergy > 0 ? sec.Energy / maxEnergy : 0.5;
                if (rel < 0.45) { Move(b0, 0, 0, 4); continue; }
                // pump with the bars: out on the downbeat, back over the bar
                var bars = downbeats.Where(b => b >= b0 - 0.01 && b < b1 - 0.01 && !InBuild(b)).ToList();
                for (int i = 0; i < bars.Count; i++)
                    Move(bars[i], (i % 2 == 0 ? 1 : 0.4) * range * rel, range * 0.3 * rel, 1, 0.25);
            }
            foreach (var bu in builds)
                Move(bu.Start, -0.2 * range, 0, bu.Drop - bu.Start - 0.25); // pull in over the build
            foreach (var d in drops)
                Move(d, range, range * 0.5, 0.5, 0.5); // and burst out on the drop
        }

        // one motion per group and beat (later rules win)
        var rot = map.GroupRotations.GroupBy(r => (Math.Round(r.Beat, 4), r.Group)).Select(x => x.Last()).OrderBy(r => r.Beat).ToList();
        map.GroupRotations.Clear();
        map.GroupRotations.AddRange(rot);
        var tr = map.GroupTranslations.GroupBy(t => (Math.Round(t.Beat, 4), t.Group)).Select(x => x.Last()).OrderBy(t => t.Beat).ToList();
        map.GroupTranslations.Clear();
        map.GroupTranslations.AddRange(tr);
    }

    /// <summary>Neighbouring groups (consecutive ids) that curated maps use alike form a left/right pair.</summary>
    static List<Unit> Pair(List<LightGroupInfo> groups)
    {
        var units = new List<Unit>();
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (i + 1 < groups.Count && groups[i + 1] is var h && h.Id == g.Id + 1 && Alike(g, h))
            {
                units.Add(new Unit([g, h]));
                i++;
            }
            else units.Add(new Unit([g]));
        }
        return units;
    }

    static bool Alike(LightGroupInfo g, LightGroupInfo h) =>
        Math.Abs(g.Color - h.Color) <= 0.25
        && (g.Rotation >= 0.3) == (h.Rotation >= 0.3)
        && (g.Translation >= 0.3) == (h.Translation >= 0.3)
        && (g.Lights == 0 || h.Lights == 0 || Math.Max(g.Lights, h.Lights) <= 2 * Math.Min(g.Lights, h.Lights));

    /// <summary>Roles go to units in order of prominence. With fewer units than roles the later roles
    /// share a unit: snares flash the kick unit, melody the ambient one, hats the chase (or notes) one.</summary>
    static Dictionary<Role, Unit> AssignRoles(List<Unit> units)
    {
        var order = new[] { Role.Kick, Role.Ambient, Role.Notes, Role.Snare, Role.Melody, Role.Chase, Role.Hats };
        var roles = new Dictionary<Role, Unit>();
        for (int i = 0; i < order.Length && i < units.Count; i++) roles[order[i]] = units[i];
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
