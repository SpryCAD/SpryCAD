using System.Globalization;

namespace SpryCad.Core;

/// <summary>
/// Small expression evaluator for parameters and equations ("W/2 + 5", "sqrt(A^2+B^2)").
/// Trig functions use degrees, like SolidWorks equations. Comparisons (<c>&lt; &lt;= &gt; &gt;= == !=</c>) and logic
/// (<c>and or not</c>, or <c>&amp;&amp; || !</c>) give 1 for true and 0 for false ("Teeth &lt; 30 or Bore &gt; 20"),
/// for conditions such as a feature's Suppress When; any value but 0 counts as true.
/// </summary>
public static class Expr
{
    public sealed class ExprException(string msg) : Exception(msg);

    public static double Eval(string text, Func<string, double?>? lookup = null)
    {
        var p = new Parser(text, lookup);
        var v = p.ParseExpr();
        p.SkipWs();
        if (!p.End) throw new ExprException($"Unexpected '{p.Rest}'");
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new ExprException("Result is not a number");
        return v;
    }

    public static bool TryEval(string? text, Func<string, double?>? lookup, out double value, out string? error)
    {
        value = 0; error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Empty value"; return false; }
        try { value = Eval(text, lookup); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Identifiers referenced by an expression.</summary>
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
                yield return id;
            }
            else if (text[i] == '"')
            {
                int s = ++i;
                while (i < text.Length && text[i] != '"') i++;
                yield return text[s..i];
                i++;
            }
            else i++;
        }
    }

    /// <summary>The logic words, never variable names.</summary>
    public static bool IsKeyword(string id) => id.ToLowerInvariant() is "and" or "or" or "not";

    /// <summary>Whether a condition holds (any value but 0).</summary>
    public static bool Truthy(double v) => Math.Abs(v) > 1e-9;

    sealed class Parser(string s, Func<string, double?>? lookup)
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

        static double B(bool b) => b ? 1 : 0;

        // equal within rounding: 0.1 + 0.2 == 0.3, 25.4 == 1in
        static double Tol(double a, double b) => 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

        public double ParseExpr() => ParseOr();

        double ParseOr()
        {
            var v = ParseAnd();
            while (EatOp("||") || EatWord("or")) { var r = ParseAnd(); v = B(Truthy(v) || Truthy(r)); }
            return v;
        }

        double ParseAnd()
        {
            var v = ParseNot();
            while (EatOp("&&") || EatWord("and")) { var r = ParseNot(); v = B(Truthy(v) && Truthy(r)); }
            return v;
        }

        double ParseNot()
        {
            SkipWs();
            if (_i < s.Length && s[_i] == '!' && !(_i + 1 < s.Length && s[_i + 1] == '=')) { _i++; return B(!Truthy(ParseNot())); }
            if (EatWord("not")) return B(!Truthy(ParseNot()));
            return ParseCompare();
        }

        double ParseCompare()
        {
            var v = ParseSum();
            while (true)
            {
                // (two-character operators first: "<=" is not "<" then "=")
                string? op = EatOp("<=") ? "<=" : EatOp(">=") ? ">=" : EatOp("==") ? "==" : EatOp("!=") || EatOp("<>") ? "!="
                    : EatOp("<") ? "<" : EatOp(">") ? ">" : EatOp("=") ? "==" : null;
                if (op == null) return v;
                var r = ParseSum();
                var tol = Tol(v, r);
                v = B(op switch
                {
                    "<" => v < r - tol,
                    "<=" => v <= r + tol,
                    ">" => v > r + tol,
                    ">=" => v >= r - tol,
                    "==" => Math.Abs(v - r) <= tol,
                    _ => Math.Abs(v - r) > tol,
                });
            }
        }

        double ParseSum()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        double ParseTerm()
        {
            var v = ParseUnary();
            while (true)
            {
                if (Eat('*')) v *= ParseUnary();
                else if (Eat('/'))
                {
                    var d = ParseUnary();
                    if (d == 0) throw new ExprException("Division by zero");
                    v /= d;
                }
                else return v;
            }
        }

        double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParsePow();
        }

        double ParsePow()
        {
            var b = ParseAtom();
            if (Eat('^')) return Math.Pow(b, ParseUnary());
            return b;
        }

        double ParseAtom()
        {
            SkipWs();
            if (End) throw new ExprException("Unexpected end of expression");
            var c = s[_i];
            if (c == '(')
            {
                _i++;
                var v = ParseExpr();
                if (!Eat(')')) throw new ExprException("Missing ')'");
                return v;
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
                // optional unit suffix
                SkipWs();
                if (TryUnit(out var factor)) num *= factor;
                return num;
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
                    var args = new List<double>();
                    if (!Eat(')'))
                    {
                        do args.Add(ParseExpr()); while (Eat(','));
                        if (!Eat(')')) throw new ExprException("Missing ')'");
                    }
                    return Call(name, args);
                }
                switch (name.ToLowerInvariant())
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }
                return Lookup(name);
            }
            throw new ExprException($"Unexpected '{c}'");
        }

        bool TryUnit(out double factor)
        {
            factor = 1;
            foreach (var (u, f) in Units)
            {
                if (string.Compare(s, _i, u, 0, u.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var after = _i + u.Length;
                    if (after < s.Length && char.IsLetterOrDigit(s[after])) continue;
                    _i = after; factor = f; return true;
                }
            }
            return false;
        }

        // Units are converted to the document base (mm / degrees). "in" and "deg" are the common cases.
        static readonly (string, double)[] Units = { ("mm", 1), ("cm", 10), ("in", 25.4), ("deg", 1), ("\"", 25.4) };

        double Lookup(string name)
        {
            var v = lookup?.Invoke(name);
            if (v == null) throw new ExprException($"Unknown variable '{name}'");
            return v.Value;
        }

        static double Rad(double d) => d * Math.PI / 180;
        static double Deg(double r) => r * 180 / Math.PI;

        static double Call(string name, List<double> a)
        {
            double A(int i) => i < a.Count ? a[i] : throw new ExprException($"{name}: missing argument");
            return name.ToLowerInvariant() switch
            {
                "sin" => Math.Sin(Rad(A(0))),
                "cos" => Math.Cos(Rad(A(0))),
                "tan" => Math.Tan(Rad(A(0))),
                "asin" => Deg(Math.Asin(A(0))),
                "acos" => Deg(Math.Acos(A(0))),
                "atan" => Deg(Math.Atan(A(0))),
                "atan2" => Deg(Math.Atan2(A(0), A(1))),
                "sqrt" => Math.Sqrt(A(0)),
                "abs" => Math.Abs(A(0)),
                "min" => a.Count == 0 ? throw new ExprException("min: no args") : a.Min(),
                "max" => a.Count == 0 ? throw new ExprException("max: no args") : a.Max(),
                "round" => Math.Round(A(0), a.Count > 1 ? (int)A(1) : 0, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(A(0)),
                "ceil" => Math.Ceiling(A(0)),
                "int" => Math.Truncate(A(0)),
                "exp" => Math.Exp(A(0)),
                "log" => Math.Log(A(0)),
                "log10" => Math.Log10(A(0)),
                "pow" => Math.Pow(A(0), A(1)),
                "if" => Truthy(A(0)) ? A(1) : A(2),
                // choose(2, a, b, c) = b
                "choose" => a.Count < 2 ? throw new ExprException("choose: needs an index and values")
                    : (int)Math.Round(A(0)) is var k && k >= 1 && k < a.Count ? a[k] : throw new ExprException($"choose: index {Format(A(0))} is not 1 to {a.Count - 1}"),
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
