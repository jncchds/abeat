using Abeat.Core.Model;

namespace Abeat.Core.Evaluation;

/// <summary>How a generated difficulty differs from a human one for the same song.</summary>
public sealed record Comparison
{
    public DifficultyName Difficulty { get; init; }
    /// <summary>Generated note times matching human note times within the tolerance.</summary>
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1 => Precision + Recall > 0 ? 2 * Precision * Recall / (Precision + Recall) : 0;
    /// <summary>Median signed offset (generated - human) of matched notes, in ms.</summary>
    public double OffsetMs { get; init; }
    public double HumanNps { get; init; }
    public double GeneratedNps { get; init; }
    public double HumanFlow { get; init; }
    public double GeneratedFlow { get; init; }
    public int HumanResets { get; init; }
    public int GeneratedResets { get; init; }
    /// <summary>0..1 total-variation distance between the cut-direction distributions.</summary>
    public double DirectionDistance { get; init; }
    /// <summary>0..1 total-variation distance between the 12-cell position distributions.</summary>
    public double PositionDistance { get; init; }
    public double HumanDoubles { get; init; }
    public double GeneratedDoubles { get; init; }
    public MovementReport HumanMovement { get; init; } = new();
    public MovementReport GeneratedMovement { get; init; } = new();

    public override string ToString() =>
        $"{Difficulty,-10} F1 {F1,4:0.00} (P {Precision:0.00} R {Recall:0.00}, offset {OffsetMs,4:0} ms)  " +
        $"nps {GeneratedNps,4:0.0}/{HumanNps,4:0.0}  flow {GeneratedFlow,4:0}/{HumanFlow,4:0}  resets {GeneratedResets}/{HumanResets}  " +
        $"dbl {GeneratedDoubles:P0}/{HumanDoubles:P0}  dirΔ {DirectionDistance:0.00}  posΔ {PositionDistance:0.00}  " +
        $"strain {GeneratedMovement.StrainP90:0.0}/{HumanMovement.StrainP90:0.0}  travel {GeneratedMovement.TravelMean:0.00}/{HumanMovement.TravelMean:0.00}  " +
        $"angle {GeneratedMovement.AngleMean:0}/{HumanMovement.AngleMean:0}°";
}

public static class MapComparer
{
    /// <summary>Compares note timing in seconds of the *original* audio. Generated maps are shifted by
    /// the analysis padding so both refer to the same audio time.</summary>
    public static Comparison Compare(DifficultyMap human, TempoMap humanBpm, DifficultyMap generated, TempoMap generatedBpm,
        double generatedPadSec, double toleranceSec = 0.05)
    {
        var h = OnsetTimes(human, humanBpm, 0);
        var g = OnsetTimes(generated, generatedBpm, generatedPadSec);
        var (matched, offsets) = Match(g, h, toleranceSec);
        var hr = FlowAnalyzer.Analyze(human, humanBpm);
        var gr = FlowAnalyzer.Analyze(generated, generatedBpm);
        return new Comparison
        {
            Difficulty = human.Difficulty,
            Precision = g.Length > 0 ? matched / (double)g.Length : 0,
            Recall = h.Length > 0 ? matched / (double)h.Length : 0,
            OffsetMs = offsets.Count > 0 ? Median(offsets) * 1000 : 0,
            HumanNps = hr.Nps,
            GeneratedNps = gr.Nps,
            HumanFlow = hr.FlowScore,
            GeneratedFlow = gr.FlowScore,
            HumanResets = hr.Resets,
            GeneratedResets = gr.Resets,
            DirectionDistance = Tv(Hist(human.Notes, n => (int)n.Direction, 9), Hist(generated.Notes, n => (int)n.Direction, 9)),
            PositionDistance = Tv(Hist(human.Notes, n => n.Y * 4 + n.X, 12), Hist(generated.Notes, n => n.Y * 4 + n.X, 12)),
            HumanDoubles = DoubleShare(human),
            GeneratedDoubles = DoubleShare(generated),
            HumanMovement = MovementAnalyzer.Analyze(human, humanBpm),
            GeneratedMovement = MovementAnalyzer.Analyze(generated, generatedBpm),
        };
    }

    /// <summary>Distinct note times (doubles count once), seconds in original audio.</summary>
    static double[] OnsetTimes(DifficultyMap m, TempoMap tempo, double padSec) =>
        m.Notes.Select(n => Math.Round(n.Beat, 3)).Distinct().Select(b => tempo.BeatToSeconds(b) - padSec).Order().ToArray();

    static (int matched, List<double> offsets) Match(double[] g, double[] h, double tol)
    {
        int i = 0, j = 0, matched = 0;
        var offsets = new List<double>();
        while (i < g.Length && j < h.Length)
        {
            double d = g[i] - h[j];
            if (Math.Abs(d) <= tol) { matched++; offsets.Add(d); i++; j++; }
            else if (d < 0) i++;
            else j++;
        }
        return (matched, offsets);
    }

    static double DoubleShare(DifficultyMap m)
    {
        var groups = m.Notes.GroupBy(n => Math.Round(n.Beat, 3)).ToList();
        return groups.Count == 0 ? 0 : groups.Count(g => g.Select(n => n.Hand).Distinct().Count() == 2) / (double)groups.Count;
    }

    static double[] Hist(IEnumerable<ColorNote> notes, Func<ColorNote, int> key, int bins)
    {
        var h = new double[bins];
        int n = 0;
        foreach (var x in notes)
        {
            int k = key(x);
            if (k >= 0 && k < bins) { h[k]++; n++; }
        }
        if (n > 0) for (int i = 0; i < bins; i++) h[i] /= n;
        return h;
    }

    static double Tv(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Sum() / 2;

    static double Median(List<double> xs)
    {
        var s = xs.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
