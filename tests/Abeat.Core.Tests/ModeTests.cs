using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class ModeTests
{
    [Fact]
    public void ExtraModesPlayable()
    {
        var a = TestSongs.Fake();
        var s = new GeneratorSettings { Difficulties = [DifficultyName.Expert], Modes = ["OneSaber", "90Degree", "360Degree"] };
        var r = MapGenerator.Generate(a, s);
        Assert.Equal(3, r.Extra.Count);
        var one = r.Extra.Single(x => x.Map.Characteristic == Characteristic.OneSaber);
        Assert.NotEmpty(one.Map.Notes);
        Assert.All(one.Map.Notes, n => Assert.Equal(Hand.Right, n.Hand));
        Assert.Equal(one.Map.Notes.Count, one.Map.Notes.Select(n => n.Beat).Distinct().Count()); // no doubles
        Assert.Equal(0, one.Report.Resets + one.Report.WallClashes + one.Report.BombHits);
        Assert.Contains(one.Map.Notes, n => n.X == 0); // the one saber covers the whole grid

        var ninety = r.Extra.Single(x => x.Map.Characteristic == Characteristic.Degree90).Map;
        Assert.NotEmpty(ninety.Rotations);
        double heading = 0;
        foreach (var rot in ninety.Rotations) { heading += rot.Degrees; Assert.InRange(heading, -45, 45); }
        Assert.All(ninety.Rotations, rot => Assert.Contains(ninety.Notes, n => n.Beat == rot.Beat));
        Assert.Equal(r.Difficulties[0].Map.Notes, ninety.Notes);
        Assert.Equal("Expert90Degree.dat", ninety.FileName);
    }
}
