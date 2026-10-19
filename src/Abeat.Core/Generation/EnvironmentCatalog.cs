using System.Text.Json;

namespace Abeat.Core.Generation;

/// <summary>Classic environments take basic lighting events (types 0-4 lights, 8/9 rings, 12/13 laser
/// speeds, plus a few environment-specific types); group environments (Weave and later) take v3 light
/// groups that can be lit, rotated and moved individually.</summary>
public enum LightingSystem { Classic, Groups }

/// <summary>A light group of a group-lighting environment as curated maps use it: the share of maps
/// lighting / rotating / moving it, the axes and 90th-percentile magnitudes they use, and a lower bound on
/// its light count.</summary>
/// <param name="Mirror">The group on the other side that mappers send the same events (the game mirrors it).</param>
/// <param name="RotationPoses">Angles (-180..180) mappers turn it to about its first rotation axis, most used first.</param>
/// <param name="HomeShare">Share of the time curated maps leave it at its home pose (the first of <paramref name="RotationPoses"/>).</param>
/// <param name="AxisPoses">The same for each further rotation axis (Weave's lights also turn about y).</param>
/// <param name="TranslationValues">Per translation axis: 10th percentile, median and 90th percentile of the
/// distances mappers move it to.</param>
/// <param name="Rate">Median share of song beats on which maps switch it on (Pyro's fire: 0.03).</param>
/// <param name="TranslationIntro">Per translation axis: where curated maps start it when they start it away
/// from home (The Weeknd's ring sets start lowered and rise on the first big moment).</param>
/// <param name="RotationRate">Median share of song beats on which maps that turn it turn it to a pose.</param>
/// <param name="TranslationRate">Median share of song beats on which maps that move it move it to a position.</param>
/// <param name="Lit">Median share of the playing time curated maps keep it lit (runways and floors mappers keep
/// on: Collider's runway 0.91; lights they only flash: Rock Mixtape's runway lasers 0.14).</param>
public sealed record LightGroupInfo(int Id, double Color, double Rotation, double Translation, int Lights,
    int[] RotationAxes, double RotationRange, int[] TranslationAxes, double TranslationRange,
    int? Mirror = null, int[]? RotationPoses = null, IReadOnlyDictionary<int, double[]>? TranslationValues = null,
    IReadOnlyDictionary<int, int[]>? AxisPoses = null, double? Rate = null, double HomeShare = 0, double? RotationRate = null,
    IReadOnlyDictionary<int, double>? TranslationIntro = null, double? TranslationRate = null, double? Lit = null)
{
    /// <summary>The poses mappers turn it to about <paramref name="axis"/> (null when unknown).</summary>
    public int[]? PosesOn(int axis) =>
        RotationAxes.Length > 0 && axis == RotationAxes[0] ? RotationPoses : AxisPoses?.GetValueOrDefault(axis);
}

/// <summary>A basic light type (0-7, or 8-12 where they switch lights: Lizzo's peaches 7/8) of a group environment
/// as curated maps use it next to the groups (e.g. Pyro: 4 runway, 2/3 projectors, 1 logo): share of maps, events per song beat, the share of
/// "on" and "transition" values among its events, the share of white among its lit events, and the type
/// on the other side that gets identical events, and the median share of the playing time it is lit.</summary>
public sealed record BasicLightInfo(int Type, double Share, double Rate, double On, double Transition, double White, int? Mirror, double? Lit = null);

/// <summary>A basic event type whose value is a position step (The Second's ring spacing), as curated maps
/// pulse it: share of maps, events per song beat and the low / high step they alternate between.</summary>
public sealed record StepTypeInfo(int Type, double Share, double Rate, int Low, int High);

/// <param name="Drive">0 live band (backbeat) .. 1 electronic four-on-the-floor.</param>
/// <param name="Intensity">0 calm .. 1 relentless.</param>
/// <param name="Darkness">0 bright / happy .. 1 dark / heavy.</param>
/// <param name="Bpm">Tempo the environment's own songs sit around.</param>
public sealed record EnvironmentCharacter(double Drive, double Intensity, double Darkness, double Bpm);

/// <param name="BasicTypes">Basic event types curated maps use in this environment (classic lights and
/// specials such as ring spins or hydraulics).</param>
/// <param name="BasicValues">Per special basic event type: lowest, highest and most common value curated maps use.</param>
/// <param name="RotationEase">Median length (beats) of the eased light group turns curated maps make here.</param>
/// <param name="TranslationEase">Median length (beats) of their eased light group moves.</param>
/// <param name="BasicLights">Basic light types curated maps light next to the groups (group environments).</param>
/// <param name="Steps">Step types curated maps pulse next to the groups (group environments).</param>
public sealed record EnvironmentInfo(string Id, string Name, LightingSystem System, EnvironmentCharacter Character,
    IReadOnlySet<int> BasicTypes, IReadOnlyList<LightGroupInfo> Groups, int SurveyMaps, IReadOnlyDictionary<int, int[]> BasicValues,
    double? RotationEase = null, double? TranslationEase = null, IReadOnlyList<BasicLightInfo>? BasicLights = null,
    IReadOnlyList<StepTypeInfo>? Steps = null)
{
    public bool Has(int basicType) => BasicTypes.Contains(basicType);

    public (int Lo, int Hi) ValueRange(int basicType) =>
        BasicValues.TryGetValue(basicType, out var v) && v.Length >= 2 ? (v[0], v[1]) : (0, 1);
}

/// <summary>Every environment of moddable Beat Saber (up to 1.40), with the light layout learned from
/// curated maps by scripts/environment_prior.py (embedded environment-prior.json).</summary>
public static class EnvironmentCatalog
{
    public const string Auto = "Auto";
    public const string AllDirections = "GlassDesertEnvironment";

    static readonly (string Id, string Name, LightingSystem System, EnvironmentCharacter C)[] Table =
    [
        ("DefaultEnvironment", "The First", LightingSystem.Classic, new(0.5, 0.5, 0.4, 128)),
        ("OriginsEnvironment", "Origins", LightingSystem.Classic, new(0.4, 0.4, 0.3, 120)),
        ("TriangleEnvironment", "Triangle", LightingSystem.Classic, new(0.8, 0.6, 0.5, 128)),
        ("NiceEnvironment", "Nice", LightingSystem.Classic, new(0.7, 0.5, 0.2, 120)),
        ("BigMirrorEnvironment", "Big Mirror", LightingSystem.Classic, new(0.8, 0.6, 0.4, 125)),
        ("DragonsEnvironment", "Imagine Dragons", LightingSystem.Classic, new(0.2, 0.6, 0.5, 120)),
        ("KDAEnvironment", "K/DA", LightingSystem.Classic, new(0.6, 0.6, 0.5, 110)),
        ("MonstercatEnvironment", "Monstercat", LightingSystem.Classic, new(0.9, 0.7, 0.5, 128)),
        ("CrabRaveEnvironment", "Crab Rave", LightingSystem.Classic, new(0.9, 0.7, 0.1, 125)),
        ("PanicEnvironment", "Panic! at the Disco", LightingSystem.Classic, new(0.2, 0.7, 0.4, 140)),
        ("RocketEnvironment", "Rocket League", LightingSystem.Classic, new(0.7, 0.8, 0.3, 140)),
        ("GreenDayEnvironment", "Green Day", LightingSystem.Classic, new(0.1, 0.8, 0.5, 160)),
        ("GreenDayGrenadeEnvironment", "Green Day Grenade", LightingSystem.Classic, new(0.1, 0.8, 0.6, 160)),
        ("TimbalandEnvironment", "Timbaland", LightingSystem.Classic, new(0.6, 0.5, 0.6, 100)),
        ("FitBeatEnvironment", "FitBeat", LightingSystem.Classic, new(0.8, 0.8, 0.2, 130)),
        ("LinkinParkEnvironment", "Linkin Park", LightingSystem.Classic, new(0.2, 0.8, 0.8, 105)),
        ("BTSEnvironment", "BTS", LightingSystem.Classic, new(0.6, 0.6, 0.3, 115)),
        ("KaleidoscopeEnvironment", "Kaleidoscope", LightingSystem.Classic, new(0.8, 0.5, 0.5, 125)),
        ("InterscopeEnvironment", "Interscope", LightingSystem.Classic, new(0.5, 0.6, 0.4, 105)),
        ("SkrillexEnvironment", "Skrillex", LightingSystem.Classic, new(0.9, 0.9, 0.7, 140)),
        ("BillieEnvironment", "Billie Eilish", LightingSystem.Classic, new(0.3, 0.3, 0.8, 90)),
        ("HalloweenEnvironment", "Spooky", LightingSystem.Classic, new(0.4, 0.5, 0.9, 120)),
        ("GagaEnvironment", "Lady Gaga", LightingSystem.Classic, new(0.7, 0.7, 0.4, 120)),
        ("WeaveEnvironment", "Weave", LightingSystem.Groups, new(0.8, 0.6, 0.4, 128)),
        ("PyroEnvironment", "Fall Out Boy", LightingSystem.Groups, new(0.2, 0.8, 0.5, 140)),
        ("EDMEnvironment", "EDM", LightingSystem.Groups, new(0.9, 0.8, 0.4, 128)),
        ("TheSecondEnvironment", "The Second", LightingSystem.Groups, new(0.6, 0.6, 0.4, 120)),
        ("LizzoEnvironment", "Lizzo", LightingSystem.Groups, new(0.5, 0.6, 0.1, 115)),
        ("TheWeekndEnvironment", "The Weeknd", LightingSystem.Groups, new(0.6, 0.5, 0.6, 110)),
        ("RockMixtapeEnvironment", "Rock Mixtape", LightingSystem.Groups, new(0.1, 0.8, 0.6, 140)),
        ("Dragons2Environment", "Imagine Dragons 2", LightingSystem.Groups, new(0.2, 0.7, 0.5, 120)),
        ("Panic2Environment", "Panic! at the Disco 2", LightingSystem.Groups, new(0.2, 0.7, 0.4, 140)),
        ("QueenEnvironment", "Queen", LightingSystem.Groups, new(0.1, 0.7, 0.3, 120)),
        ("LinkinPark2Environment", "Linkin Park 2", LightingSystem.Groups, new(0.2, 0.9, 0.8, 105)),
        ("TheRollingStonesEnvironment", "The Rolling Stones", LightingSystem.Groups, new(0.1, 0.7, 0.3, 125)),
        ("LatticeEnvironment", "Lattice", LightingSystem.Groups, new(0.9, 0.7, 0.5, 130)),
        ("DaftPunkEnvironment", "Daft Punk", LightingSystem.Groups, new(0.9, 0.6, 0.2, 115)),
        ("HipHopEnvironment", "Hip Hop Mixtape", LightingSystem.Groups, new(0.5, 0.5, 0.6, 95)),
        ("ColliderEnvironment", "Collider", LightingSystem.Groups, new(0.9, 0.8, 0.6, 135)),
        ("BritneyEnvironment", "Britney Spears", LightingSystem.Groups, new(0.7, 0.6, 0.3, 120)),
        ("Monstercat2Environment", "Monstercat 2", LightingSystem.Groups, new(0.9, 0.8, 0.5, 128)),
        ("MetallicaEnvironment", "Metallica", LightingSystem.Groups, new(0.0, 1.0, 0.9, 120)),
        ("GridEnvironment", "Grid", LightingSystem.Groups, new(0.8, 0.6, 0.4, 128)),
        ("ColdplayEnvironment", "Coldplay", LightingSystem.Groups, new(0.4, 0.5, 0.2, 120)),
        ("ProdigyEnvironment", "The Prodigy", LightingSystem.Groups, new(0.9, 0.9, 0.8, 140)),
    ];

    static readonly int[] ClassicDefault = [0, 1, 2, 3, 4, 8, 9, 12, 13];

    static readonly Lazy<IReadOnlyList<EnvironmentInfo>> AllLazy = new(Load);

    /// <summary>Environments a Standard map can use (GlassDesert is the 90/360 environment).</summary>
    public static IReadOnlyList<EnvironmentInfo> All => AllLazy.Value;

    /// <summary>The environment with this id ("PyroEnvironment"), short name ("Pyro") or display name;
    /// null when unknown (and for "Auto").</summary>
    public static EnvironmentInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name.Trim();
        return All.FirstOrDefault(e => e.Id.Equals(n, StringComparison.OrdinalIgnoreCase)
            || e.Id.Equals(n + "Environment", StringComparison.OrdinalIgnoreCase)
            || e.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    public static EnvironmentInfo Default => Find("DefaultEnvironment")!;

    static IReadOnlyList<EnvironmentInfo> Load()
    {
        using var s = typeof(EnvironmentCatalog).Assembly.GetManifestResourceStream("Abeat.Core.environment-prior.json");
        var prior = s == null ? null : JsonSerializer.Deserialize<Dictionary<string, PriorEntry>>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var list = new List<EnvironmentInfo>();
        foreach (var (id, name, system, c) in Table)
        {
            var p = prior?.GetValueOrDefault(id);
            var basic = p?.Basic?.Keys.Select(int.Parse).ToHashSet() ?? [];
            if (system == LightingSystem.Classic && basic.Count(t => t is >= 0 and <= 4) < 3) basic = [.. ClassicDefault];
            var groups = (p?.Groups ?? []).Select(kv => new LightGroupInfo(int.Parse(kv.Key), kv.Value.Color, kv.Value.Rotation,
                    kv.Value.Translation, kv.Value.Lights, kv.Value.RotationAxes ?? [], kv.Value.RotationRange,
                    kv.Value.TranslationAxes ?? [], kv.Value.TranslationRange, kv.Value.Mirror, kv.Value.RotationPoses,
                    kv.Value.TranslationValues?.ToDictionary(t => int.Parse(t.Key), t => t.Value),
                    kv.Value.AxisPoses?.ToDictionary(t => int.Parse(t.Key), t => t.Value), kv.Value.Rate, kv.Value.HomeShare ?? 0, kv.Value.RotationRate,
                    kv.Value.TranslationIntro?.ToDictionary(t => int.Parse(t.Key), t => t.Value), kv.Value.TranslationRate, kv.Value.Lit))
                .OrderBy(g => g.Id).ToList();
            // a group environment nobody has mapped yet cannot be lit safely; fall back to classic events
            var sys = system == LightingSystem.Groups && groups.Count(g => g.Color > 0) == 0 ? LightingSystem.Classic : system;
            if (sys == LightingSystem.Classic && system == LightingSystem.Groups) basic = [.. ClassicDefault];
            var values = (p?.BasicValues ?? []).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            var basicLights = sys == LightingSystem.Groups
                ? (p?.BasicLights ?? []).Select(kv => new BasicLightInfo(int.Parse(kv.Key), kv.Value.Share, kv.Value.Rate, kv.Value.On,
                    kv.Value.Transition, kv.Value.White, kv.Value.Mirror, kv.Value.Lit)).OrderBy(b => b.Type).ToList()
                : [];
            var steps = sys == LightingSystem.Groups
                ? (p?.Steps ?? []).Select(kv => new StepTypeInfo(int.Parse(kv.Key), kv.Value.Share, kv.Value.Rate, kv.Value.Low, kv.Value.High))
                    .OrderBy(x => x.Type).ToList()
                : [];
            list.Add(new EnvironmentInfo(id, name, sys, c, basic, groups, p?.Maps ?? 0, values, p?.RotationEase, p?.TranslationEase, basicLights, steps));
        }
        return list;
    }

    sealed record PriorGroup(double Color, double Rotation, double Translation, int Lights, int[]? RotationAxes,
        double RotationRange, int[]? TranslationAxes, double TranslationRange, int? Mirror, int[]? RotationPoses,
        Dictionary<string, double[]>? TranslationValues, Dictionary<string, int[]>? AxisPoses, double? Rate, double? HomeShare,
        double? RotationRate, Dictionary<string, double>? TranslationIntro, double? TranslationRate, double? Lit);
    sealed record PriorBasicLight(double Share, double Rate, double On, double Transition, double White, int? Mirror, double? Lit);
    sealed record PriorStep(double Share, double Rate, int Low, int High);
    sealed record PriorEntry(int Maps, Dictionary<string, double>? Basic, Dictionary<string, int[]>? BasicValues, Dictionary<string, PriorGroup>? Groups,
        double? RotationEase, double? TranslationEase, Dictionary<string, PriorBasicLight>? BasicLights, Dictionary<string, PriorStep>? Steps);
}
