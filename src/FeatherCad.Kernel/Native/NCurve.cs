namespace FeatherCad.Kernel.Native;

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

/// <summary>Cubic B-spline through exact intersection points (parameter 0..1).</summary>
public sealed class NSpline : NCurve
{
    public BSplineCurve S { get; }
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
        // golden section on distance
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
        return new NSpline(new BSplineCurve(S.Degree, poles, S.Knots));
    }
    public override int Samples(double t0, double t1, double tol) => Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) * S.Poles.Length * 3), 2, 600);
    public override ExactCurve ToExact() => S;
}
