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
            // classic event types mean other things here; only special events (40+) are written
            Assert.All(d.Lights, l => Assert.True(l.Type >= 40));
        }
    }

    [Fact]
    public void RotatedVersionsOfGroupEnvironmentsKeepClassicLights()
    {
        var r = MapGenerator.Generate(Drummed(), new GeneratorSettings { Difficulties = [DifficultyName.Hard], Environment = "Weave", Modes = ["360Degree"] });
        Assert.Contains(r.Extra[0].Map.Lights, l => l.Type < 40);
        Assert.DoesNotContain(r.Difficulties[0].Map.Lights, l => l.Type < 40);
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
    public void LatticeMirrorsAreLearned()
    {
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
            var rot = d.GroupRotations.ToLookup(r => (Math.Round(r.Beat, 4), r.Group));
            foreach (var r in d.GroupRotations.Where(r => byId[r.Group].Mirror is int m && rot[(Math.Round(r.Beat, 4), m)].Any()))
                Assert.Equal(r.Degrees, rot[(Math.Round(r.Beat, 4), byId[r.Group].Mirror!.Value)].Single().Degrees);

            // turns stay within the furthest learned pose from home (within 135°)
            foreach (var r in d.GroupRotations)
            {
                var poses = Lead(r.Group).RotationPoses;
                if (poses is not { Length: > 1 }) continue;
                double reach = poses.Skip(1).Take(5).Select(p => Math.Abs(Signed(p - poses[0]))).Where(x => x <= 135).DefaultIfEmpty(45).Max();
                Assert.True(Math.Abs(Signed(r.Degrees - poses[0])) <= reach + 1e-6, $"{env.Id} group {r.Group} turns to {r.Degrees}");
            }

            // rails move within the distances mappers use on their axis, starting from home
            foreach (var t in d.GroupTranslations)
            {
                var g = Lead(t.Group);
                if (g.TranslationValues?.GetValueOrDefault(g.TranslationAxes[0]) is not { Length: 3 } v) continue;
                Assert.InRange(t.Distance, Math.Min(v[0], v[1]) - 1e-6, Math.Max(v[2], v[1]) + 1e-6);
            }
            foreach (var first in d.GroupTranslations.GroupBy(t => t.Group).Select(x => x.MinBy(t => t.Beat)!))
                if (Lead(first.Group).TranslationValues?.GetValueOrDefault(Lead(first.Group).TranslationAxes[0]) is { Length: 3 } v)
                    Assert.Equal(v[1], first.Distance);
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
}
