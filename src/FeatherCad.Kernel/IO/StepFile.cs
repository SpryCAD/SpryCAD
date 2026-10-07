using System.Globalization;
using System.Text;

namespace FeatherCad.Kernel;

/// <summary>Reference to another instance (#123) in a STEP file.</summary>
public readonly record struct StepRef(int Id);

/// <summary>Enumeration value (.T., .UNSPECIFIED.).</summary>
public readonly record struct StepEnum(string Value);

/// <summary>Typed parameter, e.g. LENGTH_MEASURE(1.0).</summary>
public sealed record StepTyped(string Type, object? Value);

/// <summary>The "*" (derived) parameter.</summary>
public sealed class StepDerived { public static readonly StepDerived Instance = new(); }

/// <summary>
/// One instance of a STEP data section: a simple entity has one part (type + arguments); a complex instance
/// (#1=(A(...) B(...))) has one part per partial entity. Arguments are double, string, <see cref="StepRef"/>,
/// <see cref="StepEnum"/>, <see cref="StepTyped"/>, List&lt;object?&gt;, null ($) or <see cref="StepDerived"/>.
/// </summary>
public sealed class StepInstance
{
    public int Id { get; init; }
    public List<(string Type, List<object?> Args)> Parts { get; } = new();
    public string Type => Parts.Count == 1 ? Parts[0].Type : "(" + string.Join(" ", Parts.Select(p => p.Type)) + ")";
    public List<object?> Args => Parts[0].Args;
    /// <summary>Arguments of the named partial entity (complex instances), or null.</summary>
    public List<object?>? Part(string type) => Parts.FirstOrDefault(p => p.Type == type).Args;
    public bool Is(string type) => Parts.Any(p => p.Type == type);
}

/// <summary>Reader for ISO 10303-21 (STEP Part 21) exchange files: the DATA section as instances by id.</summary>
public sealed class StepFile
{
    public Dictionary<int, StepInstance> Instances { get; } = new();
    public List<(string Type, List<object?> Args)> Header { get; } = new();

    public StepInstance this[StepRef r] => Instances.TryGetValue(r.Id, out var i) ? i : throw new InvalidDataException($"STEP: #{r.Id} is referenced but not defined.");
    public StepInstance Get(object? arg) => arg is StepRef r ? this[r] : throw new InvalidDataException("STEP: expected a reference.");
    public IEnumerable<StepInstance> OfType(string type) => Instances.Values.Where(i => i.Is(type)).OrderBy(i => i.Id);

    public static StepFile Parse(byte[] data) => Parse(Encoding.Latin1.GetString(data));

    public static StepFile Parse(string text)
    {
        var f = new StepFile();
        var p = new Parser(text);
        p.SkipWs();
        if (!p.TryKeyword("ISO-10303-21")) throw new InvalidDataException("Not a STEP file (no ISO-10303-21 header).");
        p.Expect(';');
        bool inData = false;
        while (true)
        {
            p.SkipWs();
            if (p.End) break;
            if (p.TryKeyword("END-ISO-10303-21")) break;
            if (p.TryKeyword("HEADER")) { p.Expect(';'); continue; }
            if (p.TryKeyword("ENDSEC")) { p.Expect(';'); inData = false; continue; }
            if (p.TryKeyword("DATA"))
            {
                // DATA; or DATA('name', ...); (multiple data sections, Part 21 edition 3)
                p.SkipWs();
                if (p.Peek == '(') p.ParseList();
                p.Expect(';');
                inData = true;
                continue;
            }
            if (p.Peek == '#' && inData)
            {
                var id = p.ParseInstanceId();
                p.Expect('=');
                var inst = new StepInstance { Id = id };
                p.SkipWs();
                if (p.Peek == '(')
                {
                    // complex instance: (A(...) B(...) ...)
                    p.Next();
                    while (true)
                    {
                        p.SkipWs();
                        if (p.Peek == ')') { p.Next(); break; }
                        var name = p.ParseKeyword();
                        inst.Parts.Add((name, p.ParseList()));
                    }
                }
                else
                {
                    var name = p.ParseKeyword();
                    inst.Parts.Add((name, p.ParseList()));
                }
                p.Expect(';');
                f.Instances[id] = inst;
                continue;
            }
            // header entity (FILE_DESCRIPTION(...);) or anything else outside DATA
            var hname = p.ParseKeyword();
            f.Header.Add((hname, p.ParseList()));
            p.Expect(';');
        }
        return f;
    }

    sealed class Parser
    {
        readonly string _s;
        int _i;
        public Parser(string s) { _s = s; }
        public bool End => _i >= _s.Length;
        public char Peek => _i < _s.Length ? _s[_i] : '\0';
        public char Next() => _s[_i++];

        public void SkipWs()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    var e = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = e < 0 ? _s.Length : e + 2;
                    continue;
                }
                break;
            }
        }

        public void Expect(char c)
        {
            SkipWs();
            if (Peek != c) throw Error($"expected '{c}'");
            _i++;
        }

        public bool TryKeyword(string k)
        {
            SkipWs();
            if (string.CompareOrdinal(_s, _i, k, 0, k.Length) != 0) return false;
            var after = _i + k.Length;
            if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) return false;
            _i = after;
            return true;
        }

        public string ParseKeyword()
        {
            SkipWs();
            int start = _i;
            if (Peek == '!') _i++; // user-defined keyword
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '-')) _i++;
            if (_i == start) throw Error("expected an entity name");
            return _s[start.._i].ToUpperInvariant();
        }

        /// <summary>Instance ids past int.MaxValue, numbered -1, -2, … (no real id is negative, so they never collide).</summary>
        readonly Dictionary<string, int> _bigIds = new();

        public int ParseInstanceId()
        {
            Expect('#');
            int start = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            var digits = _s.AsSpan(start, _i - start);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;
            if (digits.Length == 0) throw Error("expected an instance number");
            // any size is valid Part 21; SolidWorks 2024 also writes a missing reference as #18446744073709551615 (-1
            // as an unsigned 64-bit number), e.g. in PERSON_AND_ORGANIZATION: harmless unless followed
            var key = digits.ToString();
            if (!_bigIds.TryGetValue(key, out id)) _bigIds[key] = id = -(_bigIds.Count + 1);
            return id;
        }

        public List<object?> ParseList()
        {
            Expect('(');
            var list = new List<object?>();
            SkipWs();
            if (Peek == ')') { _i++; return list; }
            while (true)
            {
                list.Add(ParseValue());
                SkipWs();
                if (Peek == ',') { _i++; continue; }
                if (Peek == ')') { _i++; return list; }
                throw Error("expected ',' or ')'");
            }
        }

        object? ParseValue()
        {
            SkipWs();
            var c = Peek;
            switch (c)
            {
                case '$': _i++; return null;
                case '*': _i++; return StepDerived.Instance;
                case '#': return new StepRef(ParseInstanceId());
                case '(': return ParseList();
                case '\'': return ParseString();
                case '"': // binary
                    {
                        _i++; int start = _i;
                        while (_i < _s.Length && _s[_i] != '"') _i++;
                        var v = _s[start.._i]; _i++;
                        return v;
                    }
                case '.':
                    {
                        _i++; int start = _i;
                        while (_i < _s.Length && _s[_i] != '.') _i++;
                        var v = _s[start.._i]; _i++;
                        return new StepEnum(v.ToUpperInvariant());
                    }
            }
            if (c == '-' || c == '+' || char.IsDigit(c))
            {
                int start = _i; _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] is '.' or 'E' or 'e' or '-' or '+')) _i++;
                return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(c) || c == '!')
            {
                var name = ParseKeyword();
                var args = ParseList();
                return new StepTyped(name, args.Count == 1 ? args[0] : args);
            }
            throw Error($"unexpected '{c}'");
        }

        string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                var ch = _s[_i++];
                if (ch == '\'')
                {
                    if (Peek == '\'') { sb.Append('\''); _i++; continue; }
                    return Decode(sb.ToString());
                }
                sb.Append(ch);
            }
            throw Error("unterminated string");
        }

        /// <summary>Part 21 control directives: \X2\hhhh...\X0\ (UTF-16), \X\hh (8-bit), \\ (backslash).</summary>
        static string Decode(string s)
        {
            if (!s.Contains('\\')) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }
                if (s.AsSpan(i).StartsWith("\\X2\\"))
                {
                    var end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                    if (end < 0) break;
                    var hex = s[(i + 4)..end];
                    for (int k = 0; k + 4 <= hex.Length; k += 4) sb.Append((char)Convert.ToInt32(hex.Substring(k, 4), 16));
                    i = end + 3; continue;
                }
                if (s.AsSpan(i).StartsWith("\\X\\") && i + 5 <= s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 3, 2), 16)); i += 4; continue; }
                if (s.AsSpan(i).StartsWith("\\\\")) { sb.Append('\\'); i++; continue; }
                sb.Append('\\');
            }
            return sb.ToString();
        }

        Exception Error(string msg)
        {
            int line = 1; for (int k = 0; k < Math.Min(_i, _s.Length); k++) if (_s[k] == '\n') line++;
            return new InvalidDataException($"STEP syntax error on line {line}: {msg}.");
        }
    }
}
