using SpryCad.Core.Model;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Core.Regen;

/// <summary>
/// Modelled threads: ISO metric sizes (coarse pitch) and the cutting tool for an external thread (on a shaft) or an
/// internal one (in a hole), ISO 60° basic profile, swept along a helix.
/// </summary>
public static class Threads
{
    /// <summary>ISO 261 metric threads, coarse pitch: (name, major diameter, pitch).</summary>
    public static readonly (string name, double d, double p)[] MetricCoarse =
    {
        ("M1.6", 1.6, 0.35), ("M2", 2, 0.4), ("M2.5", 2.5, 0.45), ("M3", 3, 0.5), ("M4", 4, 0.7), ("M5", 5, 0.8),
        ("M6", 6, 1.0), ("M8", 8, 1.25), ("M10", 10, 1.5), ("M12", 12, 1.75), ("M14", 14, 2), ("M16", 16, 2),
        ("M18", 18, 2.5), ("M20", 20, 2.5), ("M22", 22, 2.5), ("M24", 24, 3), ("M27", 27, 3), ("M30", 30, 3.5),
        ("M33", 33, 3.5), ("M36", 36, 4), ("M42", 42, 4.5), ("M48", 48, 5),
    };

    /// <summary>Fundamental triangle height of a 60° thread of pitch p.</summary>
    public static double H(double p) => p * Math.Sqrt(3) / 2;

    /// <summary>Minor diameter of an internal thread (the hole to drill): D − 1.0825 P.</summary>
    public static double MinorDiameter(double d, double p) => d - 5 * H(p) / 4;

    /// <summary>The standard size fitting a shaft (major diameter) or a hole (minor diameter) best.</summary>
    public static (string name, double d, double p) Fit(double diameter, bool external)
        => MetricCoarse.MinBy(s => Math.Abs((external ? s.d : MinorDiameter(s.d, s.p)) - diameter));

    /// <summary>
    /// The groove a thread cuts, in the (r, z) half plane (z along the thread's axis, centred on 0): the ISO basic
    /// profile's space between two teeth. External: from the root (5H/8 below the major radius, P/4 flat) out past
    /// the surface; internal: from the major radius (P/8 flat) in past the hole's wall. <paramref name="clearance"/>
    /// moves the flanks and the root into the material (a looser fit for printed parts); where that would narrow the
    /// crest below its standard flat (P/8 on a shaft, P/4 in a hole), the crest is lowered instead: the shaft is
    /// turned down (the hole bored out) to <c>turn</c> over the thread. The groove reaches past the surface (short of
    /// where its turns would meet), so the surfaces cross cleanly.
    /// </summary>
    public static (List<Vec2> groove, double? turn) Groove(bool external, double surfaceR, double major, double p, double clearance)
    {
        var t30 = Math.Tan(Math.PI / 6);
        var shift = clearance / Math.Cos(Math.PI / 6); // flanks moved by the clearance along their normal
        var h = H(p);
        if (external)
        {
            var root0 = major - 5 * h / 8;
            double hw(double r) => p / 8 + shift + (r - root0) * t30;
            var rRoot = root0 - clearance;
            if (rRoot <= 1e-6 * major) throw new FeatureException("The thread is too deep for this shaft.");
            // the crest keeps its P/8 flat: where the groove is 7P/16 wide on each side
            var crest = root0 + (7 * p / 16 - p / 8 - shift) / t30;
            double? turn = crest < surfaceR - 1e-9 * major ? crest : null;
            var top = turn ?? surfaceR;
            // past the crest, short of where the turns would meet (the groove's width under a pitch)
            var rOut = Math.Min(top + 0.1 * p, root0 + (0.49 * p - p / 8 - shift) / t30);
            if (rOut <= rRoot) throw new FeatureException("The clearance is too large for this pitch.");
            return (new List<Vec2> { new(rRoot, -hw(rRoot)), new(rOut, -hw(rOut)), new(rOut, hw(rOut)), new(rRoot, hw(rRoot)) }, turn);
        }
        else
        {
            double hw(double r) => p / 16 + shift + (major - r) * t30;
            var rRoot = major + clearance;
            if (rRoot <= surfaceR) throw new FeatureException("The hole is larger than the thread's major diameter.");
            // the crest keeps its P/4 flat: where the groove is 3P/8 wide on each side
            var crest = major - (3 * p / 8 - p / 16 - shift) / t30;
            double? turn = crest > surfaceR + 1e-9 * major ? crest : null;
            var top = turn ?? surfaceR;
            var rOut = Math.Max(top - 0.1 * p, major - (0.49 * p - p / 16 - shift) / t30);
            rOut = Math.Max(rOut, 1e-3 * major);
            if (rOut >= rRoot) throw new FeatureException("The clearance is too large for this pitch.");
            return (new List<Vec2> { new(rOut, -hw(rOut)), new(rRoot, -hw(rRoot)), new(rRoot, hw(rRoot)), new(rOut, hw(rOut)) }, turn);
        }
    }
}
