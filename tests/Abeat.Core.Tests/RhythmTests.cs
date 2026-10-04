using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class RhythmTests
{
    [Fact]
    public void TripletsOnlyForSongsWithTripletFeel()
    {
        var straight = TestSongs.Fake();
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


    [Fact]
    public void RespectsMinimumGap()
    {
        var a = TestSongs.Fake();
        var p = DifficultyProfile.Default(DifficultyName.Hard);
        var events = RhythmSelector.Select(a, p, new GeneratorSettings());
        double minGap = events.Zip(events.Skip(1), (x, y) => y.Time - x.Time).Min();
        Assert.True(minGap >= p.MinGapSec - 1e-6, $"min gap {minGap}");
    }


    [Fact]
    public void DenserSettingGivesMoreNotes()
    {
        var a = TestSongs.Fake();
        int n1 = MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Density = 0.6 }, DifficultyName.Expert).Map.Notes.Count;
        int n2 = MapGenerator.GenerateDifficulty(a, new GeneratorSettings { Density = 1.4 }, DifficultyName.Expert).Map.Notes.Count;
        Assert.True(n2 > n1 * 1.3, $"{n1} vs {n2}");
    }


    [Fact]
    public void PauseAndDoubleOnTheDrop()
    {
        // FakeAnalysis jumps from 0.4 to 0.9 energy at beat 100, a downbeat
        var a = TestSongs.Fake();
        var s = new GeneratorSettings();
        var events = RhythmSelector.Select(a, s.Profile(DifficultyName.Expert), s);
        var drop = Assert.Single(events, e => Math.Abs(e.Beat - 100) < 1e-6);
        Assert.True(drop.IsDouble);
        Assert.DoesNotContain(events, e => e.Beat >= 98 - 1e-6 && e.Beat < 100 - 1e-6);
        var without = RhythmSelector.Select(a, s.Profile(DifficultyName.Expert), s with { DropPause = false });
        Assert.Contains(without, e => e.Beat >= 98 - 1e-6 && e.Beat < 100 - 1e-6);
    }

    [Fact]
    public void HatsCountLessThanKicks()
    {
        var a = TestSongs.Fake();
        // the same drum stem twice as loud on the off-beats: labelled hats there, kicks on the beats
        a.Layers["drums"] = [.. a.Layers["low"].Select(o => new Onset { T = o.T, S = 0.6, Br = 0.2, K = "k" }),
            .. a.Layers["high"].Where(o => o.S >= 0.5).Select(o => new Onset { T = o.T, S = 1, Br = 0.9, K = "h" })];
        a.Layers.Remove("low"); a.Layers.Remove("high");
        double OnBeat(double hat)
        {
            var s = new GeneratorSettings { DropPause = false, DrumWeights = new() { ["k"] = 1, ["s"] = 1, ["h"] = hat } };
            var e = RhythmSelector.Select(a, s.Profile(DifficultyName.Normal), s);
            return e.Count(x => Math.Abs(x.Beat - Math.Round(x.Beat)) < 1e-6) / (double)e.Count;
        }
        Assert.True(OnBeat(0.4) > OnBeat(1.0), $"{OnBeat(0.4)} vs {OnBeat(1.0)}");
        Assert.Contains(RhythmSelector.Select(a, new GeneratorSettings().Profile(DifficultyName.Normal), new GeneratorSettings()), e => e.Drum == 'k');
    }
}
