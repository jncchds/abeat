using System.Text.Json;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>How human mappers use the grid and cut directions per difficulty, learned from curated maps
/// by scripts/style_prior.py and embedded as style-prior.json. The planner matches the cell and direction
/// shares and only places figures (hand + cell + cut direction), moves (a hand's figure -> its next figure)
/// and double shapes from the difficulty's vocabulary: what curated mappers commonly use, so lower difficulties keep notes lower.</summary>
public sealed class StylePrior
{
    const double Eps = 0.004;

    public required double[] Directions { get; init; }  // 9, by CutDirection
    public required double[][] Cells { get; init; }     // [hand][y * 4 + x]
    /// <summary>Figures (<see cref="Figure"/>) in the vocabulary, per hand.</summary>
    public required bool[][] Figures { get; init; }
    /// <summary>Double shapes in the vocabulary: (left figure, right figure).</summary>
    public required HashSet<(int L, int R)> DoubleShapes { get; init; }
    /// <summary>Stacks in the vocabulary, per hand, indexed head figure * 4 + notes: one swing through notes
    /// lined up along its cut direction, head first. Null when the prior has none.</summary>
    public bool[][]? Stacks { get; init; }
    /// <summary>Share of notes that sit in stacks.</summary>
    public double Stacked { get; init; }
    /// <summary>Moves in the vocabulary, per hand, indexed from figure * 108 + to figure; null when the prior has none.</summary>
    public bool[][]? Moves { get; init; }
    /// <summary>Streaks in the vocabulary: runs of one hand's consecutive figures (first note first), per
    /// hand, per length in <see cref="StreakLengths"/>, as <see cref="StreakHash"/> values. Null when the prior has none.
    /// Every streak's shorter tails are streaks too (a run used twice in two maps has tails used as often),
    /// so a lookup can stop at the first length that is not known.</summary>
    public Dictionary<int, HashSet<ulong>>[]? Streaks { get; init; }
    /// <summary>Share of a curated map's notes (with three earlier notes since the hand's last pause) that complete
    /// a 4-note streak other curated maps use: the share the planner matches.</summary>
    public double StreakShare { get; init; }
    /// <summary>Two-hand streaks: 4 note moments in a row of both hands (<see cref="PairToken"/> /
    /// <see cref="PairDoubleToken"/>, first moment first) as <see cref="StreakHash"/> values, each with both hands
    /// in it. Null when the prior has none.</summary>
    public HashSet<ulong>? PairStreaks { get; init; }
    /// <summary>Share of a curated map's two-hand 4-windows (4 note moments since the last pause with both hands
    /// in them) that are two-hand streaks other curated maps use: the share the planner matches.</summary>
    public double PairStreakShare { get; init; }
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

    public bool HasStack(Hand h, int x, int y, CutDirection d, int notes) =>
        Stacks is not null && notes <= 3 && Stacks[(int)h][Figure(x, y, d) * 4 + notes];

    public bool HasMove(Hand h, int px, int py, CutDirection pd, int x, int y, CutDirection d) =>
        Moves is null || Moves[(int)h][Figure(px, py, pd) * 108 + Figure(x, y, d)];

    public bool HasDouble(int lx, int ly, CutDirection ld, int rx, int ry, CutDirection rd) =>
        DoubleShapes.Contains((Figure(lx, ly, ld), Figure(rx, ry, rd)));

    /// <summary>Streak lengths the prior learns (see scripts/style_prior.py).</summary>
    public static readonly int[] StreakLengths = [3, 4, 6, 8, 12, 16];
    /// <summary>Longest learned streak, and how many earlier figures a hand needs to remember for it.</summary>
    public static int MaxStreak => StreakLengths[^1];

    public const ulong StreakSeed = 0xcbf29ce484222325UL;
    /// <summary>Order-dependent hash of a run of figures, built first note first: fold <see cref="StreakStep"/> from <see cref="StreakSeed"/>.</summary>
    public static ulong StreakStep(ulong h, int figure)
    {
        h = (h ^ (ulong)(figure + 1)) * 0x100000001B3UL;
        return h ^ (h >> 29);
    }
    public static ulong StreakHash(IEnumerable<int> figures) => figures.Aggregate(StreakSeed, StreakStep);

    /// <summary>Length of a two-hand streak, in note moments.</summary>
    public const int PairLength = 4;
    /// <summary>Token of a note moment where one hand plays (a stack by its first note).</summary>
    public static int PairToken(Hand h, int figure) => (int)h * 108 + figure;
    /// <summary>Token of a double: both hands at once.</summary>
    public static int PairDoubleToken(int left, int right) => 216 + left * 108 + right;
    /// <summary>Whether a window of note moments has both hands in it (a double counts as both).</summary>
    public static bool BothHands(ReadOnlySpan<int> tokens)
    {
        bool l = false, r = false;
        foreach (int t in tokens)
        {
            if (t >= 216) return true;
            if (t >= 108) r = true; else l = true;
        }
        return l && r;
    }

    public bool HasPairStreak(ulong hash) => PairStreaks is not null && PairStreaks.Contains(hash);

    public bool HasStreak(Hand h, int length, ulong hash) =>
        Streaks is not null && Streaks[(int)h].TryGetValue(length, out var set) && set.Contains(hash);

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
                Stacks = v.TryGetProperty("stacks", out var stacks) ? stacks.EnumerateArray().Select(h =>
                {
                    var allowed = new bool[108 * 4];
                    foreach (var k in h.EnumerateArray()) allowed[k.GetInt32()] = true;
                    return allowed;
                }).ToArray() : null,
                Stacked = v.TryGetProperty("stacked", out var stacked) ? stacked.GetDouble() : 0,
                Moves = v.TryGetProperty("moves", out var moves) ? moves.EnumerateArray().Select(h =>
                {
                    var allowed = new bool[108 * 108];
                    foreach (var m in h.EnumerateArray()) allowed[m.GetInt32()] = true;
                    return allowed;
                }).ToArray() : null,
                Streaks = v.TryGetProperty("streaks", out var streaks) ? new[] { 0, 1 }.Select(h =>
                    streaks.EnumerateObject().ToDictionary(l => int.Parse(l.Name), l => l.Value[h].EnumerateArray()
                        .Select(x => StreakHash(x.GetString()!.Split('.').Select(int.Parse))).ToHashSet())).ToArray() : null,
                StreakShare = v.TryGetProperty("streakShare", out var share) ? share.GetDouble() : 0,
                PairStreaks = v.TryGetProperty("pairStreaks", out var pairs)
                    ? pairs.EnumerateArray().Select(x => StreakHash(x.GetString()!.Split('.').Select(int.Parse))).ToHashSet() : null,
                PairStreakShare = v.TryGetProperty("pairStreakShare", out var pshare) ? pshare.GetDouble() : 0,
                Nps = v.GetProperty("nps").GetDouble(),
                Doubles = v.GetProperty("doubles").GetDouble(),
                Dots = v.GetProperty("dots").GetDouble(),
            };
        }
        return result;
    }
}
