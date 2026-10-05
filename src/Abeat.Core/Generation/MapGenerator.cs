using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <param name="HandRoleShare">Share of melody/rhythm-led single notes played by their role's hand.</param>
public sealed record GeneratedDifficulty(DifficultyMap Map, IReadOnlyList<RhythmEvent> Events, FlowReport Report, double HandRoleShare);

/// <param name="Difficulties">Standard difficulties.</param>
/// <param name="Extra">One Saber / 90° / 360° difficulties (<see cref="GeneratorSettings.Modes"/>).</param>
public sealed record GenerationResult(MapSet Map, IReadOnlyList<GeneratedDifficulty> Difficulties, IReadOnlyList<GeneratedDifficulty> Extra);

public static class MapGenerator
{
    public static GenerationResult Generate(SongAnalysis a, GeneratorSettings s)
    {
        var map = new MapSet
        {
            SongName = a.Source.Title,
            SongAuthor = a.Source.Artist,
            LevelAuthor = s.LevelAuthor,
            Tempo = a.TempoMap,
            PreviewStart = a.Preview.StartSec,
            PreviewDuration = a.Preview.DurationSec,
            SongFile = a.Audio.File,
            CoverFile = a.Cover,
            Environment = s.Lights && s.Environment == "Pyro" ? GroupLightshow.Environment : "DefaultEnvironment",
        };
        var results = s.Difficulties.Distinct().Order().AsParallel().AsOrdered()
            .Select(d => GenerateDifficulty(a, s, d)).ToList();
        map.Difficulties.AddRange(results.Select(r => r.Map));
        var extra = s.Modes.Intersect(Characteristic.Extra).OrderBy(m => Array.IndexOf(Characteristic.Extra, m))
            .SelectMany(m => results.Select(r => (m, r))).AsParallel().AsOrdered()
            .Select(x => x.m == Characteristic.OneSaber ? GenerateDifficulty(a, s, x.r.Map.Difficulty, oneSaber: true) : Rotated(a, s, x.r, x.m))
            .ToList();
        map.Difficulties.AddRange(extra.Select(r => r.Map));
        return new GenerationResult(map, results, extra);
    }

    /// <summary>90/360 degree version of a Standard difficulty: the same objects plus lane rotations.</summary>
    static GeneratedDifficulty Rotated(SongAnalysis a, GeneratorSettings s, GeneratedDifficulty standard, string mode)
    {
        var m = standard.Map;
        var dm = new DifficultyMap
        {
            Difficulty = m.Difficulty, Characteristic = mode, NoteJumpSpeed = m.NoteJumpSpeed, NoteJumpOffset = m.NoteJumpOffset,
            Notes = [.. m.Notes], Bombs = [.. m.Bombs], Arcs = [.. m.Arcs], Chains = [.. m.Chains], Obstacles = [.. m.Obstacles],
            Lights = [.. m.Lights], Boosts = [.. m.Boosts], GroupLights = [.. m.GroupLights], GroupRotations = [.. m.GroupRotations],
        };
        RotationGenerator.Generate(a, dm, mode == Characteristic.Degree360, s.Seed + (int)m.Difficulty * 31);
        return standard with { Map = dm };
    }

    /// <param name="oneSaber">One Saber mode: a single (right) saber, no doubles, sparser rhythm.</param>
    public static GeneratedDifficulty GenerateDifficulty(SongAnalysis a, GeneratorSettings s, DifficultyName d, bool oneSaber = false)
    {
        var p = s.Profile(d);
        if (oneSaber)
        {
            // one hand plays everything: its own recovery time between all notes, and a bit less of them
            p = p with { MinGapSec = Math.Max(p.MinGapSec, p.MinSameHandGapSec), BaseNps = p.BaseNps * 0.8, MaxNps = Math.Min(p.MaxNps, 0.9 / p.MinSameHandGapSec), DoubleRate = 0 };
            s = s with { Weights = s.Weights with { HandRole = 0, Crossover = 0 } };
        }
        var events = RhythmSelector.Select(a, p, s);
        if (oneSaber) events = [.. events.Select(e => e with { IsDouble = false })];
        var model = new SwingCostModel(s.Weights);
        int seed = s.Seed + (int)d * 7919;
        var notes = new FlowPlanner(model, p, s.BeamWidth, seed, oneSaber).Plan(events);
        if (s.Weights.Repetition > 0 && events.Select(e => e.Section).Distinct().Count() < events.Select(e => (e.Section, Math.Round(e.GridBeat - e.BeatInSection))).Distinct().Count())
            notes = new FlowPlanner(model, p, s.BeamWidth, seed, oneSaber).Plan(events, notes);

        var dm = new DifficultyMap
        {
            Difficulty = d,
            Characteristic = oneSaber ? Characteristic.OneSaber : Characteristic.Standard,
            NoteJumpSpeed = p.NoteJumpSpeed,
            NoteJumpOffset = JumpOffsetFor(a.Tempo.Bpm, p.NoteJumpSpeed, p.JumpDistance),
            Notes = notes,
        };
        if (s.AngleOffsets) Expression.ApplyAngleOffsets(dm, events, p);
        WallGenerator.Generate(a, dm, p, s);
        BombGenerator.Generate(dm, p, events, s, a.TempoMap);
        if (s.Arcs) Expression.AddArcs(dm, events, a.TempoMap);
        if (s.Chains) Expression.AddChains(dm, events, a.TempoMap, p);
        if (s.Lights) LightingGenerator.Generate(a, dm, events);
        if (s.Lights && s.Environment == "Pyro") GroupLightshow.Generate(a, dm, events);
        var report = FlowAnalyzer.Analyze(dm, a.TempoMap, s.Weights, p.MinSameHandGapSec);
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
