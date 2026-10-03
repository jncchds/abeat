using System.IO.Compression;
using System.Text.Json.Nodes;
using Abeat.Core.Model;

namespace Abeat.Core.Formats;

/// <summary>Reads existing maps (folder, zip or Info.dat path). Supports Info v2/v4 and difficulty
/// v2/v3/v4 notes, bombs and obstacles; enough to evaluate flow of human-made maps.</summary>
public static class MapReader
{
    public static MapSet Read(string path)
    {
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            return Read(name =>
            {
                var e = zip.Entries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (e == null) return null;
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            });
        }
        string folder = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : path;
        return Read(name =>
        {
            var f = Directory.EnumerateFiles(folder).FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase));
            return f == null ? null : File.ReadAllText(f);
        });
    }

    static MapSet Read(Func<string, string?> open)
    {
        var infoText = open("Info.dat") ?? throw new FileNotFoundException("Info.dat not found");
        var info = JsonNode.Parse(infoText)!.AsObject();
        var map = new MapSet();
        var diffs = new List<(DifficultyName name, string file, double njs, double ofs)>();

        if (info["_version"] != null || info["_songName"] != null)
        {
            map.SongName = (string?)info["_songName"] ?? "";
            map.SongAuthor = (string?)info["_songAuthorName"] ?? "";
            map.LevelAuthor = (string?)info["_levelAuthorName"] ?? "";
            map.Bpm = (double?)info["_beatsPerMinute"] ?? 120;
            foreach (var set in info["_difficultyBeatmapSets"]?.AsArray() ?? [])
            {
                if ((string?)set!["_beatmapCharacteristicName"] != "Standard") continue;
                foreach (var d in set["_difficultyBeatmaps"]!.AsArray())
                    diffs.Add((Enum.Parse<DifficultyName>((string)d!["_difficulty"]!), (string)d["_beatmapFilename"]!,
                        (double?)d["_noteJumpMovementSpeed"] ?? 0, (double?)d["_noteJumpStartBeatOffset"] ?? 0));
            }
        }
        else // Info v4
        {
            map.SongName = (string?)info["song"]?["title"] ?? "";
            map.SongAuthor = (string?)info["song"]?["author"] ?? "";
            map.Bpm = (double?)info["audio"]?["bpm"] ?? 120;
            foreach (var d in info["difficultyBeatmaps"]?.AsArray() ?? [])
            {
                if ((string?)d!["characteristic"] != "Standard") continue;
                diffs.Add((Enum.Parse<DifficultyName>((string)d["difficulty"]!), (string)d["beatmapDataFilename"]!,
                    (double?)d["noteJumpMovementSpeed"] ?? 0, (double?)d["noteJumpStartBeatOffset"] ?? 0));
            }
        }

        foreach (var (name, file, njs, ofs) in diffs)
        {
            var text = open(file);
            if (text == null) continue;
            var dm = ReadDifficulty(JsonNode.Parse(text)!.AsObject(), name);
            dm.NoteJumpSpeed = njs;
            dm.NoteJumpOffset = ofs;
            map.Difficulties.Add(dm);
        }
        return map;
    }

    public static DifficultyMap ReadDifficulty(JsonObject j, DifficultyName name)
    {
        var dm = new DifficultyMap { Difficulty = name };
        string version = (string?)j["version"] ?? (string?)j["_version"] ?? "2.0.0";

        if (version.StartsWith('2'))
        {
            foreach (var n in j["_notes"]?.AsArray() ?? [])
            {
                int type = Int(n, "_type");
                double b = Num(n, "_time");
                int x = Int(n, "_lineIndex"), y = Int(n, "_lineLayer");
                if (type == 3) dm.Bombs.Add(new BombNote(b, x, y));
                else if (type is 0 or 1) dm.Notes.Add(new ColorNote(b, x, y, (Hand)type, (CutDirection)Int(n, "_cutDirection")));
            }
            foreach (var o in j["_obstacles"]?.AsArray() ?? [])
            {
                int type = Int(o, "_type");
                dm.Obstacles.Add(new Obstacle(Num(o, "_time"), Num(o, "_duration"), Int(o, "_lineIndex"),
                    type == 1 ? 2 : 0, Int(o, "_width"), type == 1 ? 3 : 5));
            }
        }
        else if (version.StartsWith('3'))
        {
            foreach (var n in j["colorNotes"]?.AsArray() ?? [])
                dm.Notes.Add(new ColorNote(Num(n, "b"), Int(n, "x"), Int(n, "y"), (Hand)Int(n, "c"), (CutDirection)Int(n, "d"), Int(n, "a")));
            foreach (var n in j["bombNotes"]?.AsArray() ?? [])
                dm.Bombs.Add(new BombNote(Num(n, "b"), Int(n, "x"), Int(n, "y")));
            foreach (var o in j["obstacles"]?.AsArray() ?? [])
                dm.Obstacles.Add(new Obstacle(Num(o, "b"), Num(o, "d"), Int(o, "x"), Int(o, "y"), Int(o, "w"), Int(o, "h")));
        }
        else // v4: objects reference shared data arrays by index
        {
            var nd = j["colorNotesData"]?.AsArray() ?? [];
            foreach (var n in j["colorNotes"]?.AsArray() ?? [])
            {
                var d = nd[Int(n, "i")]!;
                dm.Notes.Add(new ColorNote(Num(n, "b"), Int(d, "x"), Int(d, "y"), (Hand)Int(d, "c"), (CutDirection)Int(d, "d"), Int(d, "a")));
            }
            var bd = j["bombNotesData"]?.AsArray() ?? [];
            foreach (var n in j["bombNotes"]?.AsArray() ?? [])
            {
                var d = bd[Int(n, "i")]!;
                dm.Bombs.Add(new BombNote(Num(n, "b"), Int(d, "x"), Int(d, "y")));
            }
            var od = j["obstaclesData"]?.AsArray() ?? [];
            foreach (var o in j["obstacles"]?.AsArray() ?? [])
            {
                var d = od[Int(o, "i")]!;
                dm.Obstacles.Add(new Obstacle(Num(o, "b"), Num(d, "d"), Int(d, "x"), Int(d, "y"), Int(d, "w"), Int(d, "h")));
            }
        }
        dm.Notes.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        return dm;
    }

    // v3/v4 omit fields that equal their default (0)
    static double Num(JsonNode? n, string k) => n?[k] is JsonValue v ? v.GetValue<double>() : 0;
    static int Int(JsonNode? n, string k) => (int)Math.Round(Num(n, k));
}
