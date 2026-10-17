using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

/// <summary>
/// A Hole Wizard hole's profile as a sketch (as SolidWorks lets the hole's revolve sketch be edited): half the hole in
/// section, its x the radius and its y the depth into the part from the face (the origin on the hole's axis there), revolved
/// about the axis at each position. Its dimensions use the hole's values by name (<see cref="Names"/>) as well as the
/// part's equations.
/// </summary>
public static class HoleProfile
{
    /// <summary>
    /// Which way a hole drills from its sketch's plane: into the part, against the plane's normal (a sketch on a face:
    /// into it), unless the part (<paramref name="bounds"/>) is all on its normal's side (a sketch on the plane the part
    /// stands on: the Top plane under a flange; drilled away, it cut nothing); the other way reversed.
    /// </summary>
    public static Vec3 Drill(Frame frame, Box3 bounds, bool reverse)
    {
        double Reach(double dir)
        {
            if (bounds.IsEmpty) return 0;
            double far = 0;
            for (int c = 0; c < 8; c++)
            {
                var p = new Vec3((c & 1) == 0 ? bounds.Min.X : bounds.Max.X, (c & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (c & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                far = Math.Max(far, dir * Vec3.Dot(p - frame.Origin, frame.Normal));
            }
            return far;
        }
        var drill = Reach(-1) < 1e-6 && Reach(1) > 1e-6 ? frame.Normal : -frame.Normal;
        return reverse ? -drill : drill;
    }

    /// <summary>
    /// Where a position sketch puts holes: at its points; a sketch without points (one made for a cut) at its circles'
    /// centers, so a sketch can be used again for holes.
    /// </summary>
    public static List<Vec2> Positions(Sketch sk)
    {
        var pts = sk.Entities.OfType<SkPointEntity>().Where(p => !p.Construction).Select(p => sk.P(p.P)).ToList();
        if (pts.Count == 0)
            foreach (var c in sk.Entities.OfType<SkCircle>().Where(c => !c.Construction))
                if (sk.P(c.Center) is var ctr && !pts.Any(q => Vec2.Distance(q, ctr) < 1e-9)) pts.Add(ctr);
        return pts;
    }

    /// <summary>The hole's values a profile's dimensions can use, each with what it is.</summary>
    public static readonly (string name, string what)[] Names =
    {
        ("D", "hole diameter"), ("Depth", "depth (from the end condition)"), ("CbD", "counterbore diameter"),
        ("CbDepth", "counterbore depth"), ("CsD", "countersink diameter"), ("CsAngle", "countersink angle"),
    };

    /// <summary>
    /// The hole's values by name (the depth given: per hole, from its end condition), then the part's equations
    /// (<paramref name="equations"/>); a value that does not evaluate is missing.
    /// </summary>
    public static Func<string, Quantity?> Lookup(HoleFeature h, Func<string, Quantity?> equations, double mmPer, double depthMm)
    {
        Quantity? Len(string text) { try { return Quantity.Mm(Expr.Length(text, equations, mmPer)); } catch (Expr.ExprException) { return null; } }
        Quantity? Ang(string text) { try { return Quantity.Deg(Expr.Angle(text, equations)); } catch (Expr.ExprException) { return null; } }
        return name => h.TypeValues != null && h.TypeValues.TryGetValue(name, out var tv) ? (IsAngle(name) ? Quantity.Deg(tv) : Quantity.Mm(tv)) : name switch
        {
            "D" => Len(h.Diameter),
            "Depth" => Quantity.Mm(depthMm),
            "CbD" => Len(h.CbDiameter),
            "CbDepth" => Len(h.CbDepth),
            "CsD" => Len(h.CsDiameter),
            "CsAngle" => Ang(h.CsAngle),
            _ => equations(name),
        };
    }

    /// <summary>A profile as a library hole type holds it (<see cref="Library.LibraryTable.Profile"/>: its JSON).</summary>
    public static string ToJson(Sketch profile) => System.Text.Json.JsonSerializer.Serialize(profile, Serialization.Compact);

    /// <summary>A library hole type's profile read (null for none).</summary>
    public static Sketch? FromJson(string? json) => json == null ? null : System.Text.Json.JsonSerializer.Deserialize<Sketch>(json, Serialization.Options);

    /// <summary>A library hole type's column holding an angle (degrees): named with "angle", as library tables have it.</summary>
    public static bool IsAngle(string column) => column.Contains("angle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A hole's profile as a library hole type (<paramref name="key"/>): each name its dimensions use but Depth (from the
    /// end condition) a column, in the order first used, and a first row (<paramref name="row"/>) of their values now;
    /// null with what is wrong when a name has no value.
    /// </summary>
    public static (Library.LibraryTable? table, string? problem) ToLibraryType(Sketch profile, string key, string row, Func<string, Quantity?> lookup)
    {
        var columns = new List<string>();
        foreach (var c in profile.Constraints.Where(c => c.IsDimension && !c.Driven && !string.IsNullOrWhiteSpace(c.Expr)))
            foreach (var id in Expr.Identifiers(c.Expr!))
                if (id != "Depth" && !columns.Contains(id)) columns.Add(id);
        if (columns.Count == 0) return (null, "The profile's dimensions use no names (D, CbD...): every size would be the same.");
        var values = new List<double>();
        foreach (var name in columns)
        {
            if (lookup(name) is not { } q) return (null, $"{name} has no value here: give it one (an equation) first.");
            values.Add(q.Value);
        }
        return (new Library.LibraryTable
        {
            Key = key, Title = key, Kind = Library.TableKinds.HoleType, Columns = columns, Profile = ToJson(profile),
            Rows = { new Library.LibraryRow { Key = row, Values = values } },
        }, null);
    }

    /// <summary>
    /// The profile the hole's type has (simple, counterbore, countersink; a drill point on a blind hole), its
    /// dimensions the hole's values by name, placed as <paramref name="lookup"/> has them.
    /// </summary>
    public static Sketch Default(HoleFeature h, Func<string, Quantity?> lookup, double mmPer)
    {
        var sk = new Sketch();
        double V(string e) => Expr.Length(e, lookup, mmPer);
        const string tip = "Depth + D/2/tan(59deg)"; // (a 118° drill's point)
        bool point = h.EndKind == HoleEnd.Blind && h.DrillPoint;
        // the points round the half section from the origin (the axis at the face), each placed at (x, y), then the
        // lines between them (horizontal, vertical or neither), the dimensions from the origin defining them
        var pts = new List<long> { Sketch.OriginId };
        long P(double x, double y) { var p = sk.AddPoint(new Vec2(x, y)).Id; pts.Add(p); return p; }
        void Dim(CType type, long p, string expr)
        {
            var c = sk.AddConstraint(type, Sketch.OriginId, p);
            c.Expr = expr;
            c.Value = Math.Abs(V(expr));
            SketchSolver.CaptureSign(sk, c);
        }
        var kinds = new List<CType?>();
        switch (h.Type)
        {
            case HoleType.Counterbore:
                {
                    var b = P(V("CbD/2"), 0); kinds.Add(CType.Horizontal);
                    var c = P(V("CbD/2"), V("CbDepth")); kinds.Add(CType.Vertical);
                    var e = P(V("D/2"), V("CbDepth")); kinds.Add(CType.Horizontal);
                    Dim(CType.HDistance, b, "CbD/2"); Dim(CType.VDistance, c, "CbDepth"); Dim(CType.HDistance, e, "D/2");
                    break;
                }
            case HoleType.Countersink:
                {
                    var b = P(V("CsD/2"), 0); kinds.Add(CType.Horizontal);
                    const string csDepth = "(CsD - D)/2/tan(CsAngle/2)";
                    var c = P(V("D/2"), V(csDepth)); kinds.Add(null);
                    Dim(CType.HDistance, b, "CsD/2"); Dim(CType.HDistance, c, "D/2"); Dim(CType.VDistance, c, csDepth);
                    break;
                }
            default:
                {
                    var b = P(V("D/2"), 0); kinds.Add(CType.Horizontal);
                    Dim(CType.HDistance, b, "D/2");
                    break;
                }
        }
        // the wall down to the depth, then across to the axis (or to a drill point on it), and back up the axis
        var bottom = P(V("D/2"), V("Depth")); kinds.Add(CType.Vertical);
        Dim(CType.VDistance, bottom, "Depth");
        var axis = P(0, V(point ? tip : "Depth")); kinds.Add(point ? null : CType.Horizontal);
        Dim(CType.VDistance, axis, point ? tip : "Depth");
        kinds.Add(CType.Vertical);
        for (int i = 0; i < pts.Count; i++)
        {
            var l = sk.Add(new SkLine { P1 = pts[i], P2 = pts[(i + 1) % pts.Count] });
            if (kinds[i] is { } k) sk.AddConstraint(k, l.Id);
        }
        return sk;
    }

    /// <summary>
    /// The profile solved with <paramref name="lookup"/> (the hole's values and the part's equations) and its closed
    /// region, to revolve about the axis (x = 0); failures said for the user.
    /// </summary>
    public static List<ProfileRegion> Regions(Sketch profile, Func<string, Quantity?> lookup, double mmPer)
    {
        var sk = profile.Clone();
        foreach (var c in sk.Constraints)
        {
            if (!c.IsDimension || c.Driven || string.IsNullOrWhiteSpace(c.Expr)) continue;
            Regenerator.Reading.Value?.Invoke(c.Expr!, c.Type == CType.Angle ? ValueKind.Angle : ValueKind.Length);
            try { c.Value = c.Type == CType.Angle ? Expr.Angle(c.Expr, lookup) : Expr.Length(c.Expr, lookup, mmPer); }
            catch (Expr.ExprException ex) { throw new FeatureException($"Hole profile: {ex.Message}"); }
        }
        SketchSolver.Solve(sk);
        var regions = ProfileExtractor.Extract(sk, out _);
        if (regions.Count == 0) throw new FeatureException("The hole profile has no closed profile.");
        if (regions.SelectMany(r => r.AllLoops()).SelectMany(l => l.Verts).Any(v => v.P.X < -1e-6))
            throw new FeatureException("The hole profile crosses its axis (the vertical through the sketch's origin).");
        return regions;
    }
}
