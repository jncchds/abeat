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
    Parity Parity,
    int PrevX = -1,
    int PrevY = -1)
{
    /// <summary>Hands start low-centre as if they had just swung up, so the first swing is a natural forehand down.</summary>
    public static HandState Initial(Hand h) =>
        new(false, double.NegativeInfinity, h == Hand.Left ? 1 : 2, 0, CutDirection.Up, new Vec2(0, 1),
            new Vec2(h == Hand.Left ? 1 : 2, 1.6), Parity.Backhand);

    public HandState After(double time, int x, int y, CutDirection dir, Vec2 swing, Hand hand) =>
        new(true, time, x, y, dir, swing, new Vec2(x, y) + swing * SwingCostModel.HalfSwing, Model.Swing.ParityOf(hand, swing),
            Active ? X : -1, Active ? Y : -1);
}

public sealed record CostBreakdown(double Physical, double Musical, bool Reset, bool VisionBlock, bool Crossover)
{
    public double Total => Physical + Musical;
}

/// <summary>Scores one cut given the hand's previous state. Physical terms describe how the swing feels
/// regardless of music; musical terms tie the placement to the audio features.</summary>
public sealed class SwingCostModel(FlowWeights w)
{
    /// <summary>How far (in grid cells) a swing travels past the note centre on each side.</summary>
    public const double HalfSwing = 0.6;
    /// <summary>Gap after which a hand can comfortably reset (re-wind) between swings.</summary>
    public const double ResetGapSec = 1.0;

    public FlowWeights Weights => w;

    /// <summary>The swing vector a note actually produces: dots continue the natural reversal.</summary>
    public static Vec2 EffectiveSwing(HandState s, CutDirection d) =>
        d == CutDirection.Any ? (s.Active ? -s.Swing : new Vec2(0, -1)) : Swing.Vector(d);

    public CostBreakdown Physical(Hand hand, HandState s, HandState other, double t, int x, int y, CutDirection d, double minSameHandGap)
    {
        var v = EffectiveSwing(s, d);
        double c = 0;
        bool reset = false, vision = false, cross = false;
        double gap = s.Active ? t - s.Time : double.PositiveInfinity;

        if (s.Active)
        {
            var parity = Swing.ParityOf(hand, v);
            if (parity == s.Parity)
            {
                reset = gap < ResetGapSec;
                c += reset ? w.Reset : w.SlowReset;
            }
            // speed factor: the less time, the more a deviation hurts
            double speed = Math.Clamp(0.45 / gap, 0.3, 3.0);
            double angle = v.AngleTo(-s.Swing);
            c += w.Angle * Math.Pow(angle / 45.0, 2) * speed;

            var entry = new Vec2(x, y) - v * HalfSwing;
            double dist = (entry - s.Exit).Length;
            c += w.Travel * dist * dist * Math.Clamp(0.35 / gap, 0.15, 3.0);

            if (gap < minSameHandGap) c += w.TooFast * (1 - gap / minSameHandGap) + 2;
        }
        else if (Swing.ParityOf(hand, v) == Parity.Backhand)
        {
            c += 1.0; // first swing: prefer forehand
        }

        // lanes: each hand owns its side; centre columns are shared
        int outward = hand == Hand.Right ? x - 1 : 2 - x; // 1..2 = home side, 0 = centre-other, -1 = far side
        if (outward <= 0)
        {
            c += w.Crossover * (outward < 0 ? 3 : 0.3);
            cross = outward < 0;
        }
        // crossing over the other hand's recent position
        if (other.Active && t - other.Time < 0.4 && (hand == Hand.Right ? x < other.X : x > other.X))
        {
            c += w.Crossover * 2;
            cross = true;
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

        return new CostBreakdown(c, 0, reset, vision, cross);
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

    public double Musical(double brightness, double strength, int y, CutDirection d)
    {
        double target = Math.Clamp(brightness, 0, 1) * 2;
        double c = w.Pitch * Math.Pow(y - target, 2) * 0.5;
        if (strength > 0.6)
        {
            var v = Swing.Vector(d);
            c += w.Emphasis * (strength - 0.6) * 2.5 * (1 - Math.Abs(v.Y)); // accents want big vertical swings
        }
        return c;
    }
}
