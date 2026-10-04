using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class SwingTests
{
    [Theory]
    [InlineData(Hand.Right, CutDirection.Down, Parity.Forehand)]
    [InlineData(Hand.Right, CutDirection.Up, Parity.Backhand)]
    [InlineData(Hand.Left, CutDirection.DownRight, Parity.Forehand)]
    [InlineData(Hand.Left, CutDirection.UpLeft, Parity.Backhand)]
    public void ParityOfDirections(Hand hand, CutDirection d, Parity expected) =>
        Assert.Equal(expected, Swing.FixedParity(hand, Swing.Vector(d)));

    [Fact]
    public void HorizontalCutsContinueEitherParity()
    {
        Assert.Null(Swing.FixedParity(Hand.Right, Swing.Vector(CutDirection.Left)));
        var s = HandState.Initial(Hand.Right).After(1, 2, 0, CutDirection.Down, Swing.Vector(CutDirection.Down), Hand.Right)
            .After(1.5, 3, 0, CutDirection.Right, Swing.Vector(CutDirection.Right), Hand.Right);
        Assert.Null(s.Parity);
        Assert.False(SwingCostModel.IsReset(Hand.Right, s, Swing.Vector(CutDirection.Down)));
        Assert.False(SwingCostModel.IsReset(Hand.Right, s, Swing.Vector(CutDirection.Up)));
    }

    [Fact]
    public void SameDirectionTwiceIsAReset()
    {
        var s = HandState.Initial(Hand.Right).After(1, 3, 0, CutDirection.Right, Swing.Vector(CutDirection.Right), Hand.Right);
        Assert.True(SwingCostModel.IsReset(Hand.Right, s, Swing.Vector(CutDirection.Right)));
        Assert.False(SwingCostModel.IsReset(Hand.Right, s, Swing.Vector(CutDirection.Left)));
        Assert.False(SwingCostModel.IsReset(Hand.Right, s, Swing.Vector(CutDirection.Up)));
    }

    [Fact]
    public void FromVectorRoundTrips()
    {
        foreach (var d in Swing.Directional) Assert.Equal(d, Swing.FromVector(Swing.Vector(d)));
    }
}
