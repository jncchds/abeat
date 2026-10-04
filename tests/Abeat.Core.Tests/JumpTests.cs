using System.Text.Json.Nodes;
using Abeat.Core.Analysis;
using Abeat.Core.Evaluation;
using Abeat.Core.Formats;
using Abeat.Core.Generation;
using Abeat.Core.Model;

namespace Abeat.Core.Tests;

public class JumpTests
{
    [Theory]
    [InlineData(128, 18, 26)]
    [InlineData(90, 10, 18)]
    [InlineData(200, 16, 24)]
    public void OffsetHitsWantedJumpDistance(double bpm, double njs, double jd)
    {
        double offset = MapGenerator.JumpOffsetFor(bpm, njs, jd);
        double got = MapGenerator.JumpDistance(bpm, njs, offset);
        // offsets are quantized to quarter beats
        Assert.InRange(got, jd - njs * 60 / bpm * 0.26, jd + njs * 60 / bpm * 0.26);
    }
}
