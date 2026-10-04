namespace Abeat.Core.Model;

/// <summary>Grid: X 0..3 left to right, Y 0..2 bottom to top. Times are in beats.</summary>
public sealed record ColorNote(double Beat, int X, int Y, Hand Hand, CutDirection Direction, int AngleOffset = 0);

public sealed record BombNote(double Beat, int X, int Y);

/// <summary>Arc (v3 slider): a curve one saber follows from a head note to a tail note, drawn while a
/// sound is held. Directions are the cut directions at each end; multipliers scale the curve's
/// control points (0 = straight line).</summary>
public sealed record Arc(double Beat, int X, int Y, Hand Hand, CutDirection Direction,
    double TailBeat, int TailX, int TailY, CutDirection TailDirection,
    double HeadMultiplier = 1, double TailMultiplier = 1, int MidAnchor = 0);

/// <summary>Chain (v3 burst slider): a head note followed by link segments the same saber cuts in one
/// motion along the head's cut direction, ending at the tail cell. Squish scales the link spacing.</summary>
public sealed record Chain(double Beat, int X, int Y, Hand Hand, CutDirection Direction,
    double TailBeat, int TailX, int TailY, int Segments, double Squish = 1);

public sealed record Obstacle(double Beat, double Duration, int X, int Y, int Width, int Height);

/// <summary>Classic (v2-compatible) lighting event. Type: 0 back lasers, 1 ring lights, 2 left lasers,
/// 3 right lasers, 4 center, 8 ring spin, 9 ring zoom, 12/13 laser speeds.</summary>
public sealed record LightEvent(double Beat, int Type, int Value, double Brightness = 1.0);

public sealed record BoostEvent(double Beat, bool On);

/// <summary>v3 group lighting (environments with light groups, e.g. Pyro). A box selects lights of the
/// group (filter: division into <see cref="Sections"/> parts, part <see cref="Part"/>, or all with
/// Sections = 1) and plays its events, spread over the lights by the beat distribution (a wave of
/// <see cref="BeatSpread"/> beats) and the brightness distribution.</summary>
public sealed record LightBox(int Sections, int Part, bool Reverse, double BeatSpread, double BrightnessSpread, IReadOnlyList<LightColorEvent> Events);

/// <summary>Colour 0 red, 1 blue, 2 white; Transition 0 instant, 1 fade from the previous event.</summary>
public sealed record LightColorEvent(double Beat, int Color, double Brightness, int Transition = 0, int Strobe = 0);

public sealed record LightGroupEvent(double Beat, int Group, IReadOnlyList<LightBox> Boxes);

/// <summary>Rotation of a light group's lights about an axis (0 x, 1 y), with a per-light spread.</summary>
public sealed record LightRotation(double Beat, int Group, int Axis, double Degrees, double Spread, double Duration);

public static class LightValue
{
    public const int Off = 0;
    public const int BlueOn = 1, BlueFlash = 2, BlueFade = 3, BlueTransition = 4;
    public const int RedOn = 5, RedFlash = 6, RedFade = 7, RedTransition = 8;

    public static int On(bool red) => red ? RedOn : BlueOn;
    public static int Flash(bool red) => red ? RedFlash : BlueFlash;
    public static int Fade(bool red) => red ? RedFade : BlueFade;
}

public enum DifficultyName { Easy, Normal, Hard, Expert, ExpertPlus }

/// <summary>v3 lane rotation (90/360 degree modes): from this beat the track turns by <see cref="Degrees"/>
/// (clockwise positive); early rotations already apply to objects on the same beat.</summary>
public sealed record RotationEvent(double Beat, double Degrees, bool Early = true);

public static class Characteristic
{
    public const string Standard = "Standard", OneSaber = "OneSaber", Degree90 = "90Degree", Degree360 = "360Degree";
    public static readonly string[] Extra = [OneSaber, Degree90, Degree360];
}

public sealed class DifficultyMap
{
    public DifficultyName Difficulty { get; init; }
    /// <summary>Game mode the difficulty belongs to (<see cref="Model.Characteristic"/>).</summary>
    public string Characteristic { get; init; } = Model.Characteristic.Standard;
    public double NoteJumpSpeed { get; set; }
    public double NoteJumpOffset { get; set; }
    public List<ColorNote> Notes { get; init; } = [];
    public List<BombNote> Bombs { get; init; } = [];
    public List<Arc> Arcs { get; init; } = [];
    public List<Chain> Chains { get; init; } = [];
    public List<Obstacle> Obstacles { get; init; } = [];
    public List<LightEvent> Lights { get; init; } = [];
    public List<BoostEvent> Boosts { get; init; } = [];
    public List<RotationEvent> Rotations { get; init; } = [];
    public List<LightGroupEvent> GroupLights { get; init; } = [];
    public List<LightRotation> GroupRotations { get; init; } = [];
    /// <summary>Tempo changes in the file.</summary>
    public int BpmChanges { get; set; }
    /// <summary>Tempo changes read from the file (v3 bpmEvents, v2 type-100 events), beat 0 first; null
    /// when the file has none.</summary>
    public List<(double Beat, double Bpm)>? TempoChanges { get; set; }

    public static int Rank(DifficultyName d) => d switch
    {
        DifficultyName.Easy => 1,
        DifficultyName.Normal => 3,
        DifficultyName.Hard => 5,
        DifficultyName.Expert => 7,
        _ => 9,
    };

    public string FileName => $"{Difficulty}{Characteristic}.dat";
}

public sealed class MapSet
{
    public string SongName { get; set; } = "";
    public string SongSubName { get; set; } = "";
    public string SongAuthor { get; set; } = "";
    public string LevelAuthor { get; set; } = "ABeat by CHDS";
    public double Bpm { get; set; }
    public double PreviewStart { get; set; }
    public double PreviewDuration { get; set; } = 12;
    public string SongFile { get; set; } = "song.egg";
    public string CoverFile { get; set; } = "cover.jpg";
    public string Environment { get; set; } = "DefaultEnvironment";
    public List<DifficultyMap> Difficulties { get; init; } = [];
    TempoMap? tempo;
    /// <summary>Tempo with changes (written as bpmEvents into every difficulty); defaults to the constant
    /// <see cref="Bpm"/>.</summary>
    public TempoMap Tempo
    {
        get => tempo is { } t && Math.Abs(t.Bpm - Bpm) < 1e-9 ? t : TempoMap.Constant(Bpm);
        set { tempo = value; Bpm = value.Bpm; }
    }

    public double BeatToSeconds(double beat) => Tempo.BeatToSeconds(beat);
    public double SecondsToBeat(double sec) => Tempo.SecondsToBeat(sec);
}
