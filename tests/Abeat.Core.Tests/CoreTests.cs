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
            Notes = [new(1, 0, 0, Hand.Left, CutDirection.Down, 15), new(1.5, 3, 2, Hand.Right, CutDirection.UpRight)],
            Bombs = [new(2, 1, 1)],
            Obstacles = [new(4, 2, 0, 0, 1, 5)],
            Arcs = [new(1, 0, 0, Hand.Left, CutDirection.Down, 3, 1, 0, CutDirection.Up)],
        };
        var json = JsonNode.Parse(MapWriter.DifficultyJson(dm).ToJsonString())!.AsObject();
        var back = MapReader.ReadDifficulty(json, DifficultyName.Expert);
        Assert.Equal(dm.Notes, back.Notes);
        Assert.Equal(dm.Bombs, back.Bombs);
        Assert.Equal(dm.Obstacles, back.Obstacles);
        Assert.Equal(dm.Arcs, back.Arcs);
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

    [Fact]
    public void TripletsOnlyForSongsWithTripletFeel()
    {
        var straight = FakeAnalysis();
        Assert.False(RhythmSelector.HasTripletFeel(straight));
        var expert = new GeneratorSettings();
        Assert.DoesNotContain(RhythmSelector.Select(straight, expert.Profile(DifficultyName.Expert), expert),
            e => Math.Round(e.Beat * 12) % 4 == 0 && Math.Round(e.Beat * 12) % 3 != 0);

        // shuffle: hats on the third eighth-triplet instead of the off-beat
        double spb = 60 / straight.Tempo.Bpm;
        var hats = straight.Layers["high"].Select(o => new Onset { T = (Math.Floor(o.T / spb) + 2.0 / 3) * spb, S = o.S, Br = o.Br }).ToList();
        var shuffle = new SongAnalysis
        {
            Tempo = straight.Tempo, Audio = straight.Audio, Energy = straight.Energy, Sections = straight.Sections,
            Layers = new() { ["low"] = straight.Layers["low"], ["high"] = hats },
        };
        Assert.True(RhythmSelector.HasTripletFeel(shuffle));
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
    public void HarderDifficultiesMoveHarder()
    {
        var a = FakeAnalysis();
        var ranks = new[] { DifficultyName.Easy, DifficultyName.Hard, DifficultyName.ExpertPlus }
            .Select(d => MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), d).Report.Movement.MovementRank).ToList();
        Assert.True(ranks[0] < ranks[1] && ranks[1] < ranks[2], string.Join(", ", ranks));
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

    [Fact]
    public void PauseAndDoubleOnTheDrop()
    {
        // FakeAnalysis jumps from 0.4 to 0.9 energy at beat 100, a downbeat
        var a = FakeAnalysis();
        var s = new GeneratorSettings();
        var events = RhythmSelector.Select(a, s.Profile(DifficultyName.Expert), s);
        var drop = Assert.Single(events, e => Math.Abs(e.Beat - 100) < 1e-6);
        Assert.True(drop.IsDouble);
        Assert.DoesNotContain(events, e => e.Beat >= 98 - 1e-6 && e.Beat < 100 - 1e-6);
        var without = RhythmSelector.Select(a, s.Profile(DifficultyName.Expert), s with { DropPause = false });
        Assert.Contains(without, e => e.Beat >= 98 - 1e-6 && e.Beat < 100 - 1e-6);
    }

    /// <summary>A sung line: one held note every two beats, rising and falling in pitch.</summary>
    static SongAnalysis MelodyAnalysis()
    {
        var a = FakeAnalysis();
        double spb = 60 / a.Tempo.Bpm;
        var mid = Enumerable.Range(2, 98).Select(i => new Onset { T = i * 2 * spb, S = 0.8, Br = 0.2 + 0.15 * (i % 4) }).ToList();
        return new SongAnalysis
        {
            Tempo = a.Tempo, Audio = a.Audio, Sections = a.Sections, Layers = new() { ["mid"] = mid },
            Energy = new EnergyCurve { HopSec = 1, Values = [.. a.Energy.Values.Select(_ => 0.7)] },
        };
    }

    [Fact]
    public void HeldNotesGetArcsBetweenSameHandNotes()
    {
        var a = MelodyAnalysis();
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
        Assert.Empty(MapGenerator.GenerateDifficulty(FakeAnalysis(), new GeneratorSettings(), DifficultyName.Hard).Map.Arcs);
    }

    [Fact]
    public void AnglesLeanWithThePitchLine()
    {
        var a = MelodyAnalysis();
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
    public void LoudMomentsSwingBigger()
    {
        var a = FakeAnalysis();
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
}
