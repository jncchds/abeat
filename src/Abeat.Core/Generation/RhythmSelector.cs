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

        foreach (var (layer, onsets) in a.Layers)
        {
            if (!s.LayerWeights.TryGetValue(layer, out double w) || w <= 0) continue;
            foreach (var o in onsets)
            {
                if (o.T < s.LeadInSec) continue;
                double beat = a.SecondsToBeat(o.T);
                double exact = beat * SlotsPerBeat;
                int straight = (int)Math.Round(exact / grid) * grid;
                double errStraight = Math.Abs(exact - straight) / grid; // 0..0.5 of a grid step
                int index = straight;
                double err = errStraight;
                if (p.AllowTriplets)
                {
                    int trip = (int)Math.Round(exact / 4) * 4; // 1/3 beat
                    double errTrip = Math.Abs(exact - trip) / 4;
                    if (errTrip < errStraight * 0.4 && errStraight > 0.25) { index = trip; err = errTrip; }
                }
                double contribution = w * o.S * (1 - Math.Min(1, err * 1.6));
                if (contribution <= 0) continue;
                if (!slots.TryGetValue(index, out var slot)) slots[index] = slot = new Slot { Index = index };
                slot.Score += contribution;
                slot.BrightnessSum += o.Br * contribution;
                slot.BrightnessWeight += contribution;
                slot.ByLayer[layer] = slot.ByLayer.GetValueOrDefault(layer) + contribution;
            }
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
            double t = a.BeatToSeconds((double)slot.Index / SlotsPerBeat);
            slot.Score = (slot.Score + metric * Math.Min(1, slot.Score * 2)) * (0.4 + 0.6 * a.EnergyAt(t));
        }
        return slots;
    }

    static List<RhythmEvent> MarkDoubles(List<RhythmEvent> events, DifficultyProfile p)
    {
        if (events.Count < 3 || p.DoubleRate <= 0) return events;
        int want = (int)Math.Round(events.Count * p.DoubleRate);
        double minGap = Math.Max(p.MinSameHandGapSec, 0.3);
        var order = Enumerable.Range(0, events.Count)
            .Where(i => (i == 0 || events[i].Time - events[i - 1].Time >= minGap)
                     && (i == events.Count - 1 || events[i + 1].Time - events[i].Time >= minGap))
            .OrderByDescending(i => events[i].Strength * (0.5 + events[i].Energy))
            .Take(want)
            .ToHashSet();
        return events.Select((e, i) => order.Contains(i) ? e with { IsDouble = true } : e).ToList();
    }
}
