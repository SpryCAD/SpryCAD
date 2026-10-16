using System.Text.RegularExpressions;

namespace SpryCad.Core.Model;

/// <summary>
/// "name = expression" typed in a value field (a sketch dimension's box, a feature page's field): it makes the equation,
/// or changes it if there is one, and the field then holds the name, as Inventor's parameters are made from a
/// dimension's box. "L = 10" makes L (10 in the part's units); "W = L/2" makes W from it.
/// </summary>
public static class EquationEntry
{
    // a name, one "=" (not "==", a comparison), and the expression
    static readonly Regex Assignment = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)\s*(.*?)\s*$", RegexOptions.CultureInvariant);
    static readonly Regex Identifier = new(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*$", RegexOptions.CultureInvariant);

    // what an equation cannot be called: the functions, constants and logic words of expressions (units are asked of Expr)
    static readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase)
    {
        "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sqrt", "abs", "min", "max", "round", "floor", "ceil", "int",
        "exp", "log", "log10", "pow", "if", "choose", "pi", "e", "and", "or", "not",
    };

    /// <summary>Whether <paramref name="text"/> is "name = expression"; the name and the expression (not checked).</summary>
    public static bool TryParse(string? text, out string name, out string expr)
    {
        name = expr = "";
        if (text == null || Assignment.Match(text) is not { Success: true } m) return false;
        (name, expr) = (m.Groups[1].Value, m.Groups[2].Value);
        return true;
    }

    /// <summary>Why <paramref name="name"/> cannot name an equation (a function, a constant, a unit), or null.</summary>
    public static string? NameProblem(string name) =>
        Taken.Contains(name) || Expr.IsUnitName(name) ? $"\"{name}\" is the name of a function, constant or unit: choose another." : null;

    /// <summary>Whether a value is a name alone ("L"): a dimension driven by an equation, labeled "L = 10".</summary>
    public static bool IsName(string? text) => text != null && Identifier.IsMatch(text) && NameProblem(text.Trim()) == null;
}
