using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SpryCad.App.Sketcher;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// Sketch Picture: an image inserted on the sketch plane to trace over, as SolidWorks'. Its page places it (center,
/// angle, size, opacity) and scales it from two points clicked on it a known distance apart (one distance, or one
/// along each of its axes). The image is embedded in the part; the sketch keeps where it is.
/// </summary>
public partial class MainWindow
{
    PmPage? _picturePage;
    bool _pictureSeparateXY, _pictureAspectFree;

    static readonly FilePickerFileType ImageType = new("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" } };

    /// <summary>A picture's page is the one shown (clicks in the sketch go to it).</summary>
    bool InPicturePage => _picturePage != null && _page == _picturePage && _sketch != null;

    async void SketchPictureCommand()
    {
        if (_sketch is not { } ed) { Status("Sketch Picture requires an active sketch. Edit or create a sketch first."); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Sketch Picture",
            FileTypeFilter = new[] { ImageType },
            AllowMultiple = false,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null || _sketch != ed) return;
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { App.Log(ex, "Sketch picture " + path); _ = Dialogs.Message(this, "Insert Failed", $"Could not read {Path.GetFileName(path)}: {ex.Message}"); return; }
        InsertPicture(ed, data, Path.GetFileName(path));
    }

    /// <summary>Ctrl+V in a sketch: the image on the clipboard inserted as a sketch picture, its page opened.</summary>
    async Task PastePicture()
    {
        if (_sketch is not { } ed || Clipboard is not { } clip) return;
        var got = await ClipboardImage.Read(clip);
        if (_sketch != ed) return;
        if (got is not { } image) { Status("The clipboard holds no image. Copy an image, then press Ctrl+V in the sketch."); return; }
        var (data, name) = image;
        // a bitmap from the clipboard: kept as a PNG (a tenth of the size or less)
        if (name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) && name.StartsWith("Pasted") && PictureImage.ToPng(data) is { } png)
            (data, name) = (png, Path.ChangeExtension(name, ".png"));
        InsertPicture(ed, data, name);
    }

    /// <summary>Embeds the image and adds it to the sketch as a picture across most of the view, its page opened.</summary>
    void InsertPicture(SketchEditor ed, byte[] data, string fileName)
    {
        if (PictureImage.Decode(data) is not { } img) { _ = Dialogs.Message(this, "Insert Failed", $"{fileName} is not an image SpryCAD can read. Use PNG, JPEG, BMP, GIF or WebP."); return; }
        if (InPicturePage) EndPicturePage(true);
        if (InEntityPage) EndEntityPage();
        // named by its content: the same image inserted again is stored once
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) ext = ".img";
        var name = "pictures/" + DocumentContainer.Hash(data)[..16] + ext;
        Session.Doc.AddFile(name, data);
        // across most of the view, at its center (on the sketch plane)
        var vw = Viewport.Camera.PixelSize * Viewport.Bounds.Width;
        var width = vw > 0 ? vw * 0.6 : 100;
        var center = Viewport.PlanePoint(ed.Frame, Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2) ?? Vec2.Zero;
        ed.BeginPictureEdit(null);
        var pic = ed.AddPicture(new SkPicture
        {
            File = name, Name = fileName,
            PixelWidth = img.SourceWidth, PixelHeight = img.SourceHeight,
            X = center.X, Y = center.Y, Width = width, Height = width * img.SourceHeight / img.SourceWidth,
        });
        _pictureSeparateXY = false;
        OpenPicturePage(pic.Id);
        Status("Picture inserted. Drag a corner to scale it, or click two points on it to scale it to a known distance.");
    }

    /// <summary>Opens a picture's page (one of the sketch's: edited from here until OK or Cancel).</summary>
    void EditPicture(long id)
    {
        if (_sketch is not { } ed || ed.Sk.Picture(id) == null) return;
        if (InPicturePage) EndPicturePage(true);
        if (InEntityPage) EndEntityPage();
        ed.BeginPictureEdit(id);
        _pictureSeparateXY = false;
        OpenPicturePage(id);
    }

    void OpenPicturePage(long id)
    {
        if (_sketch is not { } ed || ed.EditingPicture is not { } p) return;
        var u = Units;
        var page = new PmPage("Sketch Picture", "sketchpicture");
        var info = page.Section("Picture");
        page.Info(info, $"{p.Name}, {p.PixelWidth} × {p.PixelHeight} pixels.");
        page.Info(info, "Drag the picture to move it, a corner to scale it, or the round handle to rotate it.");

        bool Len(string v, out double mm) => UnitFormat.TryLength(v, Session.Lookup, out mm, out _);
        void Set(Action change) { change(); ed.PictureEdited(); }
        const double Same = 1e-9;
        // a length typed that is not the value as shown (the field shows it rounded: taking that back would move it)
        bool Typed(string v, double now, out double mm) =>
            Len(v, out mm) && !(Len(u.Entry(now), out var shown) && Math.Abs(mm - shown) <= Same);

        var pos = page.Section("Position");
        page.Number(pos, "X", () => u.Entry(p.X), v => { if (Typed(v, p.X, out var x)) Set(() => p.X = x); });
        page.Number(pos, "Y", () => u.Entry(p.Y), v => { if (Typed(v, p.Y, out var y)) Set(() => p.Y = y); });
        page.Number(pos, "Angle", () => u.Entry(p.Angle * 180 / Math.PI, ValueKind.Angle), v =>
        {
            try
            {
                var shown = Expr.Angle(u.Entry(p.Angle * 180 / Math.PI, ValueKind.Angle), null);
                var deg = Expr.Angle(v, Session.Lookup);
                if (Math.Abs(deg - shown) > Same) Set(() => p.Angle = deg * Math.PI / 180);
            }
            catch (Expr.ExprException) { }
        }, null, "°");

        var size = page.Section("Size");
        var aspect = p.Height / p.Width; // (as it is now: kept while locked)
        var locked = !_pictureAspectFree;
        TextBox? wBox = null, hBox = null;
        wBox = page.Number(size, "Width", () => u.Entry(p.Width), v =>
        {
            if (!Typed(v, p.Width, out var w) || w <= 0) return;
            Set(() => { p.Width = w; if (locked) p.Height = w * aspect; });
            if (locked && hBox is { IsFocused: false }) hBox.Text = u.Entry(p.Height);
        });
        hBox = page.Number(size, "Height", () => u.Entry(p.Height), v =>
        {
            if (!Typed(v, p.Height, out var h) || h <= 0) return;
            Set(() => { p.Height = h; if (locked) p.Width = h / aspect; });
            if (locked && wBox is { IsFocused: false }) wBox.Text = u.Entry(p.Width);
        });
        page.Check(size, "Lock aspect ratio", () => locked, v => { locked = v; _pictureAspectFree = !v; aspect = p.Height / p.Width; });
        page.ButtonRow(size, "Original Aspect Ratio", () =>
        {
            Set(() => p.Height = p.Width * p.PixelHeight / p.PixelWidth);
            OpenPicturePage(id);
        });

        var scale = page.Section("Scale From Two Points");
        if (ed.ScalePoints.Count < 2)
        {
            page.Info(scale, ed.PickingScalePoints
                ? "Click two points on the picture a known distance apart."
                : "Click two points on the picture a known distance apart, then enter the distance between them.");
            page.ButtonRow(scale, ed.PickingScalePoints ? "Placing Points…" : "Place Points", () => { ed.PickScalePoints(); OpenPicturePage(id); });
        }
        else
        {
            var d = p.ToLocal(ed.ScalePoints[1]) - p.ToLocal(ed.ScalePoints[0]);
            double dist = d.Length, dx = Math.Abs(d.X), dy = Math.Abs(d.Y);
            page.Check(scale, "Separate X and Y distances", () => _pictureSeparateXY, v => { _pictureSeparateXY = v; OpenPicturePage(id); });
            if (!_pictureSeparateXY)
                page.Number(scale, "Distance", () => u.Entry(dist), v => { if (Len(v, out var x)) dist = x; }, "dimension");
            else
            {
                page.Number(scale, "X distance", () => u.Entry(dx), v => { if (Len(v, out var x)) dx = x; }, "dimension");
                page.Number(scale, "Y distance", () => u.Entry(dy), v => { if (Len(v, out var y)) dy = y; }, "dimension");
                page.Info(scale, "X and Y are along the picture's own width and height. Each sets the scale in its direction.");
            }
            page.ButtonRow(scale, "Apply Scale", () =>
            {
                page.Flush();
                var problem = _pictureSeparateXY ? ed.ScalePicture(null, dx, dy) : ed.ScalePicture(dist, null, null);
                Status(problem ?? "Picture scaled.");
                if (problem == null) OpenPicturePage(id);
            });
            var level = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
            Button Half(string text, bool vertical, int col)
            {
                var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(b, $"Rotates the picture so the line between the points is {(vertical ? "vertical" : "horizontal")}.");
                b.Click += (_, _) => { if (ed.LevelPicture(vertical) is { } problem) Status(problem); else OpenPicturePage(id); };
                Grid.SetColumn(b, col);
                return b;
            }
            level.Children.Add(Half("Make Horizontal", false, 0));
            level.Children.Add(Half("Make Vertical", true, 2));
            scale.Children.Add(level);
            page.ButtonRow(scale, "Place Points Again", () => { ed.PickScalePoints(); OpenPicturePage(id); });
        }

        // a percentage field (0 to 100) of a value from 0 to 1, shown to a tenth (only a value typed applies)
        void Percent(StackPanel s, string label, Func<double> get, Action<double> set) =>
            page.Number(s, label, () => Expr.Format(Math.Round(get() * 100, 1)), v =>
            {
                try
                {
                    var x = Math.Clamp(Expr.Number(v, Session.Lookup) / 100, 0, 1);
                    if (Math.Abs(x - Math.Round(get() * 100, 1) / 100) > Same) Set(() => set(x));
                }
                catch (Expr.ExprException) { }
            }, null, "%");
        PictureImage? Original() => Session.Doc.Files.TryGetValue(p.File, out var bytes) ? PictureProcessor.Original(p.File, bytes) : null;

        var bg = page.Section("Background");
        page.Check(bg, "Remove background", () => p.Background != null, on =>
        {
            // (the color most of the border is, to start with)
            if (on && p.Background == null && Original() is { } img) Set(() => p.Background = PictureFilter.Hex(PictureFilter.BorderColor(img.Rgba, img.Width, img.Height)));
            else if (!on && p.Background != null) Set(() => p.Background = null);
            OpenPicturePage(id);
        });
        if (p.Background != null && PictureFilter.TryParseColor(p.Background, out var key))
        {
            var row = new DockPanel();
            var swatch = new Border
            {
                Width = 28, Height = 18, Margin = new Avalonia.Thickness(0, 0, 6, 0), BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = Palette.TextMuted, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(key.r, key.g, key.b)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
            var pick = new Button { Content = "Select Color", Padding = new Avalonia.Thickness(8, 2) };
            ToolTip.SetTip(pick, "Select Color\nTakes the background color from a point clicked on the picture.");
            pick.Click += (_, _) => ed.PickPoint("Click the background color on the picture.", pt =>
            {
                var q = p.ToLocal(pt);
                double cu = q.X / p.Width + 0.5, cv = 0.5 - q.Y / p.Height;
                if (cu is < 0 or > 1 || cv is < 0 or > 1 || Original() is not { } img) { Status("Click on the picture to select its background color."); return; }
                Set(() => p.Background = PictureFilter.Hex(img.At(cu, cv)));
                Status("Background color set.");
                OpenPicturePage(id);
            });
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            row.Children.Add(new TextBlock { Text = p.Background, VerticalAlignment = VerticalAlignment.Center, FontSize = TypeScale.Body });
            page.Field(bg, "Color", row);
            Percent(bg, "Tolerance", () => p.BackgroundTolerance ?? PictureLook.DefaultTolerance, x => p.BackgroundTolerance = x);
            page.Check(bg, "Only where connected to the border", () => p.BackgroundConnected == true, on => Set(() => p.BackgroundConnected = on ? true : null));
            page.Info(bg, "Colors within the tolerance of the background color become transparent.");
        }

        var edges = page.Section("Edges");
        page.Combo(edges, "Show", new List<(PictureEdges, string)> { (PictureEdges.None, "Picture"), (PictureEdges.Over, "Edges over picture"), (PictureEdges.Only, "Edges only") },
            () => p.Edges ?? PictureEdges.None, v =>
            {
                if (v == (p.Edges ?? PictureEdges.None)) return;
                Set(() => p.Edges = v == PictureEdges.None ? null : v);
                OpenPicturePage(id);
            });
        if (p.Edges is not (null or PictureEdges.None))
        {
            Percent(edges, "Sensitivity", () => p.EdgeSensitivity ?? PictureLook.DefaultSensitivity, x => p.EdgeSensitivity = x);
            page.Info(edges, "Higher sensitivity finds fainter edges. Edges are found on the picture before its background is removed.");
        }

        var show = page.Section("Display");
        page.Number(show, "Opacity", () => Expr.Format(Math.Round(p.Opacity * 100, 1)), v =>
        {
            try
            {
                var o = Math.Clamp(Expr.Number(v, Session.Lookup) / 100, 0, 1);
                if (Math.Abs(o - Math.Round(p.Opacity * 100, 1) / 100) > Same) Set(() => p.Opacity = o);
            }
            catch (Expr.ExprException) { }
        }, null, "%");
        page.ButtonRow(show, "Delete Picture", () =>
        {
            ed.DeletePicture(id);
            EndPicturePage(true);
            Status("Picture deleted.");
        }, "delete");

        page.OnOk = () => { page.Flush(); EndPicturePage(true); };
        page.OnCancel = () => EndPicturePage(false);
        _picturePage = page;
        ShowPage(page);
        ShowConfirmCorner(page.OnOk, page.OnCancel, "OK (Enter)", "Cancel (Esc)");
    }

    /// <summary>Back to the sketch's own page: the picture as edited (one undo step), or as it was (a new one removed).</summary>
    void EndPicturePage(bool keep)
    {
        if (_picturePage == null) return;
        _picturePage = null;
        _sketch?.EndPictureEdit(keep);
        if (_sketch == null) return;
        ShowConfirmCorner(() => ExitSketch(true), DiscardSketch, "Exit Sketch", "Discard Changes", okIcon: "exitsketch");
        BuildSketchPage();
    }

    /// <summary>The sketch page's Pictures section: Sketch Picture, and each picture to show or hide, edit or delete.</summary>
    void AddPictureSection(PmPage page, SketchEditor ed)
    {
        var s = page.Section("Pictures");
        page.ButtonRow(s, "Sketch Picture…", SketchPictureCommand, "sketchpicture");
        foreach (var p in ed.Sk.Pictures ?? new())
        {
            var id = p.Id;
            var row = new DockPanel { Margin = new Avalonia.Thickness(0, 1) };
            var del = Dialogs.DeleteButton("Delete picture");
            del.Click += (_, _) => { ed.DeletePicture(id); BuildSketchPage(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var edit = new Button { Content = "Edit", Padding = new Avalonia.Thickness(8, 2), Margin = new Avalonia.Thickness(4, 0) };
            ToolTip.SetTip(edit, "Edit Picture\nMoves, scales or rotates the picture, or changes its opacity.");
            edit.Click += (_, _) => EditPicture(id);
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);
            var shown = new CheckBox { Content = Labels.Trimmed(p.Name, TypeScale.Body), IsChecked = !p.Hidden };
            ToolTip.SetTip(shown, "Shows or hides the picture.");
            shown.IsCheckedChanged += (_, _) => ed.SetPictureHidden(id, shown.IsChecked != true);
            row.Children.Add(shown);
            s.Children.Add(row);
        }
        page.Info(s, ed.Sk.Pictures is { Count: > 0 }
            ? "Ctrl+V pastes an image from the clipboard. Double-click a picture in the graphics area to edit it."
            : "Ctrl+V pastes an image from the clipboard.");
    }
}
