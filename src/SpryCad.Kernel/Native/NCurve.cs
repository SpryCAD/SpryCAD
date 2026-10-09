namespace SpryCad.Kernel.Native;

/// <summary>Parametric 3D curve used by native B-rep edges.</summary>
public abstract class NCurve
{
    public abstract Vec3 Eval(double t);
    public abstract Vec3 Deriv(double t);
    /// <summary>Parameter of the point on the (unbounded) curve closest to p, near hint when periodic.</summary>
    public abstract double Param(Vec3 p, double? hint = null);
    public virtual bool Periodic => false;
    public virtual double Period => 0;
    public abstract NCurve Transformed(in Transform t);
    /// <summary>Number of samples needed over [t0,t1] for a chord tolerance.</summary>
    public abstract int Samples(double t0, double t1, double tol);

    /// <summary>Converts to the exact-export curve type.</summary>
    public abstract ExactCurve ToExact();

    public static double Wrap(double a, double center)
    {
        while (a - center > Math.PI) a -= 2 * Math.PI;
        while (a - center <= -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

public sealed class NLine : NCurve
{
    public Vec3 P { get; }
    public Vec3 D { get; }   // unit
    public NLine(Vec3 p, Vec3 d) { P = p; D = d.Normalized; }
    public override Vec3 Eval(double t) => P + D * t;
    public override Vec3 Deriv(double t) => D;
    public override double Param(Vec3 p, double? hint = null) => Vec3.Dot(p - P, D);
    public override NCurve Transformed(in Transform t) => new NLine(t.Point(P), t.Vector(D));
    public override int Samples(double t0, double t1, double tol) => 1;
    public override ExactCurve ToExact() => new LineCurve(P, D);
}

/// <summary>Circle C + R(cos t X + sin t Y), t in radians, counter-clockwise about Axis = X × Y.</summary>
public sealed class NCircle : NCurve
{
    public Vec3 C { get; }
    public Vec3 X { get; }
    public Vec3 Y { get; }
    public double R { get; }
    public Vec3 Axis => Vec3.Cross(X, Y);

    public NCircle(Vec3 c, Vec3 axis, Vec3 refDir, double r)
    {
        C = c; R = r;
        var a = axis.Normalized;
        var x = refDir - a * Vec3.Dot(refDir, a);
        if (x.Length < 1e-12) x = a.AnyPerpendicular();
        X = x.Normalized;
        Y = Vec3.Cross(a, X);
    }

    public override Vec3 Eval(double t) => C + (X * Math.Cos(t) + Y * Math.Sin(t)) * R;
    public override Vec3 Deriv(double t) => (X * -Math.Sin(t) + Y * Math.Cos(t)) * R;
    public override double Param(Vec3 p, double? hint = null)
    {
        var d = p - C;
        var a = Math.Atan2(Vec3.Dot(d, Y), Vec3.Dot(d, X));
        return hint is { } h ? Wrap(a, h) : a;
    }
    public override bool Periodic => true;
    public override double Period => 2 * Math.PI;
    public override NCurve Transformed(in Transform t)
    {
        var c = t.Point(C); var x = t.Vector(X); var y = t.Vector(Y);
        var axis = Vec3.Cross(x, y);
        var s = x.Length;
        return new NCircle(c, axis, x, R * s);
    }
    public override int Samples(double t0, double t1, double tol)
    {
        var step = 2 * Math.Acos(Math.Max(-1, 1 - Math.Min(1, tol / Math.Max(R, 1e-12))));
        return Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / Math.Max(step, 1e-4)), 1, 2000);
    }
    public override ExactCurve ToExact() => new CircleCurve(C, Axis, X, R);
}

/// <summary>Ellipse C + A cos t X + B sin t Y.</summary>
public sealed class NEllipse : NCurve
{
    public Vec3 C { get; }
    public Vec3 X { get; }
    public Vec3 Y { get; }
    public double A { get; }
    public double B { get; }

    public NEllipse(Vec3 c, Vec3 x, Vec3 y, double a, double b) { C = c; X = x.Normalized; Y = y.Normalized; A = a; B = b; }
    public override Vec3 Eval(double t) => C + X * (A * Math.Cos(t)) + Y * (B * Math.Sin(t));
    public override Vec3 Deriv(double t) => X * (-A * Math.Sin(t)) + Y * (B * Math.Cos(t));
    public override double Param(Vec3 p, double? hint = null)
    {
        var d = p - C;
        var t = Math.Atan2(Vec3.Dot(d, Y) / B, Vec3.Dot(d, X) / A);
        // Newton refinement on the distance function
        for (int i = 0; i < 20; i++)
        {
            var e = Eval(t) - p; var d1 = Deriv(t);
            var d2 = X * (-A * Math.Cos(t)) + Y * (-B * Math.Sin(t));
            var f = Vec3.Dot(e, d1); var fp = Vec3.Dot(d1, d1) + Vec3.Dot(e, d2);
            if (Math.Abs(fp) < 1e-300) break;
            var dt = f / fp; t -= dt;
            if (Math.Abs(dt) < 1e-14) break;
        }
        return hint is { } h ? Wrap(t, h) : t;
    }
    public override bool Periodic => true;
    public override double Period => 2 * Math.PI;
    public override NCurve Transformed(in Transform t)
    {
        var x = t.Vector(X); var y = t.Vector(Y);
        return new NEllipse(t.Point(C), x, y, A * x.Length, B * y.Length);
    }
    public override int Samples(double t0, double t1, double tol)
    {
        var r = Math.Max(A, B);
        var step = 2 * Math.Acos(Math.Max(-1, 1 - Math.Min(1, tol / Math.Max(Math.Min(A, B) * Math.Min(A, B) / r, 1e-12))));
        return Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / Math.Max(step, 1e-4)), 2, 2000);
    }
    public override ExactCurve ToExact() => new EllipseCurve(C, Vec3.Cross(X, Y), X, A, B);
}

/// <summary>
/// One branch of a hyperbola, C + A cosh t X + B sinh t Y (a plane across a cone, steeper than its sides: STEP's
/// hyperbola). Open: its edges run between vertices.
/// </summary>
public sealed class NHyperbola : NCurve
{
    public Vec3 C { get; }
    public Vec3 X { get; }
    public Vec3 Y { get; }
    public double A { get; }
    public double B { get; }

    public NHyperbola(Vec3 c, Vec3 x, Vec3 y, double a, double b) { C = c; X = x.Normalized; Y = y.Normalized; A = a; B = b; }
    public override Vec3 Eval(double t) => C + X * (A * Math.Cosh(t)) + Y * (B * Math.Sinh(t));
    public override Vec3 Deriv(double t) => X * (A * Math.Sinh(t)) + Y * (B * Math.Cosh(t));
    Vec3 Deriv2(double t) => X * (A * Math.Cosh(t)) + Y * (B * Math.Sinh(t));

    public override double Param(Vec3 p, double? hint = null)
    {
        // from the point's height along Y (exact on the curve), then Newton on the distance
        var t = Math.Asinh(Vec3.Dot(p - C, Y) / B);
        for (int i = 0; i < 30; i++)
        {
            var e = Eval(t) - p; var d1 = Deriv(t);
            var f = Vec3.Dot(e, d1); var fp = Vec3.Dot(d1, d1) + Vec3.Dot(e, Deriv2(t));
            if (!(fp > 1e-300)) break;
            var dt = f / fp; t -= dt;
            if (Math.Abs(dt) < 1e-15 * (1 + Math.Abs(t))) break;
        }
        return t;
    }

    public override NCurve Transformed(in Transform t)
    {
        var x = t.Vector(X); var y = t.Vector(Y);
        return new NHyperbola(t.Point(C), x, y, A * x.Length, B * y.Length);
    }

    public override int Samples(double t0, double t1, double tol)
    {
        // chord error ≤ max|C''|·h²/8, the second derivative largest at the end farthest out
        var tm = Math.Max(Math.Abs(t0), Math.Abs(t1));
        var acc = Math.Sqrt(A * A * Math.Cosh(tm) * Math.Cosh(tm) + B * B * Math.Sinh(tm) * Math.Sinh(tm));
        var h = Math.Sqrt(8 * tol / Math.Max(acc, 1e-12));
        return Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / h), 2, 2000);
    }

    public override ExactCurve ToExact() => new HyperbolaCurve(C, Vec3.Cross(X, Y), X, A, B);
}

/// <summary>
/// A parabola, C + F (t² X + 2t Y) (a plane across a cone parallel to one of its sides: STEP's parabola, F the focal
/// distance). Open: its edges run between vertices.
/// </summary>
public sealed class NParabola : NCurve
{
    public Vec3 C { get; }
    public Vec3 X { get; }
    public Vec3 Y { get; }
    public double F { get; }

    public NParabola(Vec3 c, Vec3 x, Vec3 y, double f) { C = c; X = x.Normalized; Y = y.Normalized; F = f; }
    public override Vec3 Eval(double t) => C + X * (F * t * t) + Y * (2 * F * t);
    public override Vec3 Deriv(double t) => X * (2 * F * t) + Y * (2 * F);

    public override double Param(Vec3 p, double? hint = null)
    {
        var t = Vec3.Dot(p - C, Y) / (2 * F);
        for (int i = 0; i < 30; i++)
        {
            var e = Eval(t) - p; var d1 = Deriv(t);
            var f = Vec3.Dot(e, d1); var fp = Vec3.Dot(d1, d1) + Vec3.Dot(e, X * (2 * F));
            if (!(fp > 1e-300)) break;
            var dt = f / fp; t -= dt;
            if (Math.Abs(dt) < 1e-15 * (1 + Math.Abs(t))) break;
        }
        return t;
    }

    public override NCurve Transformed(in Transform t)
    {
        var x = t.Vector(X); var y = t.Vector(Y);
        return new NParabola(t.Point(C), x, y, F * x.Length);
    }

    public override int Samples(double t0, double t1, double tol)
    {
        // the second derivative is 2F X throughout
        var h = Math.Sqrt(8 * tol / Math.Max(2 * F, 1e-12));
        return Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / h), 2, 2000);
    }

    public override ExactCurve ToExact() => new ParabolaCurve(C, Vec3.Cross(X, Y), X, F);
}

/// <summary>Cubic B-spline through exact intersection points (parameter 0..1).</summary>
public sealed class NSpline : NCurve
{
    public BSplineCurve S { get; }
    /// <summary>How far the spline may be from the exact curve it was fitted to (0: exact, e.g. imported). Tests for an
    /// edge lying on a surface allow this much: a later boolean may work at a finer tolerance than the one that fitted it.</summary>
    public double FitTol { get; init; }
    readonly Vec3[] _samples; // dense samples for closest-point seeding
    public NSpline(BSplineCurve s)
    {
        S = s;
        _samples = Enumerable.Range(0, 129).Select(i => S.Eval(i / 128.0)).ToArray();
    }
    public override Vec3 Eval(double t) => S.Eval(t);
    public override Vec3 Deriv(double t)
    {
        const double h = 1e-6;
        var a = Math.Max(0, t - h); var b = Math.Min(1, t + h);
        return (S.Eval(b) - S.Eval(a)) / (b - a);
    }
    public override double Param(Vec3 p, double? hint = null)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < _samples.Length; i++) { var d = (_samples[i] - p).LengthSq; if (d < bd) { bd = d; best = i; } }
        double lo = Math.Max(0, (best - 1) / 128.0), hi = Math.Min(1, (best + 1) / 128.0);
        // Newton in the samples' bracket; never worse than the nearest sample
        var t = S.ClosestParam(p, lo, hi, best / 128.0);
        if (!double.IsNaN(t)) return (Eval(t) - p).LengthSq <= bd ? t : best / 128.0;
        // golden section on distance (derivatives not finite)
        for (int it = 0; it < 60; it++)
        {
            var m1 = lo + (hi - lo) * 0.382; var m2 = lo + (hi - lo) * 0.618;
            if ((Eval(m1) - p).LengthSq < (Eval(m2) - p).LengthSq) hi = m2; else lo = m1;
        }
        return (lo + hi) / 2;
    }
    public override NCurve Transformed(in Transform t)
    {
        var poles = new Vec3[S.Poles.Length];
        for (int i = 0; i < poles.Length; i++) poles[i] = t.Point(S.Poles[i]);
        return new NSpline(new BSplineCurve(S.Degree, poles, S.Knots)) { FitTol = FitTol };
    }
    public override int Samples(double t0, double t1, double tol) => Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) * S.Poles.Length * 3), 2, 600);
    public override ExactCurve ToExact() => S;
}
