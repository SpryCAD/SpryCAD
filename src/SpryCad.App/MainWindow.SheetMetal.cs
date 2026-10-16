using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.SheetMetal;
using SpryCad.SheetMetal.Brep;
using SpryCad.SheetMetal.Export;
using SpryCad.SheetMetal.Features;

namespace SpryCad.App;

// Sheet metal (sheet metal spec): base and edge flanges, flattening, flat pattern export, the shop profile in use.
public partial class MainWindow
{
    /// <summary>The materials and gauges the shop profile in use offers ("Steel CRS", "16 ga"), in its order.</summary>
    static List<(string material, string gauge, string text)> Gauges(ShopProfile profile)
    {
        var list = new List<(string, string, string)>();
        if (!profile.Tables.TryGetValue(ProfileTables.Gauges, out var g)) return list;
        foreach (var r in g.Rows.Where(r => r.Keys.Count == 2 && r.Keys.All(k => k != ProfileRow.Any)))
        {
            var text = $"{r.Keys[0]}, {r.Keys[1]}";
            try { text += $" ({SheetRules.Resolve(profile, r.Keys[0], r.Keys[1]).Thickness:0.###} mm)"; }
            catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { text += " (incomplete in the profile)"; }
            list.Add((r.Keys[0], r.Keys[1], text));
        }
        return list;
    }

    void BaseFlangeCommand()
    {
        if (PartOnly("Base Flange")) return;
        var profile = LibraryLink.Current.Profile;
        var gauges = Gauges(profile);
        if (gauges.Count == 0) { Status($"The shop profile \"{profile.Name}\" lists no gauges."); return; }
        var sk = SketchFromSelection();
        // the gauge used last in this part, else the profile's first
        var last = Session.Doc.SheetBodies?.LastOrDefault();
        var (m, g, _) = last != null && gauges.Any(x => x.material == last.Material && x.gauge == last.Gauge) ? (last.Material, last.Gauge, "") : gauges[0];
        var probe = new CadDocument { SheetBodies = Session.Doc.SheetBodies?.Select(b => b.Clone()).ToList() };
        var body = SheetMetalModule.AddBody(probe, m, g);
        OpenBaseFlangePage(new BaseFlangeFeature { Body = body.Id, SketchId = sk ?? "" }, true, body);
    }

    void OpenBaseFlangePage(BaseFlangeFeature f, bool isNew, SheetMetalBody? body = null)
    {
        body ??= Session.Doc.SheetBody(f.Body)?.Clone() ?? new SheetMetalBody { Id = f.Body, Name = f.Body };
        var page = new PmPage("Base-Flange", "baseflange");
        var sheet = page.Section("Sheet");
        var gauges = Gauges(LibraryLink.Current.Profile);
        if (!gauges.Any(x => x.material == body.Material && x.gauge == body.Gauge))
            gauges.Insert(0, (body.Material, body.Gauge, $"{body.Material}, {body.Gauge} (not in the profile in use)"));
        page.Combo(sheet, "Material and gauge", gauges.Select(x => ((x.material, x.gauge), x.text)).ToList(),
            () => (body.Material, body.Gauge), v => { body.Material = v.Item1; body.Gauge = v.Item2; });
        page.Info(sheet, $"From the shop profile \"{LibraryLink.Current.Profile.Name}\": the gauge sets the thickness, its tooling the inside radius and bend allowance. Every part of the same material and gauge bends the same.");
        var p = page.Section("Profile");
        var sketch = page.Selection(p, "Sketch", SelFilter.Sketch, "A closed sketch (a plate), or lines end to end (a bent profile)", 1);
        if (f.SketchId.Length > 0) sketch.Items.Add(new SelItem { Kind = SelKind.Sketch, FeatureId = f.SketchId });
        sketch.Changed += () => f.SketchId = sketch.Items.FirstOrDefault()?.FeatureId ?? "";
        page.Check(p, "Material on the other side", () => f.ReverseSide, v => f.ReverseSide = v);
        page.Number(p, "Width (bent profile)", () => f.Width, v => f.Width = v);
        page.Check(p, "Width the other way", () => f.ReverseWidth, v => f.ReverseWidth = v);
        page.Info(p, "A bent profile: the lines are the sheet's top side, each corner a bend; the material is on the lines' left.");
        BeginEdit(f, isNew, page, overlay: false, hint: f.SketchId.Length == 0 ? "Select a sketch: closed for a plate, open lines for a bent profile." : null);
        if (_edit != null) _edit.ApplyToDoc = doc =>
        {
            var list = doc.SheetBodies ??= new();
            list.RemoveAll(b => b.Id == body.Id);
            list.Add(body.Clone());
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            SheetProfiles.Sync(doc);
        };
    }

    /// <summary>The sheet metal edge a picked face or edge is on (a panel's thin side face), as its face name.</summary>
    static string? SheetEdge(SelItem s) => s.Kind switch
    {
        SelKind.Face when s.Name != null && SheetMetalModule.IsPanelEdge(s.Name) => s.Name,
        SelKind.Edge when s.Edge != null => SheetMetalModule.IsPanelEdge(s.Edge.A) ? s.Edge.A : SheetMetalModule.IsPanelEdge(s.Edge.B) ? s.Edge.B : null,
        _ => null,
    };

    void EdgeFlangeCommand()
    {
        if (PartOnly("Edge Flange")) return;
        var sheets = Session.Doc.SheetBodies;
        if (sheets is not { Count: > 0 }) { Status("Make a base flange first (Sheet Metal > Base Flange)."); return; }
        var edges = Selection.Items.Select(SheetEdge).OfType<string>().Distinct().ToList();
        var bodyId = Selection.Items.FirstOrDefault(s => SheetEdge(s) != null)?.BodyId;
        var body = sheets.FirstOrDefault(b => b.Id == bodyId) ?? sheets[^1];
        OpenEdgeFlangePage(new EdgeFlangeFeature { Body = body.Id, Edges = edges, Length = Units == UnitSystem.IPS ? "1in" : "20" }, true);
    }

    void OpenEdgeFlangePage(EdgeFlangeFeature f, bool isNew)
    {
        var page = new PmPage("Edge-Flange", "edgeflange");
        var p = page.Section("Edges");
        var box = page.Selection(p, "Edges", SelFilter.Face | SelFilter.Edge, "The sheet's edges (its thin side faces) to put flanges on", accept: s => SheetEdge(s) != null);
        foreach (var e in f.Edges) box.Items.Add(new SelItem { Kind = SelKind.Face, Name = e, BodyId = f.Body });
        box.Changed += () => f.Edges = box.Items.Select(SheetEdge).OfType<string>().Distinct().ToList();
        var l = page.Section("Flange");
        page.Number(l, "Length", () => f.Length, v => f.Length = v);
        page.Combo(l, "Measured to", new[]
        {
            (FlangeReference.OutsideMoldLine, "Outside mold line (virtual sharp)"),
            (FlangeReference.InsideMoldLine, "Inside mold line"),
            (FlangeReference.Tangent, "Tangent line (where the bend starts)"),
        }, () => f.Reference, v => f.Reference = v);
        page.Number(l, "Angle", () => f.Angle, v => f.Angle = v, unit: "°");
        page.Combo(l, "Direction", new[] { (BendDirection.Up, "Up (toward the top side)"), (BendDirection.Down, "Down") }, () => f.Direction, v => f.Direction = v);
        page.Combo(l, "Position", new[]
        {
            (FlangePosition.MaterialInside, "Material inside (outside face at the edge)"),
            (FlangePosition.MaterialOutside, "Material outside (inside face at the edge)"),
            (FlangePosition.BendOutside, "Bend outside (the bend starts at the edge)"),
        }, () => f.Position, v => f.Position = v);
        page.Info(l, "The angle is the bend angle: 90° for a square flange, 45° for a shallow one. The inside radius and allowance come from the shop profile.");
        BeginEdit(f, isNew, page, overlay: true, hint: f.Edges.Count == 0 ? "Select the edges (the sheet's thin side faces) to put flanges on." : null);
    }

    /// <summary>
    /// Lists the sheet metal's corners (where flanges on two edges meet) with how each is made and what its check says;
    /// a corner's treatment and gap can be set apart from the profile's rule (one undoable change for all).
    /// </summary>
    void CornersCommand()
    {
        if (PartOnly("Corners")) return;
        var sheets = Session.Result?.State.Sheets.Values.ToList() ?? new();
        var corners = sheets.SelectMany(s => s.Graph.Corners.Where(c => double.IsFinite(c.Gap)).Select(c => (s, c))).ToList();
        if (corners.Count == 0) { Status("The sheet metal has no corners: they are where flanges on two edges of a panel meet."); return; }
        if (_edit != null) CancelEdit();
        var page = new PmPage("Corners", "edgeflange");
        var edits = new Dictionary<CornerId, (CornerTreatment? treatment, string gap)>();
        foreach (var (s, c) in corners)
        {
            var o = Session.Doc.SheetOverrides?.FirstOrDefault(x => x.Target == c.Id.Value);
            CornerTreatment? tr = o != null && o.Values.TryGetValue("sm.corners/treatment", out var ts) && Enum.TryParse<CornerTreatment>(ts, out var parsed) ? parsed : null;
            var gapText = o != null && o.Values.TryGetValue("sm.corners/gap", out var gs) ? gs : "";
            edits[c.Id] = (tr, gapText);
            var id = c.Id;
            var sec = page.Section(SheetMetalModule.Named(Session.Doc, $"Corner {c.Id}"));
            var check = s.Checks.FirstOrDefault(x => x.Entity == c.Id.Value && !x.Passed);
            page.Info(sec, $"Now: {c.Treatment}, gap {c.Gap:0.###} mm." + (check != null ? " ⚠ " + SheetMetalModule.Named(Session.Doc, check.Message) : ""));
            page.Combo(sec, "Treatment", new (CornerTreatment?, string)[]
            {
                (null, "The profile's rule"), (CornerTreatment.Open, "Open (both short of the other)"),
                (CornerTreatment.OverlapA, "Overlap A (the first covers the second)"), (CornerTreatment.OverlapB, "Overlap B (the second covers the first)"),
                (CornerTreatment.Closed, "Closed (overlapping, to the gap)"),
            }, () => edits[id].treatment, v => edits[id] = (v, edits[id].gap));
            page.Number(sec, "Gap", () => edits[id].gap, v => edits[id] = (edits[id].treatment, v), optional: true);
        }
        void Close() { ShowPage(null); HideConfirmCorner(); }
        void Ok()
        {
            Session.Change(() => { foreach (var (id, e) in edits) SheetMetalModule.SetCorner(Session.Doc, id, e.treatment, e.gap); });
            Close();
            Status("Corners updated.");
            RefreshScene();
        }
        page.OnOk = Ok;
        page.OnCancel = Close;
        ShowPage(page);
        ShowConfirmCorner(Ok, Close, "OK (Enter)", "Cancel (Esc)");
    }

    /// <summary>Shows the sheet metal bodies flat or folded (undoable, saved with the part).</summary>
    void ToggleFlattenCommand()
    {
        if (PartOnly("Flatten")) return;
        var sheets = Session.Doc.SheetBodies;
        if (sheets is not { Count: > 0 }) { Status("The part has no sheet metal."); return; }
        bool flat = !sheets.All(b => b.Flattened);
        Session.Change(() => { foreach (var b in Session.Doc.SheetBodies!) b.Flattened = flat; });
        Status(flat ? "Flattened." : "Folded.");
    }

    /// <summary>Writes each sheet metal body's flat pattern as DXF (or SVG), by the shop profile's layers, in millimetres.</summary>
    async Task ExportFlatCommand()
    {
        if (PartOnly("Flat pattern export")) return;
        var state = Session.Result?.State;
        var sheets = state?.Sheets.Values.ToList();
        if (sheets is not { Count: > 0 })
        {
            await Dialogs.Message(this, "Export Flat Pattern", "The part has no sheet metal: make a base flange first (Sheet Metal tab).");
            return;
        }
        var baseName = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : Session.UntitledName;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Flat Pattern",
            DefaultExtension = "dxf",
            SuggestedFileName = baseName + ".dxf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("DXF") { Patterns = new[] { "*.dxf" } },
                new FilePickerFileType("SVG") { Patterns = new[] { "*.svg" } },
            },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        bool svg = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        try
        {
            var written = new List<string>();
            foreach (var s in sheets)
            {
                var flat = FlatPattern2D.From(s.Graph);
                var style = OutputStyle.Of(s.Rules);
                // one file per body: the chosen name for one body, the body's id added for several
                var p = sheets.Count == 1 ? path : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{s.BodyId}{Path.GetExtension(path)}");
                using (var fs = File.Create(p))
                    if (svg) FlatExport.WriteSvg(fs, flat, style); else FlatExport.WriteDxf(fs, flat, style);
                written.Add(p);
            }
            Status($"Exported {string.Join(", ", written)}");
        }
        catch (Exception ex) { App.Log(ex, "Export failed"); await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    /// <summary>
    /// On opening a part: when the shop profile in use has changed rows its sheet metal uses, takes them in (sheet
    /// metal parts follow the profile, so the same material and gauge bend the same everywhere) as one undoable change
    /// that the history lists.
    /// </summary>
    void TakeProfileUpdates()
    {
        if (IsAssemblyActive || Session.ReadOnly) return;
        if (SheetProfiles.Update(Session.Doc) is not { } u) return;
        Session.Change(() => Session.Doc.SheetProfile = u.Fresh);
        Status($"Shop profile changes taken into this part ({u.Changes.Count}): {string.Join("; ", u.Changes.Take(3))}{(u.Changes.Count > 3 ? "…" : "")} (Undo to keep the old values).");
    }
}
