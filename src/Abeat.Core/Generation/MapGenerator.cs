using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <param name="HandRoleShare">Share of melody/rhythm-led single notes played by their role's hand.</param>
public sealed record GeneratedDifficulty(DifficultyMap Map, IReadOnlyList<RhythmEvent> Events, FlowReport Report, double HandRoleShare);

public sealed record GenerationResult(MapSet Map, IReadOnlyList<GeneratedDifficulty> Difficulties);

public static class MapGenerator
{
    public static GenerationResult Generate(SongAnalysis a, GeneratorSettings s)
    {
        var map = new MapSet
        {
            SongName = a.Source.Title,
            SongAuthor = a.Source.Artist,
            LevelAuthor = s.LevelAuthor,
            Bpm = a.Tempo.Bpm,
            PreviewStart = a.Preview.StartSec,
            PreviewDuration = a.Preview.DurationSec,
            SongFile = a.Audio.File,
            CoverFile = a.Cover,
        };
        var results = s.Difficulties.Distinct().Order().AsParallel().AsOrdered()
            .Select(d => GenerateDifficulty(a, s, d)).ToList();
        map.Difficulties.AddRange(results.Select(r => r.Map));
        return new GenerationResult(map, results);
    }

    public static GeneratedDifficulty GenerateDifficulty(SongAnalysis a, GeneratorSettings s, DifficultyName d)
    {
        var p = s.Profile(d);
        var events = RhythmSelector.Select(a, p, s);
        var model = new SwingCostModel(s.Weights);
        int seed = s.Seed + (int)d * 7919;
        var notes = new FlowPlanner(model, p, s.BeamWidth, seed).Plan(events);

        var dm = new DifficultyMap
        {
            Difficulty = d,
            NoteJumpSpeed = p.NoteJumpSpeed,
            NoteJumpOffset = JumpOffsetFor(a.Tempo.Bpm, p.NoteJumpSpeed, p.JumpDistance),
            Notes = notes,
        };
        if (s.AngleOffsets) Expression.ApplyAngleOffsets(dm, events, p);
        WallGenerator.Generate(a, dm, p, s);
        BombGenerator.Generate(dm, p, events, s, a.Tempo.Bpm);
        if (s.Arcs) Expression.AddArcs(dm, events, a.Tempo.Bpm);
        if (s.Lights) LightingGenerator.Generate(a, dm, events);
        var report = FlowAnalyzer.Analyze(dm, a.Tempo.Bpm, s.Weights, p.MinSameHandGapSec);
        return new GeneratedDifficulty(dm, events, report, HandRoleShare(events, notes, seed));
    }

    static double HandRoleShare(IReadOnlyList<RhythmEvent> events, List<ColorNote> notes, int seed)
    {
        var roles = FlowPlanner.HandRoles(events, seed);
        var hands = notes.GroupBy(n => n.Beat).ToDictionary(g => g.Key, g => g.Select(n => n.Hand).ToList());
        int total = 0, kept = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].IsDouble || roles[i] is not { } role || !hands.TryGetValue(events[i].Beat, out var h) || h.Count != 1) continue;
            total++;
            if (h[0] == role) kept++;
        }
        return total > 0 ? (double)kept / total : 0;
    }

    /// <summary>Beat Saber's half-jump duration: start at 4 beats, halve while the jump would exceed
    /// ~18 m, then add the offset. Solve for the offset that gives the wanted jump distance.</summary>
    public static double JumpOffsetFor(double bpm, double njs, double jumpDistance)
    {
        double beatSec = 60.0 / bpm;
        double hjd = 4;
        while (njs * beatSec * hjd > 17.999) hjd /= 2;
        double wanted = jumpDistance / (2 * njs * beatSec);
        double offset = Math.Max(0.25, wanted) - hjd;
        return Math.Round(offset * 4) / 4; // quarter-beat steps like editors use
    }

    public static double JumpDistance(double bpm, double njs, double offset)
    {
        double beatSec = 60.0 / bpm;
        double hjd = 4;
        while (njs * beatSec * hjd > 17.999) hjd /= 2;
        hjd = Math.Max(0.25, hjd + offset);
        return njs * beatSec * hjd * 2;
    }
}
