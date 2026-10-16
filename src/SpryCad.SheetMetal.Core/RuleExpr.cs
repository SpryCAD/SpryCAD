using System.Globalization;

namespace SpryCad.SheetMetal;

/// <summary>An expression in a shop profile cell or an override that cannot be read or evaluated.</summary>
public sealed class RuleExprException(string message) : Exception(message);

/// <summary>
/// The expressions shop profile cells and overrides hold (sheet metal spec section 3): "max(t, 0.5mm)",
/// "R + t + 0.02in", "0.2*t". Numbers may carry a unit: mm (the default), in, deg (degrees are the unit of angle
/// columns). Variables are given by the caller: t (thickness) and R (inside radius) for rules. Operators + - * / ^,
/// parentheses, and the functions min, max, abs, sqrt, round, floor, ceil; the constant pi.
/// Deterministic: the same text gives the same number everywhere (invariant culture, no state).
/// </summary>
public static class RuleExpr
{
    public static double Eval(string text, IReadOnlyDictionary<string, double>? vars = null)
    {
        var p = new Parser(text, vars);
        var v = p.Expression();
        p.SkipSpace();
        if (!p.AtEnd) throw new RuleExprException($"\"{text}\": unexpected \"{p.Rest}\".");
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new RuleExprException($"\"{text}\" is not a number.");
        return v;
    }

    public static bool TryEval(string text, IReadOnlyDictionary<string, double>? vars, out double value, out string? error)
    {
        try { value = Eval(text, vars); error = null; return true; }
        catch (RuleExprException ex) { value = 0; error = ex.Message; return false; }
    }

    /// <summary>The variables an expression uses (to check a cell before it is evaluated).</summary>
    public static IEnumerable<string> Variables(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try { new Parser(text, null, names).Expression(); } catch (RuleExprException) { }
        return names;
    }

    sealed class Parser(string text, IReadOnlyDictionary<string, double>? vars, HashSet<string>? collect = null)
    {
        int _i;
        public bool AtEnd => _i >= text.Length;
        public string Rest => text[_i..];

        public void SkipSpace() { while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++; }

        bool Eat(char c)
        {
            SkipSpace();
            if (_i < text.Length && text[_i] == c) { _i++; return true; }
            return false;
        }

        public double Expression()
        {
            var v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else return v;
            }
        }

        double Term()
        {
            var v = Unary();
            while (true)
            {
                if (Eat('*')) v *= Unary();
                else if (Eat('/')) v /= Unary();
                else return v;
            }
        }

        // a sign binds looser than a power: -2^2 is -4
        double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Power();
        }

        double Power()
        {
            var v = Primary();
            return Eat('^') ? Math.Pow(v, Unary()) : v;
        }

        double Primary()
        {
            SkipSpace();
            if (AtEnd) throw new RuleExprException($"\"{text}\" ends too soon.");
            if (Eat('('))
            {
                var v = Expression();
                if (!Eat(')')) throw new RuleExprException($"\"{text}\": a \")\" is missing.");
                return v;
            }
            char c = text[_i];
            if (char.IsDigit(c) || c == '.')
            {
                int s = _i;
                while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.')) _i++;
                if (_i < text.Length && (text[_i] == 'e' || text[_i] == 'E') && _i + 1 < text.Length
                    && (char.IsDigit(text[_i + 1]) || (text[_i + 1] is '+' or '-' && _i + 2 < text.Length && char.IsDigit(text[_i + 2]))))
                {
                    _i += 2;
                    while (_i < text.Length && char.IsDigit(text[_i])) _i++;
                }
                if (!double.TryParse(text.AsSpan(s, _i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new RuleExprException($"\"{text}\": \"{text[s.._i]}\" is not a number.");
                return n * Unit();
            }
            if (char.IsLetter(c) || c == '_' || c == 'θ')
            {
                var name = Name();
                if (Eat('('))
                {
                    var args = new List<double>();
                    if (!Eat(')'))
                    {
                        do args.Add(Expression()); while (Eat(','));
                        if (!Eat(')')) throw new RuleExprException($"\"{text}\": a \")\" is missing after {name}(.");
                    }
                    return Call(name, args);
                }
                if (name == "pi") return Math.PI;
                collect?.Add(name);
                if (vars != null && vars.TryGetValue(name, out var v)) return v;
                if (collect != null) return 1;
                throw new RuleExprException($"\"{text}\": \"{name}\" is not known here.");
            }
            throw new RuleExprException($"\"{text}\": unexpected \"{c}\".");
        }

        string Name()
        {
            int s = _i;
            while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] == '_' || text[_i] == 'θ')) _i++;
            return text[s.._i];
        }

        /// <summary>The unit after a number (millimetres per unit): none or mm 1, in 25.4, deg 1.</summary>
        double Unit()
        {
            SkipSpace();
            int s = _i;
            if (_i < text.Length && char.IsLetter(text[_i]))
            {
                var u = Name();
                switch (u)
                {
                    case "mm": return 1;
                    case "in": return 25.4;
                    case "deg": return 1;
                }
                _i = s; // not a unit: a variable after a number is an error the caller sees
            }
            return 1;
        }

        double Call(string name, List<double> a)
        {
            double One() => a.Count == 1 ? a[0] : throw new RuleExprException($"{name}() takes one value.");
            return name switch
            {
                "min" when a.Count > 0 => a.Min(),
                "max" when a.Count > 0 => a.Max(),
                "abs" => Math.Abs(One()),
                "sqrt" => Math.Sqrt(One()),
                "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                _ => throw new RuleExprException($"\"{name}\" is not a function."),
            };
        }
    }
}
