using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class FlowAnalyzerTests
{
    static DifficultyMap Map(params ColorNote[] notes) => new() { Difficulty = DifficultyName.Expert, Notes = [.. notes] };

    [Fact]
    public void AlternatingSwingsHaveNoResets()
    {
        var notes = Enumerable.Range(0, 16).Select(i => new ColorNote(i, 2, 0, Hand.Right, i % 2 == 0 ? CutDirection.Down : CutDirection.Up)).ToArray();
        var r = FlowAnalyzer.Analyze(Map(notes), 120);
        Assert.Equal(0, r.Resets);
        Assert.True(r.FlowScore > 90, $"flow {r.FlowScore}");
    }

    [Fact]
    public void RepeatedDownSwingsAreResets()
    {
        var notes = Enumerable.Range(0, 8).Select(i => new ColorNote(i, 2, 0, Hand.Right, CutDirection.Down)).ToArray();
        var r = FlowAnalyzer.Analyze(Map(notes), 120);
        Assert.Equal(7, r.Resets);
        Assert.True(r.FlowScore < 50);
    }

    [Fact]
    public void SideBySideSameDirectionHorizontalDoubleClashes()
    {
        // L← at x1 and R← at x2 in one row: the right saber sweeps through the left note
        var r = FlowAnalyzer.Analyze(Map(new ColorNote(4, 1, 0, Hand.Left, CutDirection.Left), new ColorNote(4, 2, 0, Hand.Right, CutDirection.Left)), 120);
        Assert.Equal(1, r.HandClashes);
        // outward horizontals are fine with room between the hands (side by side they start together)
        var ok = FlowAnalyzer.Analyze(Map(new ColorNote(4, 0, 0, Hand.Left, CutDirection.Left), new ColorNote(4, 2, 0, Hand.Right, CutDirection.Right)), 120);
        Assert.Equal(0, ok.HandClashes);
    }

    [Fact]
    public void DoubleSwingsStartingTogetherClash()
    {
        // L↖ at x1 and R↗ at x2: both swings start in the middle
        var r = FlowAnalyzer.Analyze(Map(new ColorNote(4, 1, 0, Hand.Left, CutDirection.UpLeft), new ColorNote(4, 2, 0, Hand.Right, CutDirection.UpRight)), 120);
        Assert.Equal(1, r.HandClashes);
        // the same double with a free column between the hands is fine
        var ok = FlowAnalyzer.Analyze(Map(new ColorNote(4, 0, 0, Hand.Left, CutDirection.UpLeft), new ColorNote(4, 2, 0, Hand.Right, CutDirection.UpRight)), 120);
        Assert.Equal(0, ok.HandClashes);
    }

    [Fact]
    public void OtherHandInSameCellRightAfterClashes()
    {
        // at 120 BPM a quarter beat is 0.125 s: the right hand comes up where the left just went down
        var r = FlowAnalyzer.Analyze(Map(new ColorNote(4, 2, 0, Hand.Left, CutDirection.Down), new ColorNote(4.25, 2, 0, Hand.Right, CutDirection.Up)), 120);
        Assert.Equal(1, r.HandClashes);
        // a beat later (0.5 s) there is time
        var ok = FlowAnalyzer.Analyze(Map(new ColorNote(4, 2, 0, Hand.Left, CutDirection.Down), new ColorNote(5, 2, 0, Hand.Right, CutDirection.Up)), 120);
        Assert.Equal(0, ok.HandClashes);
    }

    [Fact]
    public void BombBetweenMakesResetIntentional()
    {
        var dm = Map(new ColorNote(0, 2, 0, Hand.Right, CutDirection.Down), new ColorNote(1, 2, 0, Hand.Right, CutDirection.Down));
        dm.Bombs.Add(new BombNote(0.5, 2, 2));
        var r = FlowAnalyzer.Analyze(dm, 120);
        Assert.Equal(0, r.Resets);
        Assert.Equal(1, r.BombResets);
    }
}
