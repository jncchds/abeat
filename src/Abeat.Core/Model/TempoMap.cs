namespace Abeat.Core.Model;

/// <summary>A tempo change: from <see cref="Beat"/> (at <see cref="Time"/> seconds) on, beats last 60 / Bpm s.</summary>
public readonly record struct TempoPoint(double Beat, double Time, double Bpm);

/// <summary>Piecewise-constant tempo: converts between beats and seconds across BPM changes (v3
/// <c>bpmEvents</c>). The first point is beat 0 at t = 0; a single point is a constant tempo. A plain BPM
/// converts implicitly, so code written for constant tempo keeps working.</summary>
public sealed class TempoMap
{
    readonly TempoPoint[] points;

    public TempoMap(IEnumerable<(double Beat, double Bpm)> changes)
    {
        var list = new List<TempoPoint>();
        foreach (var (beat, bpm) in changes.Where(c => c.Bpm > 0).OrderBy(c => c.Beat))
        {
            if (list.Count == 0) { list.Add(new(0, 0, bpm)); continue; } // the first tempo holds from beat 0
            var last = list[^1];
            if (beat <= last.Beat + 1e-6) { list[^1] = last with { Bpm = bpm }; continue; }
            if (Math.Abs(bpm - last.Bpm) < 1e-6) continue;
            list.Add(new(beat, last.Time + (beat - last.Beat) * 60 / last.Bpm, bpm));
        }
        if (list.Count == 0) list.Add(new(0, 0, 120));
        points = [.. list];
    }

    public static TempoMap Constant(double bpm) => new([(0, bpm)]);

    public static implicit operator TempoMap(double bpm) => Constant(bpm);

    public IReadOnlyList<TempoPoint> Points => points;
    /// <summary>Tempo at beat 0 (what Info.dat states).</summary>
    public double Bpm => points[0].Bpm;
    public bool IsConstant => points.Length == 1;

    TempoPoint AtBeat(double beat)
    {
        int i = points.Length - 1;
        while (i > 0 && points[i].Beat > beat) i--;
        return points[i];
    }

    TempoPoint AtTime(double t)
    {
        int i = points.Length - 1;
        while (i > 0 && points[i].Time > t) i--;
        return points[i];
    }

    public double BeatToSeconds(double beat)
    {
        var p = AtBeat(beat);
        return p.Time + (beat - p.Beat) * 60 / p.Bpm;
    }

    public double SecondsToBeat(double t)
    {
        var p = AtTime(t);
        return p.Beat + (t - p.Time) * p.Bpm / 60;
    }

    public double BpmAtBeat(double beat) => AtBeat(beat).Bpm;
    public double BpmAt(double t) => AtTime(t).Bpm;

    /// <summary>Seconds between two beats.</summary>
    public double Seconds(double fromBeat, double toBeat) => BeatToSeconds(toBeat) - BeatToSeconds(fromBeat);

    /// <summary>Seconds per beat at a beat.</summary>
    public double SecPerBeat(double beat) => 60 / BpmAtBeat(beat);

    /// <summary>Same tempo changes (within 1e-6 beat / 1e-4 BPM).</summary>
    public bool SameAs(TempoMap o) => points.Length == o.points.Length
        && points.Zip(o.points).All(p => Math.Abs(p.First.Beat - p.Second.Beat) < 1e-6 && Math.Abs(p.First.Bpm - p.Second.Bpm) < 1e-4);

    public override string ToString() => IsConstant ? $"{Bpm:0.##} BPM" : $"{Bpm:0.##} BPM, {points.Length - 1} changes";
}
