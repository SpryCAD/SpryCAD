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
