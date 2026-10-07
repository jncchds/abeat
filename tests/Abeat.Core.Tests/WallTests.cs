using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class WallTests
{
    static bool IsRhythmWall(Obstacle o) => o.Width == 1 && o.X is 0 or 3 && o.Y == 2 && o.Height == 3 && o.Duration == 0.125;

    [Fact]
    public void RhythmWallsFollowLoudHitsAlongTheTopOfTheOuterLanes()
    {
        var a = TestSongs.Fake();
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert);
        var walls = r.Map.Obstacles.Where(IsRhythmWall).ToList();
        Assert.NotEmpty(walls);
        double loudFrom = a.SecondsToBeat(a.Sections[1].Start);
        Assert.All(walls, w => Assert.True(w.Beat >= loudFrom - 1e-6, "only in the loud section"));
        var times = walls.Select(w => a.BeatToSeconds(w.Beat)).ToList();
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= 1.2 - 1e-6));
        Assert.Contains(walls, w => w.X == 0);
        Assert.Contains(walls, w => w.X == 3);
        Assert.Equal(0, r.Report.WallClashes + r.Report.BombHits);
        Assert.DoesNotContain(r.Map.Bombs, b => r.Map.Obstacles.Any(o => WallGenerator.Inside(o, b.Beat, b.X, b.Y, 0)));

        Assert.DoesNotContain(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { RhythmWalls = false }, DifficultyName.Expert).Map.Obstacles, IsRhythmWall);
    }

    [Fact]
    public void CrouchWallsAreOptIn()
    {
        var a = TestSongs.Fake();
        static bool Crouch(Obstacle o) => o.Width == 4 && o.Y == 2;
        Assert.DoesNotContain(MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert).Map.Obstacles, Crouch);
    }
}
