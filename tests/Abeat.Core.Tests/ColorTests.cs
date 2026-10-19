using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class ColorTests
{
    /// <summary>A cover: top half orange, bottom half teal, with a grey band.</summary>
    static byte[] Cover(int w, int h, Func<int, int, (byte, byte, byte)> px)
    {
        var data = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                (data[(y * w + x) * 3], data[(y * w + x) * 3 + 1], data[(y * w + x) * 3 + 2]) = px(x, y);
        return data;
    }

    [Fact]
    public void SabersTakeTheCoversTwoHuesWarmerOnTheLeft()
    {
        var img = Cover(64, 64, (x, y) => y < 28 ? ((byte)20, (byte)180, (byte)170) : y < 36 ? ((byte)128, (byte)128, (byte)128) : ((byte)240, (byte)120, (byte)20));
        var c = CoverPalette.FromPixels(img, 64, 64)!;
        Assert.True(c.SaberLeft.R > c.SaberLeft.B);   // orange left
        Assert.True(c.SaberRight.B > c.SaberRight.R); // teal right
    }

    static double Hue(Rgb c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), d = max - Math.Min(c.R, Math.Min(c.G, c.B));
        double h = d == 0 ? 0 : max == c.R ? 60 * ((c.G - c.B) / d % 6) : max == c.G ? 60 * ((c.B - c.R) / d + 2) : 60 * ((c.R - c.G) / d + 4);
        return h < 0 ? h + 360 : h;
    }

    static double Gap(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Theory]
    [InlineData(0)]  // two hues
    [InlineData(1)]  // a third hue
    [InlineData(2)]  // one hue
    public void SceneLightsKeepClearOfTheNoteColoursAndBoostWearsThem(int kind)
    {
        var img = Cover(64, 64, (x, y) => kind switch
        {
            0 => y < 32 ? ((byte)240, (byte)40, (byte)40) : ((byte)40, (byte)90, (byte)240),
            1 => y < 24 ? ((byte)240, (byte)40, (byte)40) : y < 44 ? ((byte)40, (byte)90, (byte)240) : ((byte)60, (byte)220, (byte)60),
            _ => ((byte)200, (byte)40, (byte)200),
        });
        var c = CoverPalette.FromPixels(img, 64, 64)!;
        double l = Hue(c.SaberLeft), r = Hue(c.SaberRight);
        foreach (var scene in new[] { c.EnvLeft, c.EnvRight })
        {
            Assert.True(Gap(Hue(scene), l) >= 40, $"scene {scene.Hex} vs left {c.SaberLeft.Hex}");
            Assert.True(Gap(Hue(scene), r) >= 40, $"scene {scene.Hex} vs right {c.SaberRight.Hex}");
        }
        Assert.True(Gap(Hue(c.EnvLeft), Hue(c.EnvRight)) >= 50);
        Assert.True(Gap(Hue(c.EnvLeftBoost), l) < 5);
        Assert.True(Gap(Hue(c.EnvRightBoost), r) < 5);
    }

    [Fact]
    public void GreyCoversKeepTheGameColoursAndOverridesWin()
    {
        var grey = Cover(32, 32, (x, y) => ((byte)(x * 8), (byte)(x * 8), (byte)(x * 8)));
        Assert.Null(CoverPalette.FromPixels(grey, 32, 32));
        Assert.Null(CoverPalette.Resolve("missing.jpg", true, null));
        var c = CoverPalette.Resolve("missing.jpg", false, new Dictionary<string, string> { ["saberLeft"] = "#00ff00", ["walls"] = "bad" })!;
        Assert.Equal(new Rgb(0, 1, 0), c.SaberLeft);
        Assert.Equal(CoverPalette.GameDefault.SaberRight, c.SaberRight);
    }

    [Fact]
    public void ColoursAreWrittenForVanillaAndSongCore()
    {
        var map = new MapSet { Environment = "WeaveEnvironment", Colors = CoverPalette.GameDefault, Bpm = 120 };
        map.Difficulties.Add(new DifficultyMap { Difficulty = DifficultyName.Hard });
        map.Difficulties.Add(new DifficultyMap { Difficulty = DifficultyName.Hard, Characteristic = Characteristic.Degree360 });
        var info = MapWriter.InfoJson(map);
        Assert.Equal(1, (int)info["_colorSchemes"]![0]!["colorScheme"]!["saberAColor"]!["a"]!);
        var sets = info["_difficultyBeatmapSets"]!.AsArray();
        Assert.Equal(0, (int)sets[0]!["_difficultyBeatmaps"]![0]!["_environmentNameIdx"]!);
        Assert.Equal(1, (int)sets[1]!["_difficultyBeatmaps"]![0]!["_environmentNameIdx"]!);
        Assert.NotNull(sets[0]!["_difficultyBeatmaps"]![0]!["_customData"]!["_colorLeft"]);
        Assert.Null(MapWriter.InfoJson(new MapSet())["_colorSchemes"]);
    }
}
