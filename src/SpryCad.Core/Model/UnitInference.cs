using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpryCad.Core.Model;

/// <summary>
/// Which of a part's variables are lengths, which plain numbers (a count, a ratio), worked out from where they are
/// used as dimensions are in physics (docs/units.md), and the part's values given their units from that.
/// </summary>
public static class UnitInference
{
    /// <summary>A value as the rebuild read it: its text, and what it was read as.</summary>
    public sealed record ValueUse(string Text, ValueKind Kind);

    /// <summary>What a variable was found to be.</summary>
    public enum VarKind { Unknown, Number, Angle, Length, Area, Other, Conflict }

    public sealed class Result
    {
        public Dictionary<string, VarKind> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Each text's numbers without a unit that are lengths (their positions, where the unit goes).</summary>
        internal Dictionary<string, List<int>> LengthLiterals { get; } = new(StringComparer.Ordinal);
        /// <summary>Texts that could not be parsed (left as they are).</summary>
        public List<string> Unparsed { get; } = new();
        /// <summary>Uses that contradict the others: their text and what it was read as.</summary>
        public List<string> Conflicts { get; } = new();
    }

    // ------------------------------------------------------------------------------------------
    // the expressions, as their parse: numbers with where they end (where a unit is written)

    abstract record Node;
    sealed record Num(double Value, string? Unit, int End) : Node;
    sealed record Const() : Node;                       // pi, e: plain numbers
    sealed record Var(string Name) : Node;
    sealed record Bin(string Op, Node L, Node R) : Node; // + - * / ^ and comparisons ("cmp"), logic ("logic")
    sealed record Neg(Node X) : Node;
    sealed record Not(Node X) : Node;
    sealed record Call(string Name, List<Node> Args) : Node;
    sealed record Paren(Node X, string? Unit) : Node;

    static readonly string[] LengthUnits = { "mm", "cm", "m", "in", "ft", "\"" };
    static readonly string[] AngleUnits = { "deg", "°", "rad" };

    sealed class P(string s)
    {
        int i;
        void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        bool Eat(char c) { Ws(); if (i < s.Length && s[i] == c) { i++; return true; } return false; }
        bool Op(string op) { Ws(); if (string.CompareOrdinal(s, i, op, 0, op.Length) != 0) return false; i += op.Length; return true; }
        bool Word(string w)
        {
            Ws();
            if (string.Compare(s, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            var a = i + w.Length;
            if (a < s.Length && (char.IsLetterOrDigit(s[a]) || s[a] is '_' or '.' or '@')) return false;
            i = a; return true;
        }
        public Node Parse() { var n = Or(); Ws(); if (i < s.Length) throw new FormatException(); return n; }
        Node Or() { var v = And(); while (Op("||") || Word("or")) v = new Bin("logic", v, And()); return v; }
        Node And() { var v = NotP(); while (Op("&&") || Word("and")) v = new Bin("logic", v, NotP()); return v; }
        Node NotP()
        {
            Ws();
            if (i < s.Length && s[i] == '!' && !(i + 1 < s.Length && s[i + 1] == '=')) { i++; return new Not(NotP()); }
            if (Word("not")) return new Not(NotP());
            return Cmp();
        }
        Node Cmp()
        {
            var v = Sum();
            while (Op("<=") || Op(">=") || Op("==") || Op("!=") || Op("<>") || Op("<") || Op(">") || Op("=")) v = new Bin("cmp", v, Sum());
            return v;
        }
        Node Sum()
        {
            var v = Term();
            while (true)
            {
                if (Eat('+')) v = new Bin("+", v, Term());
                else if (Eat('-')) v = new Bin("-", v, Term());
                else return v;
            }
        }
        Node Term()
        {
            var v = Unary();
            while (true)
            {
                if (Eat('*')) v = new Bin("*", v, Unary());
                else if (Eat('/')) v = new Bin("/", v, Unary());
                else return v;
            }
        }
        Node Unary()
        {
            if (Eat('-')) return new Neg(Unary());
            if (Eat('+')) return Unary();
            var b = Atom();
            return Eat('^') ? new Bin("^", b, Unary()) : b;
        }
        string? Unit()
        {
            int at = i;
            Ws();
            foreach (var u in LengthUnits.Concat(AngleUnits))
            {
                if (string.Compare(s, i, u, 0, u.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                var a = i + u.Length;
                if (a < s.Length && (char.IsLetterOrDigit(s[a]) || s[a] is '_' or '.' or '@' or '(')) continue;
                i = a;
                return u.ToLowerInvariant();
            }
            i = at;
            return null;
        }
        Node Atom()
        {
            Ws();
            if (i >= s.Length) throw new FormatException();
            var c = s[i];
            if (c == '(') { i++; var v = Or(); if (!Eat(')')) throw new FormatException(); return new Paren(v, Unit()); }
            if (char.IsDigit(c) || c == '.')
            {
                int st = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-' || s[i + 1] == '+'))
                { i += 2; while (i < s.Length && char.IsDigit(s[i])) i++; }
                var v = double.Parse(s[st..i], NumberStyles.Float, CultureInfo.InvariantCulture);
                int end = i;
                return new Num(v, Unit(), end);
            }
            if (c == '"') { int st = ++i; while (i < s.Length && s[i] != '"') i++; var name = s[st..i]; i++; return new Var(name); }
            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '@')) i++;
                var name = s[st..i];
                if (Eat('('))
                {
                    var args = new List<Node>();
                    if (!Eat(')')) { do args.Add(Or()); while (Eat(',')); if (!Eat(')')) throw new FormatException(); }
                    return new Call(name.ToLowerInvariant(), args);
                }
                return name.ToLowerInvariant() is "pi" or "e" ? new Const() : new Var(name);
            }
            throw new FormatException();
        }
    }

    // ------------------------------------------------------------------------------------------
    // powers of length: linear forms in the unknowns (a variable's power; a plain number's, free to be what its place needs)

    sealed class Form
    {
        public readonly Dictionary<int, double> C = new();
        public double K;
        public static Form Of(double k) => new() { K = k };
        public static Form Unknown(int u) { var f = new Form(); f.C[u] = 1; return f; }
        public Form Plus(Form o, double sign = 1)
        {
            var f = new Form { K = K + sign * o.K };
            foreach (var (u, c) in C) f.C[u] = c;
            foreach (var (u, c) in o.C) f.C[u] = f.C.GetValueOrDefault(u) + sign * c;
            return f;
        }
        public Form Times(double k)
        {
            var f = new Form { K = K * k };
            foreach (var (u, c) in C) f.C[u] = c * k;
            return f;
        }
    }

    sealed class Solver
    {
        // rows in reduced form: a pivot unknown, its row (coefficients of the others, constant); unknown = -(row · x + k)
        readonly Dictionary<int, Form> _rows = new();

        Form Reduce(Form f)
        {
            var r = f.Plus(Form.Of(0));
            foreach (var (p, row) in _rows)
                if (r.C.TryGetValue(p, out var c) && Math.Abs(c) > 1e-12)
                {
                    r.C.Remove(p);
                    // p = -(row): substitute
                    r = r.Plus(row.Times(-c));
                }
            foreach (var u in r.C.Where(kv => Math.Abs(kv.Value) < 1e-12).Select(kv => kv.Key).ToList()) r.C.Remove(u);
            return r;
        }

        /// <summary>Adds form = 0; false when it contradicts what is already known (not added).</summary>
        public bool Add(Form f)
        {
            var r = Reduce(f);
            if (r.C.Count == 0) return Math.Abs(r.K) < 1e-9;
            var (p, c) = r.C.MaxBy(kv => Math.Abs(kv.Value));
            r.C.Remove(p);
            var row = r.Times(1 / c); // p + row = 0
            foreach (var key in _rows.Keys.ToList())
            {
                var other = _rows[key];
                if (other.C.TryGetValue(p, out var oc) && Math.Abs(oc) > 1e-12)
                {
                    other.C.Remove(p);
                    _rows[key] = other.Plus(row.Times(-oc));
                }
            }
            _rows[p] = row;
            return true;
        }

        /// <summary>An unknown's value, and whether it is determined (not resting on a free one).</summary>
        public (double value, bool determined) Value(int u)
        {
            if (!_rows.TryGetValue(u, out var row)) return (0, false);
            return (-row.K, row.C.All(kv => Math.Abs(kv.Value) < 1e-12));
        }
    }

    sealed class Builder
    {
        public readonly Solver S = new();
        public readonly Dictionary<string, int> Vars = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Angled = new(StringComparer.OrdinalIgnoreCase);
        // a text's plain numbers: their unknowns, where each ends
        public readonly Dictionary<string, List<(int unknown, int end)>> Literals = new(StringComparer.Ordinal);
        int _next;
        public int New() => _next++;
        public int Var(string name) => Vars.TryGetValue(name, out var u) ? u : Vars[name] = New();

        // the constraints a text's parse makes, and its power; literal unknowns shared by every use of the same text
        public (Form form, List<Form> constraints) Of(string text, Node n)
        {
            var cons = new List<Form>();
            var lits = Literals.TryGetValue(text, out var have) ? null : new List<(int, int)>();
            int litIndex = 0;
            // (a number written as a factor, "2*Gap" or "T/4", is a plain number: the variable it scales has the units)
            Form Walk(Node x, bool factor = false)
            {
                switch (x)
                {
                    case Num num:
                        if (num.Unit != null) return Form.Of(LengthUnits.Contains(num.Unit) ? 1 : 0);
                        {
                            int u;
                            if (lits != null) { u = New(); lits.Add((u, num.End)); }
                            else u = have![litIndex].unknown;
                            litIndex++;
                            if (factor) { cons.Add(Form.Unknown(u)); return Form.Of(0); }
                            return Form.Unknown(u);
                        }
                    case Const: return Form.Of(0);
                    case Var v: return Form.Unknown(Var(v.Name));
                    case Neg ng: return Walk(ng.X);
                    case Not nt: Walk(nt.X); return Form.Of(0);
                    case Paren p:
                        {
                            var inner = Walk(p.X);
                            if (p.Unit == null) return inner;
                            cons.Add(inner);
                            return Form.Of(LengthUnits.Contains(p.Unit) ? 1 : 0);
                        }
                    case Bin b:
                        {
                            switch (b.Op)
                            {
                                case "*": return Walk(b.L, true).Plus(Walk(b.R, true));
                                case "/": return Walk(b.L, true).Plus(Walk(b.R, true), -1);
                                case "^":
                                    {
                                        var bf = Walk(b.L);
                                        var ef = Walk(b.R);
                                        cons.Add(ef);
                                        if (b.R is Num { Unit: null } en) return bf.Times(en.Value);
                                        if (b.R is Neg { X: Num { Unit: null } en2 }) return bf.Times(-en2.Value);
                                        cons.Add(bf);
                                        return Form.Of(0);
                                    }
                                case "logic": Walk(b.L); Walk(b.R); return Form.Of(0);
                                default:
                                    {
                                        // + - and comparisons: the same power
                                        var l = Walk(b.L); var r = Walk(b.R);
                                        cons.Add(l.Plus(r, -1));
                                        return b.Op == "cmp" ? Form.Of(0) : l;
                                    }
                            }
                        }
                    case Call c:
                        {
                            var a = c.Args.Select(arg => Walk(arg)).ToList();
                            Form Arg(int i) => i < a.Count ? a[i] : Form.Of(0);
                            switch (c.Name)
                            {
                                case "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "exp" or "log" or "log10":
                                    foreach (var f in a) cons.Add(f);
                                    return Form.Of(0);
                                case "atan2":
                                    if (a.Count > 1) cons.Add(a[0].Plus(a[1], -1));
                                    return Form.Of(0);
                                case "sqrt": return Arg(0).Times(0.5);
                                case "pow":
                                    if (c.Args.Count > 1 && c.Args[1] is Num { Unit: null } pn) { cons.Add(Arg(1)); return Arg(0).Times(pn.Value); }
                                    cons.Add(Arg(0)); cons.Add(Arg(1)); return Form.Of(0);
                                case "abs" or "floor" or "ceil" or "int": return Arg(0);
                                case "round": if (a.Count > 1) cons.Add(a[1]); return Arg(0);
                                case "min" or "max":
                                    for (int i = 1; i < a.Count; i++) cons.Add(a[i].Plus(a[0], -1));
                                    return Arg(0);
                                case "if":
                                    if (a.Count > 2) cons.Add(a[1].Plus(a[2], -1));
                                    return Arg(1);
                                case "choose":
                                    if (a.Count > 0) cons.Add(a[0]);
                                    for (int i = 2; i < a.Count; i++) cons.Add(a[i].Plus(a[1], -1));
                                    return Arg(1);
                                default: throw new FormatException();
                            }
                        }
                    default: throw new FormatException();
                }
            }
            var form = Walk(n);
            if (lits != null) Literals[text] = lits;
            return (form, cons);
        }
    }

    static Node? Parse(string text) { try { return new P(text).Parse(); } catch (Exception) { return null; } }

    /// <summary>Whether any of the part's equations or design table cells has a number written without a unit (so may need one).</summary>
    public static bool HasPlainNumbers(CadDocument doc)
    {
        static bool Plain(Node? n) => n switch
        {
            Num num => num.Unit == null,
            Bin b => Plain(b.L) || Plain(b.R),
            Neg ng => Plain(ng.X),
            Not nt => Plain(nt.X),
            Paren p => Plain(p.X),
            Call c => c.Args.Any(Plain),
            _ => false,
        };
        return doc.Equations.Any(e => Plain(Parse(e.Expr)))
            || (doc.Tables ?? new()).Any(t => t.Rows.Any(r => r.Values.Any(v => Plain(Parse(v)))));
    }

    /// <summary>
    /// A part from before values kept their units (<see cref="CadDocument.ValueUnits"/> not set) brought up to date when
    /// opened: its values given units where it has numbers without them (<see cref="AddUnits"/>), its value units then
    /// its units. The number of values given units, and why none were where some could have been.
    /// </summary>
    public static (int changed, string? why) Upgrade(CadDocument doc, Func<CadDocument, List<ValueUse>> read, Func<CadDocument, string, ValueKind, double?> evaluate)
    {
        if (doc.ValueUnits != null) return (0, null);
        var result = HasPlainNumbers(doc) ? AddUnits(doc, read(doc), evaluate) : (0, null);
        doc.ValueUnits = doc.Units;
        return result;
    }

    /// <summary>
    /// The kinds of the part's variables, from the values a rebuild read (<paramref name="uses"/>), the equations and
    /// the design tables that set them, and the Suppress When conditions.
    /// </summary>
    public static Result Infer(CadDocument doc, IEnumerable<ValueUse> uses)
    {
        var res = new Result();
        var b = new Builder();
        void Add(string text, Form? root, string what)
        {
            if (Parse(text) is not { } n) { if (!res.Unparsed.Contains(text)) res.Unparsed.Add(text); return; }
            var (form, cons) = b.Of(text, n);
            var all = new List<Form>(cons);
            if (root != null) all.Add(form.Plus(root, -1));
            // (a contradiction: the use set aside, its variables flagged)
            foreach (var f in all)
                if (!b.S.Add(f))
                {
                    res.Conflicts.Add($"{text} ({what})");
                    foreach (var id in Expr.Identifiers(text)) res.Kinds[id] = VarKind.Conflict;
                    return;
                }
        }
        // the equations and the design tables' columns: a variable is what its value is
        foreach (var e in doc.Equations)
            if (!string.IsNullOrWhiteSpace(e.Name))
                Add(e.Expr, Form.Unknown(b.Var(e.Name.Trim())), $"{e.Name} =");
        foreach (var t in doc.Tables ?? new())
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (t.Columns[c].StartsWith('$')) continue; // (a property: text)
                foreach (var r in t.Rows)
                    if (c < r.Values.Count && r.Values[c].Trim().Length > 0)
                        Add(r.Values[c], Form.Unknown(b.Var(t.Columns[c].Trim())), $"{t.Name}: {t.Columns[c]}");
            }
        // where they are used: a length's power 1, an angle's or a count's 0
        foreach (var u in uses.Distinct())
        {
            // (a number alone: millimeters or degrees as files keep them, nothing to learn)
            if (!Expr.Identifiers(u.Text).Any()) continue;
            Add(u.Text, Form.Of(u.Kind == ValueKind.Length ? 1 : 0), u.Kind.ToString().ToLowerInvariant());
            if (u.Kind == ValueKind.Angle) foreach (var id in Expr.Identifiers(u.Text)) b.Angled.Add(id);
        }
        foreach (var f in doc.Features)
            if (!string.IsNullOrWhiteSpace(f.SuppressWhen)) Add(f.SuppressWhen!, null, $"{f.Name}: Suppress When");

        // each variable's kind; each text's plain numbers that are lengths
        foreach (var (name, u) in b.Vars)
        {
            if (res.Kinds.TryGetValue(name, out var k) && k == VarKind.Conflict) continue;
            // (one nothing pins down, as a value used nowhere: no kind)
            var (v, determined) = b.S.Value(u);
            if (!determined) { res.Kinds[name] = VarKind.Unknown; continue; }
            res.Kinds[name] = Math.Abs(v - 1) < 1e-9 ? VarKind.Length : Math.Abs(v) < 1e-9 ? (b.Angled.Contains(name) ? VarKind.Angle : VarKind.Number)
                : Math.Abs(v - 2) < 1e-9 ? VarKind.Area : VarKind.Other;
        }
        foreach (var (text, lits) in b.Literals)
        {
            var at = lits.Where(l => b.S.Value(l.unknown) is var (v, d) && d && Math.Abs(v - 1) < 1e-9).Select(l => l.end).ToList();
            if (at.Count > 0) res.LengthLiterals[text] = at;
        }
        return res;
    }

    /// <summary>
    /// The text with the part's value units written after each of its plain numbers that is a length ("OD - 32" →
    /// "OD - 32mm"); as it is where a variable it uses has no kind worked out (its numbers left as plain as it is).
    /// </summary>
    public static string WithUnits(Result r, string text, string unit)
    {
        if (!r.LengthLiterals.TryGetValue(text, out var at)) return text;
        if (Expr.Identifiers(text).Any(id => r.Kinds.GetValueOrDefault(id) is VarKind.Unknown or VarKind.Conflict or VarKind.Other)) return text;
        var s = text;
        foreach (var end in at.OrderByDescending(e => e)) s = s[..end] + unit + s[end..];
        return s;
    }

    /// <summary>A variable's equation written with its unit where it is a plain number of a length ("150" → "150mm").</summary>
    public static string ConstantWithUnit(Result r, string name, string text, string unit) =>
        Expr.IsPlainNumber(text) && r.Kinds.GetValueOrDefault(name) == VarKind.Length ? text.Trim() + unit : WithUnits(r, text, unit);

    /// <summary>
    /// The part's values given their units (docs/units.md): each length's plain numbers written with the part's value
    /// units, in its equations, design tables, Suppress When conditions, and the features' values and sketch dimensions
    /// worked out from variables (a number alone is left as it is: millimeters, as files keep them). Checked first:
    /// every value the rebuild read (<paramref name="uses"/>) is worked out again from the new texts by
    /// <paramref name="evaluate"/> (its value, given the part and a text and its kind; null when it does not evaluate),
    /// and if any differs nothing is changed. The number of values given units (0 when none or when it could not be
    /// checked), and why not where it was not done.
    /// </summary>
    public static (int changed, string? why) AddUnits(CadDocument doc, IReadOnlyCollection<ValueUse> uses, Func<CadDocument, string, ValueKind, double?> evaluate)
    {
        var r = Infer(doc, uses);
        var unit = doc.PlainUnits.Symbol();
        var copy = doc.Clone();
        int n = 0;
        string Swap(string s, Func<string, string> f) { var t = f(s); if (t != s) n++; return t; }
        foreach (var e in copy.Equations) e.Expr = Swap(e.Expr, x => ConstantWithUnit(r, e.Name.Trim(), x, unit));
        foreach (var t in copy.Tables ?? new())
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (t.Columns[c].StartsWith('$')) continue;
                foreach (var row in t.Rows)
                    if (c < row.Values.Count) row.Values[c] = Swap(row.Values[c], x => ConstantWithUnit(r, t.Columns[c].Trim(), x, unit));
            }
        for (int i = 0; i < copy.Features.Count; i++)
        {
            var f = copy.Features[i];
            if (f.SuppressWhen != null) f.SuppressWhen = Swap(f.SuppressWhen, x => WithUnits(r, x, unit));
            var node = JsonNode.Parse(CadDocument.JsonOf(f));
            var before = n;
            Replace(node, x => Expr.Identifiers(x).Any() ? Swap(x, y => WithUnits(r, y, unit)) : x);
            if (n != before && node!.Deserialize(typeof(Feature), Serialization.Compact) is Feature nf) copy.Features[i] = nf;
        }
        if (n == 0) return (0, r.Conflicts.Count > 0 ? "Some values contradict each other: " + string.Join("; ", r.Conflicts.Take(3)) : null);
        // the check: every value read, the same from the new texts
        var swapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in uses)
        {
            var after = Expr.Identifiers(u.Text).Any() ? WithUnits(r, u.Text, unit) : u.Text;
            var a = evaluate(doc, u.Text, u.Kind);
            var b = evaluate(copy, after, u.Kind);
            if (a is { } x && (b is not { } y || Math.Abs(x - y) > 1e-9 * Math.Max(1, Math.Abs(x))))
                return (0, $"{u.Text} would not be the same with units ({Expr.Format(x)} → {(b is { } yy ? Expr.Format(yy) : "an error")})");
        }
        foreach (var f in doc.Features.Where(f => !string.IsNullOrWhiteSpace(f.SuppressWhen)))
        {
            var cf = copy.Find(f.Id)!;
            if (evaluate(doc, f.SuppressWhen!, ValueKind.Number) is { } x && evaluate(copy, cf.SuppressWhen!, ValueKind.Number) is not { } y)
                return (0, $"{f.Name}'s Suppress When would not work with units");
        }
        doc.Equations = copy.Equations;
        doc.Tables = copy.Tables;
        doc.Features.Clear();
        doc.Features.AddRange(copy.Features);
        return (n, null);
    }

    // each value property's text replaced (as VariableUses.Rename does)
    static void Replace(JsonNode? node, Func<string, string> swap)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList())
                {
                    var v = o[k];
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) { if (VariableUses.IsValue(k) && swap(s) is var t && t != s) o[k] = t; }
                    else Replace(v, swap);
                }
                break;
            case JsonArray a:
                foreach (var v in a) Replace(v, swap);
                break;
        }
    }
}
