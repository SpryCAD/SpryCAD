using System.Text.RegularExpressions;
using SpryCad.Core.Sketching;

namespace SpryCad.Core.Model;

/// <summary>
/// What the user reads where ids would be (<see cref="Ids"/> are random and never shown): features by their
/// names, sketch geometry by its kind and place in the sketch ("Line3": the sketch's third line), face names
/// with both ("Boss-Extrude1.S.Line3" for "Fk3x9q2m7b1a.S.Lq8w5…").
/// </summary>
public static partial class DisplayNames
{
    /// <summary>A sketch point or entity: "Origin", "Point4", "Line3", "Centerline1", "Arc2", "Circle1".</summary>
    public static string Of(Sketch sk, long id)
    {
        if (id == Sketch.OriginId) return "Origin";
        if (sk.Entity(id) is { } e)
        {
            var kind = Kind(e);
            int n = 0;
            foreach (var x in sk.Entities) { if (Kind(x) == kind) n++; if (x == e) break; }
            return kind + n;
        }
        if (sk.Point(id) is { } p)
        {
            int n = 0;
            foreach (var x in sk.Points) { if (x.Id != Sketch.OriginId) n++; if (x == p) break; }
            return "Point" + n;
        }
        return "?";
    }

    static string Kind(SkEntity e) => e switch
    {
        SkLine l => l.Construction ? "Centerline" : "Line",
        SkArc => "Arc",
        SkCircle => "Circle",
        _ => "Point",
    };

    /// <summary>
    /// A face, edge or body name as the user reads it: each feature id replaced by the feature's name, and the sketch
    /// entity a side face was swept from by <see cref="Of(Sketch, long)"/>. Parts it does not know stay as they are.
    /// </summary>
    public static string Face(CadDocument doc, string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var parts = name.Split('.');
        Feature? last = null;
        for (int i = 0; i < parts.Length; i++)
        {
            // a body made by an import is "<id>-<k>"
            var dash = parts[i].IndexOf('-');
            var id = dash > 0 ? parts[i][..dash] : parts[i];
            if (doc.Find(id) is { } f) { parts[i] = f.Name + (dash > 0 ? parts[i][dash..] : ""); last = f; continue; }
            if (i > 0 && parts[i - 1] is "S" or "R" or "H" or "X" && SketchOf(doc, last) is { } sk
                && sk.Entities.FirstOrDefault(e => ProfileExtractor.FaceName(e) == parts[i]) is { } ent)
                parts[i] = Of(sk, ent.Id);
        }
        return string.Join('.', parts);
    }

    /// <summary>A message with the face, edge and body names in it as the user reads them (<see cref="Face"/>).</summary>
    public static string? Text(CadDocument doc, string? message) =>
        message == null || !message.Contains('F') ? message : IdName().Replace(message, m => Face(doc, m.Value));

    [GeneratedRegex(@"\bF[0-9a-z]{12}(?:-\d+)?(?:\.[A-Za-z0-9]+)*")] private static partial Regex IdName();

    static Sketch? SketchOf(CadDocument doc, Feature? f)
    {
        var id = f switch
        {
            ExtrudeFeature x => x.SketchId,
            RevolveFeature x => x.SketchId,
            HelicalSweepFeature x => x.SketchId,
            SweepFeature x => x.ProfileSketchId,
            _ => null,
        };
        return id != null && doc.Find(id) is SketchFeature s ? s.Sketch : null;
    }
}
