using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class RhythmTests
{
    /// <summary>Notes on the melody line carry its pitch and held length; the drums around it don't.</summary>
    [Fact]
    public void MelodyLayerMarksItsNotes()
    {
        var a = TestSongs.Fake();
        double spb = 60 / a.Tempo.Bpm;
        // a violin line on the off-beats of every other beat, held for most of a beat
        var melody = Enumerable.Range(4, 96).Select(i => new Onset
        {
            T = (2 * i + 0.5) * spb, S = 0.9, Br = 0.1 + 0.2 * (i % 5), E = (2 * i + 1.3) * spb, Src = "other",
        }).ToList();
        var song = new SongAnalysis
        {
            Tempo = a.Tempo, Audio = a.Audio, Energy = a.Energy, Sections = a.Sections,
            Layers = new() { ["drums"] = a.Layers["low"], ["melody"] = melody },
        };
        var s = new GeneratorSettings();
        var events = RhythmSelector.Select(song, s.Profile(DifficultyName.Expert), s);
        var onMelody = events.Where(e => melody.Any(m => Math.Abs(m.T - e.Time) < 0.01)).ToList();
        Assert.True(onMelody.Count > 40);
        Assert.All(onMelody, e =>
        {
            var m = melody.First(m => Math.Abs(m.T - e.Time) < 0.01);
            Assert.True(e.Melody);
            Assert.Equal(m.Br, e.Brightness);
            Assert.Equal(m.E!.Value - e.Time, e.Sustain, 3);
        });
        Assert.All(events.Except(onMelody), e => Assert.False(e.Melody));

        // weight 0: the layer is ignored and nothing counts as melody (no pitched layers either)
        var off = s with { LayerWeights = new(s.LayerWeights) { ["melody"] = 0 } };
        Assert.DoesNotContain(RhythmSelector.Select(song, off.Profile(DifficultyName.Expert), off), e => e.Melody);
    }

    [Fact]
    public void TripletsOnlyForSongsWithTripletFeel()
    {
        var straight = TestSongs.Fake();
        Assert.False(RhythmSelector.HasTripletFeel(straight));
        var expert = new GeneratorSettings();
        Assert.DoesNotContain(RhythmSelector.Select(straight, expert.Profile(DifficultyName.Expert), expert),
            e => Math.Round(e.GridBeat * 12) % 4 == 0 && Math.Round(e.GridBeat * 12) % 3 != 0);

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
    public void NotesSitOnTheDetectedSoundsNotTheGrid()
    {
        // a human feel: every onset 12 ms early or late of the grid
        var a = TestSongs.Fake();
        int k = 0;
        foreach (var layer in a.Layers.Values)
            foreach (var o in layer) o.T += k++ % 2 == 0 ? 0.012 : -0.012;
        var onsets = a.Layers.Values.SelectMany(l => l).Select(o => o.T).ToHashSet();
        var s = new GeneratorSettings();
        var events = RhythmSelector.Select(a, s.Profile(DifficultyName.Expert), s);
        Assert.True(events.Count(e => onsets.Contains(e.Time)) >= 0.95 * events.Count);
        Assert.All(events, e => Assert.Equal(a.SecondsToBeat(e.Time), e.Beat, 9));
        Assert.Contains(events, e => Math.Abs(a.BeatToSeconds(e.GridBeat) - e.Time) > 0.01);
        Assert.Equal(events.OrderBy(e => e.Time).Select(e => e.Time), events.Select(e => e.Time));
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
