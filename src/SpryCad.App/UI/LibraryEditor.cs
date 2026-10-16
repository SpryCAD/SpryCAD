using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Collections;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SpryCad.App.Session;
using SpryCad.Core.Library;
using SpryCad.Core.Storage;

namespace SpryCad.App.UI;

/// <summary>
/// The library editor (Tools > Libraries, section 34): an admin's way to keep a shared library without code. Its
/// tables (rows of values and text such as part numbers, CSV in and out per table) and its restrictions (per table:
/// the only rows offered, rows never offered, or the whole table hidden), published as the library's next revision
/// with a message (<see cref="LibraryFile.Publish"/>), refused when someone else published since it was read. The file
/// is read from a copy, as clients read libraries: the share's file is never held open.
/// </summary>
public static class LibraryEditor
{
    static readonly FilePickerFileType LibraryType = new("SpryCAD Library") { Patterns = new[] { "*" + LibraryFile.Extension } };
    static readonly FilePickerFileType CsvType = new("CSV") { Patterns = new[] { "*.csv" } };

    /// <summary>The columns a new table of each kind starts with (what the Thread and Hole Wizard commands read).</summary>
    static readonly (string kind, string label, string[] columns)[] Kinds =
    {
        (TableKinds.Thread, "Thread sizes", new[] { "major", "pitch" }),
        (TableKinds.Clearance, "Clearance holes", new[] { "close", "normal", "loose" }),
        (TableKinds.Counterbore, "Counterbores", new[] { "diameter", "depth" }),
        (TableKinds.Countersink, "Countersinks", new[] { "diameter", "angle" }),
        (TableKinds.Material, "Materials", Materials.Columns.ToArray()),
        (TableKinds.Appearance, "Appearances", Materials.AppearanceColumns.ToArray()),
    };

    // ------------------------------------------------------------------------------------------
    // what is edited: strings as typed, checked when published

    sealed class RowEdit
    {
        public string Key = "";
        /// <summary>As shown, in the table's units.</summary>
        public List<string> Values = new();
        /// <summary>Each value in millimetres while its text is as shown (null once typed over): unchanged values publish exactly as read.</summary>
        public List<double?> Exact = new();
        public Dictionary<string, string> Text = new(StringComparer.Ordinal);

        public void Set(int c, string text) { Pad(c + 1); Values[c] = text; Exact[c] = null; }

        public void Pad(int n)
        {
            while (Values.Count < n) Values.Add("0");
            while (Exact.Count < Values.Count) Exact.Add(null);
        }

        public void RemoveAt(int c) { Pad(c + 1); Values.RemoveAt(c); Exact.RemoveAt(c); }

        /// <summary>The value in millimetres (null: not a number).</summary>
        public double? Mm(int c, double scale) => c < Exact.Count && Exact[c] is { } e ? e : c < Values.Count && TryNumber(Values[c].Trim(), out var v) ? v * scale : null;
    }

    sealed class TableEdit
    {
        public string Key = "", Title = "", Kind = "", Standard = "";
        /// <summary>The units lengths are shown in (<see cref="LibraryTable.Units"/>): null millimetres, else inches.</summary>
        public string? Units;
        public bool Verified;

        public double Scale(string column) => new LibraryTable { Units = Units, Kind = Kind }.Scale(column);
        public bool Material => Kind == TableKinds.Material;

        /// <summary>Shows the values in other units: each the same length.</summary>
        public void Convert(string? units)
        {
            var before = Columns.Select(Scale).ToList();
            Units = units;
            foreach (var r in Rows)
                for (int c = 0; c < Columns.Count; c++)
                    if (r.Mm(c, before[c]) is { } mm) { r.Pad(c + 1); r.Values[c] = Shown(mm, Scale(Columns[c])); r.Exact[c] = mm; }
        }
        /// <summary>In the revision read: parts may name it by its key, so the key cannot change.</summary>
        public bool Published;
        public List<string> Columns = new();
        public List<string> TextColumns = new();
        public List<RowEdit> Rows = new();
    }

    sealed class RestrictionEdit
    {
        public bool Hidden;
        public List<string> Allow = new(), Deny = new();
        public bool Any => Hidden || Allow.Count > 0 || Deny.Count > 0;
    }

    sealed class State
    {
        public string Path = "";
        public Guid Id;
        public string Name = "";
        public LibraryLayer Layer;
        /// <summary>The revision read (null: a new library, not yet published).</summary>
        public Guid? ReadRevision;
        public List<TableEdit> Tables = new();
        public Dictionary<string, RestrictionEdit> Restrictions = new(StringComparer.Ordinal);
        /// <summary>The library's shop profile tables (sheet metal): kept as read, published again unchanged.</summary>
        public List<SpryCad.SheetMetal.ShopProfileTable> ProfileTables = new();
        public bool Dirty;
        /// <summary>SpryCAD's built-in standards (no file): published to a folder the user chooses.</summary>
        public bool BuiltIn => Path.Length == 0;

        public TableEdit? Table(string key) => Tables.FirstOrDefault(t => t.Key == key);

        public RestrictionEdit Restriction(string key) =>
            Restrictions.TryGetValue(key, out var r) ? r : Restrictions[key] = new RestrictionEdit();

        /// <summary>The tables and restrictions to publish, or the first problem.</summary>
        public (List<LibraryTable> tables, List<LibraryRestriction> restrictions, string? problem) Build()
        {
            var tables = new List<LibraryTable>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in Tables)
            {
                var key = t.Key.Trim();
                if (key.Length == 0 || key.Contains('/')) return (tables, new(), $"A table needs a key without '/' (\"{t.Key}\").");
                if (!keys.Add(key)) return (tables, new(), $"Two tables have the key {key}.");
                var cols = t.Columns.Select(c => c.Trim()).ToList();
                if (cols.Any(c => c.Length == 0)) return (tables, new(), $"{key}: every column needs a name.");
                if (cols.Distinct().Count() != cols.Count) return (tables, new(), $"{key}: two columns have the same name.");
                var rowKeys = new HashSet<string>(StringComparer.Ordinal);
                var lt = new LibraryTable { Key = key, Title = t.Title.Trim(), Kind = t.Kind.Trim(), Standard = t.Standard.Trim(), Units = t.Units, Verified = t.Verified, Columns = cols };
                foreach (var r in t.Rows)
                {
                    var rk = r.Key.Trim();
                    if (rk.Length == 0) return (tables, new(), $"{key}: every row needs a designation.");
                    if (!rowKeys.Add(rk)) return (tables, new(), $"{key}: {rk} is listed twice.");
                    var row = new LibraryRow { Key = rk };
                    for (int c = 0; c < cols.Count; c++)
                    {
                        if (r.Mm(c, t.Scale(cols[c])) is not { } v)
                        {
                            // (a material's value left blank: not known)
                            if (MayBeBlank(t, c) && (c >= r.Values.Count || r.Values[c].Trim().Length == 0)) { row.Values.Add(double.NaN); continue; }
                            return (tables, new(), $"{key}: {rk}'s {cols[c]} is not a number (\"{(c < r.Values.Count ? r.Values[c].Trim() : "")}\").");
                        }
                        row.Values.Add(v);
                    }
                    foreach (var n in t.TextColumns)
                        if (r.Text.TryGetValue(n, out var s) && s.Trim().Length > 0) (row.Text ??= new(StringComparer.Ordinal))[n] = s.Trim();
                    lt.Rows.Add(row);
                }
                tables.Add(lt);
            }
            var restrictions = Restrictions.Where(kv => kv.Value.Any).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new LibraryRestriction
                {
                    Table = kv.Key, Hidden = kv.Value.Hidden,
                    Allow = kv.Value.Allow.Count > 0 ? kv.Value.Allow.ToList() : null,
                    Deny = kv.Value.Deny.Count > 0 ? kv.Value.Deny.ToList() : null,
                }).ToList();
            return (tables, restrictions, null);
        }
    }

    /// <summary>
    /// A row as the table's grid binds it: whether the pickers offer it (<see cref="Enabled"/>), its
    /// <see cref="Key"/>, then a cell per value column and per text column, by index (the table's columns vary, so the
    /// grid's columns are made in code and bound to <c>[i]</c>). Either this library's own row (<see cref="Row"/>), or
    /// one of an earlier library's (<see cref="From"/>), shown greyed and copied into this library (overridden) when
    /// edited. A value that is not a number is refused (the cell keeps its value) and said through <c>problem</c>.
    /// </summary>
    sealed class GridRow : INotifyPropertyChanged
    {
        public RowEdit? Row;
        /// <summary>The earlier library the row is from (null: this library's own).</summary>
        public string? From;
        /// <summary>The earlier library's row as shown (its key, then its cells), kept to show again when this library's override is deleted.</summary>
        public readonly string InheritedKey;
        public readonly string[] Inherited;
        readonly Func<TableEdit?> _table;
        readonly Func<RestrictionEdit> _restriction;
        readonly Action _changed;
        readonly Action<string> _problem;

        public GridRow(RowEdit? row, string? from, string inheritedKey, string[] inherited, Func<TableEdit?> table, Func<RestrictionEdit> restriction, Action changed, Action<string> problem)
        {
            Row = row; From = from; InheritedKey = inheritedKey; Inherited = inherited;
            _table = table; _restriction = restriction; _changed = changed; _problem = problem;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Own => Row != null;
        public IBrush Foreground => Own ? Palette.Text : Palette.TextMuted;
        public FontStyle Style => Own ? FontStyle.Normal : FontStyle.Italic;

        public string Key
        {
            get => Row?.Key ?? InheritedKey;
            set
            {
                value = (value ?? "").Trim();
                if (Row == null || value == Row.Key) return;
                // the restriction follows the row
                var r = _restriction();
                foreach (var list in new[] { r.Allow, r.Deny }) { var i = list.IndexOf(Row.Key); if (i >= 0) list[i] = value; }
                Row.Key = value;
                _changed(); Notify();
            }
        }

        public string this[int c]
        {
            get
            {
                if (Row == null) return c < Inherited.Length ? Inherited[c] : "";
                var t = _table()!;
                return c < t.Columns.Count ? (c < Row.Values.Count ? Row.Values[c] : "0")
                    : c - t.Columns.Count < t.TextColumns.Count ? Row.Text.GetValueOrDefault(t.TextColumns[c - t.Columns.Count], "") : "";
            }
            set
            {
                value = (value ?? "").Trim();
                if (Row == null || value == this[c]) return;
                var t = _table()!;
                if (c < t.Columns.Count)
                {
                    if (!TryNumber(value, out _) && !(value.Length == 0 && MayBeBlank(t, c)))
                    {
                        _problem($"{Row.Key}'s {t.Columns[c]}: \"{value}\" is not a number, so it was left as it was.");
                        Dispatcher.UIThread.Post(Notify); // the cell shows the value kept
                        return;
                    }
                    Row.Set(c, value);
                }
                else if (c - t.Columns.Count < t.TextColumns.Count)
                {
                    // a material's type: one the app knows (else it reads as Other)
                    if (t.Material && t.TextColumns[c - t.Columns.Count] == Materials.TypeKey && value.Length > 0 && !Enum.TryParse<MaterialType>(value, out _))
                    {
                        _problem($"{Row.Key}'s type: \"{value}\" is not one of {string.Join(", ", Enum.GetNames<MaterialType>())}, so it was left as it was.");
                        Dispatcher.UIThread.Post(Notify);
                        return;
                    }
                    Row.Text[t.TextColumns[c - t.Columns.Count]] = value;
                }
                else return;
                _changed(); Notify();
            }
        }

        /// <summary>Whether the pickers offer the row (as <see cref="LibraryRestriction.Allows(string)"/>): on unless denied, or left out of an allow list a file has.</summary>
        public bool Enabled
        {
            get { var r = _restriction(); return !r.Deny.Contains(Key) && (r.Allow.Count == 0 || r.Allow.Contains(Key)); }
            set => Enable(value);
        }

        /// <summary>
        /// Offers the row, or stops offering it. Turned off, it is denied (rows other libraries add later are still offered);
        /// turned on, no longer denied, and listed in the allow list if the restriction has one.
        /// </summary>
        public void Enable(bool on)
        {
            if (Enabled == on) return;
            var r = _restriction();
            if (on) { r.Deny.Remove(Key); if (r.Allow.Count > 0 && !r.Allow.Contains(Key)) r.Allow.Add(Key); }
            else r.Deny.Add(Key);
            _changed(); Notify();
        }

        /// <summary>The row's color (an appearance's or a material's r, g, b columns) as a swatch; null when it has none.</summary>
        public Func<GridRow, IBrush?>? SwatchOf;
        public IBrush? Swatch => SwatchOf?.Invoke(this);

        public void Notify()
        {
            foreach (var p in new[] { nameof(Key), "Item[]", nameof(Enabled), nameof(Foreground), nameof(Style), nameof(Swatch) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }

    /// <summary>A grid column's sort: compares two rows.</summary>
    sealed class Comparer(Func<GridRow, GridRow, int> compare) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) => x is GridRow a && y is GridRow b ? compare(a, b) : 0;
    }

    /// <summary>Text compared with its runs of digits as numbers: M2 before M10, #4 before #10.</summary>
    static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var n = decimal.Parse(a[si..i], CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b[sj..j], CultureInfo.InvariantCulture));
                if (n != 0) return n;
            }
            else
            {
                var n = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (n != 0) return n;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Numbers compared as numbers; blanks after them.</summary>
    static int Numeric(string a, string b)
    {
        bool na = TryNumber(a.Trim(), out var x), nb = TryNumber(b.Trim(), out var y);
        return na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(a, b);
    }

    static bool TryNumber(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    /// <summary>
    /// A value in millimetres as shown in units of <paramref name="scale"/> mm: millimetres (and degrees) to 4 decimals,
    /// inches to 5 (a material's in its units alike); a value not known (a material's) blank.
    /// </summary>
    static string Shown(double mm, double scale) =>
        double.IsFinite(mm) ? Math.Round(mm / scale, scale == 1 ? 4 : 5).ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>Whether a material table's column may be left blank: not known (any but its density).</summary>
    static bool MayBeBlank(TableEdit t, int c) => t.Material && c < t.Columns.Count && t.Columns[c] != "density";

    static TableEdit Edit(LibraryTable t, bool published) => new()
    {
        Key = t.Key, Title = t.Title, Kind = t.Kind, Standard = t.Standard, Units = t.Units, Verified = t.Verified, Published = published,
        Columns = t.Columns.ToList(), TextColumns = LibraryCsv.TextColumns(t.Rows),
        Rows = t.Rows.Select(r => Edit(r, t)).ToList(),
    };

    static RowEdit Edit(LibraryRow r, LibraryTable t) => new()
    {
        Key = r.Key, Values = r.Values.Select((v, c) => Shown(v, c < t.Columns.Count ? t.Scale(t.Columns[c]) : 1)).ToList(),
        Exact = r.Values.Select(v => (double?)v).ToList(),
        Text = r.Text == null ? new(StringComparer.Ordinal) : new Dictionary<string, string>(r.Text, StringComparer.Ordinal),
    };

    /// <summary>The table with its lengths in its own units (millimetres stored, inches shown), as CSV files have them.</summary>
    static LibraryTable InUnits(LibraryTable t, bool toShown) => new()
    {
        Key = t.Key, Title = t.Title, Kind = t.Kind, Standard = t.Standard, Units = t.Units, Verified = t.Verified, Columns = t.Columns,
        Rows = t.Rows.Select(r => new LibraryRow
        {
            Key = r.Key, Text = r.Text,
            Values = r.Values.Select((v, c) => t.Scale(t.Columns[c]) is var k && k == 1 ? v : toShown ? v / k : Math.Round(v * k, 9)).ToList(),
        }).ToList(),
    };

    static State FromLibrary(LoadedLibrary lib, string path)
    {
        var s = new State { Path = path, Id = lib.LibraryId, Name = lib.Name, Layer = lib.Layer, ReadRevision = lib.RevisionId };
        s.Tables.AddRange(lib.Tables.Select(t => Edit(t, true)));
        s.ProfileTables.AddRange(lib.ProfileTables ?? Array.Empty<SpryCad.SheetMetal.ShopProfileTable>());
        // a table restricted more than once: one restriction doing the same (rows allowed by all, denied by any)
        foreach (var g in lib.Restrictions.GroupBy(r => r.Table))
        {
            var e = s.Restriction(g.Key);
            e.Hidden = g.Any(r => r.Hidden);
            List<string>? allow = null;
            foreach (var r in g.Where(r => r.Allow != null)) allow = allow == null ? r.Allow!.ToList() : allow.Where(r.Allow!.Contains).ToList();
            if (allow is { Count: 0 }) e.Hidden = true; // nothing allowed: the table is never offered
            else if (allow != null) e.Allow = allow;
            e.Deny = g.Where(r => r.Deny != null).SelectMany(r => r.Deny!).Distinct().ToList();
        }
        return s;
    }

    /// <summary>The library and its history, read from a copy (the share's file is never held open).</summary>
    static (LoadedLibrary lib, List<RevisionInfo> history) ReadCopy(string path)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sprycad-edit-" + Guid.NewGuid().ToString("N") + LibraryFile.Extension);
        try
        {
            File.Copy(path, tmp);
            if (!LibraryFile.IsLibrary(tmp)) throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a SpryCAD library.");
            return (LibraryFile.Read(tmp) with { Path = path }, PartFile.History(tmp));
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>Where pickers start: the library set's folder.</summary>
    static async Task<IStorageFolder?> StartFolder(Window w)
    {
        var dir = Libraries.Location.path is { } p ? System.IO.Path.GetDirectoryName(p) : null;
        return dir != null && Directory.Exists(dir) ? await w.StorageProvider.TryGetFolderFromPathAsync(dir) : null;
    }

    // ------------------------------------------------------------------------------------------
    // the commands

    /// <summary>Tools > Libraries > Edit Library: picks a library file and edits it (at <paramref name="table"/>, when it has it).</summary>
    public static async Task Open(MainWindow owner, string? table = null)
    {
        // no library set yet: SpryCAD's own standards, published as the start of one
        if (Libraries.Location.path == null)
        {
            var b = FromLibrary(BuiltInStandards.AsLibrary() with { Tables = LibraryFile.BuiltInTables.ToList() }, "");
            b.ReadRevision = null;
            await Show(owner, b, table);
            return;
        }
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Edit Library", AllowMultiple = false, FileTypeFilter = new[] { LibraryType }, SuggestedStartLocation = await StartFolder(owner),
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        State s;
        try { s = FromLibrary((await Task.Run(() => ReadCopy(path))).lib, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            await Dialogs.Message(owner, "Edit Library Failed", $"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        await Show(owner, s, table);
    }

    /// <summary>Tools > Libraries > New Company Library: a new, empty company library at a path chosen (written when first published).</summary>
    public static async Task New(MainWindow owner)
    {
        var f = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New Company Library", SuggestedFileName = "company" + LibraryFile.Extension, DefaultExtension = LibraryFile.Extension,
            FileTypeChoices = new[] { LibraryType }, SuggestedStartLocation = await StartFolder(owner),
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        if (File.Exists(path))
        {
            await Dialogs.Message(owner, "New Company Library", $"{System.IO.Path.GetFileName(path)} already exists. Open it with Edit Library, or choose another name.");
            return;
        }
        var name = await Dialogs.Input(owner, "New Company Library", "Library name (shown in Options):", System.IO.Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(name)) return;
        await Show(owner, new State { Path = path, Id = Guid.NewGuid(), Name = name.Trim(), Layer = LibraryLayer.Company, Dirty = true });
    }

    // ------------------------------------------------------------------------------------------
    // the window

    static async Task Show(MainWindow owner, State s, string? select = null)
    {
        string? selected = null;
        var nameBox = new TextBox { MinWidth = 220 };
        var pathText = new SelectableTextBlock { Foreground = Palette.TextMuted, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var info = new TextBlock { Foreground = Palette.TextMuted, FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        // the chosen table: its key, its settings, one grid of its rows (the page)
        var heading = new TextBlock { FontSize = TypeScale.Title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading, Margin = new Thickness(0, 0, 0, 6) };
        var body = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        void Top(Control c) { DockPanel.SetDock(c, Dock.Top); c.Margin = new Thickness(c.Margin.Left, c.Margin.Top, c.Margin.Right, Math.Max(c.Margin.Bottom, 6)); body.Children.Add(c); }
        void Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); body.Children.Add(c); }
        void Fill(Control c) => body.Children.Add(c); // last: takes the room left
        bool listing = false;
        Window w = null!;

        void Changed() { s.Dirty = true; error.Text = ""; }
        nameBox.TextChanged += (_, _) => { if ((nameBox.Text ?? "").Trim() != s.Name) { s.Name = (nameBox.Text ?? "").Trim(); Changed(); } };

        void Header()
        {
            nameBox.Text = s.Name;
            pathText.Text = s.BuiltIn
                ? "SpryCAD built-in standards and materials (no library set in use; see Options > Libraries). Publish saves this copy as standards.sprlib "
                    + "with a libraries.json, creating a shareable library set."
                : $"{s.Layer} library · {s.Path}";
            info.Text = s.BuiltIn ? "Built-in; publishing writes it to a folder." : s.ReadRevision == null ? "New; saved when first published." : $"Editing revision {s.ReadRevision.Value.ToString()[..8]}.";
        }

        // the left list: the library's tables, then tables it only restricts (another library's)
        List<string> Keys() => s.Tables.Select(t => t.Key).Concat(s.Restrictions.Where(kv => kv.Value.Any && s.Table(kv.Key) == null).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal)).ToList();
        void Relist(string? select = null, bool body = true)
        {
            listing = true;
            list.Items.Clear();
            var keys = Keys();
            if (select != null && !keys.Contains(select)) keys.Add(select); // just added to restrict: listed while it is shown
            foreach (var k in keys)
            {
                var t = s.Table(k);
                var sp = new StackPanel { Margin = new Thickness(2, 1) };
                var keyText = Labels.Trimmed(k); keyText.FontWeight = FontWeight.SemiBold;
                sp.Children.Add(keyText);
                var sub = t != null ? $"{(t.Title.Length > 0 ? t.Title + " · " : "")}{t.Rows.Count} rows" : "Restrictions only";
                if (s.Restrictions.TryGetValue(k, out var r) && r.Any && t != null) sub += " · restricted";
                var subText = Labels.Trimmed(sub, TypeScale.Small); subText.Foreground = Palette.TextMuted;
                sp.Children.Add(subText);
                list.Items.Add(new ListBoxItem { Content = sp, Tag = k, Padding = new Thickness(8, 4) });
            }
            selected = select ?? selected;
            if (selected == null || !keys.Contains(selected)) selected = keys.FirstOrDefault();
            list.SelectedIndex = selected == null ? -1 : keys.IndexOf(selected);
            listing = false;
            if (body) Body(); // (false: the page as it is, e.g. a grid being edited)
        }
        list.SelectionChanged += (_, _) =>
        {
            if (listing || list.SelectedItem is not ListBoxItem { Tag: string k }) return;
            selected = k;
            Body();
        };

        // ---- the right side: the chosen table's settings, then one grid of its rows and what the pickers offer of them
        void Body()
        {
            body.Children.Clear();
            heading.Text = selected ?? "";
            heading.IsVisible = selected != null;
            if (selected == null)
            {
                Top(Muted("No tables. Click + Table to add in-house sizes or part numbers, "
                    + "or Restrict a Table to limit the sizes another library offers."));
                return;
            }
            Section(selected);
        }

        // the table as the other libraries in use have it (this one left out), and the library each row is from
        (LibraryTable table, string[] from)? Inherited(string key)
        {
            LibraryTable? acc = null;
            var from = new List<string>();
            foreach (var lib in LibraryLink.Current.Libraries.Where(l => l.LibraryId != s.Id))
                foreach (var tb in lib.Tables.Where(x => x.Key == key))
                {
                    if (acc == null || !acc.Columns.SequenceEqual(tb.Columns))
                    {
                        acc = new LibraryTable { Key = key, Columns = tb.Columns.ToList(), Rows = tb.Rows.ToList() };
                        from = tb.Rows.Select(_ => lib.Name).ToList();
                    }
                    else
                        foreach (var r in tb.Rows)
                        {
                            var i = acc.Rows.FindIndex(x => x.Key == r.Key);
                            if (i >= 0) { acc.Rows[i] = r; from[i] = lib.Name; } else { acc.Rows.Add(r); from.Add(lib.Name); }
                        }
                    acc.Title = tb.Title; acc.Kind = tb.Kind; acc.Standard = tb.Standard; acc.Units = tb.Units;
                }
            return acc == null ? null : (acc, from.ToArray());
        }

        void Section(string key)
        {
            var t = s.Table(key);
            // another library's rows are shown when this library adds to its table (the same columns) or has none of its own
            var inh = Inherited(key) is { } i0 && (t == null || t.Columns.SequenceEqual(i0.table.Columns)) ? i0 : ((LibraryTable table, string[] from)?)null;
            if (t != null) TableProps(t);
            var hidden = new CheckBox { Content = "Hide table in Thread and Hole Wizard", IsChecked = s.Restrictions.TryGetValue(key, out var r0) && r0.Hidden };
            hidden.IsCheckedChanged += (_, _) => { s.Restriction(key).Hidden = hidden.IsChecked == true; Changed(); Relist(key, body: false); };
            Top(hidden);
            Top(Muted((t == null ? $"This library has no rows of its own for {key}. " : "")
                + (inh != null ? $"Gray rows come from {string.Join(", ", inh.Value.from.Distinct())}; edit one to override it here. " : "")
                + "Enabled rows are offered in Thread and Hole Wizard; existing parts keep disabled sizes. "
                + "Right-click rows to enable or disable them. Rows are saved in the order shown; click a header to sort."));

            var cols = t?.Columns.ToList() ?? inh?.table.Columns.ToList() ?? new List<string>();
            var texts = t?.TextColumns.ToList() ?? (inh != null ? LibraryCsv.TextColumns(inh.Value.table.Rows) : new List<string>());
            var units = t != null ? t.Units : inh?.table.Units;
            var unitsTable = new LibraryTable { Units = units, Kind = t?.Kind ?? inh?.table.Kind ?? "" };
            double Scale(string col) => unitsTable.Scale(col);

            // this library's own table, made (from the other libraries') when a row is first added or overridden
            TableEdit Own()
            {
                if (t != null) return t;
                var src = inh?.table ?? LibraryLink.Current.Table(key);
                t = new TableEdit
                {
                    Key = key, Title = src?.Title ?? "", Kind = src?.Kind ?? "", Standard = src?.Standard ?? "", Units = src?.Units,
                    Columns = cols.ToList(), TextColumns = texts.ToList(),
                };
                s.Tables.Add(t);
                Relist(key, body: false);
                return t;
            }

            // the rows: the other libraries' in their order (this library's override in place of one), then this library's new ones,
            // then any the restriction names that no table has
            void RowChanged() { Changed(); Relist(key, body: false); }
            // a color (r, g, b columns, 0-1) shown as a swatch beside the designation
            int ri = cols.IndexOf("r"), gi = cols.IndexOf("g"), bi = cols.IndexOf("b");
            bool colored = ri >= 0 && gi >= 0 && bi >= 0;
            IBrush? SwatchOf(GridRow g)
            {
                byte C(int c) => TryNumber(g[c], out var v) ? (byte)Math.Round(Math.Clamp(v, 0, 1) * 255) : (byte)0;
                return TryNumber(g[ri], out _) && TryNumber(g[gi], out _) && TryNumber(g[bi], out _) ? new SolidColorBrush(Color.FromRgb(C(ri), C(gi), C(bi))) : null;
            }
            GridRow Make(RowEdit? own, string? from, string inheritedKey, string[] cells) =>
                new(own, from, inheritedKey, cells, () => t, () => s.Restriction(key), RowChanged, p => error.Text = p) { SwatchOf = colored ? SwatchOf : null };
            var items = new ObservableCollection<GridRow>();
            var inhRows = new Dictionary<string, (LibraryRow row, string from)>(StringComparer.Ordinal);
            var used = new HashSet<RowEdit>();
            if (inh is { } ih)
                for (int i = 0; i < ih.table.Rows.Count; i++)
                {
                    var row = ih.table.Rows[i];
                    if (!inhRows.TryAdd(row.Key, (row, ih.from[i]))) continue;
                    var cells = cols.Select((c, j) => j < row.Values.Count ? Shown(row.Values[j], Scale(c)) : "")
                        .Concat(texts.Select(n => row.Text?.GetValueOrDefault(n) ?? "")).ToArray();
                    var own = t?.Rows.FirstOrDefault(x => x.Key == row.Key && !used.Contains(x));
                    if (own != null) used.Add(own);
                    items.Add(Make(own, own == null ? ih.from[i] : null, row.Key, cells));
                }
            foreach (var own in t?.Rows ?? new()) if (!used.Contains(own)) { own.Pad(cols.Count); items.Add(Make(own, null, own.Key, Array.Empty<string>())); }
            if (s.Restrictions.TryGetValue(key, out var rk))
                foreach (var n in rk.Allow.Concat(rk.Deny).Distinct().Where(n => items.All(g => g.Key != n)).ToList())
                    items.Add(Make(null, "no table", n, Array.Empty<string>()));

            // an other library's row edited: copied into this library, which then overrides it
            void Override(GridRow g)
            {
                if (g.Own) return;
                var own = Own();
                var re = new RowEdit { Key = g.InheritedKey };
                var src = inhRows.TryGetValue(g.InheritedKey, out var ir) ? ir.row : null;
                for (int c = 0; c < own.Columns.Count; c++)
                {
                    re.Values.Add(c < g.Inherited.Length && g.Inherited[c].Length > 0 ? g.Inherited[c] : "0");
                    re.Exact.Add(src != null && c < src.Values.Count ? src.Values[c] : null);
                }
                for (int c = 0; c < own.TextColumns.Count; c++)
                    if (own.Columns.Count + c < g.Inherited.Length && g.Inherited[own.Columns.Count + c].Length > 0) re.Text[own.TextColumns[c]] = g.Inherited[own.Columns.Count + c];
                own.Rows.Add(re);
                g.Row = re; g.From = null;
                g.Notify();
                RowChanged();
            }

            var cv = new DataGridCollectionView(items);
            var dg = new DataGrid
            {
                ItemsSource = cv, AutoGenerateColumns = false, CanUserSortColumns = true, CanUserReorderColumns = false, CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.All, HeadersVisibility = DataGridHeadersVisibility.All,
                FrozenColumnCount = 2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            };
            // another library's rows greyed and in italics; a cell's text centred up and down while it is edited, where it was shown
            dg.Styles.Add(new Style(x => x.OfType<DataGridCell>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.ForegroundProperty, new Binding(nameof(GridRow.Foreground))),
                    new Setter(TemplatedControl.FontStyleProperty, new Binding(nameof(GridRow.Style))),
                },
            });
            dg.Styles.Add(new Style(x => x.OfType<DataGridCell>().Descendant().OfType<TextBox>())
            {
                Setters =
                {
                    new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                    new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Stretch),
                    new Setter(Layoutable.MinHeightProperty, 0.0),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(11, 0)),
                },
            });

            // the columns, each as wide as its heading: Enabled, Designation (frozen), a column per value, a column per text.
            // fields[i]: what dg.Columns[i] shows: -1 a check box, 0 the designation, 1 + c cell c
            var fields = new List<int>();
            Control Header(string text, string tip, bool italic, Action? rename, Action? delete)
            {
                var tb = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontStyle = italic ? FontStyle.Italic : FontStyle.Normal, Background = Brushes.Transparent };
                ToolTip.SetTip(tb, tip + (rename != null ? " Right-click to rename or delete the column." : ""));
                if (rename != null && delete != null)
                {
                    var rn = new MenuItem { Header = "Rename Column…" }; rn.Click += (_, _) => rename();
                    var dl = new MenuItem { Header = "Delete Column" }; dl.Click += (_, _) => delete();
                    tb.ContextMenu = new ContextMenu { Items = { rn, dl } };
                }
                return tb;
            }
            DataGridLength ToHeader = new(1, DataGridLengthUnitType.SizeToHeader);
            void Check(string header, string tip, string property, Func<GridRow, bool> get)
            {
                dg.Columns.Add(new DataGridTemplateColumn
                {
                    Header = Header(header, tip, false, null, null), Width = ToHeader,
                    CustomSortComparer = new Comparer((a, b) => get(a).CompareTo(get(b))),
                    CellTemplate = new FuncDataTemplate<GridRow>((_, _) => new CheckBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 0, Padding = new Thickness(0),
                        [!ToggleButton.IsCheckedProperty] = new Binding(property) { Mode = BindingMode.TwoWay },
                    }),
                });
                fields.Add(-1);
            }
            Check("Enabled", "Offered by the Thread and Hole Wizard pickers. Uncheck the sizes your company does not use; parts that already use one keep it.", nameof(GridRow.Enabled), g => g.Enabled);
            dg.Columns.Add(new DataGridTextColumn
            {
                Header = Header("Designation", "Designation parts reference the row by, e.g. M8 or 1/4-20 UNC.", false, null, null),
                Binding = new Binding(nameof(GridRow.Key)) { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 90,
                CustomSortComparer = new Comparer((a, b) => Natural(a.Key, b.Key)),
            });
            fields.Add(0);
            if (colored)
            {
                // clicked: a color typed as #RRGGBB, into the r, g and b columns
                dg.Columns.Add(new DataGridTemplateColumn
                {
                    Header = Header("Color", "The row's r, g, b values. Click a swatch to pick a color.", false, null, null), Width = ToHeader,
                    CellTemplate = new FuncDataTemplate<GridRow>((_, _) =>
                    {
                        var sw = new Border
                        {
                            Width = 28, Height = 14, CornerRadius = new CornerRadius(2), BorderBrush = Palette.TextMuted, BorderThickness = new Thickness(0.5),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
                            [!Border.BackgroundProperty] = new Binding(nameof(GridRow.Swatch)),
                        };
                        sw.PointerPressed += async (_, e) =>
                        {
                            if (sw.DataContext is not GridRow g || !e.GetCurrentPoint(sw).Properties.IsLeftButtonPressed) return;
                            e.Handled = true;
                            string Hex(int c) => TryNumber(g[c], out var v) ? ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2") : "00";
                            var typed = (await Dialogs.Input(w, "Color", $"{g.Key}'s color, as #RRGGBB:", $"#{Hex(ri)}{Hex(gi)}{Hex(bi)}"))?.Trim().TrimStart('#');
                            if (typed == null) return;
                            if (typed.Length != 6 || !int.TryParse(typed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) { error.Text = $"\"{typed}\" is not a color (#RRGGBB)."; return; }
                            Override(g);
                            string Part(int shift) => Math.Round(((rgb >> shift) & 0xFF) / 255.0, 4).ToString("R", CultureInfo.InvariantCulture);
                            g[ri] = Part(16); g[gi] = Part(8); g[bi] = Part(0);
                        };
                        return sw;
                    }),
                });
                fields.Add(-1);
            }
            async Task<string?> ColumnName(string title, string label, string value)
            {
                var n = (await Dialogs.Input(w, title, label, value))?.Trim();
                if (string.IsNullOrEmpty(n) || n == value) return null;
                if (cols.Contains(n) || texts.Contains(n)) { error.Text = $"{key} already has a column {n}."; return null; }
                return n;
            }
            for (int c = 0; c < cols.Count; c++)
            {
                int cc = c;
                var unit = unitsTable.UnitLabel(cols[c]);
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Header(unit.Length > 0 ? $"{cols[c]} ({unit})" : cols[c], "A column of numbers: the commands read values by its name (major, pitch; close, normal, loose).", false,
                        t == null ? null : async () =>
                        {
                            if (await ColumnName("Rename Column", "New column name (commands read values by name):", cols[cc]) is not { } n) return;
                            t.Columns[cc] = n;
                            foreach (var r in t.Rows) if (cc < r.Exact.Count) r.Exact[cc] = null; // (an angle column now a length, or the other way: as shown)
                            Changed(); Body();
                        },
                        t == null ? null : () => { t.Columns.RemoveAt(cc); foreach (var r in t.Rows) r.RemoveAt(cc); Changed(); Body(); }),
                    Binding = new Binding($"[{c}]") { Mode = BindingMode.TwoWay }, Width = ToHeader, MinWidth = 72,
                    CustomSortComparer = new Comparer((a, b) => Numeric(a[cc], b[cc])),
                });
                fields.Add(1 + c);
            }
            for (int c = 0; c < texts.Count; c++)
            {
                int cc = cols.Count + c;
                var n = texts[c];
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Header(n, "Text column, e.g. part number or supplier.", true,
                        t == null ? null : async () =>
                        {
                            if (await ColumnName("Rename Text Column", "New column name:", n) is not { } nn) return;
                            t.TextColumns[t.TextColumns.IndexOf(n)] = nn;
                            foreach (var r in t.Rows) if (r.Text.Remove(n, out var v)) r.Text[nn] = v;
                            Changed(); Body();
                        },
                        t == null ? null : () => { t.TextColumns.Remove(n); foreach (var r in t.Rows) r.Text.Remove(n); Changed(); Body(); }),
                    Binding = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 90,
                    CustomSortComparer = new Comparer((a, b) => Natural(a[cc], b[cc])),
                });
                fields.Add(1 + cc);
            }

            // editing another library's row overrides it; a sort is the rows' order
            bool editing = false;
            dg.BeginningEdit += (_, e) =>
            {
                if (e.Row.DataContext is GridRow { Own: false } g) Override(g);
                editing = true;
            };
            dg.CellEditEnded += (_, _) => editing = false;
            dg.Sorting += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (t == null) return;
                var order = cv.Cast<GridRow>().Where(g => g.Row != null).Select(g => g.Row!).ToList();
                if (!order.SequenceEqual(t.Rows)) { t.Rows = order; Changed(); }
            });

            List<GridRow> Selected() => cv.Cast<GridRow>().Where(dg.SelectedItems.Contains).ToList(); // in the order shown
            string NewKey() { int n = (t?.Rows.Count ?? 0) + 1; while (items.Any(x => x.Key == "Row" + n)) n++; return "Row" + n; }

            void DeleteRows()
            {
                var sel = Selected();
                if (sel.Count == 0) return;
                if (sel.All(g => !g.Own)) { error.Text = "These rows belong to another library and cannot be deleted. Clear Enabled to hide them."; return; }
                foreach (var g in sel.Where(g => g.Own))
                {
                    t!.Rows.Remove(g.Row!);
                    if (inhRows.TryGetValue(g.InheritedKey, out var ir) && g.Inherited.Length > 0) { g.Row = null; g.From = ir.from; g.Notify(); } // the other library's row again
                    else items.Remove(g);
                }
                RowChanged();
            }

            // copied as tab-separated text (designation, values, text), as spreadsheets paste it
            async Task Copy()
            {
                var sel = Selected();
                if (sel.Count == 0 || TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip) return;
                var n = cols.Count + texts.Count;
                await clip.SetTextAsync(string.Join("\n", sel.Select(g => string.Join("\t", new[] { g.Key }.Concat(Enumerable.Range(0, n).Select(c => g[c]))))));
                owner.Status($"Copied {sel.Count} row{(sel.Count == 1 ? "" : "s")}.");
            }

            // pasted from the first row selected and the current column on, rows added as needed
            async Task Paste()
            {
                if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip || await clip.GetTextAsync() is not { Length: > 0 } text) return;
                var lines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
                var sel = Selected();
                int row0 = sel.Count > 0 ? cv.IndexOf(sel[0]) : cv.Count;
                int field0 = Math.Max(0, dg.CurrentColumn is { } cc && dg.Columns.IndexOf(cc) is var ci && ci >= 0 ? fields[ci] : 0);
                for (int i = 0; i < lines.Length; i++)
                {
                    var cells = lines[i].Split('\t');
                    GridRow g;
                    if (row0 + i < cv.Count) g = (GridRow)cv[row0 + i]!;
                    else
                    {
                        var own = Own();
                        var re = new RowEdit { Key = field0 == 0 ? cells[0].Trim() : NewKey() };
                        re.Pad(own.Columns.Count);
                        own.Rows.Add(re);
                        items.Add(g = Make(re, null, re.Key, Array.Empty<string>()));
                    }
                    Override(g);
                    for (int j = 0; j < cells.Length; j++)
                    {
                        int field = field0 + j;
                        if (field == 0) g.Key = cells[j];
                        else if (field - 1 < cols.Count + texts.Count) g[field - 1] = cells[j];
                    }
                }
                RowChanged();
            }

            void EnableSelected(bool on) { foreach (var g in Selected()) g.Enable(on); }

            dg.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
            {
                if (editing) return;
                if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; DeleteRows(); }
                else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await Copy(); }
                else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; await Paste(); }
            }, RoutingStrategies.Tunnel);
            MenuItem Item(string text, string? gesture, Action a)
            {
                var m = new MenuItem { Header = text };
                if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
                m.Click += (_, _) => a();
                return m;
            }
            dg.ContextMenu = new ContextMenu
            {
                Items =
                {
                    Item("Copy", "Ctrl+C", () => _ = Copy()),
                    Item("Paste", "Ctrl+V", () => _ = Paste()),
                    Item("Delete Rows", "Delete", DeleteRows),
                    new Separator(),
                    Item("Enable", null, () => EnableSelected(true)),
                    Item("Disable", null, () => EnableSelected(false)),
                    new Separator(),
                    Item("Enable All Rows", null, () => { s.Restrictions.Remove(key); Changed(); Relist(key); }),
                },
            };

            var addRow = new Button { Content = "+ Row" };
            addRow.Click += (_, _) =>
            {
                var own = Own();
                var last = own.Rows.LastOrDefault();
                var re = new RowEdit { Key = NewKey(), Values = last?.Values.ToList() ?? own.Columns.Select(_ => "0").ToList(), Exact = last?.Exact.ToList() ?? new() };
                re.Pad(own.Columns.Count);
                own.Rows.Add(re);
                var g = Make(re, null, re.Key, Array.Empty<string>());
                items.Add(g);
                RowChanged();
                dg.SelectedItem = g;
                dg.ScrollIntoView(g, dg.Columns[1]);
                dg.CurrentColumn = dg.Columns[1];
                dg.Focus();
                dg.BeginEdit();
            };
            var delRows = new Button { Content = "Delete Rows" };
            ToolTip.SetTip(delRows, "Deletes the selected rows (Delete). Rows from another library can only be disabled.");
            delRows.Click += (_, _) => DeleteRows();
            Bottom(t != null ? Bar(new Control[] { addRow, delRows }.Concat(TableButtons(t)).ToArray()) : Bar(addRow));
            Fill(cols.Count == 0 && items.Count == 0
                ? Muted("No columns. Choose the table type to add its standard columns, click + Column, or import a CSV file.")
                : dg);
        }

        void TableProps(TableEdit t)
        {
            var props = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,16,90,*"), RowDefinitions = new RowDefinitions("Auto,6,Auto,6,Auto") };
            void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); props.Children.Add(c); }
            TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
            var key = new TextBox { Text = t.Key, IsEnabled = !t.Published };
            ToolTip.SetTip(key, t.Published ? "Parts reference the table by key; a published table's key cannot change." : "Stable key that parts reference. Using another library's key adds rows to that table.");
            key.LostFocus += (_, _) =>
            {
                var k = (key.Text ?? "").Trim();
                if (k == t.Key) return;
                if (s.Restrictions.Remove(t.Key, out var r) && !s.Restrictions.ContainsKey(k)) s.Restrictions[k] = r;
                t.Key = k; Changed(); Relist(k);
            };
            var title = new TextBox { Text = t.Title };
            title.TextChanged += (_, _) => { if (title.Text != t.Title) { t.Title = title.Text ?? ""; Changed(); } };
            var standard = new TextBox { Text = t.Standard, Watermark = "e.g. ISO 273, or in-house" };
            standard.TextChanged += (_, _) => { if (standard.Text != t.Standard) { t.Standard = standard.Text ?? ""; Changed(); } };
            var kinds = Kinds.Select(k => k.label).ToList();
            if (t.Kind.Length > 0 && Kinds.All(k => k.kind != t.Kind)) kinds.Add(t.Kind);
            var kind = new ComboBox { ItemsSource = kinds, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Table type" };
            kind.SelectedIndex = Kinds.ToList().FindIndex(k => k.kind == t.Kind) is var ki && ki >= 0 ? ki : t.Kind.Length > 0 ? kinds.Count - 1 : -1;
            ToolTip.SetTip(kind, "Which command offers the rows: Thread for thread sizes, Hole Wizard for the rest.");
            kind.SelectionChanged += (_, _) =>
            {
                if (kind.SelectedIndex < 0 || kind.SelectedIndex >= Kinds.Length || Kinds[kind.SelectedIndex].kind == t.Kind) return;
                t.Kind = Kinds[kind.SelectedIndex].kind;
                // a table with no columns yet takes the ones its kind is read by
                if (t.Columns.Count == 0) { t.Columns = Kinds[kind.SelectedIndex].columns.ToList(); foreach (var r in t.Rows) { r.Values = t.Columns.Select(_ => "0").ToList(); r.Exact.Clear(); r.Pad(t.Columns.Count); } }
                // (a material says what it is: its type)
                if (t.Material && !t.TextColumns.Contains(Materials.TypeKey)) t.TextColumns.Insert(0, Materials.TypeKey);
                Changed(); Body();
            };
            var verified = new CheckBox { Content = "Verified against the published standard", IsChecked = t.Verified };
            verified.IsCheckedChanged += (_, _) => { t.Verified = verified.IsChecked == true; Changed(); };
            Cell(Label("Key"), 0, 0); Cell(key, 0, 1); Cell(Label("Title"), 0, 3); Cell(title, 0, 4);
            Cell(Label("For"), 2, 0); Cell(kind, 2, 1); Cell(Label("Standard"), 2, 3); Cell(standard, 2, 4);
            var units = new ComboBox
            {
                ItemsSource = t.Material ? new[] { "Metric (g/cm³, GPa, MPa)", "Inch-pound (lb/in³, Msi, ksi)" } : new[] { "Millimeters (mm)", "Inches (in)" },
                SelectedIndex = t.Units == LibraryTable.Inch ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(units, t.Material
                ? "Display, entry and CSV units for properties. Values are stored in SI."
                : "Display, entry and CSV units for lengths. Values are stored in millimeters.");
            units.SelectionChanged += (_, _) =>
            {
                var u = units.SelectedIndex == 1 ? LibraryTable.Inch : null;
                if (u == t.Units) return;
                t.Convert(u);
                Changed(); Body();
            };
            Cell(Label("Units"), 4, 0); Cell(units, 4, 1); Cell(verified, 4, 3); Grid.SetColumnSpan(verified, 2);
            Top(props);
            Top(Muted((t.Material
                    ? $"Properties in {(t.Units == LibraryTable.Inch ? "inch-pound" : "metric")} units, as each column header shows. Leave unknown values blank (density is required). "
                        + $"Type is one of {string.Join(", ", Enum.GetNames<MaterialType>())}; r, g, b, metallic, roughness and opacity (0 to 1) set the appearance. "
                    : $"Lengths in {(t.Units == LibraryTable.Inch ? "inches" : "millimeters")}; angle columns in degrees. ")
                + "A table keyed as one in an earlier library adds rows to it and overrides rows of the same designation, "
                + "as long as its columns are the same (otherwise it replaces the table). Text columns carry part numbers, suppliers and the like."));
        }

        // the buttons for this library's own table
        Control[] TableButtons(TableEdit t)
        {
            var addCol = new Button { Content = "+ Column" };
            ToolTip.SetTip(addCol, "A column of numbers");
            addCol.Click += async (_, _) =>
            {
                var n = (await Dialogs.Input(w, "Add Column", "Column name (numeric):", ""))?.Trim();
                if (string.IsNullOrEmpty(n)) return;
                if (t.Columns.Contains(n) || t.TextColumns.Contains(n)) { error.Text = $"{t.Key} already has a column {n}."; return; }
                t.Columns.Add(n);
                foreach (var r in t.Rows) { r.Pad(t.Columns.Count - 1); r.Values.Add("0"); r.Exact.Add(null); }
                Changed(); Body();
            };
            var addText = new Button { Content = "+ Text Column" };
            ToolTip.SetTip(addText, "A column of text: a part number, a supplier");
            addText.Click += async (_, _) =>
            {
                var n = (await Dialogs.Input(w, "Add Text Column", "Column name (text, e.g. part number):", ""))?.Trim();
                if (string.IsNullOrEmpty(n)) return;
                if (t.Columns.Contains(n) || t.TextColumns.Contains(n)) { error.Text = $"{t.Key} already has a column {n}."; return; }
                t.TextColumns.Add(n);
                Changed(); Body();
            };
            var import = new Button { Content = "Import CSV…" };
            ToolTip.SetTip(import, $"Replaces the table's columns and rows with a CSV file's: a header row (row, the columns, {LibraryCsv.TextPrefix}name for text), then a row per designation");
            import.Click += async (_, _) =>
            {
                var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Import {t.Key}", AllowMultiple = false, FileTypeFilter = new[] { CsvType } });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } p) return;
                try
                {
                    var (cols, rows) = LibraryCsv.Read(await File.ReadAllTextAsync(p));
                    if (t.Published && !cols.SequenceEqual(t.Columns)
                        && !await Dialogs.YesNo(w, "Import CSV", $"The file's columns ({string.Join(", ", cols)}) are not the table's ({string.Join(", ", t.Columns)}). Parts read values by column name. Import anyway?", "Import", "Cancel"))
                        return;
                    var imported = Edit(InUnits(new LibraryTable { Units = t.Units, Kind = t.Kind, Columns = cols, Rows = rows }, toShown: false), false);
                    t.Columns = imported.Columns; t.TextColumns = imported.TextColumns; t.Rows = imported.Rows;
                    Changed(); Relist();
                    owner.Status($"Imported {rows.Count} rows into {t.Key} (not yet published).");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    error.Text = $"{System.IO.Path.GetFileName(p)} could not be imported: {ex.Message}";
                }
            };
            var export = new Button { Content = "Export CSV…" };
            export.Click += async (_, _) =>
            {
                var (tables, _, problem) = s.Build();
                if (problem != null) { error.Text = problem; return; }
                var f = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = $"Export {t.Key}", SuggestedFileName = t.Key + ".csv", DefaultExtension = ".csv", FileTypeChoices = new[] { CsvType },
                });
                if (f?.TryGetLocalPath() is not { } p) return;
                try
                {
                    await File.WriteAllTextAsync(p, LibraryCsv.Write(InUnits(tables[s.Tables.IndexOf(t)], toShown: true)));
                    owner.Status($"Exported {t.Key} to {p}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error.Text = $"{System.IO.Path.GetFileName(p)} could not be written: {ex.Message}"; }
            };
            var delTable = new Button { Content = "Delete Table" };
            delTable.Click += async (_, _) =>
            {
                if (t.Published && !await Dialogs.YesNo(w, "Delete Table",
                        $"Delete {t.Key} from the library? Existing parts keep their values; new parts cannot use it.", "Delete", "Cancel"))
                    return;
                s.Tables.Remove(t); Changed(); Relist();
            };
            return new Control[] { addCol, addText, import, export, delTable };
        }

        // ---- the left side's buttons
        var addTable = new Button { Content = "+ Table" };
        ToolTip.SetTip(addTable, "A table of rows of this library's own: new sizes, or rows added to (or overriding) another library's table of the same key");
        addTable.Click += async (_, _) =>
        {
            var k = (await Dialogs.Input(w, "Add Table", "Table key (e.g. ACME-screws; an existing key such as ISO273 adds rows to it):", ""))?.Trim();
            if (string.IsNullOrEmpty(k)) return;
            if (k.Contains('/')) { error.Text = "A table's key cannot hold '/'."; return; }
            if (s.Table(k) != null) { error.Text = $"The library already has a table {k}."; return; }
            var src = LibraryLink.Current.Table(k);
            s.Tables.Add(new TableEdit { Key = k, Title = src?.Title ?? "", Kind = src?.Kind ?? "", Standard = src?.Standard ?? "", Units = src?.Units, Columns = src?.Columns.ToList() ?? new() });
            Changed(); Relist(k);
        };
        var restrict = new Button { Content = "Restrict a Table" };
        ToolTip.SetTip(restrict, "Limits which sizes of another library's table are offered.");
        var menu = new MenuFlyout();
        restrict.Flyout = menu;
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var listed = Keys();
            var kinds = Kinds.Select(k => k.kind).ToList();
            foreach (var kind in kinds)
                foreach (var tb in LibraryLink.Current.Libraries.SelectMany(l => l.Tables).Where(tb => tb.Kind == kind).Select(tb => tb.Key).Distinct())
                {
                    if (listed.Contains(tb)) continue;
                    var mi = new MenuItem { Header = $"{tb}  ({LibraryLink.Current.Table(tb)?.Title})" };
                    var key = tb;
                    mi.Click += (_, _) => Relist(key);
                    menu.Items.Add(mi);
                }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "All tables in use are listed", IsEnabled = false });
        };

        // ---- the shop profile (sheet metal): its tables from CSV, one per kind; the profile in use is the last library's that has one
        var profile = new Button { Content = "Shop Profile" };
        ToolTip.SetTip(profile, "Imports sheet metal tables (gauges, tooling, allowances, rules) from CSV. All sheet metal parts use this profile.");
        var profileMenu = new MenuFlyout();
        profile.Flyout = profileMenu;
        profileMenu.Opening += (_, _) =>
        {
            profileMenu.Items.Clear();
            foreach (var tpl in SpryCad.SheetMetal.ProfileTables.All)
            {
                var have = s.ProfileTables.FirstOrDefault(x => x.Key == tpl.Key);
                var item = new MenuItem { Header = $"{tpl.Title}{(have != null ? $"  ({have.Rows.Count} rows)" : "")}" };
                foreach (var (units, words) in new[] { ((string?)null, "Import CSV (mm)…"), (SpryCad.SheetMetal.ShopProfileTable.Inch, "Import CSV (inches)…") })
                {
                    var imp = new MenuItem { Header = words };
                    var key = tpl.Key;
                    imp.Click += async (_, _) => await ImportProfileTable(key, units);
                    item.Items.Add(imp);
                }
                if (have != null)
                {
                    var exp = new MenuItem { Header = "Export CSV…" };
                    exp.Click += async (_, _) =>
                    {
                        var f = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export {have.Title}", SuggestedFileName = have.Key + ".csv", DefaultExtension = "csv", FileTypeChoices = new[] { CsvType } });
                        if (f?.TryGetLocalPath() is { } p) await File.WriteAllTextAsync(p, SpryCad.SheetMetal.ProfileCsv.Write(have));
                    };
                    var del = new MenuItem { Header = "Remove" };
                    del.Click += (_, _) => { s.ProfileTables.Remove(have); Changed(); };
                    item.Items.Add(exp);
                    item.Items.Add(del);
                }
                profileMenu.Items.Add(item);
            }
            var starter = new MenuItem { Header = "Start from Example Profile" };
            starter.Click += (_, _) =>
            {
                foreach (var tb in SpryCad.SheetMetal.StarterProfile.Tables)
                    if (!s.ProfileTables.Any(x => x.Key == tb.Key)) s.ProfileTables.Add(tb.Clone());
                Changed();
                owner.Status("Example profile tables added. Publish to share them.");
            };
            profileMenu.Items.Add(new Separator());
            profileMenu.Items.Add(starter);
        };

        async Task ImportProfileTable(string key, string? units)
        {
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Import {key}", AllowMultiple = false, FileTypeFilter = new[] { CsvType } });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } p) return;
            try
            {
                var table = SpryCad.SheetMetal.ProfileCsv.Read(key, await File.ReadAllTextAsync(p), units);
                var old = s.ProfileTables.FindIndex(x => x.Key == key);
                // a bend table keeps whether it interpolates (not in the CSV)
                if (old >= 0) { table.Interpolate = s.ProfileTables[old].Interpolate; s.ProfileTables[old] = table; } else s.ProfileTables.Add(table);
                Changed();
                owner.Status($"Imported {table.Rows.Count} rows into the shop profile's {table.Title} (not yet published).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                error.Text = $"{System.IO.Path.GetFileName(p)} could not be imported: {ex.Message}";
            }
        }

        // ---- the bottom: history, publish, close
        var history = new Button { Content = "History…" };
        var publish = new Button { Content = "Publish…", IsDefault = true };
        var close = new Button { Content = "Close", IsCancel = true };

        void Reload(State fresh)
        {
            s = fresh;
            Header(); Relist();
        }

        async Task<bool> Publish()
        {
            var (tables, restrictions, problem) = s.Build();
            if (s.Name.Length == 0) problem = "The library needs a name.";
            if (problem != null) { error.Text = problem; return false; }
            // the built-in standards: written into a folder chosen, as a library set's first library
            string? folder = null;
            if (s.BuiltIn)
            {
                var picked = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Library Set Folder" });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } f) return false;
                if (File.Exists(System.IO.Path.Combine(f, "standards" + LibraryFile.Extension)))
                {
                    await Dialogs.Message(w, "Publish", $"{f} already has a standards{LibraryFile.Extension}: choose another folder, or use that library set (Options > Libraries) and edit it.");
                    return false;
                }
                folder = f;
            }
            var message = await Dialogs.Input(w, "Publish", "Change description (shown in history):", s.BuiltIn ? "SpryCAD built-in standards" : "");
            if (message == null) return false;
            if (folder != null) s.Path = System.IO.Path.Combine(folder, "standards" + LibraryFile.Extension);
            var file = System.IO.Path.GetFileName(s.Path);
            bool isNew = s.ReadRevision == null;
            Guid rev;
            try
            {
                var st = s;
                rev = await Task.Run(() => LibraryFile.Publish(st.Path, st.Id, st.Name, st.Layer, tables, restrictions, UserSettings.AuthorName, message.Trim(), st.ReadRevision, st.ProfileTables));
                if (folder != null)
                {
                    var config = System.IO.Path.Combine(folder, LibraryConfig.FileName);
                    LibraryConfig.Add(config, s.Path);
                    if (Libraries.Location.source == LibraryConfigSource.None
                        && await Dialogs.YesNo(w, "Publish", $"Published {file} and {LibraryConfig.FileName} in {folder}. Use this library set on this computer?", "Use Library Set", "Not Now"))
                    {
                        UserSettings.LibrariesPath = folder;
                        UserSettings.Save();
                    }
                }
            }
            catch (LibraryConflictException)
            {
                if (await Dialogs.YesNo(w, "Publish",
                        $"{file} was published by someone else after you opened it, so your changes were not published. "
                        + "Reload their version? Your changes will be lost; export tables as CSV first to keep them.", "Reload", "Keep Editing"))
                    await ReloadFromFile();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                Unwritten();
                await Dialogs.Message(w, "Publish failed", $"No write access to {s.Path}. Only library owners can publish.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                Unwritten();
                await Dialogs.Message(w, "Publish failed", $"Could not publish {file}: {ex.Message}");
                return false;
            }
            // the built-in standards not written after all: still the built-in ones, unsaved
            void Unwritten() { if (folder != null && !File.Exists(s.Path)) s.Path = ""; }
            var same = rev == s.ReadRevision;
            s.ReadRevision = rev;
            foreach (var t in s.Tables) t.Published = true;
            s.Dirty = false;
            Header(); Body();
            owner.Status(same ? $"No changes to publish in {file}." : $"Published {file}. Other users receive it within a few minutes.");
            Libraries.Reload();
            if (isNew) await OfferToList();
            return true;
        }

        // a new library is used only once the library set lists it
        async Task OfferToList()
        {
            var beside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s.Path) ?? "", LibraryConfig.FileName);
            var config = File.Exists(beside) ? beside : Libraries.Location.path is { } p && File.Exists(p) ? p : null;
            if (config == null)
            {
                await Dialogs.Message(w, "Publish", $"Published, but no library set ({LibraryConfig.FileName}) lists it. Add \"{System.IO.Path.GetFileName(s.Path)}\" to one, or create one in Options > Libraries.");
                return;
            }
            if (LibraryConfig.Lists(config, s.Path)) return;
            if (!await Dialogs.YesNo(w, "Publish", $"Add {System.IO.Path.GetFileName(s.Path)} to the library set ({config})? Everyone using the set will receive it.", "Add", "Not Now")) return;
            try
            {
                LibraryConfig.Add(config, s.Path);
                Libraries.Reload();
                owner.Status($"Added {System.IO.Path.GetFileName(s.Path)} to {config}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                await Dialogs.Message(w, "Publish failed", $"Could not update {config}: {ex.Message}");
            }
        }

        async Task ReloadFromFile()
        {
            try { Reload(FromLibrary((await Task.Run(() => ReadCopy(s.Path))).lib, s.Path)); error.Text = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                error.Text = $"{System.IO.Path.GetFileName(s.Path)} could not be read: {ex.Message}";
            }
        }

        publish.Click += async (_, _) => await Publish();
        history.Click += async (_, _) =>
        {
            if (s.ReadRevision == null) { error.Text = "No history. The library has not been published."; return; }
            List<RevisionInfo> revs;
            try { revs = (await Task.Run(() => ReadCopy(s.Path))).history; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                error.Text = $"Could not read history: {ex.Message}";
                return;
            }
            await ShowHistory(w, s.Name, revs, s.ReadRevision.Value);
        };

        // ---- the layout, as the Options window's
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(14, 10) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = "Library", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        nameRow.Children.Add(nameBox);
        top.Children.Add(nameRow);
        top.Children.Add(pathText);
        var topBar = new Border { Child = top, Background = Palette.Strip, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1) };

        var leftButtons = new StackPanel { Spacing = 6, Margin = new Thickness(8), Children = { addTable, restrict, profile } };
        foreach (var b in new[] { addTable, restrict, profile }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        var leftDock = new DockPanel();
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        leftDock.Children.Add(leftButtons);
        leftDock.Children.Add(new ScrollViewer { Content = list });
        var left = new Border { Child = leftDock, Background = Palette.Panel, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 1, 0) };
        var right = new DockPanel { Margin = new Thickness(18, 14, 14, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        right.Children.Add(heading); right.Children.Add(body);
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        Grid.SetColumn(right, 1);
        middle.Children.Add(left); middle.Children.Add(right);

        var status = new StackPanel { Children = { error, info } };
        var bottomBar = Dialogs.FooterBar(status, history, publish, close);

        var root = new DockPanel();
        DockPanel.SetDock(topBar, Dock.Top); DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(topBar); root.Children.Add(bottomBar); root.Children.Add(middle);

        w = new Window
        {
            Title = "Edit Library", Width = 980, Height = 680, MinWidth = 720, MinHeight = 460,
            CanResize = true, CanMinimize = false, CanMaximize = true, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root,
        };
        Dialogs.Compact(w);

        // closing with changes not published: publish them, leave them, or stay
        bool closing = false;
        w.Closing += async (_, e) =>
        {
            if (closing || !s.Dirty) return;
            e.Cancel = true;
            var answer = await Dialogs.YesNoCancel(w, "Edit Library", $"Publish your changes to {s.Name}?", "Publish", "Don't Publish");
            if (answer == null || (answer == true && !await Publish())) return;
            closing = true;
            w.Close();
        };
        close.Click += (_, _) => w.Close();

        Header(); Relist(select != null && s.Table(select) != null ? select : null);
        s.Dirty = s.ReadRevision == null && !s.BuiltIn; // (filling the boxes is not a change)
        await w.ShowDialog(owner);
    }

    static async Task ShowHistory(Window owner, string name, List<RevisionInfo> revs, Guid read)
    {
        var grid = new StackPanel { Spacing = 2 };
        const string defs = "150,130,*";
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(defs) };
        void H(string text, int col) { var tb = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold }; Grid.SetColumn(tb, col); head.Children.Add(tb); }
        H("Published", 0); H("By", 1); H("What changed", 2);
        grid.Children.Add(head);
        foreach (var r in revs)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(defs), Margin = new Thickness(0, 2) };
            var current = r.RevisionId == read;
            void C(string text, int col)
            {
                var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(tb, col); g.Children.Add(tb);
            }
            C(r.CreatedUtc.ToLocalTime().ToString("g"), 0);
            C(r.Author, 1);
            C((r.Message.Length > 0 ? r.Message : "(no message)") + (current ? "  · the revision being edited" : ""), 2);
            grid.Children.Add(g);
        }
        var ok = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Muted($"Each publish of {name} is a revision, newest first."));
        sp.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 420 });
        var w = Dialogs.MakeWindow($"History of {name}", Dialogs.WithButtons(sp, null, ok), 640);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    // ------------------------------------------------------------------------------------------

    static TextBlock Section(string title, double top) => new()
    {
        Text = title, FontSize = TypeScale.Header, FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading, Margin = new Thickness(0, top, 0, 2),
    };

    static TextBlock Muted(string text) => new() { Text = text, FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap };

    static StackPanel Bar(params Control[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }
}
