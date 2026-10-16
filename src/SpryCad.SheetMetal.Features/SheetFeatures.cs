using SpryCad.Core.Model;

namespace SpryCad.SheetMetal.Features;

/// <summary>
/// The first feature of a sheet metal body (sheet metal spec section 6): a plate from a closed sketch, or a bent
/// profile from an open chain of lines (each corner a bend, the sketch the sheet's top side, the material on the
/// chain's left; <see cref="ReverseSide"/> runs the chain the other way).
/// </summary>
public sealed class BaseFlangeFeature : Feature
{
    public override string Kind => "Base-Flange";
    /// <summary>The sheet metal body it makes (<see cref="CadDocument.SheetBodies"/>).</summary>
    public string Body { get; set; } = "";
    public string SketchId { get; set; } = "";
    /// <summary>A bent profile's width, along the sketch's normal.</summary>
    public string Width { get; set; } = "50";
    /// <summary>A bent profile: the width the other way from the sketch plane.</summary>
    public bool ReverseWidth { get; set; }
    /// <summary>The material on the other side: of the sketch plane for a plate, of the chain for a bent profile.</summary>
    public bool ReverseSide { get; set; }
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

/// <summary>
/// Holes cut through the sheet, normal to it (sheet metal spec section 6): the closed profiles of a sketch on a plane
/// parallel to panels, cut through every such panel the profile lies inside (so through both walls of a channel).
/// Lines and arcs; for now each profile within one panel (not across its edge or a bend).
/// </summary>
public sealed class SheetCutFeature : Feature
{
    public override string Kind => "Sheet-Cut";
    public string Body { get; set; } = "";
    public string SketchId { get; set; } = "";
    public override IEnumerable<string> Consumes => new[] { SketchId };
}

/// <summary>
/// Hems on panel edges (sheet metal spec section 8): the edge folded back on itself. Closed: flat against the panel
/// (in contact for the flat; the folded solid keeps the profile's small modelling gap so it stays one valid solid).
/// Open: with a gap. Teardrop: past 180°, its return angled back toward the panel.
/// </summary>
public sealed class HemFeature : Feature
{
    public override string Kind => "Hem";
    public string Body { get; set; } = "";
    public List<string> Edges { get; set; } = new();
    public HemType Type { get; set; } = HemType.Closed;
    /// <summary>The hem's length, from its fold's outside to the return's end.</summary>
    public string Length { get; set; } = "10";
    /// <summary>An open hem's gap between the layers (blank: the profile's).</summary>
    public string? Gap { get; set; }
    /// <summary>A teardrop's inside radius (blank: the thickness).</summary>
    public string? Radius { get; set; }
    /// <summary>A teardrop's return angle toward the panel, degrees past 180°.</summary>
    public string Angle { get; set; } = "30";
    /// <summary>Which side the hem folds over: Up over the top side.</summary>
    public BendDirection Direction { get; set; } = BendDirection.Up;
    /// <summary>The hem's outside at the edge (the panel shortened by it); else the hem added beyond the edge.</summary>
    public bool MaterialInside { get; set; }
}

/// <summary>Where an edge flange's bend sits relative to the edge it is made on (sheet metal spec section 5).</summary>
public enum FlangePosition
{
    /// <summary>The flange's outside face at the edge: the panel shortened by the outside setback.</summary>
    MaterialInside,
    /// <summary>The flange's inside face at the edge: the panel shortened by the inside setback.</summary>
    MaterialOutside,
    /// <summary>The bend starts at the edge: the panel as it is.</summary>
    BendOutside,
}

/// <summary>
/// Flanges on panel edges (sheet metal spec section 6): a bend and a panel per edge, the whole edge long. Edges are
/// named by the faces the folded solid gives them ("BaseFlange1.P0.Edge(2)").
/// </summary>
public sealed class EdgeFlangeFeature : Feature
{
    public override string Kind => "Edge-Flange";
    public string Body { get; set; } = "";
    public List<string> Edges { get; set; } = new();
    public string Length { get; set; } = "20";
    /// <summary>Where the length is measured to at the bend (each flange's own option).</summary>
    public FlangeReference Reference { get; set; } = FlangeReference.OutsideMoldLine;
    /// <summary>The bend (deflection) angle, degrees: 90 for a square flange, less for a shallow one.</summary>
    public string Angle { get; set; } = "90";
    public BendDirection Direction { get; set; } = BendDirection.Up;
    public FlangePosition Position { get; set; } = FlangePosition.MaterialInside;
}
