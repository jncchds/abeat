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
    /// <summary>Quick back-and-forth flicks of one hand (clean reversals, little travel, at most
    /// <see cref="BurstNotes"/> in a row) may go down to this gap. Equal to <see cref="MinSameHandGapSec"/> = off.</summary>
    public double BurstGapSec { get; init; }
    public int BurstNotes => Name >= DifficultyName.ExpertPlus ? 6 : 4;
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
        // densities, gaps and double rates follow curated human maps (playlist 1116456, see style-prior.json): base
        // density ~curated median moments/s (doubles count as one event), gaps ~5th percentile of the curated gaps;
        // note jump speed and jump distance are the curated medians (humans shorten the jump as levels get faster);
        // burst gaps ~10th percentile of curated same-hand gaps under the regular same-hand gap
        DifficultyName.Easy => new() { Name = d, NoteJumpSpeed = 12, JumpDistance = 23, BaseNps = 1.5, MaxNps = 2.4, MinGapSec = 0.35, MinSameHandGapSec = 0.45, BurstGapSec = 0.3, Subdivision = 1, DoubleRate = 0.15, DotCost = 1.6 },
        DifficultyName.Normal => new() { Name = d, NoteJumpSpeed = 13, JumpDistance = 21, BaseNps = 2.4, MaxNps = 3.3, MinGapSec = 0.2, MinSameHandGapSec = 0.3, BurstGapSec = 0.18, Subdivision = 2, DoubleRate = 0.17, DotCost = 1.6 },
        DifficultyName.Hard => new() { Name = d, NoteJumpSpeed = 14, JumpDistance = 20, BaseNps = 3.1, MaxNps = 4.2, MinGapSec = 0.15, MinSameHandGapSec = 0.24, BurstGapSec = 0.14, Subdivision = 2, DoubleRate = 0.17, DotCost = 1.6, BombRate = 0.04 },
        DifficultyName.Expert => new() { Name = d, NoteJumpSpeed = 16, JumpDistance = 19.5, BaseNps = 3.7, MaxNps = 5.4, MinGapSec = 0.12, MinSameHandGapSec = 0.18, BurstGapSec = 0.12, Subdivision = 4, AllowTriplets = true, DoubleRate = 0.15, DotCost = 2.2, BombRate = 0.06 },
        _ => new() { Name = d, NoteJumpSpeed = 17.5, JumpDistance = 18, BaseNps = 4.5, MaxNps = 7.5, MinGapSec = 0.1, MinSameHandGapSec = 0.13, BurstGapSec = 0.1, Subdivision = 4, AllowTriplets = true, DoubleRate = 0.22, DotCost = 2.2, BombRate = 0.08 },
    };
}

/// <summary>Per-difficulty speed and density limits that replace the built-in profile's; unset fields keep
/// the default, so later changes to the defaults still reach songs that override only some limits.</summary>
public sealed record ProfileOverride
{
    public double? BaseNps { get; init; }
    public double? MaxNps { get; init; }
    public double? MinGapSec { get; init; }
    public double? MinSameHandGapSec { get; init; }
    public double? BurstGapSec { get; init; }

    public DifficultyProfile ApplyTo(DifficultyProfile p) => p with
    {
        BaseNps = BaseNps ?? p.BaseNps,
        MaxNps = MaxNps ?? p.MaxNps,
        MinGapSec = MinGapSec ?? p.MinGapSec,
        MinSameHandGapSec = MinSameHandGapSec ?? p.MinSameHandGapSec,
        BurstGapSec = BurstGapSec ?? p.BurstGapSec,
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
    /// <summary>The other hand cut the same cell a moment ago (see <see cref="SwingCostModel.SameCellSec"/>).</summary>
    public double HandClash { get; init; } = 8;
    /// <summary>Same hand twice in a row faster than the profile allows.</summary>
    public double TooFast { get; init; } = 25;
    /// <summary>A quick flick under the regular same-hand gap that qualifies as a burst
    /// (<see cref="DifficultyProfile.BurstGapSec"/>). 0: the effort and strain priors alone decide how often
    /// (5-8 % of same-hand gaps on 4 bench songs; curated maps 5-19 %), as a bonus they overshoot the strain ceiling.</summary>
    public double Burst { get; init; } = 0;
    /// <summary>Horizontal cut (extra on top of the style prior).</summary>
    public double Horizontal { get; init; } = 0;
    /// <summary>-log likelihood of the grid cell under the human style prior.</summary>
    public double StyleCell { get; init; } = 0.7;
    /// <summary>-log likelihood of the cut direction under the human style prior.</summary>
    public double StyleDirection { get; init; } = 0.6;
    /// <summary>A figure (hand + cell + cut direction) or double shape outside the difficulty's human
    /// vocabulary (<see cref="StylePrior"/>). High enough that they only appear when nothing else fits.</summary>
    public double Figure { get; init; } = 25;
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
    /// <summary>Hand roles (<see cref="FlowPlanner.HandRoles"/>): one hand leads the drops, the other the
    /// rest, the support hand takes bar downbeats. Cost of a single note on the "wrong" hand.</summary>
    public double HandRole { get; init; } = 1.5;
    /// <summary>Swing size follows intensity: loud hits pull to the outer columns and big moves, soft
    /// ones stay near the centre with small moves.</summary>
    public double Dynamics { get; init; } = 1.0;
    /// <summary>Match the human distribution of moves between consecutive swings of a hand (turn angle x
    /// saber-tip travel, see movement-prior.json); without it maps turn and travel far less than human ones.</summary>
    public double MovementStyle { get; init; } = 1.0;
    /// <summary>Match the human distribution of move strain (effective swings per second per hand) for the
    /// difficulty, mostly by choosing which hand takes a note: human maps give one saber quick runs far
    /// more often than strict hand alternation does.</summary>
    public double Effort { get; init; } = 2.0;
    /// <summary>Moves more strenuous (turn + travel per second) than the difficulty's human ceiling.</summary>
    public double Strain { get; init; } = 3.0;
    /// <summary>Pattern memory: repeated sections (same label) are planned a second time with this bonus for
    /// playing a note exactly as at the same position of the section's first occurrence, so a returning
    /// chorus brings back its patterns. 0 = off (single pass).</summary>
    public double Repetition { get; init; } = 0.5;
}

public sealed record GeneratorSettings
{
    public List<DifficultyName> Difficulties { get; init; } = [.. Enum.GetValues<DifficultyName>()];
    /// <summary>Extra game modes written next to Standard, for the same difficulties: "OneSaber",
    /// "90Degree", "360Degree" (rotations on the Standard notes).</summary>
    public List<string> Modes { get; init; } = [];
    public Dictionary<DifficultyName, ProfileOverride> ProfileOverrides { get; init; } = [];
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
    /// <summary>Weight of drum-stem hits by kind (kick "k", snare "s", hat/cymbal "h") on top of the
    /// drums layer weight.</summary>
    public Dictionary<string, double> DrumWeights { get; init; } = new() { ["k"] = 1.0, ["s"] = 1.0, ["h"] = 0.6 };
    /// <summary>Seconds of the song kept free of notes at the start.</summary>
    public double LeadInSec { get; init; } = 1.5;
    public bool Lights { get; init; } = true;
    /// <summary>"Auto" (the environment that suits the song) or an environment id / name from
    /// <see cref="EnvironmentCatalog"/> ("PyroEnvironment", "Pyro", "The First"...).</summary>
    public string Environment { get; init; } = EnvironmentCatalog.Auto;
    public bool Walls { get; init; } = true;
    /// <summary>Single-lane centre walls in note-free gaps (Normal and up).</summary>
    public bool DodgeWalls { get; init; } = true;
    /// <summary>Full-width overhead walls right before drops (Hard and up). Off by default: only 2-10 % of
    /// curated maps use any.</summary>
    public bool CrouchWalls { get; init; } = false;
    /// <summary>Short walls in the upper half of the outer lanes on the strongest kicks and snares of louder sections.</summary>
    public bool RhythmWalls { get; init; } = true;
    /// <summary>Reset-signalling bombs (Normal and up) and accent bombs (Hard and up).</summary>
    public bool Bombs { get; init; } = true;
    /// <summary>Arcs from held melody notes to the same hand's next note.</summary>
    public bool Arcs { get; init; } = true;
    /// <summary>Chains on notes followed by a roll or stutter too fast for single notes (Hard and up).</summary>
    public bool Chains { get; init; } = true;
    /// <summary>Small cut-angle offsets that lean with the melody's pitch direction (Normal and up).</summary>
    public bool AngleOffsets { get; init; } = true;
    /// <summary>A short note-free pause before each drop, with a double on the drop.</summary>
    public bool DropPause { get; init; } = true;
    public string LevelAuthor { get; init; } = "ABeat by CHDS";

    public DifficultyProfile Profile(DifficultyName d) =>
        ProfileOverrides.TryGetValue(d, out var o) ? o.ApplyTo(DifficultyProfile.Default(d)) : DifficultyProfile.Default(d);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GeneratorSettings Load(string path) =>
        JsonSerializer.Deserialize<GeneratorSettings>(File.ReadAllText(path), JsonOptions) ?? new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
