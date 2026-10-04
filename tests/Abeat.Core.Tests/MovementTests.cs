using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class MovementTests
{
    static DifficultyMap Map(params ColorNote[] notes) => new() { Difficulty = DifficultyName.Expert, Notes = [.. notes] };

    [Fact]
    public void CleanReversalOnOneCellNeitherTurnsNorTravels()
    {
        var m = Assert.Single(MovementAnalyzer.Moves(Map(new(0, 2, 0, Hand.Right, CutDirection.Down), new(1, 2, 0, Hand.Right, CutDirection.Up)), 120));
        Assert.Equal(0, m.Angle, 3);
        Assert.Equal(0, m.Travel, 3);
        Assert.Equal(0.5, m.GapSec, 3);
    }

    [Fact]
    public void MeasuresTurnAndTipTravel()
    {
        // down at the top right, then left in the bottom row: a 90° turn, and the tip goes from
        // (3, 1.4) below the first note to (2.6, 0) right of the second
        var m = Assert.Single(MovementAnalyzer.Moves(Map(new(0, 3, 2, Hand.Right, CutDirection.Down), new(1, 2, 0, Hand.Right, CutDirection.Left)), 120));
        Assert.Equal(90, m.Angle, 3);
        Assert.Equal(Math.Sqrt(0.4 * 0.4 + 1.4 * 1.4), m.Travel, 3);
    }

    [Fact]
    public void AngleOffsetsTurnTheSwing()
    {
        var m = Assert.Single(MovementAnalyzer.Moves(Map(new(0, 2, 0, Hand.Right, CutDirection.Down), new(1, 2, 0, Hand.Right, CutDirection.Up, 15)), 120));
        Assert.Equal(15, m.Angle, 3);
    }

    [Fact]
    public void StrainChargesSharpTurnsAndLongTravelNotSmallOnes()
    {
        Assert.Equal(MovementAnalyzer.Strain(0, 0, 0.5), MovementAnalyzer.Strain(45, 1, 0.5), 6);
        Assert.True(MovementAnalyzer.Strain(90, 0, 0.5) > MovementAnalyzer.Strain(45, 0, 0.5) * 1.5);
        Assert.True(MovementAnalyzer.Strain(0, 3, 0.5) > MovementAnalyzer.Strain(0, 1, 0.5));
        Assert.True(MovementAnalyzer.Strain(0, 0, 0.25) > MovementAnalyzer.Strain(0, 0, 0.5));
    }

    [Fact]
    public void PriorCoversAllDifficultiesWithRisingCeilings()
    {
        var priors = Enum.GetValues<DifficultyName>().Select(d => MovementPrior.For(d)).ToList();
        Assert.All(priors, Assert.NotNull);
        Assert.All(priors, p => Assert.Equal(1, p!.Moves.Sum(), 2));
        for (int i = 1; i < priors.Count; i++)
        {
            Assert.True(priors[i]!.StrainP90 >= priors[i - 1]!.StrainP90);
            Assert.True(priors[i]!.StrainP98 >= priors[i - 1]!.StrainP98);
        }
        foreach (var (d, p90) in MovementAnalyzer.Calibration)
        {
            Assert.Equal(DifficultyMap.Rank(d), MovementAnalyzer.RankFromStrain(p90), 6);
            Assert.Equal(d, MovementAnalyzer.DifficultyOf(p90));
        }
    }
}
