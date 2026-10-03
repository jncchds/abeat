using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Assigns hand, grid position and cut direction to every rhythm event by beam search over
/// both sabers' states, minimizing <see cref="SwingCostModel"/> over the whole song. Because every
/// candidate is scored against where each saber actually is, parity and flow are built in rather
/// than repaired afterwards.</summary>
public sealed class FlowPlanner(SwingCostModel model, DifficultyProfile profile, int beamWidth, int seed)
{
    readonly record struct Cut(Hand Hand, int X, int Y, CutDirection Dir);

    sealed class Node
    {
        public HandState Left, Right;
        public double Cost;
        public Node? Parent;
        public Cut A;
        public Cut? B; // second note for doubles
        public Hand LastHand;
        public CutDirection LastDirL = CutDirection.Any, LastDirR = CutDirection.Any;

        public HandState State(Hand h) => h == Hand.Left ? Left : Right;
    }

    public List<ColorNote> Plan(IReadOnlyList<RhythmEvent> events)
    {
        var w = model.Weights;
        var beam = new List<Node> { new() { Left = HandState.Initial(Hand.Left), Right = HandState.Initial(Hand.Right), LastHand = Hand.Left } };

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var next = new Dictionary<long, Node>();
            foreach (var node in beam)
            {
                if (e.IsDouble) ExpandDouble(node, e, i, next);
                else ExpandSingle(node, e, i, next);
            }
            beam = next.Values.OrderBy(n => n.Cost).Take(beamWidth).ToList();
            if (beam.Count == 0) throw new InvalidOperationException($"beam died at event {i}");
        }

        var best = beam.MinBy(n => n.Cost)!;
        var notes = new List<ColorNote>();
        for (int i = events.Count - 1; best.Parent != null; i--, best = best.Parent)
        {
            notes.Add(new ColorNote(events[i].Beat, best.A.X, best.A.Y, best.A.Hand, best.A.Dir));
            if (best.B is { } b) notes.Add(new ColorNote(events[i].Beat, b.X, b.Y, b.Hand, b.Dir));
        }
        notes.Reverse();
        return notes;
    }

    /// <summary>Candidate cuts for one hand, scored on their own (physical + musical + noise).</summary>
    IEnumerable<(Cut cut, double cost, Vec2 swing)> Candidates(Node node, Hand hand, RhythmEvent e, int eventIndex)
    {
        var s = node.State(hand);
        var other = node.State(hand == Hand.Left ? Hand.Right : Hand.Left);
        bool mustFlip = s.Active && e.Time - s.Time < SwingCostModel.ResetGapSec;
        var lastDir = hand == Hand.Left ? node.LastDirL : node.LastDirR;
        var target = PhraseTargets.For(hand, e, seed);

        foreach (var d in Swing.Directional.Append(CutDirection.Any))
        {
            var v = SwingCostModel.EffectiveSwing(s, d);
            // prune hard parity breaks early; the cost model would reject them anyway
            if (mustFlip && Swing.ParityOf(hand, v) == s.Parity) continue;
            for (int x = 0; x < 4; x++)
            {
                if (hand == Hand.Right ? x == 0 : x == 3) continue; // far-side crossovers are never worth it
                for (int y = 0; y < 3; y++)
                {
                    var phys = model.Physical(hand, s, other, e.Time, x, y, d, profile.MinSameHandGapSec);
                    double c = phys.Total + model.Musical(e.Brightness, e.Strength, y, d);
                    if (d == CutDirection.Any) c += profile.DotCost;
                    if (s.Active && s.X == x && s.Y == y && d == lastDir) c += model.Weights.Repeat;
                    c += model.Stagnation(s, x, y) + model.Target(target, x, y);
                    c += model.Weights.Noise * Noise(eventIndex, hand, x, y, d);
                    yield return (new Cut(hand, x, y, d), c, v);
                }
            }
        }
    }

    /// <summary>Only the cheapest few continuations of a node can survive the beam; scoring all but
    /// allocating nodes for just these keeps the search fast.</summary>
    const int PerNodeSingle = 16;

    void ExpandSingle(Node node, RhythmEvent e, int i, Dictionary<long, Node> next)
    {
        foreach (var hand in new[] { Hand.Left, Hand.Right })
        {
            foreach (var (cut, cost, swing) in Candidates(node, hand, e, i).OrderBy(c => c.cost).Take(PerNodeSingle))
            {
                double total = node.Cost + cost;
                // alternating hands on quick consecutive notes is what players expect
                if (hand == node.LastHand && node.Parent != null)
                {
                    double gap = e.Time - node.State(hand).Time;
                    if (gap < 0.5) total += 1.5 * (0.5 - gap) / 0.5;
                }
                Add(next, node, total, cut, null, e.Time, swing, default);
            }
        }
    }

    void ExpandDouble(Node node, RhythmEvent e, int i, Dictionary<long, Node> next)
    {
        const int perHand = 10;
        var left = Candidates(node, Hand.Left, e, i).OrderBy(c => c.cost).Take(perHand).ToList();
        var right = Candidates(node, Hand.Right, e, i).OrderBy(c => c.cost).Take(perHand).ToList();
        foreach (var l in left)
        foreach (var r in right)
        {
            double pair = PairCost(l.cut, r.cut, l.swing, r.swing);
            if (double.IsPositiveInfinity(pair)) continue;
            Add(next, node, node.Cost + l.cost + r.cost + pair, l.cut, r.cut, e.Time, l.swing, r.swing);
        }
    }

    /// <summary>Doubles should be parallel or mirrored, side by side, and must never collide.</summary>
    static double PairCost(Cut l, Cut r, Vec2 lv, Vec2 rv)
    {
        if (l.X == r.X && l.Y == r.Y) return double.PositiveInfinity;
        double c = 0;
        if (l.X > r.X) c += 12; // crossed doubles
        else if (l.X == r.X) c += 4; // stacked in one column
        c += 0.5 * Math.Abs(l.Y - r.Y);
        // inward horizontals swing the sabers into each other
        if (lv.X > 0.5 && rv.X < -0.5) c += 10;
        double parallel = lv.AngleTo(rv);
        double mirrored = lv.AngleTo(new Vec2(-rv.X, rv.Y));
        double dev = Math.Min(parallel, mirrored);
        if (dev > 1) c += 2 + dev / 45.0;
        return c;
    }

    void Add(Dictionary<long, Node> next, Node parent, double cost, Cut a, Cut? b, double t, Vec2 sa, Vec2 sb)
    {
        var n = new Node
        {
            Left = parent.Left, Right = parent.Right, Cost = cost, Parent = parent, A = a, B = b,
            LastHand = a.Hand, LastDirL = parent.LastDirL, LastDirR = parent.LastDirR,
        };
        Apply(n, a, t, sa);
        if (b is { } bb) Apply(n, bb, t, sb);

        long key = Key(n);
        if (!next.TryGetValue(key, out var existing) || existing.Cost > cost) next[key] = n;
    }

    static void Apply(Node n, Cut c, double t, Vec2 swing)
    {
        var s = n.State(c.Hand).After(t, c.X, c.Y, c.Dir, swing, c.Hand);
        if (c.Hand == Hand.Left) { n.Left = s; n.LastDirL = c.Dir; }
        else { n.Right = s; n.LastDirR = c.Dir; }
    }

    /// <summary>States that will behave identically from here on are merged (keep the cheaper one).</summary>
    static long Key(Node n)
    {
        static long H(HandState s) => s.Active ? 1 + s.X + 4 * (s.Y + 3 * ((int)SwingDir(s) + 9 * (int)s.Parity)) : 0;
        return H(n.Left) * 1000 + H(n.Right) * 2 + (int)n.LastHand;
    }

    static CutDirection SwingDir(HandState s) => Swing.FromVector(s.Swing);

    double Noise(int eventIndex, Hand h, int x, int y, CutDirection d)
    {
        // deterministic hash noise in 0..1 so results are reproducible per seed
        ulong k = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)eventIndex * 0xBF58476D1CE4E5B9UL
                  ^ (ulong)((int)h * 1000 + x * 100 + y * 10 + (int)d) * 0x94D049BB133111EBUL;
        k ^= k >> 31; k *= 0xD6E8FEB86659FD93UL; k ^= k >> 32;
        return (k & 0xFFFFFF) / (double)0xFFFFFF;
    }
}
