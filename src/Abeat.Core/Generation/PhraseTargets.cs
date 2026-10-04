using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Deterministic per-hand target cells that change every half bar. The target depends only on
/// (seed, section label, position in a 4-bar phrase), so a chorus that comes back gets the same
/// movement again — players recognise repeated music as repeated patterns.</summary>
public static class PhraseTargets
{
    const int PhraseBeats = 16;
    const int StepBeats = 2;

    // weighted like human maps: bottom row and the outer middle cell dominate, top row is occasional
    static readonly (int x, int y)[] RightCells = [(2, 0), (3, 1), (3, 0), (2, 0), (3, 1), (1, 0), (2, 2), (3, 2)];
    static readonly (int x, int y)[] LeftCells = [(1, 0), (0, 1), (0, 0), (1, 0), (0, 1), (2, 0), (1, 2), (0, 2)];

    public static Vec2 For(Hand hand, RhythmEvent e, int seed)
    {
        int step = (int)Math.Floor(((e.BeatInSection % PhraseBeats) + PhraseBeats) % PhraseBeats / StepBeats);
        ulong h = Hash((ulong)seed, StableHash(e.Section), (ulong)step, (ulong)hand);
        var cells = hand == Hand.Right ? RightCells : LeftCells;
        var (x, y) = cells[(int)(h % (ulong)cells.Length)];
        return new Vec2(x, y);
    }

    /// <summary>One Saber: the target wanders over both halves of the grid.</summary>
    public static Vec2 ForOneSaber(RhythmEvent e, int seed)
    {
        int step = (int)Math.Floor(((e.BeatInSection % PhraseBeats) + PhraseBeats) % PhraseBeats / StepBeats);
        ulong h = Hash((ulong)seed, StableHash(e.Section), (ulong)step, 7);
        var cells = (h >> 8) % 2 == 0 ? RightCells : LeftCells;
        var (x, y) = cells[(int)(h % (ulong)cells.Length)];
        return new Vec2(x, y);
    }

    // string.GetHashCode is randomized per process; maps must be reproducible for a given seed
    static ulong StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    static ulong Hash(params ulong[] parts)
    {
        ulong k = 0xCBF29CE484222325UL;
        foreach (var p in parts)
        {
            k ^= p + 0x9E3779B97F4A7C15UL + (k << 6) + (k >> 2);
            k *= 0x100000001B3UL;
        }
        k ^= k >> 33; k *= 0xFF51AFD7ED558CCDUL; k ^= k >> 33;
        return k;
    }
}
