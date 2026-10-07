using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class PlanningTests
{
    [Theory]
    [InlineData(DifficultyName.Easy)]
    [InlineData(DifficultyName.Hard)]
    [InlineData(DifficultyName.ExpertPlus)]
    public void GeneratedMapsFlow(DifficultyName d)
    {
        var a = TestSongs.Fake();
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
        var a = TestSongs.Fake();
        var ranks = new[] { DifficultyName.Easy, DifficultyName.Hard, DifficultyName.ExpertPlus }
            .Select(d => MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), d).Report.Movement.MovementRank).ToList();
        Assert.True(ranks[0] < ranks[1] && ranks[1] < ranks[2], string.Join(", ", ranks));
    }


    [Fact]
    public void SameSeedSameMap()
    {
        var a = TestSongs.Fake();
        var s = new GeneratorSettings { Seed = 42 };
        var m1 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        var m2 = MapGenerator.GenerateDifficulty(a, s, DifficultyName.Expert).Map.Notes;
        Assert.Equal(m1, m2);
        var m3 = MapGenerator.GenerateDifficulty(a, s with { Seed = 43 }, DifficultyName.Expert).Map.Notes;
        Assert.NotEqual(m1, m3);
    }


    [Fact]
    public void LoudMomentsSwingBigger()
    {
        var a = TestSongs.Fake();
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

    /// <summary>Two A sections and a B: the second A plays more of the first A's cuts with pattern memory on.</summary>
    [Fact]
    public void RepeatedSectionsEchoTheFirst()
    {
        var a = TestSongs.Fake();
        double end = a.Audio.DurationSec;
        a.Sections = [
            new Section { Start = 0, End = end / 3, Label = "A", Energy = 0.7 },
            new Section { Start = end / 3, End = 2 * end / 3, Label = "B", Energy = 0.7 },
            new Section { Start = 2 * end / 3, End = end, Label = "A", Energy = 0.7 },
        ];
        double Same(double w)
        {
            // the fake song's even eighths (0.23 s) fit one hand at Expert's gap, which leaves the hand choice
            // so free that the second A rarely lines up with the first; real songs echo at either gap
            var s = new GeneratorSettings { ProfileOverrides = { [DifficultyName.Expert] = new ProfileOverride { MinSameHandGapSec = 0.25 } } };
            var r = MapGenerator.GenerateDifficulty(a, s with { Weights = s.Weights with { Repetition = w } }, DifficultyName.Expert);
            Assert.Equal(0, r.Report.Resets);
            return RepetitionAnalyzer.Analyze(r.Map, a).Same;
        }
        double off = Same(0), on = Same(0.5);
        // the fake song's flat audio gives repeats no other reason to match since rows stopped following
        // brightness; on the bench's real songs the pass still makes 21 % of repeated moments exact copies
        Assert.True(on > off + 0.02, $"{on} vs {off}");
    }

    /// <summary>The loud section is led by the drop hand and the quiet one by the other; bar downbeats go to the support hand.</summary>
    [Fact]
    public void HandRolesSwapBetweenQuietAndLoudSections()
    {
        var a = TestSongs.Fake();
        var events = RhythmSelector.Select(a, DifficultyProfile.Default(DifficultyName.Expert), new GeneratorSettings());
        var roles = FlowPlanner.HandRoles(events, Hand.Left);
        for (int i = 0; i < events.Count; i++)
        {
            var lead = events[i].Section == "B" ? Hand.Left : Hand.Right;
            double inBar = events[i].BeatInSection % 4;
            bool downbeat = Math.Min(inBar, 4 - inBar) < 0.05;
            Assert.Equal(downbeat ? (lead == Hand.Left ? Hand.Right : Hand.Left) : lead, roles[i]);
        }
    }

    /// <summary>The planner follows the roles: the drop hand plays most single notes of the loud section,
    /// the same for every difficulty, and the default seed makes it the left hand.</summary>
    [Fact]
    public void DropHandLeadsTheLoudSection()
    {
        var a = TestSongs.Fake();
        foreach (var d in new[] { DifficultyName.Expert, DifficultyName.ExpertPlus })
        {
            var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), d);
            Assert.Equal(0, r.Report.Resets);
            double mid = a.Audio.DurationSec / 2;
            var singles = r.Map.Notes.GroupBy(n => n.Beat).Where(g => g.Count() == 1 && a.BeatToSeconds(g.Key) > mid).Select(g => g.First()).ToList();
            double left = singles.Count(n => n.Hand == Hand.Left) / (double)singles.Count;
            Assert.True(left > 0.6, $"{d}: left share {left:0.00}");
        }
    }

    /// <summary>Same-hand gaps of the planned notes (stacks and windows under 0.09 s count as one swing).</summary>
    static List<(ColorNote From, ColorNote To, double Gap)> SameHandGaps(DifficultyMap m, TempoMap tempo) =>
        m.Notes.GroupBy(n => n.Hand).SelectMany(g =>
        {
            var ns = g.OrderBy(n => n.Beat).ToList();
            return ns.Zip(ns.Skip(1), (a, b) => (a, b, tempo.Seconds(a.Beat, b.Beat)));
        }).Where(x => x.Item3 >= SwingCostModel.SliderGapSec).ToList();

    [Fact]
    public void BurstsAreShortCleanFlips()
    {
        var a = TestSongs.Fake(); // 128 BPM: eighths are 0.23 s, under Hard's 0.24 s same-hand gap
        var p = DifficultyProfile.Default(DifficultyName.Hard);
        var r = MapGenerator.GenerateDifficulty(a, new GeneratorSettings(), DifficultyName.Hard);
        var fast = SameHandGaps(r.Map, a.TempoMap).Where(x => x.Gap < p.MinSameHandGapSec - 1e-6).ToList();
        Assert.NotEmpty(fast);
        foreach (var (from, to, gap) in fast)
        {
            Assert.True(gap >= p.BurstGapSec - 1e-6);
            if (from.Direction != CutDirection.Any && to.Direction != CutDirection.Any)
                Assert.True(Swing.Vector(to.Direction).AngleTo(-Swing.Vector(from.Direction)) <= 45, $"flip at {to.Beat}");
        }
        // at most BurstNotes notes in a row of one hand under the regular gap
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            var ns = r.Map.Notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat).ToList();
            int run = 1;
            for (int i = 1; i < ns.Count; i++)
            {
                double gap = a.TempoMap.Seconds(ns[i - 1].Beat, ns[i].Beat);
                run = gap < p.MinSameHandGapSec - 1e-6 && gap >= SwingCostModel.SliderGapSec ? run + 1 : 1;
                Assert.True(run <= p.BurstNotes, $"run of {run} at {ns[i].Beat}");
            }
        }
        Assert.Equal(0, r.Report.Resets);

        // a burst gap equal to the same-hand gap turns bursts off (the soft too-fast cost still lets a rare
        // note land just under the limit, as before bursts existed)
        var off = new GeneratorSettings { ProfileOverrides = { [DifficultyName.Hard] = new ProfileOverride { BurstGapSec = p.MinSameHandGapSec } } };
        var r2 = MapGenerator.GenerateDifficulty(a, off, DifficultyName.Hard);
        Assert.True(SameHandGaps(r2.Map, a.TempoMap).Count(x => x.Gap < p.MinSameHandGapSec - 1e-6) * 2 < fast.Count);
    }
}
