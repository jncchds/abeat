using Abeat.Core.Analysis;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class TapTests
{
    /// <summary>Taps on every other kick and some hats, late by <paramref name="lag"/> with ±10 ms jitter.</summary>
    static double[] Tapped(SongAnalysis a, double lag, int seed, int every = 2)
    {
        var rng = new Random(seed);
        var on = a.Layers["low"].Where((_, i) => i % every == 0).Concat(a.Layers["high"].Where((_, i) => i % 7 == 0));
        return [.. on.Select(o => o.T + lag + (rng.NextDouble() - 0.5) * 0.02).Order()];
    }

    [Fact]
    public void LatencyIsRemovedAndTapsSnapToTheOnsets()
    {
        var a = TestSongs.Fake();
        TapAligner.Apply(a, [new(Tapped(a, 0.06, 1), null)]);
        Assert.NotNull(a.Taps);
        Assert.True(a.Taps!.Confident[0]);
        Assert.InRange(a.Taps.OffsetsSec[0], 0.045, 0.075);
        var onsets = a.Layers["low"].Concat(a.Layers["high"]).Select(o => o.T).ToHashSet();
        Assert.True(a.Layers[TapAligner.Layer].Count(t => onsets.Contains(t.T)) >= 0.9 * a.Taps.Count, "snapped onto the song's onsets");
    }

    [Fact]
    public void AppendedTapsGetTheirOwnLatency()
    {
        var a = TestSongs.Fake();
        double half = a.Layers["low"][a.Layers["low"].Count / 2].T;
        // a human map's notes (exact) in the first half, taps appended 70 ms late in the second
        var notes = a.Layers["low"].Where(o => o.T < half).Select(o => o.T + 0.012).ToArray();
        var appended = Tapped(a, 0.07, 4).Where(t => t > half).ToArray();
        TapAligner.Apply(a, [new(notes, null, Exact: true, Appended: [appended])]);
        Assert.Equal(0, a.Taps!.OffsetsSec[0]);
        Assert.InRange(a.Taps.AppendedOffsetsSec[0][0], 0.055, 0.085);
        var layer = a.Layers[TapAligner.Layer].Select(o => o.T).ToHashSet();
        Assert.All(notes, t => Assert.Contains(t, layer)); // the map's timing is kept, not snapped
        var onsets = a.Layers["low"].Concat(a.Layers["high"]).Select(o => o.T).ToHashSet();
        Assert.True(a.Layers[TapAligner.Layer].Count(o => o.T > half && onsets.Contains(o.T)) >= 0.9 * appended.Length, "appended taps land on the onsets");

        // a few taps can't tell their latency: they borrow a typical one instead of staying late
        TapAligner.Apply(a, [new(notes, null, Exact: true, Appended: [appended[..4]])]);
        Assert.Equal(TapAligner.TypicalLagSec, a.Taps!.AppendedOffsetsSec[0][0]);
    }

    [Fact]
    public void RunsMoveOntoTheCurrentPadding()
    {
        var a = TestSongs.Fake();
        a.Audio.PadSec = 1.0;
        // recorded on a song.egg padded 0.3 s less: every tap 0.3 s early on the current one
        TapAligner.Apply(a, [new(Tapped(a, 0.03 - 0.3, 2), 0.7)]);
        Assert.InRange(a.Taps!.OffsetsSec[0], 0.015, 0.045);
    }

    [Fact]
    public void BothRunsTogetherAreADouble()
    {
        var a = TestSongs.Fake();
        var right = Tapped(a, 0.04, 3);
        var left = a.Layers["low"].Where((_, i) => i % 8 == 0).Select(o => o.T + 0.05).ToArray();
        TapAligner.Apply(a, [new(right, null), new(left, null)]);
        Assert.InRange(a.Taps!.Doubles.Count, left.Length - 2, left.Length);
        Assert.True(a.Layers[TapAligner.Layer].Count < right.Length + left.Length);

        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert);
        var doubles = r.Events.Where(e => e.IsDouble).Select(e => e.Time).ToList();
        int kept = a.Taps.Doubles.Count(t => doubles.Any(d => Math.Abs(d - t) < 0.03));
        Assert.True(kept >= 0.6 * a.Taps.Doubles.Count, $"{kept}/{a.Taps.Doubles.Count} tapped doubles");
    }

    [Fact]
    public void TappedMomentsGetNotes()
    {
        var a = TestSongs.Fake();
        // off-beat hats are what the fake song's selector likes least; tap them
        var taps = a.Layers["high"].Where((o, i) => i % 3 == 0 && o.T > 2).Select(o => o.T + 0.05).ToArray();
        TapAligner.Apply(a, [new(taps, null)]);
        double Covered(GeneratorSettings s)
        {
            var times = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Events.Select(e => e.Time).ToList();
            return a.Layers[TapAligner.Layer].Count(t => times.Any(x => Math.Abs(x - t.T) < 0.03)) / (double)a.Taps!.Count;
        }
        double off = Covered(new GeneratorSettings { TapPriority = 0 }), on = Covered(new GeneratorSettings());
        Assert.True(on >= 0.9, $"taps with a note: {on:P0}");
        Assert.True(on > off + 0.1, $"priority 1 {on:P0} vs 0 {off:P0}");
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Expert);
        Assert.Equal(0, r.Report.WallClashes + r.Report.BombHits);
    }

    [Fact]
    public void FitToTapsLetsExpertPlusUseEveryTap()
    {
        var a = TestSongs.Fake();
        double spb = 60 / 128.0;
        // a run of sixteenths for four bars (too dense for the default E+ ceiling) and a few pairs 70 ms apart
        var taps = Enumerable.Range(0, 64).Select(i => 80 * spb + i * spb / 4)
            .Concat(Enumerable.Range(0, 5).SelectMany(i => new[] { (40 + 8 * i) * spb, (40 + 8 * i) * spb + 0.07 }))
            .Order().ToArray();
        TapAligner.Apply(a, [new(taps, null)]);
        double Covered(GeneratorSettings s)
        {
            var times = MapGenerator.GenerateDifficulty(a, s, DifficultyName.ExpertPlus).Events.Select(e => e.Time).ToList();
            return a.Layers[TapAligner.Layer].Count(t => times.Any(x => Math.Abs(x - t.T) < 0.01)) / (double)a.Taps!.Count;
        }
        var fit = TapFit.Fit(a, new GeneratorSettings())!;
        var fitted = new GeneratorSettings { ProfileOverrides = fit.Overrides };
        double before = Covered(new GeneratorSettings()), after = Covered(fitted);
        Assert.True(before < 0.95, $"default limits drop taps: {before:P0}");
        Assert.Equal(1.0, after);

        // the other difficulties move by the same ratios as Expert+
        var top = DifficultyProfile.Default(DifficultyName.ExpertPlus);
        double r = fit.Overrides[DifficultyName.ExpertPlus].MaxNps!.Value / top.MaxNps;
        Assert.True(r > 1);
        Assert.Equal(Math.Round(DifficultyProfile.Default(DifficultyName.Hard).MaxNps * r, 1), fit.Overrides[DifficultyName.Hard].MaxNps);
        Assert.True(fit.Overrides[DifficultyName.Easy].MinGapSec < DifficultyProfile.Default(DifficultyName.Easy).MinGapSec);

        var res = MapGenerator.GenerateDifficulty(a, fitted, DifficultyName.ExpertPlus);
        Assert.Equal(0, res.Report.WallClashes + res.Report.BombHits);
    }

    [Fact]
    public void FitToTapsNeedsTaps() => Assert.Null(TapFit.Fit(TestSongs.Fake(), new GeneratorSettings()));

    [Fact]
    public void WithoutTapsThePriorityChangesNothing()
    {
        var a = TestSongs.Fake();
        TapAligner.Apply(a, []);
        Assert.Null(a.Taps);
        Assert.False(a.Layers.ContainsKey(TapAligner.Layer));
        static string Notes(DifficultyMap m) => string.Join(";", m.Notes.Select(n => $"{n.Beat:0.###}/{n.X}/{n.Y}/{n.Hand}/{n.Direction}"));
        string baseline = Notes(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { TapPriority = 0 }, DifficultyName.Expert).Map);
        Assert.Equal(baseline, Notes(MapGenerator.GenerateDifficulty(a, new GeneratorSettings { TapPriority = 3 }, DifficultyName.Expert).Map));
    }
}
