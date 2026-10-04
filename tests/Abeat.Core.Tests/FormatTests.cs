using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class FormatTests
{
    [Fact]
    public void V3DifficultyRoundTrips()
    {
        var dm = new DifficultyMap
        {
            Difficulty = DifficultyName.Expert,
            Notes = [new(1, 0, 0, Hand.Left, CutDirection.Down, 15), new(1.5, 3, 2, Hand.Right, CutDirection.UpRight)],
            Bombs = [new(2, 1, 1)],
            Obstacles = [new(4, 2, 0, 0, 1, 5)],
            Arcs = [new(1, 0, 0, Hand.Left, CutDirection.Down, 3, 1, 0, CutDirection.Up)],
            Chains = [new(5, 1, 2, Hand.Left, CutDirection.Down, 5.125, 1, 0, 7)],
        };
        var json = JsonNode.Parse(MapWriter.DifficultyJson(dm).ToJsonString())!.AsObject();
        var back = MapReader.ReadDifficulty(json, DifficultyName.Expert);
        Assert.Equal(dm.Notes, back.Notes);
        Assert.Equal(dm.Bombs, back.Bombs);
        Assert.Equal(dm.Obstacles, back.Obstacles);
        Assert.Equal(dm.Arcs, back.Arcs);
        Assert.Equal(dm.Chains, back.Chains);
    }

    [Fact]
    public void ReadsV2Notes()
    {
        var json = JsonNode.Parse("""
            {"_version":"2.2.0","_notes":[{"_time":4.0,"_lineIndex":1,"_lineLayer":0,"_type":0,"_cutDirection":1},
            {"_time":4.5,"_lineIndex":2,"_lineLayer":0,"_type":3,"_cutDirection":0}],"_obstacles":[]}
            """)!.AsObject();
        var dm = MapReader.ReadDifficulty(json, DifficultyName.Hard);
        Assert.Single(dm.Notes);
        Assert.Equal(new ColorNote(4, 1, 0, Hand.Left, CutDirection.Down), dm.Notes[0]);
        Assert.Single(dm.Bombs);
    }

    [Fact]
    public void FolderRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("abeat-test").FullName;
        try
        {
            var map = new MapSet { SongName = "T", SongAuthor = "A", Bpm = 128 };
            map.Difficulties.Add(new DifficultyMap { Difficulty = DifficultyName.ExpertPlus, NoteJumpSpeed = 18, Notes = [new(8, 2, 0, Hand.Right, CutDirection.Down)] });
            MapWriter.WriteFolder(map, dir);
            var back = MapReader.Read(dir);
            Assert.Equal(128, back.Bpm);
            Assert.Equal("T", back.SongName);
            Assert.Single(back.Difficulties);
            Assert.Equal(18, back.Difficulties[0].NoteJumpSpeed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
