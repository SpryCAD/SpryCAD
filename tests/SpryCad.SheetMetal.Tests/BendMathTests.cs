namespace SpryCad.SheetMetal.Tests;

/// <summary>Bend allowance, setbacks and flange references (sheet metal spec section 5), against hand calculation.</summary>
static class BendMathTests
{
    const double Deg = Math.PI / 180;

    /// <summary>A one-material profile: 1.5 mm sheet, R 1.5, the allowance method and data given.</summary>
    public static ShopProfile Profile(string method, string k = "", bool interpolate = false, params (string angle, string value)[] table)
    {
        var s = StarterProfile.Tables.ToDictionary(x => x.Key, x => x.Clone());
        s[ProfileTables.Materials].Rows = new() { new(new[] { "Steel" }, "7850", "0", "0", "") };
        s[ProfileTables.Gauges].Rows = new() { new(new[] { "Steel", "1.5" }, "1.5", "0.05", "0.05") };
        s[ProfileTables.Gauges].Units = null;
        s[ProfileTables.Tooling].Rows = new() { new(new[] { "Steel", "1.5", "V12" }, "1", "1.5", "88", "12", "88", "1", "1.5", "6", "") };
        s[ProfileTables.Allowance].Rows = new() { new(new[] { "Steel", "1.5", "V12" }, method, k) };
        s[ProfileTables.BendTable].Rows = table.Select(r => new ProfileRow(new[] { "Steel", "1.5", "V12", r.angle }, r.value)).ToList();
        s[ProfileTables.BendTable].Interpolate = interpolate;
        return new ShopProfile("Test", s.Values);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Bend math: 90° bend, K-factor, bend table and bend deduction agree with hand calculation", () =>
        {
            // θ = 90°, R = 1.5, t = 1.5, K = 0.44: BA = π/2 (1.5 + 0.66) = 3.39292; OSSB = 3 tan 45° = 3; ISSB = 1.5; BD = 6 − BA
            double th = 90 * Deg, r = 1.5, s = 1.5;
            var ba = BendMath.KFactorAllowance(th, r, s, 0.44);
            t.Near(ba, Math.PI / 2 * 2.16, 1e-12);
            t.Near(BendMath.Ossb(th, r, s), 3, 1e-12);
            t.Near(BendMath.Issb(th, r), 1.5, 1e-12);
            t.Near(BendMath.Deduction(th, r, s, ba), 6 - ba, 1e-12);
            t.Near(BendMath.AllowanceFromDeduction(th, r, s, 6 - ba), ba, 1e-12);

            // the same through the shop profile, by each method
            var kf = SheetRules.Resolve(Profile("K-factor", "0.44"), "Steel", "1.5");
            t.Eq(kf.Method, AllowanceMethod.KFactor);
            t.Near(kf.Allowance(th).Allowance, ba, 1e-12);
            var bt = SheetRules.Resolve(Profile("BendTable", "", false, ("90", "3.4")), "Steel", "1.5");
            t.Near(bt.Allowance(th).Allowance, 3.4, 1e-12);
            t.True(bt.Allowance(th).Source.Detail.Contains("Bend table 90°"), bt.Allowance(th).Source.Detail);
            var bd = SheetRules.Resolve(Profile("BendDeduction", "", false, ("90", "2.6")), "Steel", "1.5");
            t.Near(bd.Allowance(th).Allowance, 6 - 2.6, 1e-12);
        });

        t.Test("Bend math: flat length of an L bracket in OML, IML and tangent references", () =>
        {
            // flanges 50 and 30 to the bend: OML each loses OSSB, IML each ISSB, tangent nothing; plus BA
            double th = 90 * Deg, r = 1.5, s = 1.5, ba = BendMath.KFactorAllowance(th, r, s, 0.44);
            double Flat(FlangeReference f) => BendMath.FlatContribution(50, f, th, r, s) + BendMath.FlatContribution(30, f, th, r, s) + ba;
            t.Near(Flat(FlangeReference.OutsideMoldLine), 47 + 27 + ba, 1e-12);
            t.Near(Flat(FlangeReference.InsideMoldLine), 48.5 + 28.5 + ba, 1e-12);
            t.Near(Flat(FlangeReference.Tangent), 80 + ba, 1e-12);
            // and back
            foreach (var f in Enum.GetValues<FlangeReference>())
                t.Near(BendMath.DimensionOf(BendMath.FlatContribution(50, f, th, r, s), f, th, r, s), 50, 1e-12);
        });

        t.Test("Bend math: shallow and sharp bends use the bend (deflection) angle", () =>
        {
            // a shallow 45° bend: OSSB (R + t) tan 22.5°; a 135° bend: tan 67.5°
            t.Near(BendMath.Ossb(45 * Deg, 1, 1), 2 * Math.Tan(22.5 * Deg), 1e-12);
            t.Near(BendMath.Ossb(135 * Deg, 1, 1), 2 * Math.Tan(67.5 * Deg), 1e-12);
            t.Near(BendMath.KFactorAllowance(45 * Deg, 1, 1, 0.5), Math.PI / 4 * 1.5, 1e-12);
        });

        t.Test("Bend math: DIN 6935 (k from R/t; no allowance under 15°)", () =>
        {
            // R/s = 1: k = 0.65; neutral fiber at 0.65 s/2
            t.Near(BendMath.DinK(1, 1), 0.65, 1e-12);
            t.Near(BendMath.DinK(5, 1), 1, 1e-12);
            t.Near(BendMath.DinK(0.1, 1), 0.15, 1e-12);
            t.Near(BendMath.Din6935Allowance(90 * Deg, 1, 1), Math.PI / 2 * (1 + 0.325), 1e-12);
            t.Near(BendMath.Din6935Allowance(10 * Deg, 1, 1), 2 * BendMath.Ossb(10 * Deg, 1, 1), 1e-12);
            var din = SheetRules.Resolve(Profile("DIN 6935"), "Steel", "1.5");
            t.Near(din.Allowance(90 * Deg).Allowance, BendMath.Din6935Allowance(90 * Deg, 1.5, 1.5), 1e-12);
        });

        t.Test("Bend math: bend tables interpolate only within range and only when allowed", () =>
        {
            var rows = new[] { ("30", "1.0"), ("90", "3.0"), ("120", "4.0") };
            var on = SheetRules.Resolve(Profile("BendTable", "", true, rows), "Steel", "1.5");
            t.Near(on.Allowance(60 * Deg).Allowance, 2.0, 1e-12);
            t.Near(on.Allowance(105 * Deg).Allowance, 3.5, 1e-12);
            t.Throws<ProfileMissException>(() => on.Allowance(20 * Deg), "never extrapolated");
            t.Throws<ProfileMissException>(() => on.Allowance(150 * Deg), "never extrapolated");
            var off = SheetRules.Resolve(Profile("BendTable", "", false, rows), "Steel", "1.5");
            t.Near(off.Allowance(90 * Deg).Allowance, 3.0, 1e-12);
            t.Throws<ProfileMissException>(() => off.Allowance(60 * Deg), "does not interpolate");
            // no table at all for the material: an error, not a default
            var none = SheetRules.Resolve(Profile("BendTable"), "Steel", "1.5");
            t.Throws<ProfileMissException>(() => none.Allowance(90 * Deg), "no bend table");
        });

        t.Test("Rule expressions: units, variables, functions; unknown names refused", () =>
        {
            var v = new Dictionary<string, double> { ["t"] = 0.3, ["R"] = 2 };
            t.Near(RuleExpr.Eval("max(t, 0.5mm)", v), 0.5, 1e-12);
            t.Near(RuleExpr.Eval("R + t + 0.02in", v), 2.3 + 0.508, 1e-12);
            t.Near(RuleExpr.Eval("2*(R + t)", v), 4.6, 1e-12);
            t.Near(RuleExpr.Eval("-2^2"), -4, 1e-12);
            t.Near(RuleExpr.Eval("0.005in"), 0.127, 1e-12);
            t.Near(RuleExpr.Eval("1e-3 mm"), 0.001, 1e-15);
            t.Throws<RuleExprException>(() => RuleExpr.Eval("t + q", v), "\"q\" is not known");
            t.Throws<RuleExprException>(() => RuleExpr.Eval("max(t", v), ")");
            t.Throws<RuleExprException>(() => RuleExpr.Eval("1/0"), "not a number");
        });
    }
}
