namespace SpryCad.Kernel.Native;

/// <summary>
/// Analytic surface for the native B-rep: UV parameterization, implicit signed distance (positive on
/// the natural-normal side), projection and exact ray intersection.
/// UV: plane (x, y); cylinder/cone (θ, h); sphere/torus (θ, φ). θ is measured about the axis from RefDir.
/// </summary>
public sealed class NSurface
{
    public SurfaceDef Def { get; }
    public SurfaceKind Kind => Def.Kind;
    /// <summary>Plane: in-plane frame (Normal = plane normal). Revolution: X = RefDir, Y = Axis × X, Normal = Axis.</summary>
    public Frame F { get; }
    public ExactSurface Exact { get; }

    public NSurface(SurfaceDef def, Plane? plane = null)
    {
        Def = def;
        if (def.Kind == SurfaceKind.Plane)
        {
            if (plane == null) throw new ArgumentException("plane required");
            F = Frame.FromPlane(plane);
            Exact = new ExactSurface(def, plane);
        }
        else
        {
            var a = def.Axis.Normalized;
            var x = def.RefDir - a * Vec3.Dot(def.RefDir, a);
            if (x.Length < 1e-9) x = a.AnyPerpendicular();
            x = x.Normalized;
            F = new Frame(def.Origin, x, Vec3.Cross(a, x), a);
            Exact = new ExactSurface(def with { Axis = a, RefDir = x });
        }
    }

    public static NSurface FromPlane(Plane p) => new(SurfaceDef.PlaneDef, p);

    /// <summary>Pipe of radius r around the spine.</summary>
    public static NSurface Pipe(PipeSpine spine, double r)
        => new(new SurfaceDef { Kind = SurfaceKind.Pipe, Origin = spine.P[0], Axis = spine.D[0], RefDir = spine.D[0].AnyPerpendicular(), Radius = r, Spine = spine });

    public PipeSpine? Spine => Def.Spine;
    public NurbsSurface? Nurbs => Def.Nurbs;

    public NSurface? Base => Def.Base;
    public ScrewSurface? Screw => Def.Screw;

    /// <summary>A screw surface (helical sweep of a line or arc).</summary>
    public static NSurface ScrewOf(ScrewSurface s)
        => new(new SurfaceDef { Kind = SurfaceKind.Screw, Origin = s.Motion.O, Axis = s.Motion.A, RefDir = s.Motion.X, Radius = s.MaxRadius, Screw = s });

    /// <summary>
    /// The surface moved by d along its natural normal: exact (analytic) for planes and surfaces of revolution and
    /// pipes, an offset surface for B-splines (offsets of offsets add up).
    /// </summary>
    public static NSurface OffsetOf(NSurface s, double d)
    {
        if (s.Kind == SurfaceKind.Offset) return OffsetOf(s.Base!, s.R + d);
        if (s.Kind != SurfaceKind.BSpline) throw new ArgumentException("offset surfaces are built on B-spline surfaces");
        if (Math.Abs(d) < 1e-15) return s;
        return new(new SurfaceDef { Kind = SurfaceKind.Offset, Origin = s.Origin, Axis = s.Axis, RefDir = s.F.XDir, Radius = d, Base = s });
    }

    /// <summary>Unit natural normal at face parameters (u, v).</summary>
    public Vec3 NormalAt(double u, double v) => Kind switch
    {
        SurfaceKind.BSpline => Nurbs!.Normal(FromFaceU(u), FromFaceV(v)),
        SurfaceKind.Offset => Base!.NormalAt(u, v),
        SurfaceKind.Screw => Screw!.NormalAt(u, v),
        _ => Normal(Eval(u, v)),
    };

    /// <summary>
    /// The point and its derivatives along the face parameters, exactly, for the free-form kinds: B-splines (knot
    /// parameters scaled onto a full turn in a closed direction), screw surfaces, and offsets of B-splines (the normal's
    /// derivative from the base's second derivatives). False elsewhere, and where an offset's base normal degenerates.
    /// </summary>
    public bool TryPartials(double u, double v, out Vec3 S, out Vec3 Su, out Vec3 Sv)
    {
        S = Su = Sv = default;
        switch (Kind)
        {
            case SurfaceKind.Screw:
                (S, Su, Sv) = Screw!.Partials(u, v);
                return true;
            case SurfaceKind.BSpline:
                {
                    var (p, pu, pv, _, _, _) = Nurbs!.Derivs(FromFaceU(u), FromFaceV(v));
                    double cu = Nurbs.ClosedU ? (Nurbs.U1 - Nurbs.U0) / (2 * Math.PI) : 1, cv = Nurbs.ClosedV ? (Nurbs.V1 - Nurbs.V0) / (2 * Math.PI) : 1;
                    S = p; Su = pu * cu; Sv = pv * cv;
                    return true;
                }
            case SurfaceKind.Offset when Base!.Kind == SurfaceKind.BSpline:
                {
                    var nb = Base.Nurbs!;
                    var (p, pu, pv, puu, puv, pvv) = nb.Derivs(Base.FromFaceU(u), Base.FromFaceV(v));
                    var m = Vec3.Cross(pu, pv); var ml = m.Length;
                    if (!(ml > 1e-12 * nb.Box.Diagonal * nb.Box.Diagonal)) return false;
                    var n = m / ml;
                    // the unit normal's derivatives: m's, less their part along n, over |m|
                    var mu = Vec3.Cross(puu, pv) + Vec3.Cross(pu, puv);
                    var mv = Vec3.Cross(puv, pv) + Vec3.Cross(pu, pvv);
                    var nu = (mu - n * Vec3.Dot(n, mu)) / ml;
                    var nv = (mv - n * Vec3.Dot(n, mv)) / ml;
                    double cu = nb.ClosedU ? (nb.U1 - nb.U0) / (2 * Math.PI) : 1, cv = nb.ClosedV ? (nb.V1 - nb.V0) / (2 * Math.PI) : 1;
                    S = p + n * R; Su = (pu + nu * R) * cu; Sv = (pv + nv * R) * cv;
                    return true;
                }
        }
        return false;
    }

    /// <summary>A B-spline surface (parameters as in its knot vectors).</summary>
    public static NSurface BSpline(NurbsSurface s)
        => new(new SurfaceDef { Kind = SurfaceKind.BSpline, Origin = s.Box.Center, Axis = Vec3.UnitZ, RefDir = Vec3.UnitX, Nurbs = s });

    public Plane? Plane => Kind == SurfaceKind.Plane ? F.Plane : null;
    public Vec3 Axis => F.Normal;
    public Vec3 Origin => F.Origin;
    public double R => Def.Radius;
    public double R2 => Def.Param2;
    public bool PeriodicU => Kind switch { SurfaceKind.Plane or SurfaceKind.Screw => false, SurfaceKind.BSpline => Nurbs!.ClosedU, SurfaceKind.Offset => Base!.PeriodicU, _ => true };

    // ---------------------------------------------------------------------------------------
    // parameterization

    public bool PeriodicV => Kind == SurfaceKind.Torus || (Kind == SurfaceKind.BSpline && Nurbs!.ClosedV) || (Kind == SurfaceKind.Offset && Base!.PeriodicV);

    // B-spline parameters as seen by faces: a closed direction is mapped onto a full turn (period 2π), so the
    // seam and unwrapping logic of the periodic analytic surfaces applies; an open direction keeps its knots
    double ToFaceU(double u) => Nurbs!.ClosedU ? 2 * Math.PI * (u - Nurbs.U0) / (Nurbs.U1 - Nurbs.U0) : u;
    double ToFaceV(double v) => Nurbs!.ClosedV ? 2 * Math.PI * (v - Nurbs.V0) / (Nurbs.V1 - Nurbs.V0) : v;
    double FromFaceU(double u)
    {
        if (!Nurbs!.ClosedU) return u;
        var a = u % (2 * Math.PI); if (a < 0) a += 2 * Math.PI;
        return Nurbs.U0 + (Nurbs.U1 - Nurbs.U0) * a / (2 * Math.PI);
    }
    double FromFaceV(double v)
    {
        if (!Nurbs!.ClosedV) return v;
        var a = v % (2 * Math.PI); if (a < 0) a += 2 * Math.PI;
        return Nurbs.V0 + (Nurbs.V1 - Nurbs.V0) * a / (2 * Math.PI);
    }

    public Vec2 UV(Vec3 p, double uHint = double.NaN, double vHint = double.NaN)
    {
        var l = F.ToLocal3(p);
        switch (Kind)
        {
            case SurfaceKind.Plane: return new Vec2(l.X, l.Y);
            case SurfaceKind.Cylinder:
            case SurfaceKind.Cone:
                return new Vec2(Theta(l, uHint), l.Z);
            case SurfaceKind.Sphere:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    return new Vec2(Theta(l, uHint), Math.Atan2(l.Z, r));
                }
            case SurfaceKind.Torus:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    var phi = Math.Atan2(l.Z, r - R);
                    return new Vec2(Theta(l, uHint), double.IsNaN(vHint) ? phi : NCurve.Wrap(phi, vHint));
                }
            case SurfaceKind.Pipe: return Spine!.UV(p, uHint);
            case SurfaceKind.Screw: { var c = Screw!.Closest(p, uHint, vHint); return new Vec2(c.u, c.v); }
            case SurfaceKind.Offset: return Base!.UV(p, uHint, vHint);
            case SurfaceKind.BSpline:
                {
                    var c = Nurbs!.Closest(p, double.IsNaN(uHint) ? double.NaN : FromFaceU(uHint), double.IsNaN(vHint) ? double.NaN : FromFaceV(vHint));
                    double u = ToFaceU(c.u), v = ToFaceV(c.v);
                    // closed directions unwrap next to the hint, like the angles of analytic surfaces
                    if (Nurbs.ClosedU && !double.IsNaN(uHint)) u = NCurve.Wrap(u, uHint);
                    if (Nurbs.ClosedV && !double.IsNaN(vHint)) v = NCurve.Wrap(v, vHint);
                    // poles: u is free there; keep the hint
                    if (!double.IsNaN(uHint) && Nurbs.IsPole(p)) u = uHint;
                    return new Vec2(u, v);
                }
        }
        return default;
    }

    static double Theta(Vec3 l, double hint)
    {
        if (l.X * l.X + l.Y * l.Y < 1e-24) return double.IsNaN(hint) ? 0 : hint; // on the axis (pole/apex)
        var a = Math.Atan2(l.Y, l.X);
        return double.IsNaN(hint) ? a : NCurve.Wrap(a, hint);
    }

    /// <summary>True when the point is on the axis, where θ is undefined (sphere pole, cone apex).</summary>
    public bool IsPole(Vec3 p)
    {
        if (Kind == SurfaceKind.BSpline) return Nurbs!.IsPole(p);
        if (Kind == SurfaceKind.Offset) return Base!.IsPole(Base.Project(p));
        if (Kind is not (SurfaceKind.Sphere or SurfaceKind.Cone)) return false;
        var l = F.ToLocal3(p);
        return l.X * l.X + l.Y * l.Y < 1e-14 * Math.Max(1, R * R);
    }

    public Vec3 Eval(double u, double v)
    {
        switch (Kind)
        {
            case SurfaceKind.Plane: return F.ToWorld(u, v);
            case SurfaceKind.Cylinder: return F.ToWorld(R * Math.Cos(u), R * Math.Sin(u), v);
            case SurfaceKind.Cone:
                {
                    var r = R + v * Math.Tan(R2);
                    return F.ToWorld(r * Math.Cos(u), r * Math.Sin(u), v);
                }
            case SurfaceKind.Sphere:
                return F.ToWorld(R * Math.Cos(v) * Math.Cos(u), R * Math.Cos(v) * Math.Sin(u), R * Math.Sin(v));
            case SurfaceKind.Torus:
                {
                    var r = R + R2 * Math.Cos(v);
                    return F.ToWorld(r * Math.Cos(u), r * Math.Sin(u), R2 * Math.Sin(v));
                }
            case SurfaceKind.Pipe: return Spine!.Surface(u, v, R);
            case SurfaceKind.Screw: return Screw!.Eval(u, v);
            case SurfaceKind.BSpline: return Nurbs!.Eval(FromFaceU(u), FromFaceV(v));
            case SurfaceKind.Offset: return Base!.Eval(u, v) + Base.NormalAt(u, v) * R;
        }
        return default;
    }

    /// <summary>Natural (parametric) normal at a point on the surface.</summary>
    public Vec3 Normal(Vec3 p) => Exact.NaturalNormal(p);

    public Vec3 Project(Vec3 p) => Exact.Project(p);

    /// <summary>Signed distance, positive on the side of the natural normal (exact for all kinds but the cone, which is scaled).</summary>
    public double F_(Vec3 p)
    {
        if (Kind == SurfaceKind.Pipe) return Spine!.Closest(p).dist - R;
        if (Kind == SurfaceKind.Screw) return Screw!.SignedDistance(p);
        if (Kind == SurfaceKind.Offset) return Base!.F_(p) - R;
        if (Kind == SurfaceKind.BSpline)
        {
            // distance to the patch, signed by the side of its normal
            var c = Nurbs!.Closest(p);
            return Vec3.Dot(p - c.c, Nurbs.Normal(c.u, c.v)) >= 0 ? c.dist : -c.dist;
        }
        var l = F.ToLocal3(p);
        switch (Kind)
        {
            case SurfaceKind.Plane: return l.Z;
            case SurfaceKind.Cylinder: return Math.Sqrt(l.X * l.X + l.Y * l.Y) - R;
            case SurfaceKind.Cone:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    return (r - (R + l.Z * Math.Tan(R2))) * Math.Cos(R2);
                }
            case SurfaceKind.Sphere: return Math.Sqrt(l.X * l.X + l.Y * l.Y + l.Z * l.Z) - R;
            case SurfaceKind.Torus:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y) - R;
                    return Math.Sqrt(r * r + l.Z * l.Z) - R2;
                }
        }
        return 0;
    }

    /// <summary>Analytic gradient of <see cref="F_"/> (unit length except for the cone's cos scaling).</summary>
    public Vec3 Grad(Vec3 p)
    {
        if (Kind == SurfaceKind.Plane) return F.Normal;
        if (Kind == SurfaceKind.Pipe) return Spine!.Normal(p);
        if (Kind == SurfaceKind.Screw) return Screw!.Normal(p);
        if (Kind == SurfaceKind.BSpline) { var c = Nurbs!.Closest(p); return Nurbs.Normal(c.u, c.v); }
        if (Kind == SurfaceKind.Offset) return Base!.Grad(p);
        var l = F.ToLocal3(p);
        var rr = Math.Sqrt(l.X * l.X + l.Y * l.Y);
        // radial unit (arbitrary on the axis)
        var radial = rr > 1e-300 ? (F.XDir * l.X + F.YDir * l.Y) / rr : F.XDir;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: return radial;
            case SurfaceKind.Sphere:
                {
                    var d = p - F.Origin; var len = d.Length;
                    return len > 1e-300 ? d / len : F.Normal;
                }
            case SurfaceKind.Cone: return radial * Math.Cos(R2) - F.Normal * Math.Sin(R2);
            case SurfaceKind.Torus:
                {
                    var dr = rr - R; var len = Math.Sqrt(dr * dr + l.Z * l.Z);
                    return len > 1e-300 ? (radial * dr + F.Normal * l.Z) / len : radial;
                }
        }
        return F.Normal;
    }

    /// <summary>
    /// <see cref="F_"/> and <see cref="Grad"/> at p from one evaluation (the same values bit for bit: one local
    /// frame and radius for the analytic kinds, one closest-point search for the others).
    /// </summary>
    public double FGrad(Vec3 p, out Vec3 grad)
    {
        switch (Kind)
        {
            case SurfaceKind.Plane: grad = F.Normal; return F.ToLocal3(p).Z;
            case SurfaceKind.Pipe:
                {
                    var (s, c, dist) = Spine!.Closest(p);
                    grad = dist > 1e-300 ? (p - c) / dist : Spine.Frame(s).N1;
                    return dist - R;
                }
            case SurfaceKind.Screw:
                {
                    var (u, v, c, d) = Screw!.Closest(p);
                    grad = Screw.NormalAt(u, v);
                    return Vec3.Dot(p - c, grad) >= 0 ? d : -d;
                }
            case SurfaceKind.BSpline:
                {
                    var c = Nurbs!.Closest(p);
                    grad = Nurbs.Normal(c.u, c.v);
                    return Vec3.Dot(p - c.c, grad) >= 0 ? c.dist : -c.dist;
                }
            case SurfaceKind.Offset: return Base!.FGrad(p, out grad) - R;
        }
        var l = F.ToLocal3(p);
        var rr = Math.Sqrt(l.X * l.X + l.Y * l.Y);
        var radial = rr > 1e-300 ? (F.XDir * l.X + F.YDir * l.Y) / rr : F.XDir;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: grad = radial; return rr - R;
            case SurfaceKind.Cone:
                grad = radial * Math.Cos(R2) - F.Normal * Math.Sin(R2);
                return (rr - (R + l.Z * Math.Tan(R2))) * Math.Cos(R2);
            case SurfaceKind.Sphere:
                {
                    var d = p - F.Origin; var len = d.Length;
                    grad = len > 1e-300 ? d / len : F.Normal;
                    return Math.Sqrt(l.X * l.X + l.Y * l.Y + l.Z * l.Z) - R;
                }
            case SurfaceKind.Torus:
                {
                    var dr = rr - R; var len = Math.Sqrt(dr * dr + l.Z * l.Z);
                    grad = len > 1e-300 ? (radial * dr + F.Normal * l.Z) / len : radial;
                    return len - R2;
                }
        }
        grad = F.Normal;
        return 0;
    }

    /// <summary>
    /// Nearest point of A ∩ B to p by minimum-norm Gauss-Newton on (F_a, F_b) = 0; near-tangent pairs fall back
    /// to alternating projection steps.
    /// </summary>
    public static Vec3 ProjectToIntersection(NSurface a, NSurface b, Vec3 p, double tol = 1e-12)
    {
        // (counted against free-form surfaces, where each step is a closest-point search: tests check they stay few)
        if (Prof.Enabled && (a.Kind is SurfaceKind.BSpline or SurfaceKind.Offset || b.Kind is SurfaceKind.BSpline or SurfaceKind.Offset)) Prof.Count("project.freeform", 1);
        var q = p;
        for (int it = 0; it < 40; it++)
        {
            var fa = a.FGrad(q, out var na); var fb = b.FGrad(q, out var nb);
            if (Math.Abs(fa) < tol && Math.Abs(fb) < tol) return q;
            double g11 = Vec3.Dot(na, na), g12 = Vec3.Dot(na, nb), g22 = Vec3.Dot(nb, nb);
            var det = g11 * g22 - g12 * g12;
            Vec3 step;
            if (det > 1e-10 * g11 * g22)
            {
                var y1 = (g22 * fa - g12 * fb) / det; var y2 = (g11 * fb - g12 * fa) / det;
                step = na * y1 + nb * y2;
            }
            else
            {
                // tangent: project onto each in turn
                q = b.Project(a.Project(q));
                continue;
            }
            q -= step;
            if (step.LengthSq < tol * tol) break;
        }
        return q;
    }

    /// <summary>(∂u × ∂v) orientation vs. natural normal: +1 for every kind (by construction of the parameterization).</summary>
    public const int UvSign = 1;

    // ---------------------------------------------------------------------------------------
    // relations

    public bool IsRevolution => Kind is not (SurfaceKind.Plane or SurfaceKind.Pipe or SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw);

    /// <summary>Same geometric surface (orientation ignored).</summary>
    public bool CoincidentWith(NSurface o, double tol)
    {
        // a screw surface swept by a line parallel to its axis is a cylinder
        if (Kind == SurfaceKind.Screw && o.Kind == SurfaceKind.Cylinder) return Screw!.IsCylinder(o.Origin, o.Axis, o.R, tol);
        if (Kind == SurfaceKind.Cylinder && o.Kind == SurfaceKind.Screw) return o.Screw!.IsCylinder(Origin, Axis, R, tol);
        if (Kind != o.Kind) return false;
        if (Kind == SurfaceKind.Plane)
        {
            var a = Plane!; var b = o.Plane!;
            var d = Vec3.Dot(a.Normal, b.Normal);
            return Math.Abs(Math.Abs(d) - 1) < 1e-10 && Math.Abs(a.Distance(b.Origin)) < tol;
        }
        if (Kind == SurfaceKind.Pipe) return Math.Abs(R - o.R) < tol && Spine!.SameAs(o.Spine!, tol);
        if (Kind == SurfaceKind.Screw) return ReferenceEquals(Screw, o.Screw) || Screw!.SameAs(o.Screw!, tol);
        if (Kind == SurfaceKind.BSpline) return Nurbs!.SameAs(o.Nurbs!, tol);
        if (Kind == SurfaceKind.Offset) return Math.Abs(R - o.R) < tol && Base!.CoincidentWith(o.Base!, tol);
        if (!SameAxisLine(o, tol)) return false;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: return Math.Abs(R - o.R) < tol;
            case SurfaceKind.Sphere: return Vec3.Distance(Origin, o.Origin) < tol && Math.Abs(R - o.R) < tol;
            case SurfaceKind.Torus: return Vec3.Distance(Origin, o.Origin) < tol && Math.Abs(R - o.R) < tol && Math.Abs(R2 - o.R2) < tol;
            case SurfaceKind.Cone:
                {
                    // the same cone written either way: an axis with a negative half angle is the reversed axis with the
                    // positive one (a drafted extrude's side and the same cone revolved); the same axis line, the same
                    // radius at a common height
                    var (a1, s1) = R2 < 0 ? (-Axis, -R2) : (Axis, R2);
                    var (a2, s2) = o.R2 < 0 ? (-o.Axis, -o.R2) : (o.Axis, o.R2);
                    if (Vec3.Dot(a1, a2) < 1 - 1e-10 || Math.Abs(s1 - s2) > 1e-10) return false;
                    var d = o.Origin - Origin;
                    var h = Vec3.Dot(d, a1);
                    if ((d - a1 * h).Length > tol) return false;
                    return Math.Abs(R + h * Math.Tan(s1) - o.R) < tol;
                }
        }
        return false;
    }

    public bool SameAxisLine(NSurface o, double tol)
    {
        if (!IsRevolution || !o.IsRevolution) return false;
        if (Math.Abs(Math.Abs(Vec3.Dot(Axis, o.Axis)) - 1) > 1e-10) return false;
        var d = o.Origin - Origin;
        return (d - Axis * Vec3.Dot(d, Axis)).Length < tol;
    }

    // ---------------------------------------------------------------------------------------
    // ray intersection (all crossings, t > 0 not enforced)

    public List<double> RayHits(Vec3 o, Vec3 d)
    {
        if (Kind == SurfaceKind.Pipe) return Spine!.RayHits(o, d, R);
        if (Kind == SurfaceKind.Screw) return Screw!.RayHits(o, d);
        if (Kind == SurfaceKind.BSpline) return Nurbs!.RayHits(o, d, F_);
        if (Kind == SurfaceKind.Offset) return RayHitsByDistance(o, d, Base!.Nurbs!.Box, Math.Abs(R));
        var res = new List<double>();
        var lo = F.ToLocal3(o);
        var ld = new Vec3(Vec3.Dot(d, F.XDir), Vec3.Dot(d, F.YDir), Vec3.Dot(d, F.Normal));
        void Quad(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-14)
            {
                if (Math.Abs(b) > 1e-14) res.Add(-c / b);
                return;
            }
            var disc = b * b - 4 * a * c;
            if (disc < 0) return;
            var sq = Math.Sqrt(disc);
            var q = -0.5 * (b + Math.Sign(b == 0 ? 1 : b) * sq);
            res.Add(q / a);
            if (Math.Abs(q) > 1e-300) res.Add(c / q);
        }
        switch (Kind)
        {
            case SurfaceKind.Plane:
                if (Math.Abs(ld.Z) > 1e-15) res.Add(-lo.Z / ld.Z);
                break;
            case SurfaceKind.Cylinder:
                Quad(ld.X * ld.X + ld.Y * ld.Y, 2 * (lo.X * ld.X + lo.Y * ld.Y), lo.X * lo.X + lo.Y * lo.Y - R * R);
                break;
            case SurfaceKind.Sphere:
                Quad(ld.LengthSq, 2 * Vec3.Dot(lo, ld), lo.LengthSq - R * R);
                break;
            case SurfaceKind.Cone:
                {
                    var k = Math.Tan(R2);
                    // x² + y² = (R + k z)²
                    var a = ld.X * ld.X + ld.Y * ld.Y - k * k * ld.Z * ld.Z;
                    var b = 2 * (lo.X * ld.X + lo.Y * ld.Y) - 2 * k * ld.Z * (R + k * lo.Z);
                    var c = lo.X * lo.X + lo.Y * lo.Y - (R + k * lo.Z) * (R + k * lo.Z);
                    Quad(a, b, c);
                    res.RemoveAll(t => R + k * (lo.Z + t * ld.Z) < -1e-12); // other nappe
                    break;
                }
            case SurfaceKind.Torus:
                {
                    // bounding sphere interval, then sign changes of the implicit function
                    var big = R + R2;
                    var bb = Vec3.Dot(lo, ld); var cc = lo.LengthSq - big * big; var aa = ld.LengthSq;
                    var disc = bb * bb - aa * cc;
                    if (disc < 0) break;
                    var sq = Math.Sqrt(disc);
                    double t0 = (-bb - sq) / aa, t1 = (-bb + sq) / aa;
                    int n = 96;
                    double prevT = t0, prevF = F_(o + d * t0);
                    for (int i = 1; i <= n; i++)
                    {
                        var t = t0 + (t1 - t0) * i / n;
                        var f = F_(o + d * t);
                        if (prevF == 0) res.Add(prevT);
                        else if (Math.Sign(f) != Math.Sign(prevF)) res.Add(Roots.Bisect(x => F_(o + d * x), prevT, t));
                        prevT = t; prevF = f;
                    }
                    break;
                }
        }
        return res;
    }

    // ---------------------------------------------------------------------------------------
    // profile in the (r, h) half plane of an axis (for coaxial intersections)

    /// <summary>Profile primitive of a surface of revolution about the given axis.</summary>
    public abstract record Profile;
    public sealed record HLine(double H) : Profile;                  // plane ⟂ axis
    public sealed record VLine(double R) : Profile;                  // cylinder
    public sealed record SLine(Vec2 P, Vec2 Dir) : Profile;          // cone generator (r, h)
    public sealed record PCircle(Vec2 C, double Rad) : Profile;      // sphere / torus section

    /// <summary>The surface's profile about (origin, axis) if it is a surface of revolution about that axis.</summary>
    public Profile? ProfileAbout(Vec3 origin, Vec3 axis, double tol)
    {
        if (Kind is SurfaceKind.Pipe or SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw) return null;
        axis = axis.Normalized;
        double H(Vec3 p) => Vec3.Dot(p - origin, axis);
        bool OnAxis(Vec3 p) { var d = p - origin; return (d - axis * Vec3.Dot(d, axis)).Length < tol; }
        switch (Kind)
        {
            case SurfaceKind.Plane:
                return Math.Abs(Math.Abs(Vec3.Dot(Plane!.Normal, axis)) - 1) < 1e-10 ? new HLine(H(Plane.Origin)) : null;
            case SurfaceKind.Sphere:
                return OnAxis(Origin) ? new PCircle(new Vec2(0, H(Origin)), R) : null;
        }
        if (Math.Abs(Math.Abs(Vec3.Dot(Axis, axis)) - 1) > 1e-10 || !OnAxis(Origin)) return null;
        var s = Vec3.Dot(Axis, axis) > 0 ? 1.0 : -1.0;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: return new VLine(R);
            case SurfaceKind.Cone:
                {
                    // r = R + k * h_local, h_local = s * (h - h0)
                    var h0 = H(Origin);
                    var k = Math.Tan(R2);
                    // direction of increasing local h in (r, h): (k, s) normalized
                    return new SLine(new Vec2(R, h0), new Vec2(k, s).Normalized);
                }
            case SurfaceKind.Torus: return new PCircle(new Vec2(R, H(Origin)), R2);
        }
        return null;
    }

    public NSurface Transformed(in Transform t)
    {
        if (Kind == SurfaceKind.Plane) return FromPlane(Plane!.Transformed(t));
        var d = Def.Transformed(t);
        if (Math.Abs(t.Determinant - 1) > 1e-9 && Math.Abs(t.Determinant + 1) > 1e-9)
        {
            var s = Math.Cbrt(Math.Abs(t.Determinant));
            d = d with { Radius = d.Radius * s, Param2 = Kind == SurfaceKind.Torus ? d.Param2 * s : d.Param2 };
        }
        return new NSurface(d);
    }

    public string Describe() => Exact.Describe();

    /// <summary>
    /// Ray parameters where o + t d crosses this surface, for surfaces whose |F_| bounds the distance (isolation by
    /// that bound inside the padded box, then bisection).
    /// </summary>
    List<double> RayHitsByDistance(Vec3 o, Vec3 d, Box3 box, double pad)
    {
        var res = new List<double>();
        var dl = d.Length;
        if (dl < 1e-300) return res;
        pad += 1e-6 * Math.Max(box.Diagonal, 1);
        double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
        for (int k = 0; k < 3; k++)
        {
            double ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            double lo = (k == 0 ? box.Min.X : k == 1 ? box.Min.Y : box.Min.Z) - pad, hi = (k == 0 ? box.Max.X : k == 1 ? box.Max.Y : box.Max.Z) + pad;
            if (Math.Abs(dk) < 1e-300) { if (ok < lo || ok > hi) return res; continue; }
            var a = (lo - ok) / dk; var b = (hi - ok) / dk;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
        }
        if (!(t1 > t0)) return res;
        double F(double t) => F_(o + d * t);
        var minL = 1e-7 * Math.Max(box.Diagonal, 1);
        void Iso(double a, double b, double fa, double fb, int depth)
        {
            var L = (b - a) * dl;
            if (Math.Abs(fa) + Math.Abs(fb) > L * 1.0001 + 1e-12) return;
            if (depth >= 60 || L < minL * 100)
            {
                if (fa * fb < 0) res.Add(Roots.Bisect(F, a, b));
                else if (fa == 0) res.Add(a);
                return;
            }
            var m = 0.5 * (a + b); var fm = F(m);
            Iso(a, m, fa, fm, depth + 1);
            Iso(m, b, fm, fb, depth + 1);
        }
        const int n = 16;
        double pt = t0, pf = F(t0);
        for (int i = 1; i <= n; i++)
        {
            var t = t0 + (t1 - t0) * i / n; var f = F(t);
            Iso(pt, t, pf, f, 0);
            pt = t; pf = f;
        }
        res.Sort();
        for (int i = res.Count - 1; i > 0; i--) if (Math.Abs(res[i] - res[i - 1]) * dl < minL * 10) res.RemoveAt(i);
        return res;
    }
}

public static class Roots
{
    /// <summary>Root of f in [a,b] given a sign change (bisection + secant polish).</summary>
    public static double Bisect(Func<double, double> f, double a, double b, int iters = 100)
    {
        double fa = f(a), fb = f(b);
        if (fa == 0) return a;
        if (fb == 0) return b;
        for (int i = 0; i < iters; i++)
        {
            var m = 0.5 * (a + b);
            var fm = f(m);
            if (fm == 0 || Math.Abs(b - a) < 1e-15 * Math.Max(1, Math.Abs(m))) return m;
            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; } else { b = m; fb = fm; }
        }
        return 0.5 * (a + b);
    }

    /// <summary>Minimum of |f| on [a,b] by golden section (for tangential touches).</summary>
    public static double MinAbs(Func<double, double> f, double a, double b)
    {
        for (int i = 0; i < 80; i++)
        {
            var m1 = a + (b - a) * 0.382; var m2 = a + (b - a) * 0.618;
            if (Math.Abs(f(m1)) < Math.Abs(f(m2))) b = m2; else a = m1;
        }
        return 0.5 * (a + b);
    }
}
