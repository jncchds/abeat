using System.Text.Json;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>How human mappers use the grid and cut directions per difficulty, learned from curated maps
/// by scripts/style_prior.py and embedded as style-prior.json. The planner matches the cell and direction
/// shares and only places figures (hand + cell + cut direction) and double shapes from the difficulty's
/// vocabulary: what curated mappers commonly use, so lower difficulties keep notes lower.</summary>
public sealed class StylePrior
{
    const double Eps = 0.004;

    public required double[] Directions { get; init; }  // 9, by CutDirection
    public required double[][] Cells { get; init; }     // [hand][y * 4 + x]
    /// <summary>Figures (<see cref="Figure"/>) in the vocabulary, per hand.</summary>
    public required bool[][] Figures { get; init; }
    /// <summary>Double shapes in the vocabulary: (left figure, right figure).</summary>
    public required HashSet<(int L, int R)> DoubleShapes { get; init; }
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

    public static int Figure(int x, int y, CutDirection d) => y * 36 + x * 9 + (int)d;

    public bool HasFigure(Hand h, int x, int y, CutDirection d) => Figures[(int)h][Figure(x, y, d)];

    public bool HasDouble(int lx, int ly, CutDirection ld, int rx, int ry, CutDirection rd) =>
        DoubleShapes.Contains((Figure(lx, ly, ld), Figure(rx, ry, rd)));

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
                Figures = v.GetProperty("figures").EnumerateArray().Select(h =>
                {
                    var allowed = new bool[108];
                    foreach (var f in h.EnumerateArray()) allowed[f.GetInt32()] = true;
                    return allowed;
                }).ToArray(),
                DoubleShapes = v.GetProperty("doubleShapes").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToHashSet(),
                Nps = v.GetProperty("nps").GetDouble(),
                Doubles = v.GetProperty("doubles").GetDouble(),
                Dots = v.GetProperty("dots").GetDouble(),
            };
        }
        return result;
    }
}
