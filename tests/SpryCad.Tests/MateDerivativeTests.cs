using SpryCad.Core.Assembly;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// The mates' exact derivatives (<see cref="Mate.Directional"/>, the solver's Jacobian) against central differences of
/// <see cref="Mate.Residuals"/>: every type with every pair of reference kinds it takes, its options (aligned, side,
/// inside, offsets and limits on either side), at random placements, either side moved.
/// </summary>
static class MateDerivativeTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Mate derivatives: exact, as central differences of the residuals give them, for every type and option", () =>
        {
            var rnd = new Random(3);
            Vec3 V(double s) => new(s * (2 * rnd.NextDouble() - 1), s * (2 * rnd.NextDouble() - 1), s * (2 * rnd.NextDouble() - 1));
            Transform RandomPlacement() => Transform.Translation(V(80)) * Transform.Rotation(Vec3.Zero, V(1).Normalized, Math.PI * rnd.NextDouble());
            MateGeom Geom(MateGeomKind k) => new(k, V(20), V(1).Normalized, k == MateGeomKind.Plane ? 0 : 3 + 5 * rnd.NextDouble());
            var kinds = new[] { MateGeomKind.Plane, MateGeomKind.Axis, MateGeomKind.Point };
            var variants = new List<Action<Mate>>
            {
                _ => { },
                m => m.Aligned = true,
                m => m.Side = -1,
                m => m.Inside = true,
                m => m.Value = 7,
                m => { m.Value = 20; m.Min = 5; m.Max = 9; },     // limits: usually outside them
                m => { m.Value = 20; m.Min = -1e9; m.Max = 1e9; }, // limits: inside them (no change)
            };
            int checkedRows = 0, exact = 0;
            foreach (var type in Enum.GetValues<MateType>())
                foreach (var ka in kinds)
                    foreach (var kb in kinds)
                    {
                        if (!Mate.Supports(type, ka, kb)) continue;
                        foreach (var variant in variants)
                            for (int trial = 0; trial < 6; trial++)
                            {
                                var m = new Mate { Type = type, Value = type == MateType.Angle ? 35 : 0, RatioA = 2, RatioB = 3 };
                                variant(m);
                                var (la, lb) = (Geom(ka), Geom(kb));
                                var (ta, tb) = (RandomPlacement(), RandomPlacement());
                                var (a, b) = (la.Placed(ta), lb.Placed(tb));
                                m.Capture(a, b, ta, tb);
                                if (type == MateType.Lock) { ta = RandomPlacement(); a = la.Placed(ta); }
                                variant(m); // (Capture chose the alignment and side; the variant's kept)
                                // an Angle's limits in degrees: usually outside 10..20
                                if (type == MateType.Angle && m.Min is > -1e9) { m.Min = 10; m.Max = 20; }
                                foreach (var sideA in new[] { true, false })
                                {
                                    var pivot = V(30);
                                    var tw = new Mate.Twist(V(1), V(1), pivot);
                                    var zero = new Mate.Twist(Vec3.Zero, Vec3.Zero, pivot);
                                    var exactRows = new List<double>();
                                    if (!m.Directional(a, b, ta, tb, sideA ? tw : zero, sideA ? zero : tw, exactRows)) continue;
                                    // central differences along the same motion (a turn about the pivot, then a move)
                                    const double h = 1e-6;
                                    Transform Step(double s) => Transform.Translation(tw.M * s) * Transform.Rotation(pivot, tw.W.Normalized, tw.W.Length * s);
                                    List<double> At(double s)
                                    {
                                        var (sa, sb) = sideA ? (Step(s) * ta, tb) : (ta, Step(s) * tb);
                                        var r = new List<double>();
                                        m.Residuals(la.Placed(sa), lb.Placed(sb), sa, sb, r);
                                        return r;
                                    }
                                    var (rp, rm) = (At(h), At(-h));
                                    t.True(exactRows.Count == rp.Count, $"{type} {ka}-{kb}: {exactRows.Count} rows, the residuals have {rp.Count}");
                                    for (int q = 0; q < rp.Count; q++)
                                    {
                                        var fd = (rp[q] - rm[q]) / (2 * h);
                                        // a limit's penalty has a kink where the value meets the limit: skip a row right on it
                                        if (m.Min != null && Math.Abs(rp[q] - rm[q]) > 0 && (rp[q] == 0 || rm[q] == 0)) continue;
                                        t.True(Math.Abs(fd - exactRows[q]) <= 1e-5 * (1 + Math.Abs(fd)),
                                            $"{type} {ka}-{kb} ({variant.Method.Name}), side {(sideA ? "A" : "B")}, row {q}: differences {fd}, exact {exactRows[q]}");
                                        checkedRows++;
                                    }
                                    exact++;
                                }
                            }
                    }
            t.True(exact > 500 && checkedRows > 1500, $"only {exact} cases, {checkedRows} rows checked");
        });
    }
}
