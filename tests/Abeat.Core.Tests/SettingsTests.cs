using System.Text.Json;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class SettingsTests
{
    /// <summary>A song's override replaces only the limits it sets; the rest follow the built-in profile.</summary>
    [Fact]
    public void ProfileOverrideKeepsUnsetLimits()
    {
        var json = """{ "profileOverrides": { "ExpertPlus": { "minSameHandGapSec": 0.11, "maxNps": 10 } } }""";
        var s = JsonSerializer.Deserialize<GeneratorSettings>(json, GeneratorSettings.JsonOptions)!;
        var p = s.Profile(DifficultyName.ExpertPlus);
        var d = DifficultyProfile.Default(DifficultyName.ExpertPlus);
        Assert.Equal(0.11, p.MinSameHandGapSec);
        Assert.Equal(10, p.MaxNps);
        Assert.Equal(d.MinGapSec, p.MinGapSec);
        Assert.Equal(d.BaseNps, p.BaseNps);
        Assert.Equal(d.Subdivision, p.Subdivision);
        Assert.Equal(DifficultyProfile.Default(DifficultyName.Expert), s.Profile(DifficultyName.Expert));
    }
}
