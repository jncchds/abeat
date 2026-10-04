using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class TempoTests
{
    static readonly TempoMap Drift = new([(0, 120), (8, 126), (24, 114)]);

    [Fact]
    public void ConvertsAcrossTempoChanges()
    {
        Assert.Equal(4.0, Drift.BeatToSeconds(8), 9);               // 8 beats at 120
        Assert.Equal(4.0 + 16 * 60 / 126.0, Drift.BeatToSeconds(24), 9);
        foreach (double b in new[] { 0, 3.5, 8, 13.25, 24, 40 })
            Assert.Equal(b, Drift.SecondsToBeat(Drift.BeatToSeconds(b)), 9);
        Assert.Equal(126, Drift.BpmAt(5));
        Assert.True(TempoMap.Constant(128).IsConstant);
        Assert.Equal(1.0, ((TempoMap)120).Seconds(4, 6), 9);
    }

    [Fact]
    public void BpmEventsRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("abeat-test").FullName;
        try
        {
            var map = new MapSet { SongName = "T", Tempo = Drift };
            map.Difficulties.Add(new DifficultyMap { Difficulty = DifficultyName.Expert, Notes = [new(30, 2, 0, Hand.Right, CutDirection.Down)] });
            MapWriter.WriteFolder(map, dir);
            var back = MapReader.Read(dir);
            Assert.Equal(120, back.Bpm);
            Assert.True(back.Tempo.SameAs(Drift));
            Assert.Equal(Drift.BeatToSeconds(30), back.Tempo.BeatToSeconds(back.Difficulties[0].Notes[0].Beat), 6);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A drifting song: kicks on every beat of a tempo that wanders 112-124 BPM.</summary>
    [Fact]
    public void NotesFollowADriftingTempo()
    {
        var tempo = new TempoMap(Enumerable.Range(0, 50).Select(i => (i * 4.0, 118 + 6 * Math.Sin(i / 3.0))));
        var low = Enumerable.Range(4, 190).Select(b => new Onset { T = tempo.BeatToSeconds(b), S = b % 4 == 0 ? 1 : 0.7, Br = 0.1 }).ToList();
        double end = tempo.BeatToSeconds(200);
        var a = new SongAnalysis
        {
            Tempo = new TempoInfo
            {
                Bpm = tempo.Bpm, Changes = [.. tempo.Points.Select(p => new TempoChange { Beat = p.Beat, Bpm = p.Bpm })],
                Downbeats = [.. Enumerable.Range(0, 50).Select(i => tempo.BeatToSeconds(i * 4))],
            },
            Audio = new AudioInfo { DurationSec = end },
            Energy = new EnergyCurve { HopSec = 1, Values = [.. Enumerable.Range(0, (int)end + 1).Select(_ => 0.7)] },
            Sections = [new Section { Start = 0, End = end, Label = "A", Energy = 0.7 }],
            Layers = new() { ["low"] = low },
        };
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert);
        Assert.NotEmpty(r.Map.Notes);
        Assert.All(r.Map.Notes, n => Assert.Equal(Math.Round(n.Beat), n.Beat, 6)); // every note on a kick
        Assert.Equal(0, r.Report.Resets + r.Report.WallClashes + r.Report.BombHits);
        var gen = MapGenerator.Generate(a, new GeneratorSettings { Difficulties = [DifficultyName.Expert] });
        Assert.True(gen.Map.Tempo.SameAs(tempo));
    }
}
