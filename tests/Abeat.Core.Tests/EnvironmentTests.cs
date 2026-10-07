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
