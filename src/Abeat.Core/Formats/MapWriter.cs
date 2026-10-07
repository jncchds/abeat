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
        ["_difficultyBeatmapSets"] = new JsonArray(map.Difficulties
            .GroupBy(d => d.Characteristic)
            .OrderBy(g => Array.IndexOf(CharacteristicOrder, g.Key))
            .Select(g => (JsonNode)new JsonObject
            {
                ["_beatmapCharacteristicName"] = g.Key,
                ["_difficultyBeatmaps"] = new JsonArray(g
                    .OrderBy(d => d.Difficulty)
                    .Select(d => (JsonNode)new JsonObject
                    {
                        ["_difficulty"] = d.Difficulty.ToString(),
                        ["_difficultyRank"] = DifficultyMap.Rank(d.Difficulty),
                        ["_beatmapFilename"] = d.FileName,
                        ["_noteJumpMovementSpeed"] = Math.Round(d.NoteJumpSpeed, 2),
                        ["_noteJumpStartBeatOffset"] = Math.Round(d.NoteJumpOffset, 3),
                    }).ToArray()),
            }).ToArray()),
    };

    static readonly string[] CharacteristicOrder = [Characteristic.Standard, .. Characteristic.Extra];

    static double B(double beat) => Math.Round(beat, 5);

    /// <summary>Rotation/translation box events: hold the previous value at the box's beat, then ease
    /// (in-out quad) to the target over the duration. Value key "r" degrees or "t" distance.</summary>
    static JsonArray Motion(double duration, double value, string key)
    {
        JsonObject Ev(double b, int p, int e, double v)
        {
            var o = new JsonObject { ["b"] = B(b), ["p"] = p, ["e"] = e, [key] = v };
            if (key == "r") { o["l"] = 0; o["o"] = 0; } // rotation events also carry loops and direction
            return o;
        }
        return duration <= 0.001 ? new JsonArray(Ev(0, 0, -1, value)) : new JsonArray(Ev(0, 1, 0, 0), Ev(duration, 0, 3, value));
    }

    public static JsonObject DifficultyJson(DifficultyMap d, TempoMap? tempo = null) => new()
    {
        ["version"] = "3.3.0",
        // the first tempo is Info.dat's BPM; only the changes after beat 0 are events
        ["bpmEvents"] = new JsonArray((tempo?.Points.Skip(1) ?? []).Select(p => (JsonNode)new JsonObject
        {
            ["b"] = B(p.Beat), ["m"] = Math.Round(p.Bpm, 4),
        }).ToArray()),
        ["rotationEvents"] = new JsonArray(d.Rotations.OrderBy(r => r.Beat).Select(r => (JsonNode)new JsonObject
        {
            ["b"] = B(r.Beat), ["e"] = r.Early ? 0 : 1, ["r"] = Math.Round(r.Degrees, 2),
        }).ToArray()),
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
        ["lightColorEventBoxGroups"] = new JsonArray(d.GroupLights.OrderBy(g => g.Beat).ThenBy(g => g.Group).Select(g => (JsonNode)new JsonObject
        {
            ["b"] = B(g.Beat), ["g"] = g.Group,
            ["e"] = new JsonArray(g.Boxes.Select(x => (JsonNode)new JsonObject
            {
                ["f"] = new JsonObject { ["f"] = 1, ["p"] = x.Sections, ["t"] = x.Part, ["r"] = x.Reverse ? 1 : 0 },
                ["w"] = B(x.BeatSpread), ["d"] = 1, ["r"] = Math.Round(x.BrightnessSpread, 3), ["t"] = 1, ["b"] = 0,
                ["e"] = new JsonArray(x.Events.Select(e => (JsonNode)new JsonObject
                {
                    ["b"] = B(e.Beat), ["i"] = e.Transition, ["c"] = e.Color, ["s"] = Math.Round(e.Brightness, 3), ["f"] = e.Strobe,
                }).ToArray()),
            }).ToArray()),
        }).ToArray()),
        ["lightRotationEventBoxGroups"] = new JsonArray(d.GroupRotations.OrderBy(g => g.Beat).ThenBy(g => g.Group).Select(r => (JsonNode)new JsonObject
        {
            ["b"] = B(r.Beat), ["g"] = r.Group,
            ["e"] = new JsonArray(new JsonObject
            {
                ["f"] = new JsonObject { ["f"] = 1, ["p"] = r.Sections, ["t"] = r.Part, ["r"] = 0 },
                ["w"] = B(r.BeatSpread), ["d"] = 1, ["s"] = Math.Round(r.Spread, 2), ["t"] = 1, ["b"] = 1, ["a"] = r.Axis, ["r"] = 0,
                // rotation boxes list their events under "l" (colour boxes use "e"); the first event holds
                // the previous angle so the eased turn starts at this beat, not at the previous event
                ["l"] = Motion(r.Duration, Math.Round(r.Degrees, 1), "r"),
            }),
        }).ToArray()),
        ["lightTranslationEventBoxGroups"] = new JsonArray(d.GroupTranslations.OrderBy(g => g.Beat).ThenBy(g => g.Group).Select(t => (JsonNode)new JsonObject
        {
            ["b"] = B(t.Beat), ["g"] = t.Group,
            ["e"] = new JsonArray(new JsonObject
            {
                ["f"] = new JsonObject { ["f"] = 1, ["p"] = 1, ["t"] = 0, ["r"] = 0 },
                ["w"] = B(t.BeatSpread), ["d"] = 1, ["s"] = Math.Round(t.Spread, 3), ["t"] = 1, ["b"] = 1, ["a"] = t.Axis, ["r"] = 0,
                ["l"] = Motion(t.Duration, Math.Round(t.Distance, 3), "t"),
            }),
        }).ToArray()),
        ["basicEventTypesWithKeywords"] = new JsonObject { ["d"] = new JsonArray() },
        ["useNormalEventsAsCompatibleEvents"] = true,
    };
}
