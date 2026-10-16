using SpryCad.Core.Assembly;
using Avalonia.Controls;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// Section View, as SolidWorks': the model (a part's bodies, or an assembly's components) cut along a plane, what is
/// on one side taken away and the cut filled, so the inside shows. The plane: one of the origin planes, a reference
/// plane or any flat face (of the part, or of a component), moved along by an offset (typed, or by dragging the
/// arrow on it), the side cut away flipped if wanted. A view setting: nothing in the model changes. The heads-up bar's
/// section button (and View > Section View) opens the page, and turns it off.
/// </summary>
public partial class MainWindow
{
    bool _sectionOn;
    SelItem? _sectionRef;          // a part's plane or face (null: none picked)
    (Vec3 p, Vec3 n)? _sectionFace; // an assembly component's flat face (null: the origin plane below, if any)
    string? _sectionStd;           // an assembly's origin plane (null: none chosen)
    string _sectionOffset = "0";
    bool _sectionFlip;
    PmPage? _sectionPage;
    TextBox? _sectionOffsetBox;
    double? _sectionDragFrom;      // the offset when the arrow was pressed

    /// <summary>Whether the Section View page is open (an assembly's face clicks go to it).</summary>
    bool SectionPageOpen => _sectionPage != null && _page == _sectionPage;

    /// <summary>
    /// The section plane as set: the chosen plane moved by the offset along its normal, the side its normal points to
    /// cut away (flipped: the other). Null until a face or plane is picked: nothing is cut.
    /// </summary>
    Plane? SectionPlane()
    {
        Frame? picked = Asm != null && _sectionFace is { } sf ? Frame.FromPlane(Plane.FromPointNormal(sf.p, sf.n))
            : Asm == null && _sectionRef != null && TryPlaneOf(_sectionRef, out _, out var fr) ? fr
            : Asm != null && _sectionStd != null ? Resolver.StdPlane(_sectionStd)
            : null;
        if (picked is not { } f) return null;
        var n = f.Normal.Normalized;
        return Plane.FromPointNormal(f.Origin + n * SectionOffset(), _sectionFlip ? -n : n);
    }

    // (millimetres, the offset kept with its units)
    double SectionOffset() => UnitFormat.TryLength(_sectionOffset, Session.Lookup, out var v, out _) ? v : 0;

    void ApplySection()
    {
        UpdateHudStates();
        Viewport.Section = _sectionOn ? SectionPlane() : null;
        Viewport.Redraw();
    }

    /// <summary>The section button: on, the page to choose the plane (live); already on, off again.</summary>
    void SectionCommand()
    {
        if (SectionPageOpen) return;
        if (_sectionOn) { _sectionOn = false; ApplySection(); Status("Section view off."); return; }
        if (_sketch != null || _edit != null) { Status("Finish the current edit before sectioning."); return; }
        var before = (_sectionRef, _sectionFace, _sectionStd, _sectionOffset, _sectionFlip);
        // nothing picked to start: the user picks the face or plane to cut along (nothing is cut until then)
        (_sectionRef, _sectionFace, _sectionStd, _sectionOffset, _sectionFlip) = (null, null, null, "0", false);
        var page = new PmPage("Section View", "section");
        var s = page.Section("Section Plane");
        if (Asm == null)
        {
            var box = page.Selection(s, "Plane", SelFilter.Plane | SelFilter.PlanarFace, "Pick a flat face of the part, or a plane (in the graphics area or the tree), to cut along.", 1);
            box.Changed += () => { _sectionRef = box.Items.FirstOrDefault(); ApplySection(); };
        }
        else
        {
            page.Combo(s, "Plane", new List<(string, string)> { ("", "None (pick a face)"), ("Front", "Front Plane"), ("Top", "Top Plane"), ("Right", "Right Plane") },
                () => _sectionStd ?? "", v => { _sectionStd = v.Length > 0 ? v : null; _sectionFace = null; ApplySection(); }, "plane");
            page.Info(s, "Click any flat face of a component to cut along it, or choose an origin plane.");
        }
        _sectionOffsetBox = page.Number(s, "Offset", () => _sectionOffset, v => { _sectionOffset = v; ApplySection(); }, "dimension");
        page.Check(s, "Flip the side cut away", () => _sectionFlip, v => { _sectionFlip = v; ApplySection(); });
        page.Info(s, "Drag the arrow on the plane to move the cut. A view only: the model is not changed; the section button turns it off.");
        void Close() { _sectionPage = null; _sectionOffsetBox = null; Viewport.SectionArrow = false; ShowPage(null); }
        page.OnOk = () =>
        {
            Close();
            // (nothing picked: no section)
            if (SectionPlane() == null) { _sectionOn = false; ApplySection(); Status("No face or plane picked: no section."); return; }
            ApplySection();
            Status("Section view on (the section button turns it off).");
        };
        page.OnCancel = () =>
        {
            (_sectionRef, _sectionFace, _sectionStd, _sectionOffset, _sectionFlip) = before;
            _sectionOn = false;
            Close();
            ApplySection();
        };
        // the arrow: dragged, the offset follows (its field too)
        Viewport.SectionDragged = d =>
        {
            _sectionDragFrom ??= SectionOffset();
            var off = _sectionDragFrom.Value + (_sectionFlip ? -d : d);
            var text = Units.Entry(off);
            if (_sectionOffsetBox != null) _sectionOffsetBox.Text = text; // (its setter applies it)
            else { _sectionOffset = text; ApplySection(); }
        };
        Viewport.SectionDragEnded = () => _sectionDragFrom = null;
        _sectionPage = page;
        _sectionOn = true;
        ShowPage(page);
        Viewport.SectionArrow = true;
        ApplySection();
    }

    /// <summary>A component's face clicked while the page is open: a flat one is the plane cut along.</summary>
    void SectionPickFace(string component, string face)
    {
        if (Asm is not { } a) return;
        if (a.Model.RefTo(component, face) is not { } r || a.Model.World(r) is not { Kind: MateGeomKind.Plane } g)
        { Status($"{a.Model.NameAt(component)}: that face is not flat (pick a flat face to cut along)."); return; }
        _sectionFace = (g.P, g.D);
        ApplySection();
        Status($"Cutting along a face of {a.Model.NameAt(component)}.");
    }

    /// <summary>Another document shown: its own model, so the section goes.</summary>
    void EndSection()
    {
        if (!_sectionOn && _sectionPage == null) return;
        _sectionOn = false; _sectionRef = null; _sectionFace = null; _sectionPage = null; _sectionOffsetBox = null;
        Viewport.SectionArrow = false;
        Viewport.Section = null;
        UpdateHudStates();
    }
}
