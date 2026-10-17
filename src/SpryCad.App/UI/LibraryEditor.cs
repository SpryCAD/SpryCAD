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
    static readonly FilePickerFileType ExcelType = new("Excel Workbook") { Patterns = new[] { "*.xlsx" } };

    /// <summary>The columns a new table of each kind starts with (what the Thread and Hole Wizard commands read).</summary>
    static readonly (string kind, string label, string[] columns)[] Kinds =
    {
        (TableKinds.Thread, "Thread sizes", new[] { "major", "pitch" }),
        (TableKinds.FastenerHoles, "Holes for fasteners", new[] { "close", "normal", "loose", "head.diameter", "head.depth", "head.angle" }),
        (TableKinds.Material, "Materials", Materials.Columns.ToArray()),
        (TableKinds.Appearance, "Appearances", Materials.AppearanceColumns.ToArray()),
    };

    // ------------------------------------------------------------------------------------------
    // what is edited: strings as typed, checked when published

    sealed class RowEdit
    {
        public string Key = "";
        /// <summary>As shown: in the table's units, as stored.</summary>
        public List<string> Values = new();
        /// <summary>Each value as stored while its text is as shown (null once typed over): unchanged values publish exactly as read.</summary>
        public List<double?> Exact = new();
        public Dictionary<string, string> Text = new(StringComparer.Ordinal);

        public void Set(int c, string text) { Pad(c + 1); Values[c] = text; Exact[c] = null; }

        public void Pad(int n)
        {
            while (Values.Count < n) Values.Add("0");
            while (Exact.Count < Values.Count) Exact.Add(null);
        }

        public void RemoveAt(int c) { Pad(c + 1); Values.RemoveAt(c); Exact.RemoveAt(c); }

        /// <summary>The value, in the table's units (null: not a number).</summary>
        public double? Number(int c) => c < Exact.Count && Exact[c] is { } e ? e : c < Values.Count && TryNumber(Values[c].Trim(), out var v) ? v : null;
    }

    sealed class TableEdit
    {
        public string Key = "", Title = "", Kind = "", Standard = "";
        /// <summary>The units the values are in (<see cref="LibraryTable.Units"/>).</summary>
        public TableUnits Units;
        public bool Verified;

        public bool Material => Kind == TableKinds.Material;

        /// <summary>The values converted to other units: each the same amount (rounded to 6 decimals in inches, 4 in metric).</summary>
        public void Convert(TableUnits units)
        {
            var old = Units;
            Units = units;
            foreach (var r in Rows)
                for (int c = 0; c < Columns.Count; c++)
                {
                    var q = Schemas.UnitOf(Kind, Columns[c]);
                    if (r.Number(c) is not { } v) continue;
                    var nv = Math.Round(q.FromBase(q.ToBase(v, old), units), units == TableUnits.Inch ? 6 : 4);
                    r.Pad(c + 1); r.Values[c] = Shown(nv); r.Exact[c] = nv;
                }
        }
        /// <summary>In the revision read: parts may name it by its key, so the key cannot change.</summary>
        public bool Published;
        public List<string> Columns = new();
        public List<string> TextColumns = new();
        public List<RowEdit> Rows = new();
        /// <summary>A hole type's profile (<see cref="LibraryTable.Profile"/>, its JSON): kept as read, made from a hole's.</summary>
        public string? Profile;
    }

    sealed class State
    {
        public string Path = "";
        public Guid Id;
        public string Name = "";
        /// <summary>What the library holds: standards or materials (the table kinds it may have).</summary>
        public LibraryDomain Domain;
        /// <summary>The revision read (null: a new library, not yet published).</summary>
        public Guid? ReadRevision;
        public List<TableEdit> Tables = new();
        /// <summary>The library's shop profile tables (sheet metal): kept as read, published again unchanged.</summary>
        public List<SpryCad.SheetMetal.ShopProfileTable> ProfileTables = new();
        public bool Dirty;
        /// <summary>SpryCAD's defaults (no file): published to a folder the user chooses, as a company's own library.</summary>
        public bool BuiltIn => Path.Length == 0;

        public TableEdit? Table(string key) => Tables.FirstOrDefault(t => t.Key == key);

        /// <summary>The tables to publish, or the first problem.</summary>
        public (List<LibraryTable> tables, string? problem) Build()
        {
            var tables = new List<LibraryTable>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in Tables)
            {
                var key = t.Key.Trim();
                if (key.Length == 0 || key.IndexOfAny(new[] { ':', '/' }) >= 0) return (tables, $"A table needs a key without ':' or '/' (\"{t.Key}\").");
                if (!keys.Add(key)) return (tables, $"Two tables have the key {key}.");
                var cols = t.Columns.Select(c => c.Trim()).ToList();
                if (cols.Any(c => c.Length == 0)) return (tables, $"{key}: every column needs a name.");
                if (cols.Distinct().Count() != cols.Count) return (tables, $"{key}: two columns have the same name.");
                var rowKeys = new HashSet<string>(StringComparer.Ordinal);
                var lt = new LibraryTable { Key = key, Title = t.Title.Trim(), Kind = t.Kind.Trim(), Standard = t.Standard.Trim(), Units = t.Units, Verified = t.Verified, Columns = cols, Profile = t.Profile };
                foreach (var r in t.Rows)
                {
                    var rk = r.Key.Trim();
                    if (rk.Length == 0) return (tables, $"{key}: every row needs a designation.");
                    if (!rowKeys.Add(rk)) return (tables, $"{key}: {rk} is listed twice.");
                    var row = new LibraryRow { Key = rk };
                    for (int c = 0; c < cols.Count; c++)
                    {
                        if (r.Number(c) is not { } v)
                        {
                            // (a material's value left blank: not known)
                            if (MayBeBlank(t, c) && (c >= r.Values.Count || r.Values[c].Trim().Length == 0)) { row.Values.Add(double.NaN); continue; }
                            return (tables, $"{key}: {rk}'s {cols[c]} is not a number (\"{(c < r.Values.Count ? r.Values[c].Trim() : "")}\").");
                        }
                        row.Values.Add(v);
                    }
                    foreach (var n in t.TextColumns)
                        if (r.Text.TryGetValue(n, out var s) && s.Trim().Length > 0) (row.Text ??= new(StringComparer.Ordinal))[n] = s.Trim();
                    lt.Rows.Add(row);
                }
                // (a column of the kind's that every row leaves blank: not the table's, as it was read)
                var blank = Enumerable.Range(0, cols.Count).Where(c => MayBeBlank(t, c) && lt.Rows.All(r => !double.IsFinite(r.Values[c]))).ToHashSet();
                if (blank.Count > 0 && Schemas.Of(lt.Kind) is { Open: false })
                {
                    lt.Columns = cols.Where((_, c) => !blank.Contains(c)).ToList();
                    foreach (var r in lt.Rows) r.Values = r.Values.Where((_, c) => !blank.Contains(c)).ToList();
                }
                if (LibraryFile.Problem(lt) is { } problem) return (tables, problem);
                if (LibraryDomains.Of(lt.Kind) != Domain) return (tables, $"{key} ({lt.Kind}) belongs in a {LibraryDomains.Name(LibraryDomains.Of(lt.Kind))} library, not this {LibraryDomains.Name(Domain)} one.");
                tables.Add(lt);
            }
            return (tables, null);
        }
    }

    /// <summary>
    /// A row as the table's grid binds it: its <see cref="Key"/>, then a cell per value column and per text column, by
    /// index (the table's columns vary, so the grid's columns are made in code and bound to <c>[i]</c>). A value that is
    /// not a number is refused (the cell keeps its value) and said through <c>problem</c>.
    /// </summary>
    sealed class GridRow : INotifyPropertyChanged
    {
        public readonly RowEdit Row;
        readonly Func<TableEdit> _table;
        readonly Action _changed;
        readonly Action<string> _problem;

        public GridRow(RowEdit row, Func<TableEdit> table, Action changed, Action<string> problem)
        {
            Row = row; _table = table; _changed = changed; _problem = problem;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key
        {
            get => Row.Key;
            set
            {
                value = (value ?? "").Trim();
                if (value == Row.Key) return;
                Row.Key = value;
                _changed(); Notify();
            }
        }

        /// <summary>A two-part key's parts (a thread's designation and class, a fastener hole's fastener and size).</summary>
        public string Key0 { get => Part(0); set => SetPart(0, value); }
        public string Key1 { get => Part(1); set => SetPart(1, value); }
        string Part(int i) => LibraryRow.SplitKeys(Row.Key) is var k && i < k.Count ? k[i] : "";
        void SetPart(int i, string? value)
        {
            var k = LibraryRow.SplitKeys(Row.Key);
            while (k.Count < 2) k.Add("");
            k[i] = (value ?? "").Trim();
            Key = k.All(x => x.Length == 0) ? "" : LibraryRow.JoinKeys(k);
        }

        public string this[int c]
        {
            get
            {
                var t = _table();
                return c < t.Columns.Count ? (c < Row.Values.Count ? Row.Values[c] : "0")
                    : c - t.Columns.Count < t.TextColumns.Count ? Row.Text.GetValueOrDefault(t.TextColumns[c - t.Columns.Count], "") : "";
            }
            set
            {
                value = (value ?? "").Trim();
                if (value == this[c]) return;
                var t = _table();
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

        /// <summary>The row's color (an appearance's or a material's r, g, b columns) as a swatch; null when it has none.</summary>
        public Func<GridRow, IBrush?>? SwatchOf;
        public IBrush? Swatch => SwatchOf?.Invoke(this);
        /// <summary>The swatch's color as hex ("#C0C0C0"; "" with none).</summary>
        public string Hex => Swatch is ISolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "";

        public void Notify()
        {
            foreach (var p in new[] { nameof(Key), nameof(Key0), nameof(Key1), "Item[]", nameof(Swatch), nameof(Hex) })
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
    /// A value as shown: as stored, in full (so it publishes back unchanged); a value not known blank.
    /// </summary>
    static string Shown(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>Whether a column may be left blank (not known): one its kind's schema does not require.</summary>
    static bool MayBeBlank(TableEdit t, int c) => c < t.Columns.Count && Schemas.Of(t.Kind)?.Column(t.Columns[c]) is { Required: false };

    static TableEdit Edit(LibraryTable t, bool published) => WithSchema(new()
    {
        Key = t.Key, Title = t.Title, Kind = t.Kind, Standard = t.Standard, Units = t.Units, Verified = t.Verified, Published = published, Profile = t.Profile,
        Columns = t.Columns.ToList(), TextColumns = LibraryCsv.TextColumns(t.Rows),
        Rows = t.Rows.Select(r => Edit(r, t)).ToList(),
    });

    /// <summary>
    /// A table of a kind with fixed columns shown with all of its schema's, in the schema's order (blank where the table
    /// has none; left out again when published while every row leaves them blank), then any of its own; a hole type's
    /// (its columns its own) as it is.
    /// </summary>
    static TableEdit WithSchema(TableEdit e)
    {
        if (Schemas.Of(e.Kind) is not { Open: false } schema) return e;
        var values = schema.Columns.Where(c => !c.IsText).Select(c => c.Path).ToList();
        var order = values.Concat(e.Columns.Where(c => !values.Contains(c))).ToList();
        foreach (var r in e.Rows)
        {
            var at = order.Select(c => e.Columns.IndexOf(c)).ToList();
            r.Values = at.Select(i => i >= 0 && i < r.Values.Count ? r.Values[i] : "").ToList();
            r.Exact = at.Select(i => i >= 0 && i < r.Exact.Count ? r.Exact[i] : null).ToList();
        }
        e.Columns = order;
        var texts = schema.Columns.Where(c => c.IsText).Select(c => c.Path).ToList();
        e.TextColumns = texts.Concat(e.TextColumns.Where(n => !texts.Contains(n))).ToList();
        return e;
    }

    static RowEdit Edit(LibraryRow r, LibraryTable t) => new()
    {
        Key = r.Key, Values = r.Values.Select(Shown).ToList(),
        Exact = r.Values.Select(v => (double?)v).ToList(),
        Text = r.Text == null ? new(StringComparer.Ordinal) : new Dictionary<string, string>(r.Text, StringComparer.Ordinal),
    };

    static State FromLibrary(LoadedLibrary lib, string path)
    {
        var s = new State { Path = path, Id = lib.LibraryId, Name = lib.Name, Domain = lib.Domain, ReadRevision = lib.RevisionId };
        s.Tables.AddRange(lib.Tables.Select(t => Edit(t, true)));
        s.ProfileTables.AddRange(lib.ProfileTables ?? Array.Empty<SpryCad.SheetMetal.ShopProfileTable>());
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
    /// <param name="add">
    /// A table to add to the library opened (a hole type made from a hole's profile), not yet published; selected.
    /// </param>
    /// <param name="domain">Which library: the standards or the materials in use.</param>
    public static async Task Open(MainWindow owner, string? table = null, LibraryTable? add = null, LibraryDomain domain = LibraryDomain.Standards)
    {
        State With(State s)
        {
            if (add == null) return s;
            var key = add.Key;
            for (int i = 2; s.Table(key) != null; i++) key = $"{add.Key}-{i}";
            var e = Edit(add, false);
            e.Key = key;
            s.Tables.Add(e);
            s.Dirty = true;
            table = key;
            return s;
        }
        // SpryCAD's defaults in use: a copy of them, published as a company's own library
        var inUse = domain == LibraryDomain.Materials ? LibraryLink.Current.Materials : LibraryLink.Current.Standards;
        if (inUse.Path is not { } path)
        {
            var b = FromLibrary(inUse, "");
            b.ReadRevision = null;
            await Show(owner, With(b), table);
            return;
        }
        State s;
        try { s = FromLibrary((await Task.Run(() => ReadCopy(path))).lib, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            await Dialogs.Message(owner, "Edit Library Failed", $"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        await Show(owner, With(s), table);
    }

    /// <summary>
    /// Tools > Libraries > New Company Library: a company's own standards or materials library at a path chosen,
    /// starting as a copy of SpryCAD's defaults (written when first published).
    /// </summary>
    public static async Task New(MainWindow owner)
    {
        var materials = !await Dialogs.YesNo(owner, "New Company Library",
            "Make a company standards library (threads, holes, fasteners) or materials library? It starts as a copy of SpryCAD's defaults.", "Standards", "Materials");
        var domain = materials ? LibraryDomain.Materials : LibraryDomain.Standards;
        var f = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New Company Library", SuggestedFileName = LibraryDomains.Name(domain) + LibraryFile.Extension, DefaultExtension = LibraryFile.Extension,
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
        var s = FromLibrary(new LoadedLibrary(Guid.NewGuid(), name.Trim(), domain, Guid.Empty, path, LibraryFile.Defaults(domain)), path);
        s.ReadRevision = null; s.Dirty = true;
        foreach (var t in s.Tables) t.Published = false;
        await Show(owner, s);
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
            var what = LibraryDomains.Name(s.Domain);
            pathText.Text = s.BuiltIn
                ? $"SpryCAD's default {what} (no company {what} library in use; see Options > Libraries). Publish saves this copy as your company's own "
                    + $"{what}{LibraryFile.Extension}, with a libraries.json, to use in place of the defaults."
                : $"{char.ToUpperInvariant(what[0])}{what[1..]} library · {s.Path}";
            info.Text = s.BuiltIn ? "SpryCAD's defaults; publishing writes your own copy to a folder." : s.ReadRevision == null ? "New; saved when first published." : $"Editing revision {s.ReadRevision.Value.ToString()[..8]}.";
        }

        // the left list: the library's tables
        List<string> Keys() => s.Tables.Select(t => t.Key).ToList();
        void Relist(string? select = null, bool body = true)
        {
            listing = true;
            list.Items.Clear();
            var keys = Keys();
            foreach (var k in keys)
            {
                var t = s.Table(k)!;
                var sp = new StackPanel { Margin = new Thickness(2, 1) };
                var keyText = Labels.Trimmed(k); keyText.FontWeight = FontWeight.SemiBold;
                sp.Children.Add(keyText);
                var sub = $"{(t.Title.Length > 0 ? t.Title + " · " : "")}{t.Rows.Count} rows";
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

        // ---- the right side: the chosen table's settings, then one grid of its rows
        void Body()
        {
            body.Children.Clear();
            heading.Text = selected ?? "";
            heading.IsVisible = selected != null;
            if (selected == null)
            {
                Top(Muted("No tables. Click + Table to add one, or Import from Excel."));
                return;
            }
            Section(selected);
        }

        void Section(string key)
        {
            var t = s.Table(key)!;
            TableProps(t);
            Top(Muted("Rows are offered in the order shown; click a header to sort. Type a new row into the blank one at the end. Parts that already use a size keep it when it is deleted here."));

            var cols = t.Columns.ToList();
            var texts = t.TextColumns.ToList();
            var unitsTable = new LibraryTable { Units = t.Units, Kind = t.Kind };

            void RowChanged() { Changed(); Relist(key, body: false); }
            // a color (r, g, b columns, 0-1) shown as a swatch beside the designation
            int ri = cols.IndexOf("r"), gi = cols.IndexOf("g"), bi = cols.IndexOf("b");
            bool colored = ri >= 0 && gi >= 0 && bi >= 0;
            IBrush? SwatchOf(GridRow g)
            {
                byte C(int c) => TryNumber(g[c], out var v) ? (byte)Math.Round(Math.Clamp(v, 0, 1) * 255) : (byte)0;
                return TryNumber(g[ri], out _) && TryNumber(g[gi], out _) && TryNumber(g[bi], out _) ? new SolidColorBrush(Color.FromRgb(C(ri), C(gi), C(bi))) : null;
            }
            GridRow Make(RowEdit row) => new(row, () => t, RowChanged, p => error.Text = p) { SwatchOf = colored ? SwatchOf : null };
            var items = new ObservableCollection<GridRow>();
            foreach (var row in t.Rows) { row.Pad(cols.Count); items.Add(Make(row)); }
            // a blank row at the end: typed into, it is the table's (and another blank one follows)
            GridRow pending = null!;
            void AddPending()
            {
                var re = new RowEdit { Values = cols.Select(_ => "").ToList(), Exact = cols.Select(_ => (double?)null).ToList() };
                pending = new GridRow(re, () => t, () =>
                {
                    if (!t.Rows.Contains(re)) { t.Rows.Add(re); AddPending(); }
                    RowChanged();
                }, p => error.Text = p) { SwatchOf = colored ? SwatchOf : null };
                items.Add(pending);
            }
            bool Real(GridRow g) => t.Rows.Contains(g.Row);

            var cv = new DataGridCollectionView(items);
            var dg = new DataGrid
            {
                ItemsSource = cv, AutoGenerateColumns = false, CanUserSortColumns = true, CanUserReorderColumns = false, CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.All, HeadersVisibility = DataGridHeadersVisibility.All,
                FrozenColumnCount = 1, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            };
            // a cell's text centered up and down while it is edited, where it was shown
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

            // the columns, each as wide as its heading: the key (frozen: Designation, or its two parts), a color swatch where
            // there is one, a column per value, a column per text. access[i]: what dg.Columns[i] shows, to copy and paste
            var access = new List<(Func<GridRow, string> get, Action<GridRow, string>? set)>();
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
            if (Schemas.Of(t.Kind)?.Key is { Length: 2 } keyDefs)
            {
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Header(keyDefs[0].Heading[^1], "With the next column, what parts reference the row by.", false, null, null),
                    Binding = new Binding(nameof(GridRow.Key0)) { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 90,
                    CustomSortComparer = new Comparer((a, b) => Natural(a.Key0, b.Key0) is var c && c != 0 ? c : Natural(a.Key1, b.Key1)),
                });
                access.Add((g => g.Key0, (g, v) => g.Key0 = v));
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Header(keyDefs[1].Heading[^1], "With the previous column, what parts reference the row by.", false, null, null),
                    Binding = new Binding(nameof(GridRow.Key1)) { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 60,
                    CustomSortComparer = new Comparer((a, b) => Natural(a.Key1, b.Key1)),
                });
                access.Add((g => g.Key1, (g, v) => g.Key1 = v));
                dg.FrozenColumnCount = 2;
            }
            else
            {
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Header("Designation", "Designation parts reference the row by, e.g. M8 or 1/4-20 UNC.", false, null, null),
                    Binding = new Binding(nameof(GridRow.Key)) { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 90,
                    CustomSortComparer = new Comparer((a, b) => Natural(a.Key, b.Key)),
                });
                access.Add((g => g.Key, (g, v) => g.Key = v));
            }
            if (colored)
            {
                // clicked: a color typed as #RRGGBB, into the r, g and b columns
                dg.Columns.Add(new DataGridTemplateColumn
                {
                    Header = Header("Color", "The row's color (its r, g and b), as hex. Click it to type another.", false, null, null), Width = DataGridLength.Auto, MinWidth = 110,
                    CellTemplate = new FuncDataTemplate<GridRow>((_, _) =>
                    {
                        var sw = new Border
                        {
                            Width = 28, Height = 14, CornerRadius = new CornerRadius(2), BorderBrush = Palette.TextMuted, BorderThickness = new Thickness(0.5),
                            VerticalAlignment = VerticalAlignment.Center,
                            [!Border.BackgroundProperty] = new Binding(nameof(GridRow.Swatch)),
                        };
                        var hex = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [!TextBlock.TextProperty] = new Binding(nameof(GridRow.Hex)) };
                        var cell = new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Margin = new Thickness(11, 0), Background = Brushes.Transparent,
                            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Children = { sw, hex },
                        };
                        cell.PointerPressed += async (_, e) =>
                        {
                            if (cell.DataContext is not GridRow g || !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
                            e.Handled = true;
                            string Hex(int c) => TryNumber(g[c], out var v) ? ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2") : "00";
                            var typed = (await Dialogs.Input(w, "Color", $"{g.Key}'s color, as #RRGGBB:", $"#{Hex(ri)}{Hex(gi)}{Hex(bi)}"))?.Trim().TrimStart('#');
                            if (typed == null) return;
                            if (typed.Length != 6 || !int.TryParse(typed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) { error.Text = $"\"{typed}\" is not a color (#RRGGBB)."; return; }
                            string Part(int shift) => (((rgb >> shift) & 0xFF) / 255.0).ToString("R", CultureInfo.InvariantCulture);
                            g[ri] = Part(16); g[gi] = Part(8); g[bi] = Part(0);
                        };
                        return cell;
                    }),
                });
                access.Add((g => g.Hex, null));
            }
            async Task<string?> ColumnName(string title, string label, string value)
            {
                var n = (await Dialogs.Input(w, title, label, value))?.Trim();
                if (string.IsNullOrEmpty(n) || n == value) return null;
                if (cols.Contains(n) || texts.Contains(n)) { error.Text = $"{key} already has a column {n}."; return null; }
                return n;
            }
            // (a column of the kind's schema named by its headings, "External Major Dia Max (in)", and fixed: it is not
            // renamed or deleted; a hole type's and a table's own columns are)
            var schema = Schemas.Of(t.Kind);
            string Name(string path) => schema?.Column(path) is { } def ? string.Join(" ", def.Heading.Distinct()) : path;
            bool Fixed(string path) => schema is { Open: false } && schema.Column(path) != null;
            for (int c = 0; c < cols.Count; c++)
            {
                int cc = c;
                if (colored && cols[c] is "r" or "g" or "b") continue; // (the Color column shows them)
                var unit = unitsTable.Label(cols[c]);
                var name = Name(cols[c]);
                var req = schema?.Column(cols[c])?.Required == true;
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Fixed(cols[c])
                        ? Header(unit.Length > 0 ? $"{name} ({unit})" : name, $"{cols[c]}: {(req ? "required" : "may be left blank")}.", false, null, null)
                        : Header(unit.Length > 0 ? $"{cols[c]} ({unit})" : cols[c], "A column of numbers: the commands read values by its name (a hole type's profile by its dimensions' names).", false,
                        async () =>
                        {
                            if (await ColumnName("Rename Column", "New column name (commands read values by name):", cols[cc]) is not { } n) return;
                            t.Columns[cc] = n;
                            foreach (var r in t.Rows) if (cc < r.Exact.Count) r.Exact[cc] = null; // (an angle column now a length, or the other way: as shown)
                            Changed(); Body();
                        },
                        () => { t.Columns.RemoveAt(cc); foreach (var r in t.Rows) r.RemoveAt(cc); Changed(); Body(); }),
                    Binding = new Binding($"[{c}]") { Mode = BindingMode.TwoWay }, Width = ToHeader, MinWidth = 72,
                    CustomSortComparer = new Comparer((a, b) => Numeric(a[cc], b[cc])),
                });
                access.Add((g => g[cc], (g, v) => g[cc] = v));
            }
            for (int c = 0; c < texts.Count; c++)
            {
                int cc = cols.Count + c;
                var n = texts[c];
                dg.Columns.Add(new DataGridTextColumn
                {
                    Header = Fixed(n) ? Header(Name(n), $"{n}: text.", false, null, null) : Header(n, "A column of this library's own, e.g. a supplier.", true,
                        async () =>
                        {
                            if (await ColumnName("Rename Text Column", "New column name:", n) is not { } nn) return;
                            t.TextColumns[t.TextColumns.IndexOf(n)] = nn;
                            foreach (var r in t.Rows) if (r.Text.Remove(n, out var v)) r.Text[nn] = v;
                            Changed(); Body();
                        },
                        () => { t.TextColumns.Remove(n); foreach (var r in t.Rows) r.Text.Remove(n); Changed(); Body(); }),
                    Binding = new Binding($"[{cc}]") { Mode = BindingMode.TwoWay }, Width = DataGridLength.Auto, MinWidth = 90,
                    CustomSortComparer = new Comparer((a, b) => Natural(a[cc], b[cc])),
                });
                access.Add((g => g[cc], (g, v) => g[cc] = v));
            }

            // a sort is the rows' order
            bool editing = false;
            dg.BeginningEdit += (_, _) => editing = true;
            dg.CellEditEnded += (_, _) => editing = false;
            dg.Sorting += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var order = cv.Cast<GridRow>().Where(Real).Select(g => g.Row).ToList();
                if (!order.SequenceEqual(t.Rows)) { t.Rows = order; Changed(); }
            });

            List<GridRow> Selected() => cv.Cast<GridRow>().Where(g => dg.SelectedItems.Contains(g) && Real(g)).ToList(); // in the order shown
            string NewKey() { int n = t.Rows.Count + 1; while (items.Any(x => x.Key == "Row" + n)) n++; return "Row" + n; }

            void DeleteRows()
            {
                var sel = Selected();
                if (sel.Count == 0) return;
                foreach (var g in sel) { t.Rows.Remove(g.Row); items.Remove(g); }
                RowChanged();
            }

            // copied as tab-separated text, the columns as shown, as spreadsheets paste it
            async Task Copy()
            {
                var sel = Selected();
                if (sel.Count == 0 || TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip) return;
                await clip.SetTextAsync(string.Join("\n", sel.Select(g => string.Join("\t", access.Select(a => a.get(g))))));
                owner.Status($"Copied {sel.Count} row{(sel.Count == 1 ? "" : "s")}.");
            }

            // pasted from the first row selected and the current column on, rows added as needed
            async Task Paste()
            {
                if (TopLevel.GetTopLevel(dg)?.Clipboard is not { } clip || await clip.GetTextAsync() is not { Length: > 0 } text) return;
                var lines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
                var sel = Selected();
                int row0 = sel.Count > 0 ? cv.IndexOf(sel[0]) : dg.SelectedItem is GridRow cur ? cv.IndexOf(cur) : cv.IndexOf(pending);
                int col0 = Math.Max(0, dg.CurrentColumn is { } cc ? dg.Columns.IndexOf(cc) : 0);
                for (int i = 0; i < lines.Length; i++)
                {
                    var cells = lines[i].Split('\t');
                    GridRow g;
                    if (row0 + i < cv.Count) g = (GridRow)cv[row0 + i]!;
                    else
                    {
                        var re = new RowEdit { Key = col0 == 0 ? "" : NewKey() };
                        re.Pad(t.Columns.Count);
                        t.Rows.Add(re);
                        items.Insert(items.IndexOf(pending), g = Make(re));
                    }
                    for (int j = 0; j < cells.Length && col0 + j < access.Count; j++)
                        access[col0 + j].set?.Invoke(g, cells[j]);
                }
                RowChanged();
            }

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
                },
            };

            AddPending();
            var delRows = new Button { Content = "Delete Rows" };
            ToolTip.SetTip(delRows, "Deletes the selected rows (Delete).");
            delRows.Click += (_, _) => DeleteRows();
            Bottom(Bar(new Control[] { delRows }.Concat(TableButtons(t)).ToArray()));
            Fill(cols.Count == 0 && t.Rows.Count == 0
                ? Muted(t.Kind.Length == 0 ? "Choose what the table is for: its columns are that kind's." : "No columns. Click + Column to add the profile's dimensions.")
                : dg);
        }

        void TableProps(TableEdit t)
        {
            var props = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,16,90,*"), RowDefinitions = new RowDefinitions("Auto,6,Auto,6,Auto") };
            void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); props.Children.Add(c); }
            TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
            var key = new TextBox { Text = t.Key, IsEnabled = !t.Published };
            ToolTip.SetTip(key, t.Published ? "Parts reference the table by key; a published table's key cannot change." : "Stable key that parts reference.");
            key.LostFocus += (_, _) =>
            {
                var k = (key.Text ?? "").Trim();
                if (k == t.Key) return;
                t.Key = k; Changed(); Relist(k);
            };
            var title = new TextBox { Text = t.Title };
            title.TextChanged += (_, _) => { if (title.Text != t.Title) { t.Title = title.Text ?? ""; Changed(); } };
            var standard = new TextBox { Text = t.Standard, Watermark = "e.g. ISO 273, or in-house" };
            standard.TextChanged += (_, _) => { if (standard.Text != t.Standard) { t.Standard = standard.Text ?? ""; Changed(); } };
            // (the kinds this library may hold: a standards library's, or a materials library's)
            var mine = Kinds.Where(k => LibraryDomains.Of(k.kind) == s.Domain).ToArray();
            var kinds = mine.Select(k => k.label).ToList();
            if (t.Kind.Length > 0 && mine.All(k => k.kind != t.Kind)) kinds.Add(Schemas.Of(t.Kind)?.Title ?? t.Kind);
            var kind = new ComboBox { ItemsSource = kinds, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Table type" };
            kind.SelectedIndex = mine.ToList().FindIndex(k => k.kind == t.Kind) is var ki && ki >= 0 ? ki : t.Kind.Length > 0 ? kinds.Count - 1 : -1;
            ToolTip.SetTip(kind, "Which command offers the rows: Thread for thread sizes, Hole Wizard for the rest.");
            kind.SelectionChanged += (_, _) =>
            {
                if (kind.SelectedIndex < 0 || kind.SelectedIndex >= mine.Length || mine[kind.SelectedIndex].kind == t.Kind) return;
                t.Kind = mine[kind.SelectedIndex].kind;
                // (its kind's columns, those it has kept)
                WithSchema(t);
                Changed(); Body();
            };
            var verified = new CheckBox { Content = "Verified against the published standard", IsChecked = t.Verified };
            verified.IsCheckedChanged += (_, _) => { t.Verified = verified.IsChecked == true; Changed(); };
            Cell(Label("Key"), 0, 0); Cell(key, 0, 1); Cell(Label("Title"), 0, 3); Cell(title, 0, 4);
            Cell(Label("For"), 2, 0); Cell(kind, 2, 1); Cell(Label("Standard"), 2, 3); Cell(standard, 2, 4);
            var units = new ComboBox
            {
                ItemsSource = t.Material ? new[] { "Metric (g/cm³, GPa, MPa)", "Inch-pound (lb/in³, Msi, ksi)" } : new[] { "Millimeters (mm)", "Inches (in)" },
                SelectedIndex = t.Units == TableUnits.Inch ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(units, "The units the values are given in, as the standard publishes them. Changing them converts the values.");
            units.SelectionChanged += (_, _) =>
            {
                var u = units.SelectedIndex == 1 ? TableUnits.Inch : TableUnits.Metric;
                if (u == t.Units) return;
                t.Convert(u);
                Changed(); Body();
            };
            Cell(Label("Units"), 4, 0); Cell(units, 4, 1); Cell(verified, 4, 3); Grid.SetColumnSpan(verified, 2);
            Top(props);
            Top(Muted((t.Material
                    ? $"Properties in {(t.Units == TableUnits.Inch ? "inch-pound" : "metric")} units, as each column header shows. Leave unknown values blank (density is required). "
                        + $"Type is one of {string.Join(", ", Enum.GetNames<MaterialType>())}; r, g, b, metallic, roughness and opacity (0 to 1) set the appearance. "
                    : $"Lengths in {(t.Units == TableUnits.Inch ? "inches" : "millimeters")}; angle columns in degrees. ")
                + "Text columns carry part numbers, suppliers and the like."));
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
            ToolTip.SetTip(addText, "A column of text of this library's own: a supplier, a note");
            addText.Click += async (_, _) =>
            {
                var n = (await Dialogs.Input(w, "Add Text Column", "Column name (text, e.g. supplier):", ""))?.Trim();
                if (string.IsNullOrEmpty(n)) return;
                if (t.Columns.Contains(n) || t.TextColumns.Contains(n)) { error.Text = $"{t.Key} already has a column {n}."; return; }
                t.TextColumns.Add(n);
                Changed(); Body();
            };
            var delTable = new Button { Content = "Delete Table" };
            delTable.Click += async (_, _) =>
            {
                if (t.Published && !await Dialogs.YesNo(w, "Delete Table",
                        $"Delete {t.Key} from the library? Existing parts keep their values; new parts cannot use it.", "Delete", "Cancel"))
                    return;
                s.Tables.Remove(t); Changed(); Relist();
            };
            // (a column of numbers: a hole type's, whose profile names its columns; a fixed kind's are its schema's)
            addCol.IsVisible = Schemas.Of(t.Kind) is not { Open: false };
            return new Control[] { addCol, addText, delTable };
        }

        // ---- the left side's buttons
        var addTable = new Button { Content = "+ Table" };
        ToolTip.SetTip(addTable, "A new table of this library: in-house sizes, another standard");
        addTable.Click += async (_, _) =>
        {
            var k = (await Dialogs.Input(w, "Add Table", "Table key (e.g. ACME-screws):", ""))?.Trim();
            if (string.IsNullOrEmpty(k)) return;
            if (k.IndexOfAny(new[] { ':', '/' }) >= 0) { error.Text = "A table's key cannot hold ':' or '/'."; return; }
            if (s.Table(k) != null) { error.Text = $"The library already has a table {k}."; return; }
            s.Tables.Add(new TableEdit { Key = k });
            Changed(); Relist(k);
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
            var (tables, problem) = s.Build();
            if (s.Name.Length == 0) problem = "The library needs a name.";
            if (problem != null) { error.Text = problem; return false; }
            // SpryCAD's defaults: written into a folder chosen, as the company's own library (an id and name of its own)
            string? folder = null;
            var fileName = LibraryDomains.Name(s.Domain) + LibraryFile.Extension;
            if (s.BuiltIn)
            {
                var picked = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Library Set Folder" });
                if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } f) return false;
                if (File.Exists(System.IO.Path.Combine(f, fileName)))
                {
                    await Dialogs.Message(w, "Publish", $"{f} already has a {fileName}: choose another folder, or use that library set (Options > Libraries) and edit it.");
                    return false;
                }
                folder = f;
            }
            var message = await Dialogs.Input(w, "Publish", "Change description (shown in history):", s.BuiltIn ? $"From SpryCAD's default {LibraryDomains.Name(s.Domain)}" : "");
            if (message == null) return false;
            if (folder != null)
            {
                s.Path = System.IO.Path.Combine(folder, fileName);
                s.Id = Guid.NewGuid();
                if (s.Name is BuiltInStandards.Name or BuiltInMaterials.Name) s.Name = s.Domain == LibraryDomain.Materials ? "Company materials" : "Company standards";
            }
            var file = System.IO.Path.GetFileName(s.Path);
            bool isNew = s.ReadRevision == null;
            Guid rev;
            try
            {
                var st = s;
                rev = await Task.Run(() => LibraryFile.Publish(st.Path, st.Id, st.Name, st.Domain, tables, UserSettings.AuthorName, message.Trim(), st.ReadRevision, st.ProfileTables));
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
                        + "Reload their version? Your changes will be lost; export them to Excel first to keep them.", "Reload", "Keep Editing"))
                    await ReloadFromFile();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                Unwritten();
                await Dialogs.Message(w, "Publish Failed", $"No write access to {s.Path}. Only library owners can publish.");
                return false;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                Unwritten();
                await Dialogs.Message(w, "Publish Failed", $"Could not publish {file}: {ex.Message}");
                return false;
            }
            // the defaults not written after all: still the defaults, unsaved
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
                await Dialogs.Message(w, "Publish Failed", $"Could not update {config}: {ex.Message}");
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

        // the tables read from a workbook taken into the library: what changes, table by table, shown first (with what
        // the reader noted), and nothing changed unless confirmed
        async Task Take(string p, List<LibraryTable> read, string title, List<string>? notes)
        {
            // what changes, table by table, before anything does
            var lines = new List<string>();
            var plan = new List<(TableEdit? old, TableEdit edit)>();
            foreach (var t in read)
            {
                var old = s.Table(t.Key);
                var e = Edit(t, old?.Published ?? false);
                e.Profile = old?.Profile; // (a hole type's profile is not in the workbook: kept)
                if (old == null) { lines.Add($"{t.Key}: new table, {t.Rows.Count} rows"); plan.Add((null, e)); continue; }
                int added = 0, changed = 0;
                foreach (var r in e.Rows)
                {
                    var o = old.Rows.FirstOrDefault(x => x.Key == r.Key);
                    if (o == null) { added++; continue; }
                    bool differs = false;
                    for (int c = 0; c < e.Columns.Count; c++)
                    {
                        var oc = old.Columns.IndexOf(e.Columns[c]);
                        var before = oc >= 0 ? o.Number(oc) : null;
                        var now = r.Exact[c];
                        // (within a spreadsheet's 15 digits, or a color the same to 1/255 as its hex gives it: unchanged,
                        // the exact value kept)
                        bool color = e.Columns[c] is "r" or "g" or "b";
                        if (before is { } b0 && now is { } n0 && (b0.Equals(n0) || Math.Abs(b0 - n0) <= 1e-12 * Math.Max(1, Math.Abs(b0))
                            || color && Math.Round(Math.Clamp(b0, 0, 1) * 255) == Math.Round(n0 * 255)))
                        { r.Values[c] = o.Values[oc]; r.Exact[c] = b0; }
                        else differs = true;
                    }
                    if (differs || !r.Text.OrderBy(x => x.Key).SequenceEqual(o.Text.OrderBy(x => x.Key))) changed++;
                }
                int removed = old.Rows.Count(o => e.Rows.All(r => r.Key != o.Key));
                bool columns = !e.Columns.SequenceEqual(old.Columns);
                if (added + changed + removed == 0 && !columns && e.Title == old.Title && e.Standard == old.Standard && e.Kind == old.Kind && e.Units == old.Units) continue;
                var parts = new List<string>();
                if (added > 0) parts.Add($"{added} added");
                if (changed > 0) parts.Add($"{changed} changed");
                if (removed > 0) parts.Add($"{removed} removed");
                if (columns) parts.Add(old.Published ? $"columns now {string.Join(", ", e.Columns)} (parts read values by column name)" : $"columns now {string.Join(", ", e.Columns)}");
                lines.Add($"{t.Key}: " + (parts.Count > 0 ? string.Join(", ", parts) : "title, standard or units changed"));
                plan.Add((old, e));
            }
            if (plan.Count == 0) { owner.Status($"{System.IO.Path.GetFileName(p)} has no changes for this library."); return; }
            var untouched = s.Tables.Count(x => read.All(t => t.Key != x.Key));
            if (lines.Count > 20) lines = lines.Take(20).Append($"… {lines.Count - 20} more").ToList();
            if (notes is { Count: > 0 }) lines.Add("\n" + string.Join("\n", notes.Take(10)) + (notes.Count > 10 ? $"\n… {notes.Count - 10} more" : ""));
            if (!await Dialogs.YesNo(w, title, string.Join("\n", lines)
                    + (untouched > 0 ? $"\n\n{untouched} table{(untouched == 1 ? " is" : "s are")} not in the workbook and stay as they are." : ""), "Import", "Cancel"))
                return;
            foreach (var (old, e) in plan)
                if (old == null) s.Tables.Add(e);
                else s.Tables[s.Tables.IndexOf(old)] = e;
            Changed(); Relist();
            owner.Status($"Imported {plan.Count} table{(plan.Count == 1 ? "" : "s")} from {System.IO.Path.GetFileName(p)} (not yet published).");
        }

        // ---- the whole library to Excel and back (as Inventor's hole and thread spreadsheets): a sheet per table
        var toExcel = new Button { Content = "Export to Excel…" };
        ToolTip.SetTip(toExcel, "Writes the library's tables to an Excel workbook, a sheet per table, to edit in a spreadsheet and import again");
        toExcel.Click += async (_, _) =>
        {
            var (tables, problem) = s.Build();
            if (problem != null) { error.Text = problem; return; }
            var f = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export to Excel", SuggestedFileName = (s.Name.Length > 0 ? s.Name : "library") + ".xlsx", DefaultExtension = ".xlsx", FileTypeChoices = new[] { ExcelType },
            });
            if (f?.TryGetLocalPath() is not { } p) return;
            // (made in memory first: a workbook that fails leaves no empty file behind)
            try
            {
                var ms = new MemoryStream();
                LibraryWorkbook.Write(tables, ms);
                await File.WriteAllBytesAsync(p, ms.ToArray());
                owner.Status($"Exported {tables.Count} tables to {p}.");
            }
            catch (Exception ex)
            {
                App.Log(ex, $"Exporting the library to {p}");
                await Dialogs.Message(w, "Export Failed", $"{System.IO.Path.GetFileName(p)} could not be written: {ex.Message}");
            }
        };
        var fromExcel = new Button { Content = "Import from Excel…" };
        ToolTip.SetTip(fromExcel, "Reads an Excel workbook's tables into the library (its sheets as Export to Excel writes them); shows what changes first. Publish to share them");
        fromExcel.Click += async (_, _) =>
        {
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import from Excel", AllowMultiple = false, FileTypeFilter = new[] { ExcelType } });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } p) return;
            List<LibraryTable> read; List<string> problems;
            try { await using var fs = File.OpenRead(p); (read, problems) = LibraryWorkbook.Read(fs); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
            { error.Text = $"{System.IO.Path.GetFileName(p)} could not be read: {ex.Message}"; return; }
            // (each table by its kind's schema, and of this library's domain)
            foreach (var t in read)
                if (LibraryFile.Problem(t) is { } tp) problems.Add(tp);
                else if (LibraryDomains.Of(t.Kind) != s.Domain) problems.Add($"{t.Key} ({t.Kind}) belongs in a {LibraryDomains.Name(LibraryDomains.Of(t.Kind))} library, not this {LibraryDomains.Name(s.Domain)} one.");
            // all or nothing: a workbook with problems changes nothing
            if (problems.Count > 0)
            {
                await Dialogs.Message(w, "Import Failed", $"{System.IO.Path.GetFileName(p)} has {problems.Count} problem{(problems.Count == 1 ? "" : "s")}; nothing was imported. Correct them and import again:\n\n"
                    + string.Join("\n", problems.Take(15)) + (problems.Count > 15 ? $"\n… {problems.Count - 15} more" : ""));
                return;
            }
            await Take(p, read, "Import from Excel", null);
        };

        // Inventor's thread and clearance spreadsheets (Design Data\XLS: thread.xlsx, clearance.xlsx) as tables
        var fromInventor = new Button { Content = "Import from Inventor…" };
        ToolTip.SetTip(fromInventor, "Reads Autodesk Inventor's thread.xlsx or clearance.xlsx: thread sheets as thread tables (a table per extra class), taper sheets as taper thread tables, clearance sheets as fastener holes tables; shows what changes first");
        fromInventor.Click += async (_, _) =>
        {
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import from Inventor", AllowMultiple = false, FileTypeFilter = new[] { ExcelType } });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } p) return;
            InventorWorkbook.Result result;
            // (read while Excel has it open too)
            try { await using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); result = InventorWorkbook.Read(fs); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
            { error.Text = $"{System.IO.Path.GetFileName(p)} could not be read: {ex.Message}"; return; }
            if (result.Tables.Count == 0) { await Dialogs.Message(w, "Import from Inventor", $"{System.IO.Path.GetFileName(p)} has no thread or clearance sheets.\n\n" + string.Join("\n", result.Notes.Take(10))); return; }
            await Take(p, result.Tables, "Import from Inventor", result.Notes);
        };
        fromInventor.IsVisible = s.Domain == LibraryDomain.Standards;

        // (a shop profile belongs in a standards library)
        profile.IsVisible = s.Domain == LibraryDomain.Standards;
        var leftButtons = new StackPanel { Spacing = 6, Margin = new Thickness(8), Children = { addTable, profile, toExcel, fromExcel, fromInventor } };
        foreach (var b in new[] { addTable, profile, toExcel, fromExcel, fromInventor }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
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
