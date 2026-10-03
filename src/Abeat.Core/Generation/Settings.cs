using System.Text.Json;
using System.Text.Json.Serialization;
using Abeat.Core.Model;

namespace Abeat.Core.Generation;

/// <summary>Per-difficulty knobs. Defaults come from <see cref="DifficultyProfile.Default"/>.</summary>
public sealed record DifficultyProfile
{
    public DifficultyName Name { get; init; }
    public double NoteJumpSpeed { get; init; }
    /// <summary>Target jump distance in meters; converted to a note jump offset for the song's BPM.</summary>
    public double JumpDistance { get; init; }
    /// <summary>Average notes per second at mid energy; scaled by section energy.</summary>
    public double BaseNps { get; init; }
    public double MaxNps { get; init; }
    /// <summary>Minimum time between consecutive rhythm events (any hand).</summary>
    public double MinGapSec { get; init; }
    /// <summary>Minimum time between two notes of the same hand.</summary>
    public double MinSameHandGapSec { get; init; }
    /// <summary>Finest grid subdivision used: 1 = quarter notes (beats), 2 = eighths, 4 = sixteenths.</summary>
    public int Subdivision { get; init; }
    public bool AllowTriplets { get; init; }
    /// <summary>Fraction of rhythm events that become two-hand doubles.</summary>
    public double DoubleRate { get; init; }
    /// <summary>Extra cost for dot (any-direction) notes; lower = more dots.</summary>
    public double DotCost { get; init; }
    /// <summary>Chance that a strong single-hand accent gets a decorative bomb (Hard and up).</summary>
    public double BombRate { get; init; }

    public static DifficultyProfile Default(DifficultyName d) => d switch
    {
        // densities and double rates follow curated human maps (see style-prior.json); doubles count as one event
        DifficultyName.Easy => new() { Name = d, NoteJumpSpeed = 10, JumpDistance = 18, BaseNps = 1.5, MaxNps = 2.4, MinGapSec = 0.45, MinSameHandGapSec = 0.8, Subdivision = 1, DoubleRate = 0.15, DotCost = 0.5 },
        DifficultyName.Normal => new() { Name = d, NoteJumpSpeed = 11, JumpDistance = 20, BaseNps = 2.1, MaxNps = 3.3, MinGapSec = 0.3, MinSameHandGapSec = 0.55, Subdivision = 2, DoubleRate = 0.17, DotCost = 0.8 },
        DifficultyName.Hard => new() { Name = d, NoteJumpSpeed = 13, JumpDistance = 22, BaseNps = 2.7, MaxNps = 4.2, MinGapSec = 0.22, MinSameHandGapSec = 0.42, Subdivision = 2, DoubleRate = 0.17, DotCost = 1.6, BombRate = 0.04 },
        DifficultyName.Expert => new() { Name = d, NoteJumpSpeed = 16, JumpDistance = 24, BaseNps = 3.3, MaxNps = 5.4, MinGapSec = 0.16, MinSameHandGapSec = 0.3, Subdivision = 4, AllowTriplets = true, DoubleRate = 0.15, DotCost = 2.2, BombRate = 0.06 },
        _ => new() { Name = d, NoteJumpSpeed = 18, JumpDistance = 26, BaseNps = 4.2, MaxNps = 7.5, MinGapSec = 0.12, MinSameHandGapSec = 0.24, Subdivision = 4, AllowTriplets = true, DoubleRate = 0.22, DotCost = 2.2, BombRate = 0.08 },
    };
}

/// <summary>Weights of the swing cost model. Physical costs are also used to score existing maps.</summary>
public sealed record FlowWeights
{
    /// <summary>Same-parity swing (reset) when there is no time to reset comfortably.</summary>
    public double Reset { get; init; } = 40;
    /// <summary>Same-parity swing after a long pause (allowed, mildly discouraged).</summary>
    public double SlowReset { get; init; } = 2.5;
    /// <summary>Deviation of the swing angle from a clean reversal of the previous swing.</summary>
    public double Angle { get; init; } = 1.2;
    /// <summary>Distance between where the saber ended and where the next cut starts.</summary>
    public double Travel { get; init; } = 0.9;
    /// <summary>Note placed in the middle-row center, which hides notes behind it.</summary>
    public double VisionBlock { get; init; } = 2.0;
    /// <summary>Hand reaching into the other hand's side.</summary>
    public double Crossover { get; init; } = 3.0;
    /// <summary>Same hand twice in a row faster than the profile allows.</summary>
    public double TooFast { get; init; } = 25;
    /// <summary>Horizontal cut (extra on top of the style prior).</summary>
    public double Horizontal { get; init; } = 0;
    /// <summary>-log likelihood of the grid cell under the human style prior.</summary>
    public double StyleCell { get; init; } = 0.7;
    /// <summary>-log likelihood of the cut direction under the human style prior.</summary>
    public double StyleDirection { get; init; } = 0.6;
    /// <summary>Note row should follow brightness ("pitch") of the sound.</summary>
    public double Pitch { get; init; } = 0.5;
    /// <summary>Strong accents prefer big vertical swings.</summary>
    public double Emphasis { get; init; } = 0.8;
    /// <summary>Random jitter for variety; also what the seed changes.</summary>
    public double Noise { get; init; } = 0.6;
    /// <summary>Penalty for repeating the exact same note (hand, position, direction) as last time.</summary>
    public double Repeat { get; init; } = 0.5;
    /// <summary>Penalty for a hand hitting the same cell as its previous (and second previous) note.</summary>
    public double Stagnation { get; init; } = 1.2;
    /// <summary>Pull towards the per-phrase target cell; drives movement around the grid and makes
    /// repeated sections reuse similar patterns.</summary>
    public double Target { get; init; } = 0.45;
    /// <summary>Hand roles: one saber follows the melody (vocals, other), the other the rhythm (drums,
    /// bass); the roles swap at section changes. Cost of a single note on the "wrong" hand.</summary>
    public double HandRole { get; init; } = 0.8;
}

public sealed record GeneratorSettings
{
    public List<DifficultyName> Difficulties { get; init; } = [DifficultyName.Expert, DifficultyName.ExpertPlus];
    public Dictionary<DifficultyName, DifficultyProfile> ProfileOverrides { get; init; } = [];
    /// <summary>Global note density multiplier (1 = profile default).</summary>
    public double Density { get; init; } = 1.0;
    public int Seed { get; init; } = 1;
    public int BeamWidth { get; init; } = 64;
    public FlowWeights Weights { get; init; } = new();
    /// <summary>Weight per onset layer. Missing layers get weight 0.</summary>
    public Dictionary<string, double> LayerWeights { get; init; } = new()
    {
        // band layers (no stem separation); "mid" carries most of the vocals
        ["full"] = 0.5, ["low"] = 0.9, ["mid"] = 1.0, ["high"] = 0.35,
        // demucs stems: vocals lead, drums keep the pulse, the mix only fills gaps
        ["vocals"] = 1.5, ["drums"] = 0.9, ["other"] = 0.6, ["bass"] = 0.4, ["mix"] = 0.25,
    };
    /// <summary>Seconds of the song kept free of notes at the start.</summary>
    public double LeadInSec { get; init; } = 1.5;
    public bool Lights { get; init; } = true;
    public bool Walls { get; init; } = true;
    /// <summary>Single-lane centre walls in note-free gaps (Normal and up).</summary>
    public bool DodgeWalls { get; init; } = true;
    /// <summary>Full-width overhead walls right before drops (Hard and up).</summary>
    public bool CrouchWalls { get; init; } = true;
    /// <summary>Reset-signalling bombs (Normal and up) and accent bombs (Hard and up).</summary>
    public bool Bombs { get; init; } = true;
    public string LevelAuthor { get; init; } = "ABeat by CHDS";

    public DifficultyProfile Profile(DifficultyName d) =>
        ProfileOverrides.TryGetValue(d, out var p) ? p : DifficultyProfile.Default(d);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GeneratorSettings Load(string path) =>
        JsonSerializer.Deserialize<GeneratorSettings>(File.ReadAllText(path), JsonOptions) ?? new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
