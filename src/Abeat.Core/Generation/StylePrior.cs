using System.Text.Json;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>How human mappers use the grid and cut directions per difficulty, learned from curated maps
/// by scripts/style_prior.py and embedded as style-prior.json. The planner pays -log(probability) for
/// unusual cells and directions, which pulls placements towards the familiar human style.</summary>
public sealed class StylePrior
{
    const double Eps = 0.004;

    public required double[] Directions { get; init; }  // 9, by CutDirection
    public required double[][] Cells { get; init; }     // [hand][y * 4 + x]
    public double Nps { get; init; }
    public double Doubles { get; init; }
    public double Dots { get; init; }

    double[]? dirCost;
    double[][]? cellCost;

    /// <summary>Pseudo-count weight of the prior in the running frequency estimate.</summary>
    const double Alpha = 24;

    /// <summary>Distribution matching rather than likelihood: the cost is how much choosing this option
    /// would push the path's running share above the human share (negative when under-represented).
    /// A plain -log p cost is mode-seeking and made maps far more up/down-heavy than human maps.</summary>
    public static double MatchCost(int count, int total, double p) =>
        Math.Clamp(Math.Log((count + 1 + Alpha * p) / (total + 1 + Alpha) / (p + Eps)), -1.5, 3.5);

    /// <summary>0 for the most common direction, growing with rarity.</summary>
    public double DirectionCost(CutDirection d) => (dirCost ??= ToCost(Directions))[(int)d];

    public double CellCost(Hand h, int x, int y) => (cellCost ??= [ToCost(Cells[0]), ToCost(Cells[1])])[(int)h][y * 4 + x];

    static double[] ToCost(double[] p)
    {
        double best = Math.Log(p.Max() + Eps);
        return p.Select(v => best - Math.Log(v + Eps)).ToArray();
    }

    static readonly Lazy<Dictionary<DifficultyName, StylePrior>> All = new(Load);

    public static StylePrior? For(DifficultyName d) => All.Value.GetValueOrDefault(d);

    static Dictionary<DifficultyName, StylePrior> Load()
    {
        using var s = typeof(StylePrior).Assembly.GetManifestResourceStream("Abeat.Core.style-prior.json");
        if (s == null) return [];
        using var doc = JsonDocument.Parse(s);
        var result = new Dictionary<DifficultyName, StylePrior>();
        foreach (var p in doc.RootElement.GetProperty("difficulties").EnumerateObject())
        {
            if (!Enum.TryParse<DifficultyName>(p.Name, out var name)) continue;
            var v = p.Value;
            result[name] = new StylePrior
            {
                Directions = v.GetProperty("directions").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                Cells = v.GetProperty("cells").EnumerateArray().Select(h => h.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray(),
                Nps = v.GetProperty("nps").GetDouble(),
                Doubles = v.GetProperty("doubles").GetDouble(),
                Dots = v.GetProperty("dots").GetDouble(),
            };
        }
        return result;
    }
}
