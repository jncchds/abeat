using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

/// <summary>Synthetic analyses shared by the generator tests.</summary>
public static class TestSongs
{
    /// <summary>Synthetic analysis: 128 BPM, kick on beats, hats on off-beats, two sections.</summary>
    public static SongAnalysis Fake()
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


    /// <summary>A sung line: one held note every two beats, rising and falling in pitch.</summary>
    public static SongAnalysis Melody()
    {
        var a = TestSongs.Fake();
        double spb = 60 / a.Tempo.Bpm;
        var mid = Enumerable.Range(2, 98).Select(i => new Onset { T = i * 2 * spb, S = 0.8, Br = 0.2 + 0.15 * (i % 4) }).ToList();
        return new SongAnalysis
        {
            Tempo = a.Tempo, Audio = a.Audio, Sections = a.Sections, Layers = new() { ["mid"] = mid },
            Energy = new EnergyCurve { HopSec = 1, Values = [.. a.Energy.Values.Select(_ => 0.7)] },
        };
    }
}
