using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Where a saber is after its last swing.</summary>
public readonly record struct HandState(
    bool Active,
    double Time,
    int X,
    int Y,
    CutDirection Direction,
    Vec2 Swing,
    Vec2 Exit,
    Parity? Parity, // null = free (last swing was horizontal)
    int PrevX = -1,
    int PrevY = -1,
    int BurstRun = 0) // consecutive same-hand gaps under the regular same-hand gap (see FlowPlanner bursts)
{
    /// <summary>Hands start low-centre as if they had just swung up, so the first swing is a natural forehand down.</summary>
    public static HandState Initial(Hand h) =>
        new(false, double.NegativeInfinity, h == Hand.Left ? 1 : 2, 0, CutDirection.Up, new Vec2(0, 1),
            new Vec2(h == Hand.Left ? 1 : 2, 1.6), Model.Parity.Backhand);

    /// <param name="fastBelowSec">Same-hand gaps shorter than this extend <see cref="BurstRun"/>.</param>
    public HandState After(double time, int x, int y, CutDirection dir, Vec2 swing, Hand hand, double fastBelowSec = 0) =>
        new(true, time, x, y, dir, swing, new Vec2(x, y) + swing * SwingCostModel.HalfSwing, Model.Swing.ParityAfter(hand, swing),
            Active ? X : -1, Active ? Y : -1, Active && time - Time < fastBelowSec ? BurstRun + 1 : 0);
}

public sealed record CostBreakdown(double Physical, double Musical, bool Reset, bool VisionBlock, bool Crossover, bool HandClash = false)
{
    public double Total => Physical + Musical;
}

/// <summary>Scores one cut given the hand's previous state. Physical terms describe how the swing feels
/// regardless of music; musical terms tie the placement to the audio features.</summary>
public sealed class SwingCostModel(FlowWeights w)
{
    /// <summary>How far (in grid cells) a swing travels past the note centre on each side.</summary>
    public const double HalfSwing = 0.6;
    /// <summary>Saber travel between swings that costs nothing (grid cells).</summary>
    public const double TravelSlack = 1.25;
    /// <summary>Gap after which a hand can comfortably reset (re-wind) between swings.</summary>
    public const double ResetGapSec = 1.0;
    /// <summary>Same-hand notes closer than this are one swing (sliders / windows).</summary>
    public const double SliderGapSec = 0.09;
    /// <summary>The other hand cutting the same cell sooner than this after this hand brings the sabers
    /// together; curated maps do it 0.6 times per 1000 notes (generated maps did 7x as often).</summary>
    public const double SameCellClashSec = 0.2;
    /// <summary>Up to this gap a same-cell cut by the other hand still costs something (curated maps
    /// 3.7 per 1000 notes within 0.3 s; from there on humans do it more often than we did).</summary>
    public const double SameCellSec = 0.3;

    public FlowWeights Weights => w;

    /// <summary>The swing vector a note actually produces: dots continue the natural reversal.</summary>
    /// <summary>A swing that needs a re-wind: it forces the same parity as the last swing, or points
    /// (nearly) the same way as the last swing (e.g. two right-cuts in a row).</summary>
    public static bool IsReset(Hand hand, HandState s, Vec2 v) =>
        s.Active && ((Swing.FixedParity(hand, v) is { } p && p == s.Parity) || v.AngleTo(s.Swing) < 60);

    public static Vec2 EffectiveSwing(HandState s, CutDirection d) =>
        d == CutDirection.Any ? (s.Active ? -s.Swing : new Vec2(0, -1)) : Swing.Vector(d);

    /// <param name="burstGap">Quick back-and-forth flicks of one hand may go down to this gap (below
    /// <paramref name="minSameHandGap"/>): a clean reversal (turn <= 45°) with free travel, at most
    /// <paramref name="burstNotes"/> notes in a row; >= minSameHandGap = no bursts.</param>
    public CostBreakdown Physical(Hand hand, HandState s, HandState other, double t, int x, int y, CutDirection d, double minSameHandGap,
        double burstGap = double.MaxValue, int burstNotes = 0)
    {
        var v = EffectiveSwing(s, d);
        double c = 0;
        bool reset = false, vision = false, cross = false, clash = false;
        double gap = s.Active ? t - s.Time : double.PositiveInfinity;

        if (s.Active)
        {
            if (IsReset(hand, s, v))
            {
                reset = gap < ResetGapSec;
                c += reset ? w.Reset : w.SlowReset;
            }
            // speed factor: the less time, the more a deviation hurts
            double speed = Math.Clamp(0.45 / gap, 0.3, 3.0);
            double turn = v.AngleTo(-s.Swing);
            c += w.Angle * TurnCost(turn) * speed;

            // real swings overshoot the grid, so moving up to TravelSlack cells between notes is free;
            // without the slack curated human maps scored ~20 points lower than ours on travel alone
            var entry = new Vec2(x, y) - v * HalfSwing;
            double dist = Math.Max(0, (entry - s.Exit).Length - TravelSlack);
            c += w.Travel * dist * dist * Math.Clamp(0.35 / gap, 0.15, 3.0);

            if (gap < minSameHandGap)
            {
                bool burst = gap >= burstGap && turn <= 45 && dist == 0 && !reset && s.BurstRun + 2 <= burstNotes;
                c += burst ? w.Burst : w.TooFast * (1 - gap / minSameHandGap) + 2;
            }
        }
        else if (Swing.FixedParity(hand, v) == Parity.Backhand)
        {
            c += 1.0; // first swing: prefer forehand
        }

        // reaching past the other hand's recent position (passing in the middle columns is normal)
        if (other.Active && t - other.Time < 0.4)
        {
            int past = hand == Hand.Right ? other.X - x : x - other.X;
            if (past >= 1) c += w.Crossover * (past >= 2 ? 2 : 0.3);
            if (past >= 2) cross = true;
        }

        // the other hand just cut this cell: one saber comes in where the other one is
        if (other.Active && other.X == x && other.Y == y && t - other.Time < SameCellSec)
        {
            double og = t - other.Time;
            c += w.HandClash * Math.Min(1, (SameCellSec - og) / (SameCellSec - SameCellClashSec));
            clash = og < SameCellClashSec;
        }

        if (y == 1 && x is 1 or 2)
        {
            c += w.VisionBlock;
            vision = true;
        }
        if (Swing.IsHorizontal(d)) c += w.Horizontal;
        // upward cuts in the top row / downward in the bottom row force the arm to over-extend
        if (y == 2 && v.Y > 0.5) c += 0.4;
        if (y == 0 && v.Y < -0.5 && x is 1 or 2) c += 0.2;

        return new CostBreakdown(c, 0, reset, vision, cross, clash);
    }

    /// <summary>Turn away from a clean reversal, in units of the old (angle/45)² cost at 45°. Humans give
    /// 45° turns the same time as straight reversals but 90° turns ~1.7x the time (abeat movement), so
    /// turns up to 45° are cheap and the cost grows steeply beyond.</summary>
    public static double TurnCost(double angle)
    {
        double small = Math.Min(angle, 45) / 45, big = Math.Max(0, angle - 45) / 45;
        return 0.35 * small * small + 2.0 * big * big;
    }

    /// <summary>Two simultaneous cuts (a double) where one saber swings towards the other hand's note
    /// in the same or next column of the same row: the sabers collide. Returns +inf for horizontal
    /// swings into the other note (e.g. L← and R← side by side: the right saber sweeps through the left
    /// note), a penalty for inward diagonals there and for swings that start or end together, 0 otherwise.</summary>
    public static double DoubleClash(int lx, int ly, Vec2 lv, int rx, int ry, Vec2 rv)
    {
        double c = 0;
        if (ly == ry && rx - lx <= 1) // same row, no free column between the notes
        {
            foreach (var (v, inwardSign) in new[] { (lv, 1.0), (rv, -1.0) })
            {
                if (v.X * inwardSign < 0.5) continue; // not moving towards the other hand
                if (Math.Abs(v.Y) < 0.1) return double.PositiveInfinity;
                c += 8;
            }
        }
        // both swings start (or end) in the same spot, e.g. L↖ at x1 with R↗ at x2: the hands begin the
        // double together in the middle. Curated maps: ~0.1 per 1000 notes; generated maps had 7
        if (c == 0 && lv.Length > 0.5 && rv.Length > 0.5)
        {
            Vec2 l = new(lx, ly), r = new(rx, ry);
            if ((l - lv * HalfSwing - (r - rv * HalfSwing)).Length < 0.5 || (l + lv * HalfSwing - (r + rv * HalfSwing)).Length < 0.5)
                c = 8;
        }
        return c;
    }

    /// <summary>Swing size against intensity (see <see cref="RhythmEvent.Intensity"/>): size is how far the
    /// note sits from the centre columns and how far the hand moves from its last note, both 0..1. Loud
    /// moments pay for small swings and soft ones for big swings; the median costs nothing.</summary>
    public double Dynamics(HandState s, double intensity, int x, int y)
    {
        double spread = Math.Abs(x - 1.5) / 1.5;
        double move = s.Active ? Math.Min(1, new Vec2(x - s.X, y - s.Y).Length / 2) : 0.5;
        double size = 0.5 * spread + 0.5 * move;
        return w.Dynamics * (2 * intensity - 1) * (0.5 - size) * 2;
    }

    /// <summary>Hands that stay in one cell feel monotonous even when the flow is perfect.</summary>
    public double Stagnation(HandState s, int x, int y)
    {
        if (!s.Active) return 0;
        double c = 0;
        if (s.X == x && s.Y == y) c += w.Stagnation;
        if (s.PrevX == x && s.PrevY == y) c += w.Stagnation * 0.6;
        return c;
    }

    /// <summary>Pull towards the phrase target cell (see <see cref="PhraseTargets"/>).</summary>
    public double Target(Vec2 target, int x, int y)
    {
        double dx = x - target.X, dy = y - target.Y;
        return w.Target * (dx * dx + dy * dy);
    }

    /// <summary>Strong accents want big vertical swings. Note height deliberately does not follow the audio:
    /// rows come from the human figure vocabulary and cell shares (<see cref="StylePrior"/>).</summary>
    public double Musical(double strength, CutDirection d) =>
        strength > 0.6 ? w.Emphasis * (strength - 0.6) * 2.5 * (1 - Math.Abs(Swing.Vector(d).Y)) : 0;
}
