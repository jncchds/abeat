using Abeat.Core.Analysis;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class EnvironmentTests
{
    static SongAnalysis Drummed()
    {
        var a = TestSongs.Fake();
        foreach (var o in a.Layers["low"]) o.K = "k";
        a.Layers["drums"] = a.Layers["low"];
        return a;
    }

    static GenerationResult Gen(SongAnalysis a, string env, int seed = 1) =>
        MapGenerator.Generate(a, new GeneratorSettings { Difficulties = [DifficultyName.Hard], Environment = env, Seed = seed });

    [Fact]
    public void CatalogCoversEveryEnvironment()
    {
        Assert.True(EnvironmentCatalog.All.Count >= 45);
        Assert.Equal(EnvironmentCatalog.All.Count, EnvironmentCatalog.All.Select(e => e.Id).Distinct().Count());
        Assert.Equal("PyroEnvironment", EnvironmentCatalog.Find("Pyro")!.Id);
        Assert.Equal("DefaultEnvironment", EnvironmentCatalog.Find("Default")!.Id);
        Assert.Equal("BillieEnvironment", EnvironmentCatalog.Find("Billie Eilish")!.Id);
        // the survey found light groups for the group environments
        var weave = EnvironmentCatalog.Find("WeaveEnvironment")!;
        Assert.Equal(LightingSystem.Groups, weave.System);
        Assert.NotEmpty(weave.Groups);
    }

    [Fact]
    public void EveryGroupEnvironmentGetsALightshowOnItsOwnGroups()
    {
        var a = Drummed();
        foreach (var env in EnvironmentCatalog.All.Where(e => e.System == LightingSystem.Groups))
        {
            var r = Gen(a, env.Id);
            Assert.Equal(env.Id, r.Map.Environment);
            var d = r.Difficulties[0].Map;
            var ids = env.Groups.Select(g => g.Id).ToHashSet();
            Assert.NotEmpty(d.GroupLights);
            Assert.All(d.GroupLights, g => Assert.Contains(g.Group, ids));
            Assert.All(d.GroupRotations, x => Assert.True(env.Groups.Single(g => g.Id == x.Group).Rotation >= 0.3, $"{env.Id} rotates group {x.Group}"));
            Assert.All(d.GroupTranslations, x => Assert.True(env.Groups.Single(g => g.Id == x.Group).Translation >= 0.3, $"{env.Id} moves group {x.Group}"));
            // classic event types mean other things here: only the environment's own basic lights and
            // special events (40+) are written
            var basic = (env.BasicLights ?? []).Select(b => b.Type).Concat((env.Steps ?? []).Select(x => x.Type)).ToHashSet();
            Assert.All(d.Lights, l => Assert.True(l.Type >= 40 || basic.Contains(l.Type), $"{env.Id} writes type {l.Type}"));
        }
    }

    [Fact]
    public void PyroFiresItsFireRarelyAndLightsItsStage()
    {
        var env = EnvironmentCatalog.Find("Pyro")!;
        var d = Gen(Drummed(), "Pyro").Difficulties[0].Map;
        double end = d.Notes.Max(n => n.Beat);
        // the fire jets (12/13) burst about as often as curated maps fire them, not on every note
        var fire = env.Groups.Single(g => g.Id == 12);
        Assert.True(fire.Rate < 0.1);
        int bursts = d.GroupLights.Count(g => g.Group == 12 && g.Boxes[0].Events[0].Brightness > 0);
        Assert.InRange(bursts, 1, Math.Max(2, 3 * fire.Rate!.Value * end));
        Assert.Equal(bursts, d.GroupLights.Count(g => g.Group == 13 && g.Boxes[0].Events[0].Brightness > 0));
        // the runway (4) washes with the sections, the projectors (2/3) mirror each other
        Assert.Contains(env.BasicLights!, b => b.Type == 4);
        Assert.Contains(d.Lights, l => l.Type == 4 && l.Value != LightValue.Off);
        var left = d.Lights.Where(l => l.Type == 2).Select(l => (l.Beat, l.Value, l.Brightness)).ToList();
        Assert.NotEmpty(left);
        Assert.Equal(left, d.Lights.Where(l => l.Type == 3).Select(l => (l.Beat, l.Value, l.Brightness)).ToList());
    }

    [Fact]
    public void TheSecondPulsesItsRingSpacing()
    {
        var env = EnvironmentCatalog.Find("TheSecond")!;
        var step = Assert.Single(env.Steps!);
        Assert.Equal(9, step.Type);
        Assert.True(step.High > step.Low);
        var d = Gen(Drummed(), "TheSecond").Difficulties[0].Map;
        var rings = d.Lights.Where(l => l.Type == 9).ToList();
        Assert.All(rings, l => Assert.InRange(l.Value, step.Low, step.High));
        Assert.Contains(rings, l => l.Value == step.High);
        Assert.Equal(step.Low, rings[^1].Value);
    }

    [Fact]
    public void TheBusiestRoleGoesToTheGroupMappersSwitchOnMostOften()
    {
        // The Second: curated maps switch the runway lasers (2/3) on most often, so they carry the notes
        // (left hand on 2)
        var env = EnvironmentCatalog.Find("TheSecond")!;
        Assert.Equal(2, env.Groups.Where(g => g.Color >= 0.3).MaxBy(g => g.Rate)!.Id);
        var d = Gen(Drummed(), "TheSecond").Difficulties[0].Map;
        var runway = d.GroupLights.Where(x => x.Group == 2).Select(x => Math.Round(x.Beat, 4)).ToHashSet();
        Assert.All(d.Notes.Where(n => n.Hand == Hand.Left), n => Assert.Contains(Math.Round(n.Beat, 4), runway));
    }

    [Fact]
    public void RotatedVersionsOfGroupEnvironmentsKeepClassicLights()
    {
        var r = MapGenerator.Generate(Drummed(), new GeneratorSettings { Difficulties = [DifficultyName.Hard], Environment = "Weave", Modes = ["360Degree"] });
        Assert.Contains(r.Extra[0].Map.Lights, l => l.Type < 40);
        Assert.DoesNotContain(r.Difficulties[0].Map.Lights, l => l.Type < 40);
    }

    [Fact]
    public void EdmCirclesRestAtHomeBetweenSwings()
    {
        // curated maps leave EDM's circle rotation groups at 0° and flick them out and back: home is the
        // pose held longest, not the most keyframed one
        var env = EnvironmentCatalog.Find("EDM")!;
        foreach (int id in new[] { 12, 13, 14, 15 })
        {
            var g = env.Groups.Single(x => x.Id == id);
            Assert.Equal(0, g.RotationPoses![0]);
            Assert.True(g.HomeShare >= 0.5, $"group {id} rests {g.HomeShare}");
        }
        var d = Gen(Drummed(), "EDM").Difficulties[0].Map;
        var turns = d.GroupRotations.Where(r => r.Group == 13 && r.Axis == env.Groups.Single(x => x.Id == 13).RotationAxes[0]).ToList();
        Assert.True(turns.Count(r => r.Degrees == 0) * 3 >= turns.Count, "group 13 keeps coming home");
    }

    [Fact]
    public void PyroKeepsItsKicksAndMotion()
    {
        var r = Gen(Drummed(), "Pyro");
        var d = r.Difficulties[0].Map;
        Assert.All(d.GroupLights, g => Assert.InRange(g.Group, 0, 13));
        Assert.NotEmpty(d.GroupRotations);
        var json = MapWriter.DifficultyJson(d);
        Assert.NotNull(json["lightColorEventBoxGroups"]![0]!["e"]![0]!["f"]?["p"]);
        // a turn holds the previous angle at its start, then eases to the target
        var events = json["lightRotationEventBoxGroups"]![0]!["e"]![0]!["l"]!.AsArray();
        Assert.True(events.Count is 1 or 2);
    }

    [Fact]
    public void WeaveTurnsAboutBothAxes()
    {
        // Weave's lights turn about x and y; mappers set both in one event group
        var env = EnvironmentCatalog.Find("Weave")!;
        Assert.All(env.Groups.Where(g => g.Id < 16), g => Assert.Equal([0, 1], g.RotationAxes));
        // light counts from filters at least three maps agree on (the game has 8 per group, 12 on 12/13)
        Assert.All(env.Groups.Where(g => g.Id < 16), g => Assert.Equal(g.Id is 12 or 13 ? 12 : 8, g.Lights));
        var d = Gen(Drummed(), "Weave").Difficulties[0].Map;
        Assert.Equal([0, 1], d.GroupRotations.Select(r => r.Axis).Distinct().Order());
        var json = MapWriter.DifficultyJson(d);
        var groups = json["lightRotationEventBoxGroups"]!.AsArray();
        Assert.Equal(groups.Count, groups.Select(g => (g!["b"]!.GetValue<double>(), g["g"]!.GetValue<int>())).Distinct().Count());
        Assert.Contains(groups, g => g!["e"]!.AsArray().Count == 2);
    }

    [Fact]
    public void LatticeMirrorsAreLearned()
    {
        // Lattice's cube clusters are moved on all three axes: every one gets its home pose
        var d = Gen(Drummed(), "Lattice").Difficulties[0].Map;
        Assert.Equal([0, 1, 2], d.GroupTranslations.Where(t => t.Group == 0).Select(t => t.Axis).Distinct().Order());
        // the survey pairs each Lattice group with the one the game mirrors (n <-> n + 7)
        var env = EnvironmentCatalog.Find("Lattice")!;
        Assert.Equal(8, env.Groups.Single(g => g.Id == 1).Mirror);
        Assert.Equal(7, env.Groups.Single(g => g.Id == 0).Mirror);
    }

    [Fact]
    public void GroupMotionMovesMirrorPartnersTogetherBetweenLearnedPoses()
    {
        var a = Drummed();
        foreach (var env in EnvironmentCatalog.All.Where(e => e.System == LightingSystem.Groups))
        {
            var d = Gen(a, env.Id).Difficulties[0].Map;
            var byId = env.Groups.ToDictionary(g => g.Id);
            // a mirror pair moves with the lower id's poses
            LightGroupInfo Lead(int id) => byId[id].Mirror is int m && byId.ContainsKey(m) && m < id ? byId[m] : byId[id];
            static double Signed(double x) => ((x % 360) + 540) % 360 - 180;

            // partners get identical events (no sign flip: the game mirrors the other side itself)
            var rot = d.GroupRotations.ToLookup(r => (Math.Round(r.Beat, 4), r.Group, r.Axis));
            foreach (var r in d.GroupRotations.Where(r => byId[r.Group].Mirror is int m && rot[(Math.Round(r.Beat, 4), m, r.Axis)].Any()))
                Assert.Equal(r.Degrees, rot[(Math.Round(r.Beat, 4), byId[r.Group].Mirror!.Value, r.Axis)].Single().Degrees);

            // turns about each axis stay within the furthest pose learned on it from home (within 135°)
            foreach (var r in d.GroupRotations)
            {
                Assert.Contains(r.Axis, Lead(r.Group).RotationAxes);
                var poses = Lead(r.Group).PosesOn(r.Axis);
                if (poses is not { Length: > 1 }) continue;
                double reach = poses.Skip(1).Take(5).Select(p => Math.Abs(Signed(p - poses[0]))).Where(x => x <= 135).DefaultIfEmpty(45).Max();
                Assert.True(Math.Abs(Signed(r.Degrees - poses[0])) <= reach + 1e-6, $"{env.Id} group {r.Group} turns to {r.Degrees}");
            }

            // rails move on each axis within the distances mappers use there, starting from home, or from the
            // intro pose mappers start them at (The Weeknd's ring sets start lowered) and rising home next
            double? Intro(LightTranslation t) => Lead(t.Group).TranslationIntro?.TryGetValue(t.Axis, out var x) == true ? x : null;
            foreach (var t in d.GroupTranslations.GroupBy(t => (t.Group, t.Axis)))
            {
                var moves = t.OrderBy(x => x.Beat).ToList();
                if (Lead(moves[0].Group).TranslationValues?.GetValueOrDefault(moves[0].Axis) is not { Length: 3 } v) continue;
                int from = 0;
                if (Intro(moves[0]) is double intro)
                {
                    Assert.Equal(intro, moves[0].Distance);
                    from = 1;
                }
                Assert.Equal(v[1], moves[from].Distance);
                foreach (var m in moves.Skip(from))
                    Assert.InRange(m.Distance, Math.Min(v[0], v[1]) - 1e-6, Math.Max(v[2], v[1]) + 1e-6);
            }
        }
    }

    [Fact]
    public void DropsFlashWhiteAndFoldOpen()
    {
        var a = Drummed();
        var drops = SongShape.Drops(a).Select(t => Math.Round(a.SecondsToBeat(t))).ToList();
        Assert.Equal([100.0], drops);
        Assert.Single(SongShape.BuildUps(a, SongShape.Drops(a)));

        var classic = Gen(a, "DefaultEnvironment").Difficulties[0].Map;
        Assert.Contains(classic.Lights, l => l.Beat == 100 && l.Value == LightValue.WhiteFlash);
        Assert.Contains(classic.Boosts, b => b.Beat == 100 && b.On);
        // the build-up spins the rings faster towards the drop
        var spins = classic.Lights.Where(l => l.Type == 8 && l.Beat is >= 84 and < 100).Select(l => l.Beat).ToList();
        Assert.True(spins.Count >= 6);

        var weave = Gen(a, "WeaveEnvironment").Difficulties[0].Map;
        Assert.Contains(weave.GroupLights, g => g.Beat == 100 && g.Boxes[0].Events[0].Color == 2);
    }

    [Fact]
    public void ClassicExtrasOnlyWhereTheEnvironmentHasThem()
    {
        var a = Drummed();
        var gaga = EnvironmentCatalog.Find("GagaEnvironment")!;
        var d = Gen(a, gaga.Id).Difficulties[0].Map;
        if (gaga.Has(6)) Assert.Contains(d.Lights, l => l.Type is 6 or 7);
        Assert.DoesNotContain(Gen(a, "DefaultEnvironment").Difficulties[0].Map.Lights, l => l.Type is 6 or 7 or 10 or 11 or >= 16);
        Assert.DoesNotContain(Gen(a, "WeaveEnvironment").Difficulties[0].Map.Lights, l => l.Type is 6 or 7 or 10 or 11 or >= 16);
    }

    [Fact]
    public void AutoPicksAFittingEnvironmentDeterministically()
    {
        var a = Drummed();
        var r1 = Gen(a, "Auto");
        var r2 = Gen(a, "Auto");
        Assert.Equal(r1.Map.Environment, r2.Map.Environment);
        Assert.NotNull(EnvironmentCatalog.Find(r1.Map.Environment));
        // kick on every beat: an electronic, four-on-the-floor environment
        var c = SongShape.Character(a);
        Assert.True(c.Drive > 0.8);
        Assert.True(EnvironmentCatalog.Find(r1.Map.Environment)!.Character.Drive >= 0.5);
        Assert.Equal(MapWriter.DifficultyJson(r1.Difficulties[0].Map).ToJsonString(), MapWriter.DifficultyJson(r2.Difficulties[0].Map).ToJsonString());
    }

    /// <summary>The drummed song with 32 beats of music after the onsets stop, then 4 s of silence.</summary>
    static SongAnalysis WithOutro()
    {
        var a = Drummed();
        double spb = 60 / a.Tempo.Bpm, music = 232 * spb, end = music + 4;
        a.Audio.DurationSec = end;
        a.Energy.Values = [.. Enumerable.Range(0, (int)end + 1).Select(i => i < 100 * spb ? 0.4 : i < music ? 0.9 : 0)];
        a.Sections[^1].End = music;
        return a;
    }

    [Fact]
    public void LightsDimWithTheOutroInsteadOfCuttingOut()
    {
        var a = WithOutro();
        foreach (var env in new[] { "DefaultEnvironment", "WeaveEnvironment" })
        {
            var d = Gen(a, env).Difficulties[0].Map;
            double last = d.Notes.Max(n => n.Beat), musicEnd = a.SecondsToBeat(a.Sections[^1].End);
            Assert.True(musicEnd - last > 16, env);
            if (env == "DefaultEnvironment")
            {
                var back = d.Lights.Where(l => l.Type == 0 && l.Beat > last).ToList();
                // on in the outro, still lit just before the music stops, dark by the time it has stopped
                Assert.Contains(back, l => l.Value != LightValue.Off && l.Brightness > 0.3 && l.Beat >= musicEnd - 4);
                Assert.Equal(0, back[^1].Brightness);
                Assert.InRange(back[^1].Beat, musicEnd - 1, musicEnd + 2);
                Assert.DoesNotContain(back, l => l.Value == LightValue.Off);
                Assert.False(d.Boosts.Where(b => b.Beat <= last + 2).Last().On);
            }
            else
            {
                var outro = d.GroupLights.Where(g => g.Beat > last).ToList();
                Assert.NotEmpty(outro);
                var ev = outro[0].Boxes[0].Events;
                Assert.True(ev[0].Brightness > 0.3);
                Assert.Equal(0, ev[^1].Brightness);
                Assert.InRange(outro[0].Beat + ev[^1].Beat, musicEnd - 1, musicEnd + 2);
            }
        }
    }

    /// <summary>Share of [first note, last note] a light group is lit (brightness over 0.05; transitions hold).</summary>
    static double LitShare(DifficultyMap d, int group)
    {
        var steps = d.GroupLights.Where(g => g.Group == group)
            .SelectMany(g => g.Boxes.SelectMany(b => b.Events.Select(e => (Beat: g.Beat + e.Beat, e.Brightness)))).OrderBy(x => x.Beat).ToList();
        double first = d.Notes.Min(n => n.Beat), last = d.Notes.Max(n => n.Beat);
        int lit = 0, n = 0, k = 0;
        double level = 0;
        for (double t = first; t < last; t += 0.25, n++)
        {
            while (k < steps.Count && steps[k].Beat <= t) level = steps[k++].Brightness;
            if (level > 0.05) lit++;
        }
        return (double)lit / n;
    }

    [Fact]
    public void LightsMappersKeepOnGlowBetweenFlashes()
    {
        var collider = EnvironmentCatalog.Find("ColliderEnvironment")!;
        var runway = collider.Groups.Where(g => g.Lit >= 0.8 && g.Color >= 0.3).ToList();
        Assert.NotEmpty(runway); // the runway lights (19 / 20)
        var d = Gen(Drummed(), collider.Id).Difficulties[0].Map;
        foreach (var g in runway.Where(g => d.GroupLights.Any(x => x.Group == g.Id)))
            Assert.True(LitShare(d, g.Id) >= 0.6, $"group {g.Id} lit {LitShare(d, g.Id):0.00}");

        // the lights mappers mostly flash (Rock Mixtape's runway lasers) get exactly the events they get without
        // the learned lit shares; only the ones kept lit change
        var rock = EnvironmentCatalog.Find("RockMixtapeEnvironment")!;
        var unlearned = rock with { Groups = rock.Groups.Select(g => g with { Lit = null }).ToList() };
        var s = new GeneratorSettings { Difficulties = [DifficultyName.Hard], Seed = 1 };
        var a = Drummed();
        var with = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Hard, env: rock).Map;
        var without = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Hard, env: unlearned).Map;
        string Events(DifficultyMap m, int id) => string.Join(";", m.GroupLights.Where(x => x.Group == id)
            .Select(x => $"{x.Beat}:" + string.Join(",", x.Boxes.SelectMany(b => b.Events).Select(e => $"{e.Beat}/{e.Color}/{e.Brightness}"))));
        var flashed = rock.Groups.Where(g => g.Lit < GroupLightshow.GlowLit && g.Color >= 0.3).Select(g => g.Id).ToList();
        Assert.Contains(18, flashed);
        foreach (int id in flashed)
            Assert.Equal(Events(without, id), Events(with, id));
        Assert.Contains(rock.Groups.Where(g => g.Lit >= GroupLightshow.GlowLit), g => Events(without, g.Id) != Events(with, g.Id));
    }

    [Fact]
    public void BoostIsAShortAccent()
    {
        var d = Gen(Drummed(), "DefaultEnvironment").Difficulties[0].Map;
        // on at the drop, off again two bars later even though the loud section goes on
        Assert.Contains(d.Boosts, b => b.Beat == 100 && b.On);
        Assert.Contains(d.Boosts, b => b.Beat == 100 + LightingGenerator.BoostBeats && !b.On);
    }
}
