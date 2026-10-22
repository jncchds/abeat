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
        bool Ends(Arc arc) => r.Map.Notes.Any(n => n.Hand == arc.Hand && n.Beat == arc.TailBeat && n.X == arc.TailX && n.Y == arc.TailY);
        var connected = r.Map.Arcs.Where(Ends).ToList();
        Assert.NotEmpty(connected);
        foreach (var arc in r.Map.Arcs)
        {
            Assert.Contains(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat == arc.Beat && n.X == arc.X && n.Y == arc.Y);
            Assert.DoesNotContain(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat > arc.Beat && n.Beat < arc.TailBeat);
        }
        Assert.Equal(0, r.Report.Resets);
        // as sparing as curated maps: about 9 % of the Hard same-hand gaps of 1-4 beats
        int gaps = r.Map.Notes.GroupBy(n => n.Hand).Sum(g => g.OrderBy(n => n.Beat).Zip(g.OrderBy(n => n.Beat).Skip(1))
            .Count(p => p.Second.Beat - p.First.Beat is >= 1 and <= 4));
        Assert.InRange(connected.Count, 1, Math.Round(0.09 * gaps));
        Assert.Empty(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Arcs = false }, DifficultyName.Hard).Map.Arcs);
        // percussive layers never hold
        Assert.Empty(MapGenerator.GenerateDifficulty(TestSongs.Fake(), new GeneratorSettings(), DifficultyName.Hard).Map.Arcs);
    }


    /// <summary>A held note the hand has nothing after gets an arc that follows the sound and ends in a
    /// dot without a note, with room before the hand's next note; a few of them, like curated maps.</summary>
    [Fact]
    public void LongNotesWithoutANextNoteGetHeldArcs()
    {
        var a = TestSongs.Melody();
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Hard);
        var held = r.Map.Arcs.Where(arc => !r.Map.Notes.Any(n => n.Hand == arc.Hand && Math.Abs(n.Beat - arc.TailBeat) < 1e-4)).ToList();
        Assert.NotEmpty(held);
        Assert.True(held.Count <= Math.Max(1, r.Map.Arcs.Count / 4), $"{held.Count} of {r.Map.Arcs.Count}");
        foreach (var arc in held)
        {
            Assert.Equal(CutDirection.Any, arc.TailDirection);
            Assert.InRange(arc.TailBeat - arc.Beat, 0.75, 4);
            var e = r.Events.Single(e => e.Beat == arc.Beat);
            Assert.True(e.Melody && e.Sustain >= 0.4);
            // the tail stays next to the head and the hand cuts again only after it
            Assert.InRange(Math.Abs(arc.TailX - arc.X), 0, 1);
            Assert.InRange(Math.Abs(arc.TailY - arc.Y), 0, 1);
            Assert.DoesNotContain(r.Map.Notes, n => n.Hand == arc.Hand && n.Beat > arc.Beat && n.Beat < arc.TailBeat + 0.5);
        }
        // no two arcs start on the same note
        Assert.Equal(r.Map.Arcs.Count, r.Map.Arcs.Select(x => (x.Beat, x.Hand)).Distinct().Count());
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
