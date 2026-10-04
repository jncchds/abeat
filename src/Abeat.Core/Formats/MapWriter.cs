using System.Text.Json;
using System.Text.Json.Nodes;
using Abeat.Core.Model;

namespace Abeat.Core.Formats;

/// <summary>Writes Info.dat (v2.1.0, the most widely supported) and difficulties in beatmap v3.3.0.</summary>
public static class MapWriter
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void WriteFolder(MapSet map, string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Info.dat"), InfoJson(map).ToJsonString(Indented));
        foreach (var d in map.Difficulties)
            File.WriteAllText(Path.Combine(folder, d.FileName), DifficultyJson(d, map.Tempo).ToJsonString());
    }

    public static JsonObject InfoJson(MapSet map) => new()
    {
        ["_version"] = "2.1.0",
        ["_songName"] = map.SongName,
        ["_songSubName"] = map.SongSubName,
        ["_songAuthorName"] = map.SongAuthor,
        ["_levelAuthorName"] = map.LevelAuthor,
        ["_beatsPerMinute"] = Math.Round(map.Bpm, 4),
        ["_shuffle"] = 0,
        ["_shufflePeriod"] = 0.5,
        ["_previewStartTime"] = Math.Round(map.PreviewStart, 2),
        ["_previewDuration"] = Math.Round(map.PreviewDuration, 2),
        ["_songFilename"] = map.SongFile,
        ["_coverImageFilename"] = map.CoverFile,
        ["_environmentName"] = map.Environment,
        ["_allDirectionsEnvironmentName"] = "GlassDesertEnvironment",
        ["_songTimeOffset"] = 0,
        ["_customData"] = new JsonObject { ["_generator"] = "ABeat by CHDS" },
        ["_difficultyBeatmapSets"] = new JsonArray
        {
            new JsonObject
            {
                ["_beatmapCharacteristicName"] = "Standard",
                ["_difficultyBeatmaps"] = new JsonArray(map.Difficulties
                    .OrderBy(d => d.Difficulty)
                    .Select(d => (JsonNode)new JsonObject
                    {
                        ["_difficulty"] = d.Difficulty.ToString(),
                        ["_difficultyRank"] = DifficultyMap.Rank(d.Difficulty),
                        ["_beatmapFilename"] = d.FileName,
                        ["_noteJumpMovementSpeed"] = Math.Round(d.NoteJumpSpeed, 2),
                        ["_noteJumpStartBeatOffset"] = Math.Round(d.NoteJumpOffset, 3),
                    }).ToArray()),
            },
        },
    };

    static double B(double beat) => Math.Round(beat, 5);

    public static JsonObject DifficultyJson(DifficultyMap d, TempoMap? tempo = null) => new()
    {
        ["version"] = "3.3.0",
        // the first tempo is Info.dat's BPM; only the changes after beat 0 are events
        ["bpmEvents"] = new JsonArray((tempo?.Points.Skip(1) ?? []).Select(p => (JsonNode)new JsonObject
        {
            ["b"] = B(p.Beat), ["m"] = Math.Round(p.Bpm, 4),
        }).ToArray()),
        ["rotationEvents"] = new JsonArray(),
        ["colorNotes"] = new JsonArray(d.Notes.OrderBy(n => n.Beat).Select(n => (JsonNode)new JsonObject
        {
            ["b"] = B(n.Beat), ["x"] = n.X, ["y"] = n.Y, ["c"] = (int)n.Hand, ["d"] = (int)n.Direction, ["a"] = n.AngleOffset,
        }).ToArray()),
        ["bombNotes"] = new JsonArray(d.Bombs.OrderBy(n => n.Beat).Select(n => (JsonNode)new JsonObject
        {
            ["b"] = B(n.Beat), ["x"] = n.X, ["y"] = n.Y,
        }).ToArray()),
        ["obstacles"] = new JsonArray(d.Obstacles.OrderBy(o => o.Beat).Select(o => (JsonNode)new JsonObject
        {
            ["b"] = B(o.Beat), ["x"] = o.X, ["y"] = o.Y, ["d"] = B(o.Duration), ["w"] = o.Width, ["h"] = o.Height,
        }).ToArray()),
        ["sliders"] = new JsonArray(d.Arcs.OrderBy(a => a.Beat).Select(a => (JsonNode)new JsonObject
        {
            ["b"] = B(a.Beat), ["c"] = (int)a.Hand, ["x"] = a.X, ["y"] = a.Y, ["d"] = (int)a.Direction, ["mu"] = Math.Round(a.HeadMultiplier, 3),
            ["tb"] = B(a.TailBeat), ["tx"] = a.TailX, ["ty"] = a.TailY, ["tc"] = (int)a.TailDirection, ["tmu"] = Math.Round(a.TailMultiplier, 3), ["m"] = a.MidAnchor,
        }).ToArray()),
        ["burstSliders"] = new JsonArray(d.Chains.OrderBy(c => c.Beat).Select(c => (JsonNode)new JsonObject
        {
            ["b"] = B(c.Beat), ["c"] = (int)c.Hand, ["x"] = c.X, ["y"] = c.Y, ["d"] = (int)c.Direction,
            ["tb"] = B(c.TailBeat), ["tx"] = c.TailX, ["ty"] = c.TailY, ["sc"] = c.Segments, ["s"] = Math.Round(c.Squish, 3),
        }).ToArray()),
        ["waypoints"] = new JsonArray(),
        ["basicBeatmapEvents"] = new JsonArray(d.Lights.OrderBy(e => e.Beat).Select(e => (JsonNode)new JsonObject
        {
            ["b"] = B(e.Beat), ["et"] = e.Type, ["i"] = e.Value, ["f"] = Math.Round(e.Brightness, 3),
        }).ToArray()),
        ["colorBoostBeatmapEvents"] = new JsonArray(d.Boosts.OrderBy(e => e.Beat).Select(e => (JsonNode)new JsonObject
        {
            ["b"] = B(e.Beat), ["o"] = e.On,
        }).ToArray()),
        ["lightColorEventBoxGroups"] = new JsonArray(),
        ["lightRotationEventBoxGroups"] = new JsonArray(),
        ["lightTranslationEventBoxGroups"] = new JsonArray(),
        ["basicEventTypesWithKeywords"] = new JsonObject { ["d"] = new JsonArray() },
        ["useNormalEventsAsCompatibleEvents"] = true,
    };
}
