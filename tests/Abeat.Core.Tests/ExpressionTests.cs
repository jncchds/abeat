using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class ExpressionTests
{
    [Fact]
    public void HeldNotesGetArcsBetweenSameHandNotes()
    {
        var a = TestSongs.Melody();
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Hard);
        Assert.NotEmpty(r.Map.Arcs);
        foreach (var arc in r.Map.Arcs)
        {
            Assert.Contains(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat == arc.Beat && n.X == arc.X && n.Y == arc.Y);
            Assert.Contains(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat == arc.TailBeat && n.X == arc.TailX && n.Y == arc.TailY);
            Assert.DoesNotContain(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat > arc.Beat && n.Beat < arc.TailBeat);
        }
        Assert.Equal(0, r.Report.Resets);
        Assert.Empty(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Arcs = false }, DifficultyName.Hard).Map.Arcs);
        // percussive layers never hold
        Assert.Empty(MapGenerator.GenerateDifficulty(TestSongs.Fake(), new GeneratorSettings(), DifficultyName.Hard).Map.Arcs);
    }


    [Fact]
    public void AnglesLeanWithThePitchLine()
    {
        var a = TestSongs.Melody();
        var s = new GeneratorSettings();
        var r = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert);
        var angled = r.Map.Notes.Where(n => n.AngleOffset != 0).ToList();
        Assert.NotEmpty(angled);
        Assert.All(angled, n => Assert.Contains(Math.Abs(n.AngleOffset), new[] { 15, 30 }));
        foreach (var n in angled)
        {
            var e = r.Events.Single(e => e.Beat == n.Beat);
            bool rising = e.PitchSlope > 0;
            // rising: vertical cuts lean "/" (clockwise), right cuts lift (counter-clockwise)
            if (n.Direction is CutDirection.Up or CutDirection.Down) Assert.Equal(rising, n.AngleOffset < 0);
            if (n.Direction == CutDirection.Right) Assert.Equal(rising, n.AngleOffset > 0);
        }
        Assert.DoesNotContain(MapGenerator.GenerateDifficulty(a, s with { AngleOffsets = false }, DifficultyName.Expert).Map.Notes, n => n.AngleOffset != 0);
    }


    [Fact]
    public void RollsBecomeChainsAlongTheCut()
    {
        var a = TestSongs.Fake();
        double spb = 60 / a.Tempo.Bpm;
        // a 32nd-note snare roll after every other downbeat
        for (int b = 16; b < 196; b += 8)
            for (int k = 1; k <= 3; k++) a.Layers["high"].Add(new Onset { T = (b + k / 8.0) * spb, S = 0.6, Br = 0.8 });
        a.Layers["high"].Sort((x, y) => x.T.CompareTo(y.T));
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert);
        Assert.NotEmpty(r.Map.Chains);
        foreach (var c in r.Map.Chains)
        {
            Assert.Contains(r.Map.Notes, n => n.Hand == c.Hand && n.Beat == c.Beat && n.X == c.X && n.Y == c.Y && n.Direction == c.Direction);
            var v = Swing.Vector(c.Direction);
            Assert.True((c.TailX - c.X) * v.X + (c.TailY - c.Y) * v.Y > 0.5, "links follow the cut");
            Assert.InRange(c.TailBeat - c.Beat, 0.0625, 0.25);
            Assert.DoesNotContain(r.Map.Notes, n => n.Hand == c.Hand && n.Beat > c.Beat && n.Beat < c.Beat + 1 - 1e-6);
        }
        Assert.Equal(0, r.Report.WallClashes + r.Report.BombHits + r.Report.Resets);
        Assert.Empty(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Chains = false }, DifficultyName.Expert).Map.Chains);
        // steady hats are not rolls
        Assert.Empty(MapGenerator.GenerateDifficulty(TestSongs.Fake(), new GeneratorSettings(), DifficultyName.Expert).Map.Chains);
    }
}
