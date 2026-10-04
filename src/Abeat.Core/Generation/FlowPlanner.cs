using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Assigns hand, grid position and cut direction to every rhythm event by beam search over
/// both sabers' states, minimizing <see cref="SwingCostModel"/> over the whole song. Because every
/// candidate is scored against where each saber actually is, parity and flow are built in rather
/// than repaired afterwards.</summary>
/// <param name="oneSaber">One Saber mode: every note goes to the right saber, which covers the whole grid.</param>
public sealed class FlowPlanner(SwingCostModel model, DifficultyProfile profile, int beamWidth, int seed, bool oneSaber = false)
{
    readonly StylePrior? style = StylePrior.For(profile.Name);
    readonly MovementPrior? movement = MovementPrior.For(profile.Name);

    /// <summary>Offset of the movement buckets in <see cref="Node.Counts"/>.</summary>
    const int MoveBase = 33;
    /// <summary>Offset of the strain buckets in <see cref="Node.Counts"/>.</summary>
    static readonly int StrainBase = MoveBase + MovementPrior.Buckets;

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
        /// <summary>Running style counts on this path: [0..8] directions, [9..20] left cells, [21..32] right cells,
        /// then the <see cref="MovementPrior"/> move and strain buckets.</summary>
        public int[] Counts = new int[StrainBase + MovementPrior.StrainBuckets];
        public int NotesL, NotesR, Moves;

        public HandState State(Hand h) => h == Hand.Left ? Left : Right;
    }

    /// <summary>Hand that should play each event's layer under the hand-role split, or null for events
    /// that belong to neither role.</summary>
    Hand?[] roles = [];

    /// <summary>Cuts each event should copy (pattern memory), from <see cref="RepeatReference"/>.</summary>
    Dictionary<int, List<Cut>> reference = [];

    /// <param name="previous">An earlier plan of the same events: repeated sections are pulled towards the
    /// cuts their first occurrence got there (see <see cref="FlowWeights.Repetition"/>).</param>
    public List<ColorNote> Plan(IReadOnlyList<RhythmEvent> events, IReadOnlyList<ColorNote>? previous = null)
    {
        var w = model.Weights;
        roles = HandRoles(events, seed);
        reference = previous is null ? [] : RepeatReference(events, previous);
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
        var target = oneSaber ? PhraseTargets.ForOneSaber(e, seed) : PhraseTargets.For(hand, e, seed);

        foreach (var d in Swing.Directional.Append(CutDirection.Any))
        {
            var v = SwingCostModel.EffectiveSwing(s, d);
            // prune hard parity breaks early; the cost model would reject them anyway
            if (mustFlip && SwingCostModel.IsReset(hand, s, v)) continue;
            for (int x = 0; x < 4; x++)
            {
                if (!oneSaber && (hand == Hand.Right ? x == 0 : x == 3)) continue; // far-side crossovers are never worth it
                for (int y = 0; y < 3; y++)
                {
                    var phys = model.Physical(hand, s, other, e.Time, x, y, d, profile.MinSameHandGapSec);
                    double c = phys.Total + model.Musical(e.Brightness, e.Strength, y, d);
                    if (d == CutDirection.Any) c += profile.DotCost;
                    if (s.Active && s.X == x && s.Y == y && d == lastDir) c += model.Weights.Repeat;
                    c += model.Stagnation(s, x, y) + model.Target(target, x, y) + model.Dynamics(s, e.Intensity, x, y);
                    if (style != null)
                    {
                        int handNotes = hand == Hand.Left ? node.NotesL : node.NotesR;
                        int cellBase = hand == Hand.Left ? 9 : 21;
                        // one saber plays both sides of the grid: the two hands' cell shares, averaged
                        double cellShare = oneSaber ? (style.Cells[0][y * 4 + x] + style.Cells[1][y * 4 + x]) / 2 : style.Cells[(int)hand][y * 4 + x];
                        c += model.Weights.StyleCell * StylePrior.MatchCost(node.Counts[cellBase + y * 4 + x], handNotes, cellShare);
                        c += model.Weights.StyleDirection * StylePrior.MatchCost(node.Counts[(int)d], node.NotesL + node.NotesR, style.Directions[(int)d]);
                    }
                    if (movement != null && Move(s, e.Time, x, y, v) is var (bucket, strain))
                    {
                        c += model.Weights.MovementStyle * StylePrior.MatchCost(node.Counts[MoveBase + bucket], node.Moves, movement.Moves[bucket]);
                        int sb = MovementPrior.StrainBucket(strain);
                        c += model.Weights.Effort * StylePrior.MatchCost(node.Counts[StrainBase + sb], node.Moves, movement.Strain[sb]);
                        if (strain > movement.StrainP98) c += model.Weights.Strain * 4 * (strain / movement.StrainP98 - 1);
                    }
                    c += model.Weights.Noise * Noise(eventIndex, hand, x, y, d);
                    if (reference.TryGetValue(eventIndex, out var refCuts) && refCuts.Contains(new Cut(hand, x, y, d))) c -= model.Weights.Repetition;
                    yield return (new Cut(hand, x, y, d), c, v);
                }
            }
        }
    }

    /// <summary>Movement bucket and strain of the move from the hand's last swing to this cut, or null when
    /// the hand is idle or resting (see <see cref="Evaluation.MovementAnalyzer"/>).</summary>
    static (int bucket, double strain)? Move(HandState s, double t, int x, int y, Vec2 v)
    {
        double gap = t - s.Time;
        if (!s.Active || gap > Evaluation.MovementAnalyzer.MaxGapSec) return null;
        double angle = v.AngleTo(-s.Swing);
        double travel = (new Vec2(x, y) - v * SwingCostModel.HalfSwing - s.Exit).Length;
        return (MovementPrior.Bucket(angle, travel), Evaluation.MovementAnalyzer.Strain(angle, travel, gap));
    }

    /// <summary>For every event in a section whose label came before, the cuts at the same beat of the
    /// label's first occurrence in <paramref name="previous"/>; events of first occurrences keep their own
    /// cuts, so the second pass doesn't drift away from what is being copied.</summary>
    static Dictionary<int, List<Cut>> RepeatReference(IReadOnlyList<RhythmEvent> events, IReadOnlyList<ColorNote> previous)
    {
        var cuts = previous.GroupBy(n => Math.Round(n.Beat * 24)).ToDictionary(g => g.Key, g => g.Select(n => new Cut(n.Hand, n.X, n.Y, n.Direction)).ToList());
        var firstStart = new Dictionary<string, double>();
        foreach (var e in events)
            if (!firstStart.ContainsKey(e.Section)) firstStart[e.Section] = Math.Round(e.Beat - e.BeatInSection);
        var result = new Dictionary<int, List<Cut>>();
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            double at = firstStart[e.Section] + e.BeatInSection;
            if (cuts.TryGetValue(Math.Round(at * 24), out var c)) result[i] = c;
        }
        return result;
    }

    static readonly HashSet<string> MelodyLayers = RhythmSelector.MelodyLayers;
    static readonly HashSet<string> RhythmLayers = ["drums", "bass", "low"];

    /// <summary>Melody layers go to one hand and rhythm layers to the other; the roles swap at every
    /// section change (the first section's melody hand depends on the seed).</summary>
    public static Hand?[] HandRoles(IReadOnlyList<RhythmEvent> events, int seed)
    {
        var roles = new Hand?[events.Count];
        int flips = seed & 1;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0 && events[i].Section != events[i - 1].Section) flips++;
            var melodyHand = flips % 2 == 0 ? Hand.Right : Hand.Left;
            var rhythmHand = melodyHand == Hand.Right ? Hand.Left : Hand.Right;
            roles[i] = MelodyLayers.Contains(events[i].Layer) ? melodyHand
                : RhythmLayers.Contains(events[i].Layer) ? rhythmHand : null;
        }
        return roles;
    }

    /// <summary>Only the cheapest few continuations of a node can survive the beam; scoring all but
    /// allocating nodes for just these keeps the search fast.</summary>
    const int PerNodeSingle = 16;

    void ExpandSingle(Node node, RhythmEvent e, int i, Dictionary<long, Node> next)
    {
        foreach (var hand in oneSaber ? [Hand.Right] : new[] { Hand.Left, Hand.Right })
        {
            foreach (var (cut, cost, swing) in Candidates(node, hand, e, i).OrderBy(c => c.cost).Take(PerNodeSingle))
            {
                double total = node.Cost + cost;
                // alternating hands on quick consecutive notes is what players expect
                if (hand == node.LastHand && node.Parent != null)
                {
                    double gap = e.Time - node.State(hand).Time;
                    if (gap < 0.5) total += 0.75 * (0.5 - gap) / 0.5;
                }
                if (roles[i] is { } role && role != hand) total += model.Weights.HandRole;
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
        double clash = SwingCostModel.DoubleClash(l.X, l.Y, lv, r.X, r.Y, rv);
        if (double.IsPositiveInfinity(clash)) return clash;
        c += clash;
        // both swinging inward collides even with a column between them
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
            Counts = (int[])parent.Counts.Clone(), NotesL = parent.NotesL, NotesR = parent.NotesR, Moves = parent.Moves,
        };
        Apply(n, a, t, sa);
        if (b is { } bb) Apply(n, bb, t, sb);

        long key = Key(n);
        if (!next.TryGetValue(key, out var existing) || existing.Cost > cost) next[key] = n;
    }

    static void Apply(Node n, Cut c, double t, Vec2 swing)
    {
        if (Move(n.State(c.Hand), t, c.X, c.Y, swing) is var (bucket, strain))
        {
            n.Counts[MoveBase + bucket]++;
            n.Counts[StrainBase + MovementPrior.StrainBucket(strain)]++;
            n.Moves++;
        }
        n.Counts[(int)c.Dir]++;
        n.Counts[(c.Hand == Hand.Left ? 9 : 21) + c.Y * 4 + c.X]++;
        if (c.Hand == Hand.Left) n.NotesL++; else n.NotesR++;
        var s = n.State(c.Hand).After(t, c.X, c.Y, c.Dir, swing, c.Hand);
        if (c.Hand == Hand.Left) { n.Left = s; n.LastDirL = c.Dir; }
        else { n.Right = s; n.LastDirR = c.Dir; }
    }

    /// <summary>States that will behave identically from here on are merged (keep the cheaper one).</summary>
    static long Key(Node n)
    {
        static long H(HandState s) => s.Active ? 1 + s.X + 4 * (s.Y + 3 * ((int)SwingDir(s) + 9 * (int)(s.Parity ?? (Parity)2))) : 0;
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
