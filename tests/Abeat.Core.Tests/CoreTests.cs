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

public class FormatTests
{
    [Fact]
    public void V3DifficultyRoundTrips()
    {
        var dm = new DifficultyMap
        {
            Difficulty = DifficultyName.Expert,
            Notes = [new(1, 0, 0, Hand.Left, CutDirection.Down), new(1.5, 3, 2, Hand.Right, CutDirection.UpRight)],
            Bombs = [new(2, 1, 1)],
            Obstacles = [new(4, 2, 0, 0, 1, 5)],
        };
        var json = JsonNode.Parse(MapWriter.DifficultyJson(dm).ToJsonString())!.AsObject();
        var back = MapReader.ReadDifficulty(json, DifficultyName.Expert);
        Assert.Equal(dm.Notes, back.Notes);
        Assert.Equal(dm.Bombs, back.Bombs);
        Assert.Equal(dm.Obstacles, back.Obstacles);
    }

    [Fact]
    public void ReadsV2Notes()
    {
        var json = JsonNode.Parse("""
            {"_version":"2.2.0","_notes":[{"_time":4.0,"_lineIndex":1,"_lineLayer":0,"_type":0,"_cutDirection":1},
            {"_time":4.5,"_lineIndex":2,"_lineLayer":0,"_type":3,"_cutDirection":0}],"_obstacles":[]}
            """)!.AsObject();
        var dm = MapReader.ReadDifficulty(json, DifficultyName.Hard);
        Assert.Single(dm.Notes);
        Assert.Equal(new ColorNote(4, 1, 0, Hand.Left, CutDirection.Down), dm.Notes[0]);
        Assert.Single(dm.Bombs);
    }

    [Fact]
    public void FolderRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("abeat-test").FullName;
        try
        {
            var map = new MapSet { SongName = "T", SongAuthor = "A", Bpm = 128 };
            map.Difficulties.Add(new DifficultyMap { Difficulty = DifficultyName.ExpertPlus, NoteJumpSpeed = 18, Notes = [new(8, 2, 0, Hand.Right, CutDirection.Down)] });
            MapWriter.WriteFolder(map, dir);
            var back = MapReader.Read(dir);
            Assert.Equal(128, back.Bpm);
            Assert.Equal("T", back.SongName);
            Assert.Single(back.Difficulties);
            Assert.Equal(18, back.Difficulties[0].NoteJumpSpeed);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class JumpTests
{
    [Theory]
    [InlineData(128, 18, 26)]
    [InlineData(90, 10, 18)]
    [InlineData(200, 16, 24)]
    public void OffsetHitsWantedJumpDistance(double bpm, double njs, double jd)
    {
        double offset = MapGenerator.JumpOffsetFor(bpm, njs, jd);
        double got = MapGenerator.JumpDistance(bpm, njs, offset);
        // offsets are quantized to quarter beats
        Assert.InRange(got, jd - njs * 60 / bpm * 0.26, jd + njs * 60 / bpm * 0.26);
    }
}

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
        // outward horizontals are fine
        var ok = FlowAnalyzer.Analyze(Map(new ColorNote(4, 1, 0, Hand.Left, CutDirection.Left), new ColorNote(4, 2, 0, Hand.Right, CutDirection.Right)), 120);
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

public class GeneratorTests
{
    /// <summary>Synthetic analysis: 128 BPM, kick on beats, hats on off-beats, two sections.</summary>
    static SongAnalysis FakeAnalysis()
    {
        const double bpm = 128;
        double spb = 60 / bpm;
        var low = new List<Onset>();
        var high = new List<Onset>();
        for (int b = 4; b < 200; b++)
        {
            low.Add(new Onset { T = b * spb, S = b % 4 == 0 ? 1 : 0.7, Br = 0.1 });
            high.Add(new Onset { T = (b + 0.5) * spb, S = 0.5, Br = 0.9 });
            if (b % 2 == 1) high.Add(new Onset { T = (b + 0.25) * spb, S = 0.3, Br = 0.8 });
        }
        double end = 200 * spb;
        return new SongAnalysis
        {
            Tempo = new TempoInfo { Bpm = bpm, Downbeats = [.. Enumerable.Range(0, 50).Select(i => i * 4 * spb)] },
            Audio = new AudioInfo { DurationSec = end },
            Energy = new EnergyCurve { HopSec = 1, Values = [.. Enumerable.Range(0, (int)end + 1).Select(i => i < end / 2 ? 0.4 : 0.9)] },
            Sections = [new Section { Start = 0, End = end / 2, Label = "A", Energy = 0.4 }, new Section { Start = end / 2, End = end, Label = "B", Energy = 0.9 }],
            Layers = new() { ["low"] = low, ["high"] = high },
        };
    }

    [Theory]
    [InlineData(DifficultyName.Easy)]
    [InlineData(DifficultyName.Hard)]
    [InlineData(DifficultyName.ExpertPlus)]
    public void GeneratedMapsFlow(DifficultyName d)
    {
        var a = FakeAnalysis();
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
    public void RespectsMinimumGap()
    {
        var a = FakeAnalysis();
        var p = DifficultyProfile.Default(DifficultyName.Hard);
        var events = RhythmSelector.Select(a, p, new GeneratorSettings());
        double minGap = events.Zip(events.Skip(1), (x, y) => y.Time - x.Time).Min();
        Assert.True(minGap >= p.MinGapSec - 1e-6, $"min gap {minGap}");
    }

    [Fact]
    public void SameSeedSameMap()
    {
        var a = FakeAnalysis();
        var s = new GeneratorSettings { Seed = 42 };
        var m1 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        var m2 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        Assert.Equal(m1, m2);
        var m3 = MapGenerator.GenerateDifficulty(a, s with { Seed = 43 }, DifficultyName.Expert).Map.Notes;
        Assert.NotEqual(m1, m3);
    }

    [Fact]
    public void DenserSettingGivesMoreNotes()
    {
        var a = FakeAnalysis();
        int n1 = MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Density = 0.6 }, DifficultyName.Expert).Map.Notes.Count;
        int n2 = MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Density = 1.4 }, DifficultyName.Expert).Map.Notes.Count;
        Assert.True(n2 > n1 * 1.3, $"{n1} vs {n2}");
    }
}
