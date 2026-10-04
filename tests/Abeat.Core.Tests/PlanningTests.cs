using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class PlanningTests
{
    [Theory]
    [InlineData(DifficultyName.Easy)]
    [InlineData(DifficultyName.Hard)]
    [InlineData(DifficultyName.ExpertPlus)]
    public void GeneratedMapsFlow(DifficultyName d)
    {
        var a = TestSongs.Fake();
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), d);
        Assert.NotEmpty(r.Map.Notes);
        Assert.Equal(0, r.Report.Resets);
        Assert.Equal(0, r.Report.Crossovers);
        Assert.Equal(0, r.Report.HandClashes);
        Assert.InRange(r.Report.LeftShare, 0.35, 0.65);
        Assert.All(r.Map.Notes, n => Assert.InRange(n.X, 0, 3));
        Assert.All(r.Map.Notes, n => Assert.InRange(n.Y, 0, 2));
    }


    [Fact]
    public void HarderDifficultiesMoveHarder()
    {
        var a = TestSongs.Fake();
        var ranks = new[] { DifficultyName.Easy, DifficultyName.Hard, DifficultyName.ExpertPlus }
            .Select(d => MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), d).Report.Movement.MovementRank).ToList();
        Assert.True(ranks[0] < ranks[1] && ranks[1] < ranks[2], string.Join(", ", ranks));
    }


    [Fact]
    public void SameSeedSameMap()
    {
        var a = TestSongs.Fake();
        var s = new GeneratorSettings { Seed = 42 };
        var m1 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        var m2 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        Assert.Equal(m1, m2);
        var m3 = MapGenerator.GenerateDifficulty(a, s with { Seed = 43 }, DifficultyName.Expert).Map.Notes;
        Assert.NotEqual(m1, m3);
    }


    [Fact]
    public void LoudMomentsSwingBigger()
    {
        var a = TestSongs.Fake();
        double Size(GeneratorSettings s)
        {
            var r = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert);
            var loud = r.Events.Where(e => e.Intensity > 0.7).Select(e => e.Beat).ToHashSet();
            var notes = r.Map.Notes.Where(n => loud.Contains(n.Beat)).ToList();
            return notes.Average(n => Math.Abs(n.X - 1.5) + Math.Abs(n.Y - 1));
        }
        var on = new GeneratorSettings();
        var off = on with { Weights = on.Weights with { Dynamics = 0 } };
        Assert.True(Size(on) > Size(off), $"{Size(on)} vs {Size(off)}");
    }

    /// <summary>Two A sections and a B: the second A plays more of the first A's cuts with pattern memory on.</summary>
    [Fact]
    public void RepeatedSectionsEchoTheFirst()
    {
        var a = TestSongs.Fake();
        double end = a.Audio.DurationSec;
        a.Sections = [
            new Section { Start = 0, End = end / 3, Label = "A", Energy = 0.7 },
            new Section { Start = end / 3, End = 2 * end / 3, Label = "B", Energy = 0.7 },
            new Section { Start = 2 * end / 3, End = end, Label = "A", Energy = 0.7 },
        ];
        double Same(double w)
        {
            var s = new GeneratorSettings();
            var r = MapGenerator.GenerateDifficulty(a, s with { Weights = s.Weights with { Repetition = w } }, DifficultyName.Expert);
            Assert.Equal(0, r.Report.Resets);
            return RepetitionAnalyzer.Analyze(r.Map, a).Same;
        }
        double off = Same(0), on = Same(0.5);
        Assert.True(on > off + 0.1, $"{on} vs {off}");
    }
}
