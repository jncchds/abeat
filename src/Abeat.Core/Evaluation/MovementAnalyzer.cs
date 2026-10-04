using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Evaluation;

/// <summary>One saber's move from one swing to its next swing.</summary>
/// <param name="Angle">Deviation of the second swing from a clean reversal of the first, degrees:
/// 0 = down→up, 45 = down→up-left, 90 = down→left, 180 = down→down (a reset).</param>
/// <param name="Travel">Saber-tip travel between the swings in grid cells: from where the first swing
/// leaves its note (overshoot included) to where the second swing has to start. 0 for a clean
/// reversal on the same cell.</param>
/// <param name="Distance">Centre-to-centre distance between the two notes, grid cells.</param>
public sealed record HandMove(Hand Hand, double Beat, double GapSec, double Angle, double Travel, double Distance,
    CutDirection From, CutDirection To)
{
    /// <summary>Tip speed needed between the swings, cells per second.</summary>
    public double Speed => Travel / GapSec;
    /// <summary>Wrist rotation speed needed, degrees per second.</summary>
    public double AngularSpeed => Angle / GapSec;
    /// <summary>Effective swings per second this move asks for; see <see cref="MovementAnalyzer.Strain"/>.</summary>
    public double Strain => MovementAnalyzer.Strain(Angle, Travel, GapSec);
}

/// <summary>Movement summary of a difficulty: percentiles over its <see cref="HandMove"/>s.</summary>
public sealed record MovementReport
{
    public DifficultyName Difficulty { get; init; }
    public int Moves { get; init; }
    public double AngleMean { get; init; }
    public double AngleP90 { get; init; }
    public double TravelMean { get; init; }
    public double TravelP90 { get; init; }
    public double SpeedP50 { get; init; }
    public double SpeedP90 { get; init; }
    public double StrainP50 { get; init; }
    public double StrainP90 { get; init; }
    public double StrainP98 { get; init; }
    /// <summary>Share of moves that turn 90° or more away from a clean reversal (excluding resets).</summary>
    public double SharpTurns { get; init; }
    /// <summary>Share of moves whose strain is above this difficulty's calibration (moves that belong to a
    /// harder difficulty).</summary>
    public double AboveLevel { get; init; }
    /// <summary>Difficulty the movement corresponds to, as a continuous rank on the 1/3/5/7/9 scale of
    /// <see cref="DifficultyMap.Rank"/>, from <see cref="MovementAnalyzer.RankFromStrain"/>.</summary>
    public double MovementRank { get; init; }
    public DifficultyName MovementDifficulty => MovementAnalyzer.NearestDifficulty(MovementRank);
    public IReadOnlyList<HandMove> Items { get; init; } = [];

    public override string ToString() =>
        $"{Difficulty,-10} moves {Moves,5}  angle {AngleMean,4:0}° (p90 {AngleP90,3:0}°)  travel {TravelMean,4:0.00} (p90 {TravelP90,4:0.00}) cells  " +
        $"speed p50 {SpeedP50,4:0.0} p90 {SpeedP90,4:0.0} c/s  strain p50 {StrainP50,4:0.0} p90 {StrainP90,4:0.0} p98 {StrainP98,4:0.0}  " +
        $"sharp {SharpTurns,4:P0}  above level {AboveLevel,4:P0}  plays like {MovementDifficulty} ({MovementRank:0.0})";
}

/// <summary>Measures how each saber moves between consecutive swings: how far the swing direction turns
/// away from a clean back-and-forth, and how far the saber tip travels between the end of one swing and
/// the start of the next. Uses the same swing model as <see cref="FlowAnalyzer"/> (overshoot of
/// <see cref="SwingCostModel.HalfSwing"/> cells, stacks and sliders are one swing, dots take the direction
/// that leads into the next note).</summary>
public static class MovementAnalyzer
{
    /// <summary>Moves slower than this are rests, not movement; they are left out of the statistics.</summary>
    public const double MaxGapSec = 1.5;

    /// <summary>Turns up to this far from a clean reversal cost no extra time.</summary>
    public const double FreeAngle = 45;
    /// <summary>Extra swing-equivalents per 45° of turn beyond <see cref="FreeAngle"/>.</summary>
    public const double AngleWeight = 0.7;
    /// <summary>Tip travel (cells) that costs no extra time.</summary>
    public const double FreeTravel = 1.0;
    /// <summary>Extra swing-equivalents per cell of travel beyond <see cref="FreeTravel"/>.</summary>
    public const double TravelWeight = 0.5;

    /// <summary>Effective swings per second a move asks for: a plain swing counts 1, turns and travel
    /// beyond the free zone add to it, and the sum is divided by the time available.
    /// Fitted on curated maps (abeat movement): humans give the same minimum gap to 0° and 45° moves and to
    /// any travel up to ~2.5 cells, but ~1.7x the time to 90° turns; travel beyond a cell is what best
    /// separates the difficulties of one song (98.9% of within-song difficulty pairs ordered correctly by
    /// strain p90, against 78% for the swing rate alone).</summary>
    public static double Strain(double angle, double travel, double gapSec) =>
        (1 + AngleWeight * Math.Max(0, angle - FreeAngle) / 45 + TravelWeight * Math.Max(0, travel - FreeTravel)) / Math.Max(gapSec, 0.05);

    /// <summary>Median strain p90 of curated human maps per difficulty, from movement-prior.json (61
    /// difficulties of 20 maps in work/beatsaver; these constants are the fallback);
    /// <see cref="RankFromStrain"/> interpolates between them.</summary>
    public static readonly (DifficultyName Difficulty, double StrainP90)[] Calibration =
        Enum.GetValues<DifficultyName>().Select(d => (d, MovementPrior.For(d)?.StrainP90 ?? d switch
        {
            DifficultyName.Easy => 3.1,
            DifficultyName.Normal => 3.4,
            DifficultyName.Hard => 3.8,
            DifficultyName.Expert => 5.1,
            _ => 6.6,
        })).ToArray();

    /// <summary>Lowest difficulty whose typical hardest moves (strain p90) are at least this strenuous:
    /// the difficulty a single move belongs to.</summary>
    public static DifficultyName DifficultyOf(double strain) =>
        Calibration.FirstOrDefault(c => strain <= c.StrainP90, Calibration[^1]).Difficulty;

    /// <summary>Continuous difficulty rank (1 Easy … 9 Expert+) for a strain p90, interpolated (and
    /// extrapolated) linearly between the calibration points.</summary>
    public static double RankFromStrain(double strainP90)
    {
        var c = Calibration;
        int i = 0;
        while (i < c.Length - 2 && strainP90 > c[i + 1].StrainP90) i++;
        double r0 = DifficultyMap.Rank(c[i].Difficulty), r1 = DifficultyMap.Rank(c[i + 1].Difficulty);
        double f = (strainP90 - c[i].StrainP90) / (c[i + 1].StrainP90 - c[i].StrainP90);
        return Math.Clamp(r0 + f * (r1 - r0), 0, 12);
    }

    public static double Limit(DifficultyName d) => Calibration.First(c => c.Difficulty == d).StrainP90;

    /// <summary>Strain above which a move is a spike for the difficulty: the human 98th percentile.</summary>
    public static double Ceiling(DifficultyName d) => MovementPrior.For(d)?.StrainP98 ?? Limit(d) * 1.3;

    public static DifficultyName NearestDifficulty(double rank) =>
        Enum.GetValues<DifficultyName>().MinBy(d => Math.Abs(DifficultyMap.Rank(d) - rank));

    /// <summary>Swing vector of a note including its angle offset (counter-clockwise degrees).</summary>
    public static Vec2 Vector(CutDirection d, int angleOffset)
    {
        var v = Swing.Vector(d);
        if (angleOffset == 0) return v;
        double a = angleOffset * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return new Vec2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    public static List<HandMove> Moves(DifficultyMap map, double bpm)
    {
        double spb = 60.0 / bpm;
        var moves = new List<HandMove>();
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            // one entry per swing: the top note of a stack, the head of a slider
            var swings = new List<ColorNote>();
            foreach (var n in map.Notes.Where(n => n.Hand == hand).OrderBy(n => n.Beat).ThenByDescending(n => n.Y))
            {
                if (swings.Count > 0 && (n.Beat - swings[^1].Beat) * spb < SwingCostModel.SliderGapSec) continue;
                swings.Add(n);
            }

            var state = HandState.Initial(hand);
            for (int i = 0; i < swings.Count; i++)
            {
                var n = swings[i];
                var v = n.Direction == CutDirection.Any ? DotSwing(state, swings, i, hand, spb) : Vector(n.Direction, n.AngleOffset);
                double t = n.Beat * spb;
                if (state.Active && t - state.Time <= MaxGapSec)
                {
                    var p = new Vec2(n.X, n.Y);
                    var entry = p - v * SwingCostModel.HalfSwing;
                    moves.Add(new HandMove(hand, n.Beat, t - state.Time, v.AngleTo(-state.Swing), (entry - state.Exit).Length,
                        (p - new Vec2(state.X, state.Y)).Length, state.Direction, Swing.FromVector(v)));
                }
                state = state.After(t, n.X, n.Y, n.Direction, v, hand);
            }
        }
        moves.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        return moves;
    }

    /// <summary>Dots: reverse the last swing, unless the hand's next swing is a directional note soon
    /// after that wants a different approach and taking it does not cost a reset.</summary>
    static Vec2 DotSwing(HandState s, List<ColorNote> swings, int i, Hand hand, double spb)
    {
        var v = SwingCostModel.EffectiveSwing(s, CutDirection.Any);
        var next = i + 1 < swings.Count ? swings[i + 1] : null;
        if (next != null && next.Direction != CutDirection.Any && (next.Beat - swings[i].Beat) * spb < 1.5)
        {
            var want = -Vector(next.Direction, next.AngleOffset);
            if (!s.Active || !SwingCostModel.IsReset(hand, s, want)) v = want;
        }
        return v;
    }

    public static MovementReport Analyze(DifficultyMap map, double bpm)
    {
        var m = Moves(map, bpm);
        if (m.Count == 0) return new MovementReport { Difficulty = map.Difficulty };
        var strain = m.Select(x => x.Strain).Order().ToArray();
        double p90 = Percentile(strain, 0.9);
        return new MovementReport
        {
            Difficulty = map.Difficulty,
            Moves = m.Count,
            AngleMean = m.Average(x => x.Angle),
            AngleP90 = Percentile(m.Select(x => x.Angle).Order().ToArray(), 0.9),
            TravelMean = m.Average(x => x.Travel),
            TravelP90 = Percentile(m.Select(x => x.Travel).Order().ToArray(), 0.9),
            SpeedP50 = Percentile(m.Select(x => x.Speed).Order().ToArray(), 0.5),
            SpeedP90 = Percentile(m.Select(x => x.Speed).Order().ToArray(), 0.9),
            StrainP50 = Percentile(strain, 0.5),
            StrainP90 = p90,
            StrainP98 = Percentile(strain, 0.98),
            SharpTurns = m.Count(x => x.Angle >= 89 && x.Angle < 179) / (double)m.Count,
            AboveLevel = m.Count(x => x.Strain > Limit(map.Difficulty)) / (double)m.Count,
            MovementRank = RankFromStrain(p90),
            Items = m,
        };
    }

    /// <summary>Linear-interpolated percentile of sorted values, q in 0..1.</summary>
    public static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
