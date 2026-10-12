using SpryCad.Kernel;

namespace SpryCad.Core.Model;

/// <summary>
/// Colors to tell features apart (View > Color by Feature): each feature that can make faces gets one, by its place
/// among them in the document (ids are random, so their order is what keeps a feature's color from one rebuild or
/// session to the next). Consecutive features get hues far apart (golden ratio steps round the wheel), with the
/// lightness varied as well, so even many features stay distinct. A face's feature is the id its name starts with.
/// </summary>
public static class FeatureColors
{
    /// <summary>The feature that made a face: the id its name starts with ("F12.S.L5" → "F12"), or null.</summary>
    public static string? FeatureOf(string faceName)
    {
        var dot = faceName.IndexOf('.');
        return dot > 0 ? faceName[..dot] : null;
    }

    /// <summary>Whether a feature gets a color (sketches and planes make no faces).</summary>
    public static bool Colored(Feature f) => f is not (SketchFeature or PlaneFeature);

    /// <summary>The color (RGB in 0..1) of each feature that gets one, by id.</summary>
    public static Dictionary<string, double[]> Of(CadDocument doc)
    {
        var map = new Dictionary<string, double[]>();
        int n = 0;
        foreach (var f in doc.Features)
            if (Colored(f)) map[f.Id] = Color(n++);
        return map;
    }

    /// <summary>The <paramref name="index"/>th color.</summary>
    public static double[] Color(int index)
    {
        var hue = (0.58 + index * 0.6180339887498949) % 1.0;
        var light = (index % 3) switch { 0 => 0.62, 1 => 0.52, _ => 0.70 };
        return Hsl(hue, 0.55, light);
    }

    /// <summary>The stronger color for highlights of the feature with a given color from <see cref="Color"/>.</summary>
    public static double[] Vivid(double[] c)
    {
        var (h, s, l) = ToHsl(c);
        return Hsl(h, Math.Min(1, s + 0.4), Math.Max(0.3, l - 0.06));
    }

    /// <summary>
    /// A mesh's triangles by feature: each run of consecutive faces of one feature as one index range (start, count)
    /// with its feature id (null for faces whose names hold none), in mesh order. Drawn run by run, a part takes one
    /// draw per run instead of one per face.
    /// </summary>
    public static List<(int start, int count, string? feature)> Runs(DisplayMesh m)
    {
        var runs = new List<(int start, int count, string? feature)>();
        foreach (var fr in m.Faces)
        {
            if (fr.IndexCount == 0) continue;
            var id = FeatureOf(fr.Name);
            if (runs.Count > 0 && runs[^1] is var last && last.feature == id && last.start + last.count == fr.IndexStart)
                runs[^1] = (last.start, last.count + fr.IndexCount, id);
            else runs.Add((fr.IndexStart, fr.IndexCount, id));
        }
        return runs;
    }

    static double[] Hsl(double h, double s, double l)
    {
        double C = (1 - Math.Abs(2 * l - 1)) * s, x = C * (1 - Math.Abs(h * 6 % 2 - 1)), m = l - C / 2;
        var (r, g, b) = (int)(h * 6) switch
        {
            0 => (C, x, 0.0), 1 => (x, C, 0.0), 2 => (0.0, C, x), 3 => (0.0, x, C), 4 => (x, 0.0, C), _ => (C, 0.0, x),
        };
        return new[] { r + m, g + m, b + m };
    }

    static (double h, double s, double l) ToHsl(double[] c)
    {
        double r = c[0], g = c[1], b = c[2], max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-12) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6; if (h < 0) h += 1;
        return (h, s, l);
    }
}
