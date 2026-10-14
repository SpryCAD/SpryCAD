using System.Globalization;

namespace SpryCad.Core;

/// <summary>
/// A value with its units: lengths in millimetres and angles in degrees, whatever units they were written in, with
/// the power of each (<see cref="Length"/> 1 is a length, 2 an area; both 0 a plain number).
/// </summary>
public readonly record struct Quantity(double Value, int Length = 0, int Angle = 0)
{
    public bool IsNumber => Length == 0 && Angle == 0;
    public bool IsLength => Length == 1 && Angle == 0;
    public bool IsAngle => Length == 0 && Angle == 1;
    public bool SameUnits(Quantity o) => Length == o.Length && Angle == o.Angle;
    public static Quantity Mm(double mm) => new(mm, 1);
    public static Quantity Deg(double deg) => new(deg, 0, 1);

    /// <summary>The base units ("mm", "mm²", "deg"; "" for a plain number).</summary>
    public string UnitText
    {
        get
        {
            static string Pow(string u, int n) => n == 1 ? u : n == 2 ? u + "²" : n == 3 ? u + "³" : $"{u}^{n}";
            var above = new List<string>();
            if (Length > 0) above.Add(Pow("mm", Length));
            if (Angle > 0) above.Add(Pow("deg", Angle));
            var below = new List<string>();
            if (Length < 0) below.Add(Pow("mm", -Length));
            if (Angle < 0) below.Add(Pow("deg", -Angle));
            var s = string.Join("·", above);
            if (below.Count > 0) s = (s.Length == 0 ? "1" : s) + "/" + string.Join("·", below);
            return s;
        }
    }

    public override string ToString() => IsNumber ? Expr.Format(Value) : Expr.Format(Value) + " " + UnitText;
}

/// <summary>
/// Small expression evaluator for parameters and equations ("W/2 + 5mm", "sqrt(A^2+B^2)").
/// Values carry units: a number may be followed by one (<c>10mm</c>, <c>2in</c>, <c>1.5 ft</c>, <c>30deg</c>, <c>45°</c>,
/// <c>0.5rad</c>), as may a bracket (<c>(W/2)in</c>), and units mix freely (<c>1in + 2mm</c>). Lengths and angles are worked
/// in millimetres and degrees. Adding or comparing values of different kinds (a length and a plain number) is an error,
/// except a plain 0; multiplying and dividing combine the units (length × length is an area, length / length a number).
/// Trig functions take angles, or plain numbers as degrees, and the inverse ones give degrees as plain numbers, like
/// SolidWorks equations. Comparisons
/// (<c>&lt; &lt;= &gt; &gt;= == !=</c>) and logic (<c>and or not</c>, or <c>&amp;&amp; || !</c>) give 1 for true and 0 for false
/// ("Teeth &lt; 30 or Bore &gt; 20mm"), for conditions such as a feature's Suppress When; any value but 0 counts as true.
/// </summary>
public static class Expr
{
    public sealed class ExprException(string msg) : Exception(msg);

    /// <summary>The value with its units; names are looked up with theirs.</summary>
    public static Quantity Evaluate(string text, Func<string, Quantity?>? lookup = null)
    {
        var p = new Parser(text, lookup);
        var v = p.ParseExpr();
        p.SkipWs();
        if (!p.End) throw new ExprException($"Unexpected '{p.Rest}'");
        if (double.IsNaN(v.Value) || double.IsInfinity(v.Value)) throw new ExprException("Result is not a number");
        return v;
    }

    public static bool TryEvaluate(string? text, Func<string, Quantity?>? lookup, out Quantity value, out string? error)
    {
        value = default; error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Empty value"; return false; }
        try { value = Evaluate(text, lookup); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The value in base units (millimetres, degrees), names looked up as plain numbers.</summary>
    public static double Eval(string text, Func<string, double?>? lookup = null) => Evaluate(text, Plain(lookup)).Value;

    public static bool TryEval(string? text, Func<string, double?>? lookup, out double value, out string? error)
    {
        var ok = TryEvaluate(text, Plain(lookup), out var q, out error);
        value = q.Value;
        return ok;
    }

    static Func<string, Quantity?>? Plain(Func<string, double?>? lookup) => lookup == null ? null : n => lookup(n) is { } v ? new Quantity(v) : null;

    /// <summary>
    /// A length in millimetres: one with units as it is, a plain number as millimetres (the units a part keeps).
    /// Throws <see cref="ExprException"/> for anything else (an area, an angle).
    /// </summary>
    public static double Length(string text, Func<string, Quantity?>? lookup)
    {
        var q = Evaluate(text, lookup);
        return q.IsLength || q.IsNumber ? q.Value : throw new ExprException($"Expected a length, not {Kind(q)}");
    }

    /// <summary>An angle in degrees: one with units as it is, a plain number as degrees.</summary>
    public static double Angle(string text, Func<string, Quantity?>? lookup)
    {
        var q = Evaluate(text, lookup);
        return q.IsAngle || q.IsNumber ? q.Value : throw new ExprException($"Expected an angle, not {Kind(q)}");
    }

    /// <summary>A plain number (a count, a ratio): one with units is an error.</summary>
    public static double Number(string text, Func<string, Quantity?>? lookup)
    {
        var q = Evaluate(text, lookup);
        return q.IsNumber ? q.Value : throw new ExprException($"Expected a number without units, not {Kind(q)}");
    }

    /// <summary>What a value is, for messages ("a length (25.4 mm)").</summary>
    public static string Kind(Quantity q) => q.IsNumber ? $"a plain number ({Format(q.Value)})" : q.IsLength ? $"a length ({q})"
        : q.IsAngle ? $"an angle ({q})" : q.Length == 2 && q.Angle == 0 ? $"an area ({q})" : $"a value in {q.UnitText} ({q})";

    /// <summary>Identifiers referenced by an expression (units after numbers and brackets are not names).</summary>
    public static IEnumerable<string> Identifiers(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                int s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' || text[i] == '@')) i++;
                var id = text[s..i];
                int j = i; while (j < text.Length && text[j] == ' ') j++;
                if (j < text.Length && text[j] == '(') continue; // function call
                if (IsKeyword(id)) continue;
                if (AfterValue(text, s) && IsUnit(id)) continue;
                yield return id;
            }
            else if (char.IsDigit(text[i]) || text[i] == '.')
            {
                // a number, exponent and all ("1e5" names no "e5")
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                if (i + 1 < text.Length && text[i] is 'e' or 'E' && (char.IsDigit(text[i + 1]) || text[i + 1] is '-' or '+'))
                {
                    i += 2;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                }
            }
            else if (text[i] == '"')
            {
                if (AfterValue(text, i)) { i++; continue; } // inches (10")
                int s = ++i;
                while (i < text.Length && text[i] != '"') i++;
                yield return text[s..i];
                i++;
            }
            else i++;
        }
    }

    // whether text[at] follows a number or a closing bracket (where a unit goes)
    static bool AfterValue(string text, int at)
    {
        int k = at - 1;
        while (k >= 0 && text[k] == ' ') k--;
        return k >= 0 && (char.IsDigit(text[k]) || text[k] is '.' or ')');
    }

    /// <summary>The logic words, never variable names.</summary>
    public static bool IsKeyword(string id) => id.ToLowerInvariant() is "and" or "or" or "not";

    /// <summary>Whether a condition holds (any value but 0).</summary>
    public static bool Truthy(double v) => Math.Abs(v) > 1e-9;

    /// <summary>The units a value may be written in: millimetres or degrees per one, and whether it is an angle (else a length).</summary>
    static readonly (string name, double factor, bool angle)[] UnitTable =
    {
        ("mm", 1, false), ("cm", 10, false), ("m", 1000, false), ("in", 25.4, false), ("ft", 304.8, false), ("\"", 25.4, false),
        ("deg", 1, true), ("°", 1, true), ("rad", 180 / Math.PI, true),
    };

    static bool IsUnit(string id) => UnitTable.Any(u => u.name.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the text is a bare number ("10", "-2.5", "1e3"), without units or anything else.</summary>
    public static bool IsPlainNumber(string? text) =>
        text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    static readonly System.Text.RegularExpressions.Regex LiteralRx = new(
        @"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*(?<u>mm|cm|m|in|ft|""|deg|°|rad)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether the text is one number, with or without a unit ("10", "2.5in", "45 deg"), rather than a formula;
    /// <paramref name="unit"/> is its unit as written, lower case ("" for none).
    /// </summary>
    public static bool IsLiteral(string? text, out string unit)
    {
        unit = "";
        if (text == null || LiteralRx.Match(text) is not { Success: true } m) return false;
        unit = m.Groups["u"].Value.ToLowerInvariant();
        return true;
    }

    /// <summary>Millimetres (or degrees) per one of a unit as <see cref="IsLiteral"/> gives it; 1 for none.</summary>
    public static double UnitFactor(string unit) =>
        UnitTable.FirstOrDefault(u => u.name.Equals(unit, StringComparison.OrdinalIgnoreCase)) is { name: not null } t ? t.factor : 1;

    sealed class Parser(string s, Func<string, Quantity?>? lookup)
    {
        int _i;
        public bool End => _i >= s.Length;
        public string Rest => s[_i..];

        public void SkipWs() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        bool Eat(char c) { SkipWs(); if (_i < s.Length && s[_i] == c) { _i++; return true; } return false; }

        bool EatOp(string op)
        {
            SkipWs();
            if (string.CompareOrdinal(s, _i, op, 0, op.Length) != 0) return false;
            _i += op.Length; return true;
        }

        // logic words: whole words only ("order" is a variable, not "or" and "der")
        bool EatWord(string w)
        {
            SkipWs();
            if (string.Compare(s, _i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            var after = _i + w.Length;
            if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] is '_' or '.' or '@')) return false;
            _i = after; return true;
        }

        static Quantity B(bool b) => new(b ? 1 : 0);

        // equal within rounding: 0.1 + 0.2 == 0.3, 25.4mm == 1in
        static double Tol(double a, double b) => 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

        /// <summary>
        /// The two in the same units, for adding or comparing: a plain 0 takes the other's units ("W > 0"); values of
        /// different kinds are an error.
        /// </summary>
        static (Quantity a, Quantity b) Match(Quantity a, Quantity b, string op)
        {
            if (a.SameUnits(b)) return (a, b);
            if (a.IsNumber && a.Value == 0) return (a with { Length = b.Length, Angle = b.Angle }, b);
            if (b.IsNumber && b.Value == 0) return (a, b with { Length = a.Length, Angle = a.Angle });
            var plain = a.IsNumber ? a : b.IsNumber ? b : (Quantity?)null;
            var hint = plain is { } p ? $"; give {Format(p.Value)} its units" : "";
            throw new ExprException($"Units do not match: {a} {op} {b}{hint}");
        }

        public Quantity ParseExpr() => ParseOr();

        Quantity ParseOr()
        {
            var v = ParseAnd();
            while (EatOp("||") || EatWord("or")) { var r = ParseAnd(); v = B(Truthy(v.Value) || Truthy(r.Value)); }
            return v;
        }

        Quantity ParseAnd()
        {
            var v = ParseNot();
            while (EatOp("&&") || EatWord("and")) { var r = ParseNot(); v = B(Truthy(v.Value) && Truthy(r.Value)); }
            return v;
        }

        Quantity ParseNot()
        {
            SkipWs();
            if (_i < s.Length && s[_i] == '!' && !(_i + 1 < s.Length && s[_i + 1] == '=')) { _i++; return B(!Truthy(ParseNot().Value)); }
            if (EatWord("not")) return B(!Truthy(ParseNot().Value));
            return ParseCompare();
        }

        Quantity ParseCompare()
        {
            var v = ParseSum();
            while (true)
            {
                // (two-character operators first: "<=" is not "<" then "=")
                string? op = EatOp("<=") ? "<=" : EatOp(">=") ? ">=" : EatOp("==") ? "==" : EatOp("!=") || EatOp("<>") ? "!="
                    : EatOp("<") ? "<" : EatOp(">") ? ">" : EatOp("=") ? "==" : null;
                if (op == null) return v;
                var (a, b) = Match(v, ParseSum(), op);
                var tol = Tol(a.Value, b.Value);
                v = B(op switch
                {
                    "<" => a.Value < b.Value - tol,
                    "<=" => a.Value <= b.Value + tol,
                    ">" => a.Value > b.Value + tol,
                    ">=" => a.Value >= b.Value - tol,
                    "==" => Math.Abs(a.Value - b.Value) <= tol,
                    _ => Math.Abs(a.Value - b.Value) > tol,
                });
            }
        }

        Quantity ParseSum()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) { var (a, b) = Match(v, ParseTerm(), "+"); v = a with { Value = a.Value + b.Value }; }
                else if (Eat('-')) { var (a, b) = Match(v, ParseTerm(), "-"); v = a with { Value = a.Value - b.Value }; }
                else return v;
            }
        }

        Quantity ParseTerm()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat('*')) { var r = ParseUnary(); v = new(v.Value * r.Value, v.Length + r.Length, v.Angle + r.Angle); }
                else if (Eat('/'))
                {
                    var d = ParseUnary();
                    if (d.Value == 0) throw new ExprException("Division by zero");
                    v = new(v.Value / d.Value, v.Length - d.Length, v.Angle - d.Angle);
                }
                else return v;
            }
        }

        Quantity ParseUnary()
        {
            if (Eat('-')) { var v = ParseUnary(); return v with { Value = -v.Value }; }
            if (Eat('+')) return ParseUnary();
            return ParsePow();
        }

        Quantity ParsePow()
        {
            var b = ParseAtom();
            if (Eat('^')) return Pow(b, ParseUnary());
            return b;
        }

        static Quantity Pow(Quantity b, Quantity e)
        {
            if (!e.IsNumber) throw new ExprException($"A power has no units: {e}");
            if (b.IsNumber) return new(Math.Pow(b.Value, e.Value));
            // the units' powers must come out whole: an area's square root is a length, a length's is nothing
            double l = b.Length * e.Value, a = b.Angle * e.Value;
            if (Math.Abs(l - Math.Round(l)) > 1e-9 || Math.Abs(a - Math.Round(a)) > 1e-9)
                throw new ExprException($"{b} to the power {Format(e.Value)} has no units");
            return new(Math.Pow(b.Value, e.Value), (int)Math.Round(l), (int)Math.Round(a));
        }

        Quantity ParseAtom()
        {
            SkipWs();
            if (End) throw new ExprException("Unexpected end of expression");
            var c = s[_i];
            if (c == '(')
            {
                _i++;
                var v = ParseExpr();
                if (!Eat(')')) throw new ExprException("Missing ')'");
                return WithUnit(v);
            }
            if (char.IsDigit(c) || c == '.')
            {
                int st = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                if (_i < s.Length && (s[_i] == 'e' || s[_i] == 'E') && _i + 1 < s.Length && (char.IsDigit(s[_i + 1]) || s[_i + 1] == '-' || s[_i + 1] == '+'))
                {
                    _i += 2;
                    while (_i < s.Length && char.IsDigit(s[_i])) _i++;
                }
                if (!double.TryParse(s[st.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new ExprException($"Bad number '{s[st.._i]}'");
                return WithUnit(new Quantity(num));
            }
            if (c == '"')
            {
                int st = ++_i;
                while (_i < s.Length && s[_i] != '"') _i++;
                var name = s[st.._i];
                _i++;
                return Lookup(name);
            }
            if (char.IsLetter(c) || c == '_')
            {
                int st = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_' || s[_i] == '.' || s[_i] == '@')) _i++;
                var name = s[st.._i];
                if (Eat('('))
                {
                    var args = new List<Quantity>();
                    if (!Eat(')'))
                    {
                        do args.Add(ParseExpr()); while (Eat(','));
                        if (!Eat(')')) throw new ExprException("Missing ')'");
                    }
                    return Call(name, args);
                }
                switch (name.ToLowerInvariant())
                {
                    case "pi": return new(Math.PI);
                    case "e": return new(Math.E);
                }
                return Lookup(name);
            }
            throw new ExprException($"Unexpected '{c}'");
        }

        // an optional unit after a number or a bracket: the value times one of it
        Quantity WithUnit(Quantity v)
        {
            int at = _i;
            SkipWs();
            foreach (var (u, f, angle) in UnitTable)
            {
                if (string.Compare(s, _i, u, 0, u.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                var after = _i + u.Length;
                if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] is '_' or '.' or '@' or '(')) continue;
                _i = after;
                return angle ? new(v.Value * f, v.Length, v.Angle + 1) : new(v.Value * f, v.Length + 1, v.Angle);
            }
            _i = at;
            return v;
        }

        Quantity Lookup(string name)
        {
            var v = lookup?.Invoke(name);
            if (v == null) throw new ExprException($"Unknown variable '{name}'");
            return v.Value;
        }

        static double Rad(double d) => d * Math.PI / 180;
        // (degrees as a plain number, as angles without units are: "atan(b/a) + 5" stays a sum of numbers)
        static Quantity Deg(double r) => new(r * 180 / Math.PI);

        static Quantity Call(string name, List<Quantity> a)
        {
            Quantity A(int i) => i < a.Count ? a[i] : throw new ExprException($"{name}: missing argument");
            // an angle, or a plain number as degrees
            double Ang(int i) => A(i) is var q && (q.IsAngle || q.IsNumber) ? Rad(q.Value) : throw new ExprException($"{name}: expected an angle, not {Kind(q)}");
            double N(int i) => A(i) is var q && q.IsNumber ? q.Value : throw new ExprException($"{name}: expected a number without units, not {Kind(q)}");
            Quantity Same(Func<double, double, double> f)
            {
                if (a.Count == 0) throw new ExprException($"{name}: no args");
                var v = a[0];
                for (int i = 1; i < a.Count; i++) { var (x, y) = Match(v, a[i], ","); v = x with { Value = f(x.Value, y.Value) }; }
                return v;
            }
            Quantity Keep(Func<double, double> f) => A(0) with { Value = f(A(0).Value) };
            return name.ToLowerInvariant() switch
            {
                "sin" => new(Math.Sin(Ang(0))),
                "cos" => new(Math.Cos(Ang(0))),
                "tan" => new(Math.Tan(Ang(0))),
                "asin" => Deg(Math.Asin(N(0))),
                "acos" => Deg(Math.Acos(N(0))),
                "atan" => Deg(Math.Atan(N(0))),
                "atan2" => Match(A(0), A(1), ",") is var (y, x) ? Deg(Math.Atan2(y.Value, x.Value)) : default,
                "sqrt" => Pow(A(0), new(0.5)),
                "abs" => Keep(Math.Abs),
                "min" => Same(Math.Min),
                "max" => Same(Math.Max),
                "round" => Keep(v => Math.Round(v, a.Count > 1 ? (int)N(1) : 0, MidpointRounding.AwayFromZero)),
                "floor" => Keep(Math.Floor),
                "ceil" => Keep(Math.Ceiling),
                "int" => Keep(Math.Truncate),
                "exp" => new(Math.Exp(N(0))),
                "log" => new(Math.Log(N(0))),
                "log10" => new(Math.Log10(N(0))),
                "pow" => Pow(A(0), A(1)),
                "if" => Truthy(A(0).Value) ? A(1) : A(2),
                // choose(2, a, b, c) = b
                "choose" => a.Count < 2 ? throw new ExprException("choose: needs an index and values")
                    : (int)Math.Round(N(0)) is var k && k >= 1 && k < a.Count ? a[k] : throw new ExprException($"choose: index {Format(N(0))} is not 1 to {a.Count - 1}"),
                _ => throw new ExprException($"Unknown function '{name}'"),
            };
        }
    }

    public static string Format(double v)
    {
        if (Math.Abs(v) < 1e-12) v = 0;
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
