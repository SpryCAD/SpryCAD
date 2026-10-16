using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.App;

// equations made in value fields: "L = 10" typed in a sketch dimension's box or a feature page's field makes (or changes)
// the equation L, and the field holds "L" (EquationEntry). They wait with the sketch or page they were typed on: values
// and previews see them meanwhile, accepting it puts them in the part (one undo step with it), cancelling drops them.
public partial class MainWindow
{
    readonly Dictionary<string, string> _pendingEquations = new(StringComparer.OrdinalIgnoreCase);
    // the equations' values, with those waiting, and what they were worked out from
    (string key, Dictionary<string, Quantity> values)? _editValues;

    /// <summary>The page and sketch fields' hooks: making an equation, and the names to suggest.</summary>
    void InitEquationEntry()
    {
        PmPage.DefineEquation = DefineEquation;
        PmPage.EquationNames = () => EditValues().Select(kv => (kv.Key, Session.Doc.Units.Show(kv.Value)));
    }

    /// <summary>Drops the equations waiting (a new page or sketch, or the one they were typed on cancelled).</summary>
    void ClearPendingEquations() { _pendingEquations.Clear(); _editValues = null; }

    /// <summary>The part's equations with those waiting (a waiting one replaces the part's of that name).</summary>
    List<Equation> EquationsWithPending()
    {
        var list = Session.Doc.Equations
            .Select(e => _pendingEquations.TryGetValue(e.Name.Trim(), out var x) ? new Equation { Name = e.Name, Expr = x } : e).ToList();
        foreach (var (name, x) in _pendingEquations)
            if (!Session.Doc.Equations.Any(e => e.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Equation { Name = name, Expr = x });
        return list;
    }

    (Dictionary<string, Quantity> values, Dictionary<string, string> errors) Evaluate(List<Equation> eqs) =>
        Regenerator.EvaluateEquations(new CadDocument { Equations = eqs, Tables = Session.Doc.Tables, Units = Session.Doc.Units });

    /// <summary>The equations' values, those waiting included (worked out again only when they change).</summary>
    Dictionary<string, Quantity> EditValues()
    {
        var eqs = EquationsWithPending();
        var key = string.Join("\n", eqs.Select(e => e.Name + "=" + e.Expr)) + "\n" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Session.Doc.Tables ?? new());
        if (_editValues is { } c && c.key == key) return c.values;
        var values = Evaluate(eqs).values;
        _editValues = (key, values);
        return values;
    }

    /// <summary>What a name in a field comes to: the part's equations, and those typed on the open page or sketch.</summary>
    Func<string, Quantity?> EditLookup => n => EditValues().TryGetValue(n, out var q) ? q : null;

    /// <summary>
    /// "name = expression" typed in a field of kind <paramref name="kind"/>: the equation made, or changed, waiting with
    /// the page or sketch. A value without a unit takes the part's ("10" in a length field is 10mm); the problem, or null.
    /// </summary>
    string? DefineEquation(string name, string expr, ValueKind kind)
    {
        if (EquationEntry.NameProblem(name) is { } taken) return taken;
        if (string.IsNullOrWhiteSpace(expr)) return $"Enter a value for {name}.";
        if (!Session.Doc.Units.TryEnter(expr, kind, EditLookup, out var kept, out _, out var err)) return err;
        var had = _pendingEquations.TryGetValue(name, out var before);
        _pendingEquations[name] = kept;
        _editValues = null;
        // (one that cannot be worked out, using itself through others say, is not made)
        var (values, errors) = Evaluate(EquationsWithPending());
        if (!values.ContainsKey(name))
        {
            if (had) _pendingEquations[name] = before!; else _pendingEquations.Remove(name);
            _editValues = null;
            return errors.FirstOrDefault(kv => kv.Key.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? $"{name} cannot be worked out.";
        }
        Status($"{name} = {kept}: added to the equations with this.");
        return null;
    }

    /// <summary>Puts the equations waiting in <paramref name="doc"/> (changing those of the same name).</summary>
    void ApplyPendingEquations(CadDocument doc)
    {
        foreach (var (name, x) in _pendingEquations)
        {
            if (doc.Equations.FirstOrDefault(e => e.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) is { } eq) eq.Expr = x;
            else doc.Equations.Add(new Equation { Name = name, Expr = x });
        }
    }
}
