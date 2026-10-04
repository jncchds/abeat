using System.Text.Json;
using System.Text.Json.Nodes;
using Abeat.Core.Evaluation;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>How human mappers move each saber between consecutive swings, per difficulty: the joint
/// distribution of turn angle and saber-tip travel (see <see cref="MovementAnalyzer"/>) and the strain
/// the hardest moves reach. Learned by `abeat movement --write-prior` from curated maps and embedded as
/// movement-prior.json. The planner matches the move distribution like <see cref="StylePrior"/> matches
/// cells and directions, and pays for moves above the difficulty's strain ceiling.</summary>
public sealed class MovementPrior
{
    /// <summary>Upper edges of the turn-angle buckets (degrees from a clean reversal); the last is open.</summary>
    public static readonly double[] AngleEdges = [22.5, 67.5, 112.5];
    /// <summary>Upper edges of the tip-travel buckets (grid cells); the last is open.</summary>
    public static readonly double[] TravelEdges = [0.5, 1.25, 2.0, 2.75];
    public static int Buckets => (AngleEdges.Length + 1) * (TravelEdges.Length + 1);
    /// <summary>Upper edges of the strain buckets (effective swings per second); the last is open.</summary>
    public static readonly double[] StrainEdges = [1.5, 2.5, 3.5, 5, 7];
    public static int StrainBuckets => StrainEdges.Length + 1;

    public static int StrainBucket(double strain)
    {
        int i = 0;
        while (i < StrainEdges.Length && strain >= StrainEdges[i]) i++;
        return i;
    }

    public static int Bucket(double angle, double travel)
    {
        int a = 0, t = 0;
        while (a < AngleEdges.Length && angle >= AngleEdges[a]) a++;
        while (t < TravelEdges.Length && travel >= TravelEdges[t]) t++;
        return a * (TravelEdges.Length + 1) + t;
    }

    /// <summary>Share of moves per <see cref="Bucket"/>.</summary>
    public required double[] Moves { get; init; }
    /// <summary>Share of moves per <see cref="StrainBucket"/>: how hard the difficulty makes the hands work.</summary>
    public required double[] Strain { get; init; }
    /// <summary>Median over maps of the 90th / 98th percentile move strain.</summary>
    public double StrainP90 { get; init; }
    public double StrainP98 { get; init; }

    static readonly Lazy<Dictionary<DifficultyName, MovementPrior>> All = new(Load);

    public static MovementPrior? For(DifficultyName d) => All.Value.GetValueOrDefault(d);

    static Dictionary<DifficultyName, MovementPrior> Load()
    {
        using var s = typeof(MovementPrior).Assembly.GetManifestResourceStream("Abeat.Core.movement-prior.json");
        return s == null ? [] : Parse(JsonDocument.Parse(s));
    }

    public static Dictionary<DifficultyName, MovementPrior> Parse(JsonDocument doc)
    {
        var result = new Dictionary<DifficultyName, MovementPrior>();
        foreach (var p in doc.RootElement.GetProperty("difficulties").EnumerateObject())
        {
            if (!Enum.TryParse<DifficultyName>(p.Name, out var name)) continue;
            var moves = p.Value.GetProperty("moves").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var strain = p.Value.TryGetProperty("strain", out var st) ? st.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [];
            if (moves.Length != Buckets || strain.Length != StrainBuckets) continue; // stale file with other buckets
            result[name] = new MovementPrior
            {
                Moves = moves,
                Strain = strain,
                StrainP90 = p.Value.GetProperty("strainP90").GetDouble(),
                StrainP98 = p.Value.GetProperty("strainP98").GetDouble(),
            };
        }
        return result;
    }

    /// <summary>Builds the prior from analyzed human difficulties. Strain ceilings are made monotonic
    /// over the difficulties so a harder level never gets a lower ceiling.</summary>
    public static JsonObject Build(IEnumerable<MovementReport> reports, string source)
    {
        var diffs = new JsonObject();
        double p90 = 0, p98 = 0;
        foreach (var g in reports.Where(r => r.Moves > 0).GroupBy(r => r.Difficulty).OrderBy(g => g.Key))
        {
            var hist = new double[Buckets];
            var strain = new double[StrainBuckets];
            int n = 0;
            foreach (var m in g.SelectMany(r => r.Items))
            {
                hist[Bucket(m.Angle, m.Travel)]++;
                strain[StrainBucket(m.Strain)]++;
                n++;
            }
            p90 = Math.Max(p90, Median(g.Select(r => r.StrainP90)));
            p98 = Math.Max(p98, Median(g.Select(r => r.StrainP98)));
            diffs[g.Key.ToString()] = new JsonObject
            {
                ["maps"] = g.Count(),
                ["strainP90"] = Math.Round(p90, 3),
                ["strainP98"] = Math.Round(p98, 3),
                ["moves"] = new JsonArray(hist.Select(h => (JsonNode)Math.Round(h / n, 5)).ToArray()),
                ["strain"] = new JsonArray(strain.Select(h => (JsonNode)Math.Round(h / n, 5)).ToArray()),
            };
        }
        return new JsonObject
        {
            ["source"] = source,
            ["angleEdges"] = new JsonArray(AngleEdges.Select(x => (JsonNode)x).ToArray()),
            ["travelEdges"] = new JsonArray(TravelEdges.Select(x => (JsonNode)x).ToArray()),
            ["strainEdges"] = new JsonArray(StrainEdges.Select(x => (JsonNode)x).ToArray()),
            ["difficulties"] = diffs,
        };
    }

    static double Median(IEnumerable<double> xs)
    {
        var s = xs.Order().ToArray();
        return MovementAnalyzer.Percentile(s, 0.5);
    }
}
