namespace Abeat.Core.Model;

public enum Hand { Left = 0, Right = 1 }

/// <summary>Beat Saber cut direction values as stored in map files.</summary>
public enum CutDirection { Up = 0, Down = 1, Left = 2, Right = 3, UpLeft = 4, UpRight = 5, DownLeft = 6, DownRight = 7, Any = 8 }

/// <summary>Forehand = palm-side swing (down-ish), Backhand = back-of-hand swing (up-ish).</summary>
public enum Parity { Forehand, Backhand }

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;

    /// <summary>Angle between two vectors in degrees, 0..180.</summary>
    public double AngleTo(Vec2 o)
    {
        double d = Dot(o) / Math.Max(1e-9, Length * o.Length);
        return Math.Acos(Math.Clamp(d, -1, 1)) * 180 / Math.PI;
    }
}

public static class Swing
{
    const double D = 0.70710678;

    public static readonly CutDirection[] Directional =
    [
        CutDirection.Up, CutDirection.Down, CutDirection.Left, CutDirection.Right,
        CutDirection.UpLeft, CutDirection.UpRight, CutDirection.DownLeft, CutDirection.DownRight,
    ];

    /// <summary>Unit vector of the saber motion for a cut direction (x right, y up).</summary>
    public static Vec2 Vector(CutDirection d) => d switch
    {
        CutDirection.Up => new(0, 1),
        CutDirection.Down => new(0, -1),
        CutDirection.Left => new(-1, 0),
        CutDirection.Right => new(1, 0),
        CutDirection.UpLeft => new(-D, D),
        CutDirection.UpRight => new(D, D),
        CutDirection.DownLeft => new(-D, -D),
        CutDirection.DownRight => new(D, -D),
        _ => new(0, 0),
    };

    public static CutDirection FromVector(Vec2 v)
    {
        if (v.Length < 1e-6) return CutDirection.Any;
        CutDirection best = CutDirection.Down;
        double bestDot = double.NegativeInfinity;
        foreach (var d in Directional)
        {
            double dot = Vector(d).Dot(v);
            if (dot > bestDot) { bestDot = dot; best = d; }
        }
        return best;
    }

    public static Parity ParityOf(Hand hand, Vec2 v)
    {
        if (v.Y < -0.1) return Parity.Forehand;
        if (v.Y > 0.1) return Parity.Backhand;
        // horizontal: moving toward the body's other side is the forehand motion
        bool towardInside = hand == Hand.Right ? v.X < 0 : v.X > 0;
        return towardInside ? Parity.Forehand : Parity.Backhand;
    }

    public static Parity Flip(Parity p) => p == Parity.Forehand ? Parity.Backhand : Parity.Forehand;

    public static bool IsHorizontal(CutDirection d) => d is CutDirection.Left or CutDirection.Right;
    public static bool IsDiagonal(CutDirection d) => (int)d is >= 4 and <= 7;
}
