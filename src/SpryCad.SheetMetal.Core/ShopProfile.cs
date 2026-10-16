using System.Globalization;

namespace SpryCad.SheetMetal;

/// <summary>What a shop profile column holds: how it is shown and checked. Every number is stored in mm or degrees.</summary>
public enum ProfileValueKind
{
    /// <summary>A length (mm; shown in the table's units). May be an expression.</summary>
    Length,
    /// <summary>An angle in degrees. May be an expression.</summary>
    Angle,
    /// <summary>A plain number (K-factor, density, tonnage). May be an expression.</summary>
    Number,
    /// <summary>1 or 0.</summary>
    Bool,
    /// <summary>One of a fixed set of words (an allowance method, a relief type).</summary>
    Choice,
    Text,
}

public sealed record ProfileColumn(string Name, ProfileValueKind Kind);

/// <summary>A row of a shop profile table: its key values ("*" matches any) and one cell per column, as text.</summary>
public sealed class ProfileRow
{
    public List<string> Keys { get; set; } = new();
    public List<string> Cells { get; set; } = new();

    public ProfileRow() { }
    public ProfileRow(IEnumerable<string> keys, params string[] cells) { Keys = keys.ToList(); Cells = cells.ToList(); }

    public const string Any = "*";

    /// <summary>The row's keys as one string ("Steel|16 ga|*"): its identity in its table and in snapshots.</summary>
    public string KeyText => string.Join("|", Keys);

    /// <summary>How many keys are exact (not "*"): a row with more beats one with fewer.</summary>
    public int Specificity => Keys.Count(k => k != Any);
}

/// <summary>
/// One table of a shop profile (sheet metal spec section 3): every value that differs between shops, for one concern
/// (tooling, allowances, corner rules…). Rows are found by their key columns (material, gauge, tool set…); a key of
/// "*" matches any value, and the most specific row wins. A lookup with no row is an error, never a default
/// (<see cref="ProfileMissException"/>). Stored in a library (.sprlib) as an object of its own, beside the
/// standards tables.
/// </summary>
public sealed class ShopProfileTable
{
    /// <summary>The table's key (<see cref="ProfileTables"/>: "sm.tooling"). One table per key in a profile.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The units lengths are shown and typed in: "in" for inches, else millimetres (null). Stored in mm either way.</summary>
    public string? Units { get; set; }
    public List<string> KeyColumns { get; set; } = new();
    public List<ProfileColumn> Columns { get; set; } = new();
    public List<ProfileRow> Rows { get; set; } = new();
    /// <summary>Bend tables only: values between two angles of the table interpolated linearly; never beyond them.</summary>
    public bool Interpolate { get; set; }

    public const string Inch = "in";

    public int Column(string name) => Columns.FindIndex(c => c.Name == name);

    /// <summary>The problems that make the table unusable, in words (none: it is fine).</summary>
    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Contains('/')) yield return $"A table key cannot be empty or hold '/': \"{Key}\".";
        if (Columns.Select(c => c.Name).Distinct().Count() != Columns.Count) yield return $"{Key}: two columns have the same name.";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Rows)
        {
            if (r.Keys.Count != KeyColumns.Count) yield return $"{Key}/{r.KeyText}: {r.Keys.Count} keys for {KeyColumns.Count} key columns.";
            if (r.Cells.Count != Columns.Count) yield return $"{Key}/{r.KeyText}: {r.Cells.Count} values for {Columns.Count} columns.";
            if (!seen.Add(r.KeyText)) yield return $"{Key}: two rows are keyed \"{r.KeyText}\".";
            for (int i = 0; i < Math.Min(r.Cells.Count, Columns.Count); i++)
            {
                var c = Columns[i];
                var cell = r.Cells[i];
                if (cell.Length == 0 || c.Kind is ProfileValueKind.Text or ProfileValueKind.Choice) continue;
                if (c.Kind == ProfileValueKind.Bool && cell is not ("0" or "1")) yield return $"{Key}/{r.KeyText}: {c.Name} must be 1 or 0, not \"{cell}\".";
                // an expression may use t and R (and nothing else); check it reads
                var vars = new Dictionary<string, double> { ["t"] = 1, ["R"] = 1 };
                if (c.Kind != ProfileValueKind.Bool && !RuleExpr.TryEval(cell, vars, out _, out var err)) yield return $"{Key}/{r.KeyText}: {c.Name}: {err}";
            }
        }
    }

    public ShopProfileTable Clone() => new()
    {
        Key = Key, Title = Title, Units = Units, Interpolate = Interpolate,
        KeyColumns = KeyColumns.ToList(), Columns = Columns.ToList(),
        Rows = Rows.Select(r => new ProfileRow { Keys = r.Keys.ToList(), Cells = r.Cells.ToList() }).ToList(),
    };
}

/// <summary>A material, gauge or tool set the shop profile has no row for: the part cannot be made as asked.</summary>
public sealed class ProfileMissException(string table, string message) : Exception(message)
{
    public string Table { get; } = table;
}

/// <summary>A row found in a profile table: the row and where it was found, for snapshots and reports.</summary>
public sealed record ProfileHit(ShopProfileTable Table, ProfileRow Row)
{
    public string Item => Table.Key + "/" + Row.KeyText;
    public string Cell(string column)
    {
        var i = Table.Column(column);
        if (i < 0) throw new ProfileMissException(Table.Key, $"{Table.Key} has no column \"{column}\".");
        return Row.Cells[i];
    }
}

/// <summary>
/// A shop profile: its tables by key (the library set's profile, or a part's snapshot of the rows it uses). One profile
/// serves every part (the library set's), so the same material and gauge get the same rules in every part.
/// </summary>
public sealed class ShopProfile
{
    public string Name { get; }
    public IReadOnlyDictionary<string, ShopProfileTable> Tables { get; }

    public ShopProfile(string name, IEnumerable<ShopProfileTable> tables)
    {
        Name = name;
        Tables = tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
    }

    public ShopProfileTable Table(string key) =>
        Tables.TryGetValue(key, out var t) ? t : throw new ProfileMissException(key, $"The shop profile \"{Name}\" has no {ProfileTables.Title(key)} table ({key}).");

    /// <summary>
    /// The row of <paramref name="table"/> for <paramref name="keys"/> (one per key column): an exact key beats "*",
    /// and of the rows that match, the one with the most exact keys wins. No row, or two that match equally: an error.
    /// </summary>
    public ProfileHit Find(string table, params string[] keys)
    {
        var t = Table(table);
        if (keys.Length != t.KeyColumns.Count) throw new ArgumentException($"{table} is keyed by {string.Join(", ", t.KeyColumns)}.");
        var hits = Matches(t, keys).ToList();
        if (hits.Count == 0) throw new ProfileMissException(table, $"The shop profile \"{Name}\" has no {ProfileTables.Title(table)} row for {Describe(t, keys)}.");
        var best = hits.Max(r => r.Specificity);
        var top = hits.Where(r => r.Specificity == best).ToList();
        if (top.Count > 1) throw new ProfileMissException(table, $"The shop profile \"{Name}\" has {top.Count} {ProfileTables.Title(table)} rows for {Describe(t, keys)} ({string.Join("; ", top.Select(r => r.KeyText))}): it is ambiguous.");
        return new ProfileHit(t, top[0]);
    }

    /// <summary>The rows matching the leading keys given (the rest any), most specific first (for tooling and bend tables).</summary>
    public IEnumerable<ProfileRow> Matches(ShopProfileTable t, IReadOnlyList<string> keys) =>
        t.Rows.Where(r => keys.Select((k, i) => r.Keys[i] == ProfileRow.Any || r.Keys[i] == k).All(b => b))
              .OrderByDescending(r => r.Specificity);

    static string Describe(ShopProfileTable t, IReadOnlyList<string> keys) =>
        string.Join(", ", keys.Select((k, i) => $"{t.KeyColumns[i]} \"{k}\""));

    /// <summary>The problems of every table (empty: the profile is usable).</summary>
    public IEnumerable<string> Problems() => Tables.Values.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => t.Problems());

    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
