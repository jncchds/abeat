using Abeat.Core.Model;
using StbImageSharp;

namespace Abeat.Core.Generation;

/// <summary>A colour scheme taken from the song's cover art. The cover's hues are binned by how much
/// vivid colour they carry; the strongest hue and the strongest one at least 70° away (or a split
/// complement when the cover has one hue) become the sabers, the warmer one on the left as red is in the
/// default scheme. Lights use the same hues, boost lights a third hue from the cover (or both turned
/// by 30°), walls the cover's main hue. Covers with almost no colour keep the game's colours.</summary>
public static class CoverPalette
{
    const int Bins = 24;
    const double MinSaberGap = 70;

    /// <summary>Beat Saber's default colours (The First), the base for hand-set colours without the cover's.</summary>
    public static readonly ColorScheme GameDefault = new(
        new Rgb(0.7843, 0.0784, 0.0784), new Rgb(0.1569, 0.5569, 0.8235), new Rgb(0.85, 0.0850, 0.0850), new Rgb(0.1882, 0.6750, 1),
        new Rgb(0.85, 0.0850, 0.0850), new Rgb(0.1882, 0.6750, 1), new Rgb(1, 0.1882, 0.1882), new Rgb(1, 1, 1));

    public static readonly string[] Slots = ["saberLeft", "saberRight", "envLeft", "envRight", "envLeftBoost", "envRightBoost", "obstacles", "envWhite"];

    /// <summary>The map's colours: the cover's (when on) with the hand-set slots on top; null when neither
    /// applies, so the environment keeps its own.</summary>
    public static ColorScheme? Resolve(string coverPath, bool cover, IReadOnlyDictionary<string, string>? overrides)
    {
        var set = (overrides ?? new Dictionary<string, string>()).Where(kv => ParseHex(kv.Value) != null).ToDictionary(kv => kv.Key, kv => ParseHex(kv.Value)!, StringComparer.OrdinalIgnoreCase);
        var baseScheme = cover ? FromFile(coverPath) : null;
        if (baseScheme == null && set.Count == 0) return null;
        var b = baseScheme ?? GameDefault;
        Rgb Pick(string slot, Rgb fallback) => set.GetValueOrDefault(slot) ?? fallback;
        return new ColorScheme(Pick("saberLeft", b.SaberLeft), Pick("saberRight", b.SaberRight), Pick("envLeft", b.EnvLeft), Pick("envRight", b.EnvRight),
            Pick("envLeftBoost", b.EnvLeftBoost), Pick("envRightBoost", b.EnvRightBoost), Pick("obstacles", b.Obstacles), Pick("envWhite", b.EnvWhite));
    }

    public static Rgb? ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int v)) return null;
        return new Rgb(Math.Round((v >> 16 & 255) / 255.0, 4), Math.Round((v >> 8 & 255) / 255.0, 4), Math.Round((v & 255) / 255.0, 4));
    }

    public static ColorScheme? FromFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var f = File.OpenRead(path);
            var img = ImageResult.FromStream(f, ColorComponents.RedGreenBlue);
            return FromPixels(img.Data, img.Width, img.Height);
        }
        catch (Exception) // a broken cover only costs the custom colours
        {
            return null;
        }
    }

    public static ColorScheme? FromPixels(byte[] rgb, int width, int height)
    {
        var weight = new double[Bins];
        var sat = new double[Bins];
        double total = 0, colourful = 0;
        int step = Math.Max(1, Math.Max(width, height) / 96);
        for (int y = 0; y < height; y += step)
            for (int x = 0; x < width; x += step)
            {
                int i = (y * width + x) * 3;
                var (h, s, v) = ToHsv(rgb[i] / 255.0, rgb[i + 1] / 255.0, rgb[i + 2] / 255.0);
                total++;
                if (s < 0.25 || v < 0.25) continue;
                double w = s * v;
                int bin = (int)(h / (360.0 / Bins)) % Bins;
                weight[bin] += w;
                sat[bin] += w * s;
                colourful += w;
            }
        if (total == 0 || colourful / total < 0.04) return null;

        // smooth over neighbouring bins so a hue split by a bin edge still counts as one
        var smooth = Enumerable.Range(0, Bins).Select(b => weight[b] + 0.5 * (weight[(b + 1) % Bins] + weight[(b + Bins - 1) % Bins])).ToArray();
        double Hue(int b) => (b + 0.5) * 360.0 / Bins;
        double Sat(int b) => weight[b] > 0 ? sat[b] / weight[b] : 0.8;

        int first = Array.IndexOf(smooth, smooth.Max());
        int? second = Enumerable.Range(0, Bins).Where(b => HueGap(Hue(b), Hue(first)) >= MinSaberGap && smooth[b] >= 0.12 * smooth[first])
            .OrderByDescending(b => smooth[b]).Select(b => (int?)b).FirstOrDefault();
        double h1 = Hue(first), h2 = second is { } s2 ? Hue(s2) : (h1 + 150) % 360;
        double s1v = Sat(first), s2v = second is { } sb ? Sat(sb) : s1v;
        int? third = Enumerable.Range(0, Bins).Where(b => HueGap(Hue(b), h1) >= 40 && HueGap(Hue(b), h2) >= 40 && smooth[b] >= 0.1 * smooth[first])
            .OrderByDescending(b => smooth[b]).Select(b => (int?)b).FirstOrDefault();

        // the warmer hue (closer to orange-red) plays the left saber
        bool swap = HueGap(h2, 15) < HueGap(h1, 15);
        var (left, right) = swap ? (h2, h1) : (h1, h2);
        var (sl, sr) = swap ? (s2v, s1v) : (s1v, s2v);

        static double Vivid(double s) => Math.Clamp(s, 0.75, 1);
        var boostLeft = third is { } t ? Hue(t) : (left + 330) % 360;
        var boostRight = third is not null ? (Hue(third.Value) + 40) % 360 : (right + 30) % 360;
        if (HueGap(boostLeft, boostRight) < 50) boostRight = (boostLeft + 60) % 360;
        return new ColorScheme(
            SaberLeft: FromHsv(left, Vivid(sl), 1),
            SaberRight: FromHsv(right, Vivid(sr), 1),
            EnvLeft: FromHsv(left, Vivid(sl + 0.1), 1),
            EnvRight: FromHsv(right, Vivid(sr + 0.1), 1),
            EnvLeftBoost: FromHsv(boostLeft, 0.9, 1),
            EnvRightBoost: FromHsv(boostRight, 0.9, 1),
            Obstacles: FromHsv(h1, 0.85, 0.9),
            EnvWhite: Mix(FromHsv(h1, 1, 1), new Rgb(1, 1, 1), 0.85));
    }

    static double HueGap(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    static Rgb Mix(Rgb a, Rgb b, double t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    static Rgb FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return new Rgb(Math.Round(r + m, 4), Math.Round(g + m, 4), Math.Round(b + m, 4));
    }
}
