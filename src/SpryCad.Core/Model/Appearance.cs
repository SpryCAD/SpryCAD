namespace SpryCad.Core.Model;

/// <summary>
/// How a surface looks: a base color (RGB 0-1) and a cheap metallic/roughness description the viewport can draw, plus
/// opacity (1: opaque). <see cref="Name"/> is the preset's it came from ("Brushed aluminum"), if any.
/// </summary>
public sealed record Appearance(double[] Color, double Metallic = 0, double Roughness = 0.5, double Opacity = 1, string? Name = null)
{
    /// <summary>A part with no material and no appearance of its own (the grey parts have always had).</summary>
    public static readonly Appearance Default = new(new[] { 0.74, 0.76, 0.80 });

    public bool Equals(Appearance? o) =>
        o is not null && Color.SequenceEqual(o.Color) && Metallic == o.Metallic && Roughness == o.Roughness && Opacity == o.Opacity && Name == o.Name;

    public override int GetHashCode() => HashCode.Combine(Color.Length > 0 ? Color[0] : 0, Metallic, Roughness, Opacity, Name);
}
