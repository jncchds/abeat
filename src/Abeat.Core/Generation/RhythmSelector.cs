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
    /// <summary>Seconds the melody note keeps sounding after the onset (0 for percussive layers):
    /// until the layer's next onset, the end of the sung word, or the energy falling away.</summary>
    public double Sustain { get; init; }
    /// <summary>Brightness (pitch) change from the previous note of the same melody layer, -1..1.</summary>
    public double PitchSlope { get; init; }
    /// <summary>How hard the moment hits relative to the rest of the song, 0..1 by rank of strength and
    /// energy (0.5 = median), so it drives swing size without shifting the overall cell mix.</summary>
    public double Intensity { get; init; } = 0.5;
    /// <summary>Onsets in a fast run (a roll, flam or stutter, faster than sixteenths) right after this
    /// event and before the next one: sound that is too quick for single notes, drawn as a chain.</summary>
    public int BurstCount { get; init; }
    /// <summary>Seconds from the event to the last onset of that run.</summary>
    public double BurstSec { get; init; }
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
        var drops = s.DropPause ? DropSlots(a, slots) : [];
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
        ClearBeforeDrops(a, p, accepted, drops);

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

        events = AddExpression(a, events);
        var forced = drops.Select(d => events.FindIndex(e => Math.Abs(e.Beat - (double)d / SlotsPerBeat) < 1e-6)).Where(i => i >= 0).ToHashSet();
        return MarkDoubles(events, p, forced);
    }

    public static readonly HashSet<string> MelodyLayers = ["vocals", "other", "mid"];

    /// <summary>Sustain, pitch slope and intensity per event (see <see cref="RhythmEvent"/>).</summary>
    static List<RhythmEvent> AddExpression(SongAnalysis a, List<RhythmEvent> events)
    {
        var onsetTimes = a.Layers.ToDictionary(kv => kv.Key, kv => kv.Value.Select(o => o.T).Order().ToArray());
        var words = a.VocalSource == "lyrics" ? a.Lyrics?.Words ?? [] : [];
        var lastByLayer = new Dictionary<string, RhythmEvent>();
        var raw = events.Select(e => 0.6 * e.Strength + 0.4 * e.Energy).ToList();
        var order = Enumerable.Range(0, events.Count).OrderBy(i => raw[i]).ToList();
        var intensity = new double[events.Count];
        for (int r = 0; r < order.Count; r++) intensity[order[r]] = order.Count > 1 ? (double)r / (order.Count - 1) : 0.5;

        var result = new List<RhythmEvent>(events.Count);
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            double sustain = 0, slope = 0;
            if (MelodyLayers.Contains(e.Layer) && onsetTimes.TryGetValue(e.Layer, out var times))
            {
                sustain = Sustain(a, e.Time, times, words);
                if (lastByLayer.TryGetValue(e.Layer, out var prev) && e.Time - prev.Time < 1.5)
                    slope = Math.Clamp(e.Brightness - prev.Brightness, -1, 1);
                lastByLayer[e.Layer] = e;
            }
            double nextT = i + 1 < events.Count ? events[i + 1].Time : double.PositiveInfinity;
            var (count, burst) = Burst(a, e, nextT, onsetTimes);
            result.Add(e with { Sustain = sustain, PitchSlope = slope, Intensity = intensity[i], BurstCount = count, BurstSec = burst });
        }
        return result;
    }

    static readonly string[] BurstLayers = ["drums", "high", "full"];

    /// <summary>The fast run of onsets that follows an event in its own layer or the drums: each onset
    /// within ~a 32nd-ish spacing (under 0.85 of a sixteenth and 100 ms) of the previous, so steady
    /// sixteenth hats never count. Stops 50 ms before the next event.</summary>
    static (int count, double sec) Burst(SongAnalysis a, RhythmEvent e, double nextT, Dictionary<string, double[]> onsetTimes)
    {
        double maxStep = Math.Min(0.1, 0.85 * a.BeatToSeconds(0.25));
        int best = 0; double bestSec = 0;
        foreach (var layer in BurstLayers.Prepend(e.Layer).Distinct())
        {
            if (!onsetTimes.TryGetValue(layer, out var times)) continue;
            int idx = Array.BinarySearch(times, e.Time + 0.025);
            if (idx < 0) idx = ~idx;
            double last = e.Time; int count = 0;
            for (; idx < times.Length && times[idx] < nextT - 0.05 && times[idx] - last <= maxStep; idx++)
            {
                last = times[idx];
                count++;
            }
            if (count > best) { best = count; bestSec = last - e.Time; }
        }
        return (best, bestSec);
    }

    /// <summary>Time until the layer's next onset, cut short where the sung word ends (lyrics) or the
    /// energy drops below 60 % of its level just after the onset.</summary>
    static double Sustain(SongAnalysis a, double t, double[] times, List<LyricWord> words)
    {
        const double maxSec = 4;
        int idx = Array.BinarySearch(times, t + 0.12); // past the onset this event was snapped from
        if (idx < 0) idx = ~idx;
        double end = Math.Min(t + maxSec, idx < times.Length ? times[idx] : a.Audio.DurationSec);
        if (words.Count > 0)
        {
            var w = words.FirstOrDefault(w => t >= w.T - 0.15 && t < w.E);
            if (w == null) return 0;
            if (words.FirstOrDefault(x => x.T > w.T) is not { } next || next.T > w.E + 0.05) end = Math.Min(end, w.E);
        }
        double level = a.EnergyAt(t + 0.1);
        for (double x = t + 0.2; x < end; x += a.Energy.HopSec)
            if (a.EnergyAt(x) < 0.6 * level) return x - t;
        return end - t;
    }

    /// <summary>Downbeats where the energy jumps into a loud part (a drop), one per 16 beats at most,
    /// as slot indices. A slot is created when no onset landed on the downbeat.</summary>
    static List<int> DropSlots(SongAnalysis a, Dictionary<int, Slot> slots)
    {
        double Mean(double t0, double t1)
        {
            double sum = 0; int n = 0;
            for (double x = Math.Max(0, t0); x <= t1; x += a.Energy.HopSec) { sum += a.EnergyAt(x); n++; }
            return n > 0 ? sum / n : 0;
        }
        var found = new List<(int slot, double jump)>();
        foreach (var d in a.Tempo.Downbeats)
        {
            if (d < 4) continue;
            double after = Mean(d + 0.05, d + 2), jump = after - Mean(d - 2, d - 0.15);
            if (jump >= 0.3 && after >= 0.6) found.Add(((int)Math.Round(a.SecondsToBeat(d)) * SlotsPerBeat, jump));
        }
        var picked = new List<int>();
        foreach (var (slot, _) in found.OrderByDescending(f => f.jump))
            if (picked.All(x => Math.Abs(x - slot) >= 16 * SlotsPerBeat)) picked.Add(slot);
        double maxScore = slots.Count > 0 ? slots.Values.Max(x => x.Score) : 1;
        string layer = new[] { "drums", "low", "full", "mix" }.FirstOrDefault(a.Layers.ContainsKey) ?? a.Layers.Keys.FirstOrDefault() ?? "full";
        foreach (int d in picked)
            if (!slots.ContainsKey(d)) slots[d] = new Slot { Index = d, Score = maxScore, ByLayer = { [layer] = maxScore } };
        picked.Sort();
        return picked;
    }

    /// <summary>A breath before each drop: no notes for about 0.9 s (1-2 beats) before it, a note on the
    /// drop itself (made a double later) and room for both hands to recover after it.</summary>
    static void ClearBeforeDrops(SongAnalysis a, DifficultyProfile p, SortedSet<int> accepted, List<int> drops)
    {
        int pause = Math.Clamp((int)Math.Round(a.SecondsToBeat(0.9)), 1, 2) * SlotsPerBeat;
        int after = (int)Math.Ceiling(a.SecondsToBeat(p.MinSameHandGapSec) * SlotsPerBeat - 1e-6);
        pause = Math.Max(pause, after); // both hands play the drop double, so both need their recovery time
        foreach (int d in drops)
        {
            accepted.RemoveWhere(i => i >= d - pause && i < d + after && i != d);
            accepted.Add(d);
        }
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

    static List<RhythmEvent> MarkDoubles(List<RhythmEvent> events, DifficultyProfile p, HashSet<int> forced)
    {
        if (events.Count < 3 || p.DoubleRate <= 0) return events.Select((e, i) => forced.Contains(i) ? e with { IsDouble = true } : e).ToList();
        int want = (int)Math.Round(events.Count * p.DoubleRate);
        // both hands hit a double, so each needs its own same-hand recovery time around it
        double minGap = p.MinSameHandGapSec;
        var order = Enumerable.Range(0, events.Count)
            .Where(i => (i == 0 || events[i].Time - events[i - 1].Time >= minGap)
                     && (i == events.Count - 1 || events[i + 1].Time - events[i].Time >= minGap))
            .OrderByDescending(i => events[i].Strength * (0.5 + events[i].Energy))
            .Take(want)
            .ToHashSet();
        order.UnionWith(forced);
        return events.Select((e, i) => order.Contains(i) ? e with { IsDouble = true } : e).ToList();
    }
}
