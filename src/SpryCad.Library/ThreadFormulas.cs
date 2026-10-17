namespace SpryCad.Core.Library;

/// <summary>
/// Thread values worked out from the standards' formulas, which is how SpryCAD's own thread tables are filled
/// (<c>SpryCad.LibraryGen threads</c>; docs/library-v2.md). Only what the formulas give exactly is filled:
/// <list type="bullet">
/// <item>Unified inch threads (ASME B1.1), classes 2A/2B and 3A/3B: every limit, from the standard's tolerance formulas
/// (length of engagement the basic major diameter; the tolerances rounded to 0.0001 in before the limits are worked
/// out, as its tables are; the internal minor diameter to 0.001 in from #6 up, its maximum from 1/4 in up); the tap
/// drill the standard drill nearest a 75% thread.</item>
/// <item>ISO metric threads (ISO 68-1 basic profile, ISO 965-1 positions g and H), classes 6g and 6H: the limits that
/// follow from the basic profile and the fundamental deviation (the external maxima, the internal minima), rounded
/// to 0.001 mm. The others depend on ISO 965-1's tabulated tolerances, which its formulas do not give exactly (their
/// rounding differs for some pitches), so they are left out; the tap drill is the major diameter less the pitch.</item>
/// </list>
/// </summary>
public static class ThreadFormulas
{
    /// <summary>Basic pitch diameter: d − 0.649519 P (3/8 of the fundamental triangle's height off each side).</summary>
    public static double PitchDiameter(double d, double p) => d - 0.649519 * p;

    /// <summary>Basic minor diameter (an internal thread's): d − 1.082532 P (5/8 of the triangle's height).</summary>
    public static double MinorDiameter(double d, double p) => d - 1.082532 * p;

    /// <summary>
    /// ISO 965-1's fundamental deviation for position g (µm, negative), by pitch (mm): its table, which rounds −(15 +
    /// 11 P); the formula for a pitch it does not list.
    /// </summary>
    public static double DeviationG(double p) =>
        -(GTable.TryGetValue(Math.Round(p, 3), out var v) ? v : Math.Round(15 + 11 * p));

    static readonly Dictionary<double, double> GTable = new()
    {
        [0.2] = 17, [0.25] = 18, [0.3] = 18, [0.35] = 19, [0.4] = 19, [0.45] = 20, [0.5] = 20, [0.6] = 21, [0.7] = 22,
        [0.75] = 22, [0.8] = 24, [1] = 26, [1.25] = 28, [1.5] = 32, [1.75] = 34, [2] = 38, [2.5] = 42, [3] = 48,
        [3.5] = 53, [4] = 60, [4.5] = 63, [5] = 71, [5.5] = 75, [6] = 80,
    };

    /// <summary>An ISO metric thread's values (mm) by column path, class 6g with 6H (see the class summary): its row's class is 6g.</summary>
    public static (Dictionary<string, double> values, Dictionary<string, string> text) IsoMetric(double d, double p)
    {
        static double R(double x) => Math.Round(x, 3, MidpointRounding.AwayFromZero);
        double es = DeviationG(p) / 1000, d2 = PitchDiameter(d, p), d1 = MinorDiameter(d, p);
        var v = new Dictionary<string, double>
        {
            ["external.major.max"] = R(d + es), ["external.pitch.max"] = R(d2 + es), ["external.minor.max"] = R(d1 + es),
            ["internal.minor.min"] = R(d1), ["internal.pitch.min"] = R(d2), ["internal.major.min"] = R(d),
            ["tap.diameter"] = R(d - p),
        };
        return (v, new() { ["internal.class"] = "6H" });
    }

    /// <summary>The Unified classes SpryCAD's tables give: 2A with 2B, 3A with 3B.</summary>
    public static readonly string[] UnifiedClasses = { "2A", "3A" };

    /// <summary>
    /// A Unified inch thread's values (in) by column path, of class 2A with 2B or 3A with 3B (see the class summary):
    /// 3A has no allowance and three quarters of 2A's pitch diameter tolerance, 3B 1.3 times that.
    /// </summary>
    public static (Dictionary<string, double> values, Dictionary<string, string> text) Unified(double d, double p, string cls = "2A")
    {
        if (cls is not ("2A" or "3A")) throw new ArgumentException($"Unified class {cls}: 2A or 3A.");
        bool three = cls == "3A";
        static double R4(double x) => Math.Round(x, 4, MidpointRounding.AwayFromZero);
        static double R3(double x) => Math.Round(x, 3, MidpointRounding.AwayFromZero);
        // class 2A's pitch diameter tolerance (length of engagement d), 2B's 1.3 times it, 2A's allowance 0.3 times
        // it, 2A's major diameter tolerance, and the basic pitch diameter, each rounded to 0.0001 in as the standard's
        // tables round them; the limits from those
        // (to 0.000001 in first: 1/4-20's 2B tolerance is 1.3 x 0.003731, 0.0049, not 1.3 x 0.00373076)
        double td2Raw = Math.Round(0.0015 * Math.Cbrt(d) + 0.0015 * Math.Sqrt(d) + 0.015 * Math.Pow(p, 2.0 / 3), 6, MidpointRounding.AwayFromZero);
        if (three) td2Raw *= 0.75;
        double td2 = R4(td2Raw), internalTd2 = R4(1.3 * td2Raw), allowance = three ? 0 : R4(0.3 * td2Raw), td = R4(0.060 * Math.Pow(p, 2.0 / 3));
        double e = R4(PitchDiameter(d, p)), majorMax = R4(d - allowance), pitchMax = R4(e - allowance);
        double minor = MinorDiameter(d, p);
        var (drill, drillD) = TapDrillInch(d, p);
        // (the internal minor diameter to 0.001 in, below #6 to 0.0001 in)
        double Rm(double x) => d < 0.138 - 1e-9 ? R4(x) : R3(x);
        var v = new Dictionary<string, double>
        {
            ["external.major.max"] = majorMax, ["external.major.min"] = R4(majorMax - td),
            ["external.pitch.max"] = pitchMax, ["external.pitch.min"] = R4(pitchMax - td2),
            // (UNR: the rounded root's)
            ["external.minor.max"] = R4(majorMax - R4(1.190785 * p)),
            ["internal.minor.min"] = Rm(minor),
            ["internal.pitch.min"] = e, ["internal.pitch.max"] = R4(e + internalTd2),
            ["internal.major.min"] = R4(d),
            ["tap.diameter"] = drillD,
        };
        // the minor diameter's tolerance: 2B's from 1/4 in up (below, the standard's own rule, which is not given here);
        // 3B's by its formula, at least 0.23 P - 1.5 P² (13 TPI and finer) or 0.120 P (coarser), at most 0.394 P, to
        // 0.0001 in
        if (three)
        {
            var tol = 0.05 * Math.Pow(p, 2.0 / 3) + 0.03 * p / d - 0.002;
            tol = Math.Min(Math.Max(tol, 1 / p >= 13 - 1e-9 ? 0.23 * p - 1.5 * p * p : 0.120 * p), 0.394 * p);
            v["internal.minor.max"] = R4(minor + tol);
        }
        else if (d >= 0.25 - 1e-9) v["internal.minor.max"] = Rm(minor + 0.25 * p - 0.4 * p * p);
        return (v, new() { ["internal.class"] = three ? "3B" : "2B", ["tap.size"] = drill });
    }

    /// <summary>The tap drill for a Unified thread (in): the standard drill nearest a 75% thread (d − 0.974 P), named.</summary>
    public static (string name, double diameter) TapDrillInch(double d, double p)
    {
        var target = d - 0.974 * p;
        return InchDrills.MinBy(x => Math.Abs(x.diameter - target));
    }

    /// <summary>Standard inch drills: number (#60-#1), letter (A-Z) and fractional (1/64-1) sizes, in inches.</summary>
    public static readonly IReadOnlyList<(string name, double diameter)> InchDrills = new (string, double)[]
    {
        ("#60", .040), ("#59", .041), ("#58", .042), ("#57", .043), ("#56", .0465), ("#55", .052), ("#54", .055),
        ("#53", .0595), ("#52", .0635), ("#51", .067), ("#50", .070), ("#49", .073), ("#48", .076), ("#47", .0785),
        ("#46", .081), ("#45", .082), ("#44", .086), ("#43", .089), ("#42", .0935), ("#41", .096), ("#40", .098),
        ("#39", .0995), ("#38", .1015), ("#37", .104), ("#36", .1065), ("#35", .110), ("#34", .111), ("#33", .113),
        ("#32", .116), ("#31", .120), ("#30", .1285), ("#29", .136), ("#28", .1405), ("#27", .144), ("#26", .147),
        ("#25", .1495), ("#24", .152), ("#23", .154), ("#22", .157), ("#21", .159), ("#20", .161), ("#19", .166),
        ("#18", .1695), ("#17", .173), ("#16", .177), ("#15", .180), ("#14", .182), ("#13", .185), ("#12", .189),
        ("#11", .191), ("#10", .1935), ("#9", .196), ("#8", .199), ("#7", .201), ("#6", .204), ("#5", .2055),
        ("#4", .209), ("#3", .213), ("#2", .221), ("#1", .228),
        ("A", .234), ("B", .238), ("C", .242), ("D", .246), ("E", .250), ("F", .257), ("G", .261), ("H", .266),
        ("I", .272), ("J", .277), ("K", .281), ("L", .290), ("M", .295), ("N", .302), ("O", .316), ("P", .323),
        ("Q", .332), ("R", .339), ("S", .348), ("T", .358), ("U", .368), ("V", .377), ("W", .386), ("X", .397),
        ("Y", .404), ("Z", .413),
    }.Concat(Enumerable.Range(1, 64).Select(i => (Fraction(i), i / 64.0))).OrderBy(x => x.Item2).ToList();

    // i/64 in lowest terms ("27/64", "7/16", "1")
    static string Fraction(int n)
    {
        int d = 64;
        while (n % 2 == 0 && d > 1) { n /= 2; d /= 2; }
        return d == 1 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{n}/{d}";
    }
}
