using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>The visual diff's face comparison (VisualDiff) and the changes between two documents described (PartDescribe).</summary>
static class VisualDiffTests
{
    static CadDocument Bracket()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return CadDocument.Load(Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "shelled-bracket.sprt"));
    }

    static DisplayMesh Mesh(CadDocument d) => AssemblyModel.Merge(new Regenerator().Rebuild(d).State.Bodies.Select(b => b.Mesh));

    public static void Run(TestRunner t)
    {
        t.Test("Visual diff: a part against itself has every face unchanged", () =>
        {
            var doc = Bracket();
            var faces = VisualDiff.Compare(Mesh(doc), Mesh(doc.Clone()));
            t.True(faces.Count > 0 && faces.Values.All(v => v == FaceChange.Unchanged), string.Join(", ", faces.Where(kv => kv.Value != FaceChange.Unchanged).Select(kv => $"{kv.Key} {kv.Value}")));
        });

        t.Test("Visual diff: a fillet's radius changed marks faces changed, keeps the rest; a feature removed marks its faces removed", () =>
        {
            var doc = Bracket();
            var before = Mesh(doc);
            var fillet = doc.Features.OfType<FilletFeature>().First();
            var edited = doc.Clone();
            ((FilletFeature)edited.Find(fillet.Id)!).Radius = "(" + fillet.Radius + ")*1.5";
            var faces = VisualDiff.Compare(before, Mesh(edited));
            int changed = faces.Values.Count(v => v == FaceChange.Changed), kept = faces.Values.Count(v => v == FaceChange.Unchanged);
            t.True(changed > 0 && kept > 0, $"{changed} changed, {kept} unchanged");
            t.True(faces.Where(kv => kv.Value == FaceChange.Changed).Any(kv => VisualDiff.FeatureOf(kv.Key) == fillet.Id), "the fillet's own faces changed");
            t.True(faces.Values.All(v => v is FaceChange.Changed or FaceChange.Unchanged), "faces added or removed by a radius change");

            // the last feature removed: the faces it made are removed (from the older state), none added by it
            var last = doc.Features.Last(f => f is not SketchFeature);
            var without = doc.Clone();
            without.Features.RemoveAll(f => f.Id == last.Id);
            var gone = VisualDiff.Compare(before, Mesh(without));
            var removed = gone.Where(kv => kv.Value == FaceChange.Removed).Select(kv => kv.Key).ToList();
            t.True(removed.Count > 0, $"{last.Name} removed: no face removed");
            t.True(!gone.Any(kv => kv.Value == FaceChange.Added && VisualDiff.FeatureOf(kv.Key) == last.Id), "faces of the removed feature added");
        });

        t.Test("Visual diff: the feature a face name was made by", () =>
        {
            t.Eq(VisualDiff.FeatureOf("Fk3x9q2m7b1ab.S.Lq8w"), "Fk3x9q2m7b1ab");
            t.Eq(VisualDiff.FeatureOf("Fk3x9q2m7b1ab-2.I17"), "Fk3x9q2m7b1ab");
            t.Eq(VisualDiff.FeatureOf("Fk3x9q2m7b1ab"), "Fk3x9q2m7b1ab");
        });

        t.Test("Changes between two documents (the part as it is now): described, the order only when it is another", () =>
        {
            var doc = Bracket();
            var ext = doc.Features.OfType<ExtrudeFeature>().First();
            var edited = doc.Clone();
            ((ExtrudeFeature)edited.Find(ext.Id)!).Depth = "(" + ext.Depth + ")+5";
            var changes = PartDescribe.Changes(doc, edited);
            t.Eq(changes.Count, 1);
            t.True(changes[0].Change.Kind == ChangeKind.Modified && changes[0].Name == ext.Name && changes[0].Text.StartsWith(ext.Name + ": depth"), changes[0].Text);
            // a feature added at the end: added, not a reorder
            var more = edited.Clone();
            more.AddFeature(new PlaneFeature { Base = PlaneRef.Top, Offset = "12" });
            var added = PartDescribe.Changes(edited, more);
            t.True(added.Count == 1 && added[0].Change.Kind == ChangeKind.Added, string.Join(" | ", added.Select(c => c.Text)));
        });
    }
}
