using Abeat.Core.Analysis;

namespace Abeat.Core.Generation;

/// <summary>A moment that gets a note (or two, for doubles).</summary>
public sealed record RhythmEvent
{
    public double Beat { get; init; }
    public double Time { get; init; }
    /// <summary>Combined onset score, normalized 0..1 within the song.</summary>
    public double Strength { get; init; }
    public double Brightness { get; init; }
    public double Energy { get; init; }
    public string Layer { get; init; } = "";
    public bool IsDouble { get; init; }
    /// <summary>Label of the section (A, B, ...); repeated parts share a label.</summary>
    public string Section { get; init; } = "A";
    /// <summary>Beats since the start of the section (snapped to the section's first downbeat).</summary>
    public double BeatInSection { get; init; }
}

/// <summary>Chooses which onsets become notes: snap onsets to the beat grid, score grid slots by layer
/// weights, metric position and energy, then pick slots bar by bar to hit a per-section density target.</summary>
public static class RhythmSelector
{
    /// <summary>Slots are 1/12 beat, so both sixteenths (3 slots) and eighth-triplets (4 slots) are exact.</summary>
    public const int SlotsPerBeat = 12;

    sealed class Slot
    {
        public int Index;
        public double Score;
        public double BrightnessSum, BrightnessWeight;
        public readonly Dictionary<string, double> ByLayer = [];
    }

    public static List<RhythmEvent> Select(SongAnalysis a, DifficultyProfile p, GeneratorSettings s)
    {
        var slots = BuildSlots(a, p, s);
        if (slots.Count == 0) return [];

        double maxScore = slots.Values.Max(x => x.Score);
        double secPerSlot = a.BeatToSeconds(1.0 / SlotsPerBeat);
        int minGapSlots = (int)Math.Ceiling(p.MinGapSec / secPerSlot - 1e-6);
        int grid = SlotsPerBeat / p.Subdivision;
        minGapSlots = Math.Max(minGapSlots, p.AllowTriplets ? Math.Min(grid, SlotsPerBeat / 3) : grid);

        var accepted = new SortedSet<int>();
        double carry = 0;
        int barSlots = 4 * SlotsPerBeat;
        int phase = a.DownbeatPhase * SlotsPerBeat;
        int lastSlot = slots.Keys.Max();
        var rng = new Random(s.Seed);

        for (int barStart = phase - barSlots; barStart <= lastSlot; barStart += barSlots)
        {
            double t0 = a.BeatToSeconds((double)barStart / SlotsPerBeat);
            double t1 = a.BeatToSeconds((double)(barStart + barSlots) / SlotsPerBeat);
            var section = a.SectionAt(Math.Max(0, (t0 + t1) / 2));
            // blend section energy with local energy so builds ramp up inside a section
            double energy = 0.5 * section.Energy + 0.5 * a.EnergyAt((t0 + t1) / 2);
            double nps = Math.Min(p.MaxNps, p.BaseNps * s.Density * (0.35 + 1.0 * energy));
            double target = nps * (t1 - t0) / (1 + p.DoubleRate) + carry;
            int count = (int)Math.Floor(target);
            carry = target - count;

            var candidates = slots.Values
                .Where(x => x.Index >= barStart && x.Index < barStart + barSlots && x.Score > 0.08 * maxScore)
                .OrderByDescending(x => x.Score + rng.NextDouble() * 1e-3)
                .ToList();
            int taken = 0;
            foreach (var c in candidates)
            {
                if (taken >= count) break;
                if (accepted.GetViewBetween(c.Index - minGapSlots + 1, c.Index + minGapSlots - 1).Count > 0) continue;
                accepted.Add(c.Index);
                taken++;
            }
            if (taken < count) carry += Math.Min(count - taken, 2); // let a sparse bar donate a little to the next
        }

        var events = accepted.Select(i =>
        {
            var sl = slots[i];
            double beat = (double)i / SlotsPerBeat;
            double t = a.BeatToSeconds(beat);
            var sec = a.SectionAt(t);
            double secBeat = Math.Round(a.SecondsToBeat(sec.Start));
            return new RhythmEvent
            {
                Section = sec.Label,
                BeatInSection = beat - secBeat,
                Beat = beat,
                Time = t,
                Strength = sl.Score / maxScore,
                Brightness = sl.BrightnessWeight > 0 ? sl.BrightnessSum / sl.BrightnessWeight : 0.5,
                Energy = a.EnergyAt(t),
                Layer = sl.ByLayer.MaxBy(kv => kv.Value).Key,
            };
        }).ToList();

        return MarkDoubles(events, p);
    }

    static Dictionary<int, Slot> BuildSlots(SongAnalysis a, DifficultyProfile p, GeneratorSettings s)
    {
        var slots = new Dictionary<int, Slot>();
        int grid = SlotsPerBeat / p.Subdivision; // slots per grid step
        int downPhase = a.DownbeatPhase;
        bool triplets = p.AllowTriplets && HasTripletFeel(a);

        foreach (var (layer, onsets) in a.Layers)
        {
            if (!s.LayerWeights.TryGetValue(layer, out double w) || w <= 0) continue;
            // compare layers by rank, not raw strength: a vocal stem has a few sharp spikes and soft
            // syllables in between, so raw strengths made every vocal onset look weak next to drums
            var rank = RankStrengths(onsets);
            foreach (var o in onsets)
            {
                if (o.T < s.LeadInSec) continue;
                double beat = a.SecondsToBeat(o.T);
                double exact = beat * SlotsPerBeat;
                int straight = (int)Math.Round(exact / grid) * grid;
                double errStraight = Math.Abs(exact - straight) / grid; // 0..0.5 of a grid step
                int index = straight;
                double err = errStraight;
                if (triplets)
                {
                    int trip = (int)Math.Round(exact / 4) * 4; // 1/3 beat
                    double errTrip = Math.Abs(exact - trip) / 4;
                    if (errTrip < errStraight * 0.4 && errStraight > 0.25) { index = trip; err = errTrip; }
                }
                double contribution = w * rank[o] * (1 - Math.Min(1, err * 1.6));
                if (contribution <= 0) continue;
                if (!slots.TryGetValue(index, out var slot)) slots[index] = slot = new Slot { Index = index };
                slot.BrightnessSum += o.Br * contribution;
                slot.BrightnessWeight += contribution;
                slot.ByLayer[layer] = slot.ByLayer.GetValueOrDefault(layer) + contribution;
            }
        }

        // Strongest layer counts fully, the others only a little: summing everything made beats where
        // drums, bass and pads coincide outscore a lone vocal syllable even with vocals weighted highest.
        foreach (var slot in slots.Values)
        {
            double top = slot.ByLayer.Values.Max();
            slot.Score = top + 0.25 * (slot.ByLayer.Values.Sum() - top);
        }

        foreach (var slot in slots.Values)
        {
            int posInBeat = slot.Index % SlotsPerBeat;
            int beatIdx = slot.Index / SlotsPerBeat;
            double metric = posInBeat switch
            {
                0 => ((beatIdx - downPhase) % 4 + 4) % 4 == 0 ? 0.45 : ((beatIdx - downPhase) % 2 == 0 ? 0.3 : 0.25),
                6 => 0.1,
                _ => 0,
            };
            // human maps rarely use the "e" sixteenth, a bit more the "a" (a pickup into the beat);
            // weak hi-hat sixteenths otherwise rank like real accents
            double offGrid = posInBeat switch { 3 => 0.6, 9 => 0.8, 4 or 8 => 0.85, _ => 1 };
            double t = a.BeatToSeconds((double)slot.Index / SlotsPerBeat);
            slot.Score = (slot.Score + metric * Math.Min(1, slot.Score * 2)) * offGrid * (0.4 + 0.6 * a.EnergyAt(t));
        }
        return slots;
    }

    /// <summary>Whether the song has a triplet feel: a real share of the most percussive layer's onset
    /// strength sits on eighth-triplets, clearly more than on sixteenth off-beats. Straight songs keep
    /// only a few percent there (sloppy timing), and snapping those to triplets made random-looking
    /// rhythms.</summary>
    public static bool HasTripletFeel(SongAnalysis a)
    {
        var (trip, six) = TripletShares(a);
        return trip >= 0.12 && trip >= 1.5 * six;
    }

    /// <summary>Shares of onset strength within 1/48 beat of eighth-triplets and of sixteenth off-beats.</summary>
    public static (double Triplet, double Sixteenth) TripletShares(SongAnalysis a)
    {
        var onsets = new[] { "drums", "high", "full", "mix" }.Select(k => a.Layers.GetValueOrDefault(k)).FirstOrDefault(l => l is { Count: > 0 });
        if (onsets is null) return (0, 0);
        double trip = 0, six = 0, total = 0;
        foreach (var o in onsets)
        {
            double ph = a.SecondsToBeat(o.T);
            static bool Near(double ph, double c) => Math.Abs(((ph - c) % 1 + 1.5) % 1 - 0.5) < 1.0 / 48;
            total += o.S;
            if (Near(ph, 1.0 / 3) || Near(ph, 2.0 / 3)) trip += o.S;
            else if (Near(ph, 0.25) || Near(ph, 0.75)) six += o.S;
        }
        return total > 0 ? (trip / total, six / total) : (0, 0);
    }

    /// <summary>Strength as 0.25..1 by rank within the layer.</summary>
    static Dictionary<Onset, double> RankStrengths(List<Onset> onsets)
    {
        var order = onsets.OrderBy(o => o.S).ToList();
        var rank = new Dictionary<Onset, double>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = 0.25 + 0.75 * (i + 1) / order.Count;
        return rank;
    }

    static List<RhythmEvent> MarkDoubles(List<RhythmEvent> events, DifficultyProfile p)
    {
        if (events.Count < 3 || p.DoubleRate <= 0) return events;
        int want = (int)Math.Round(events.Count * p.DoubleRate);
        // both hands hit a double, so each needs its own same-hand recovery time around it
        double minGap = p.MinSameHandGapSec;
        var order = Enumerable.Range(0, events.Count)
            .Where(i => (i == 0 || events[i].Time - events[i - 1].Time >= minGap)
                     && (i == events.Count - 1 || events[i + 1].Time - events[i].Time >= minGap))
            .OrderByDescending(i => events[i].Strength * (0.5 + events[i].Energy))
            .Take(want)
            .ToHashSet();
        return events.Select((e, i) => order.Contains(i) ? e with { IsDouble = true } : e).ToList();
    }
}
