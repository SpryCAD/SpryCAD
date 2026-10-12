using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.Tests;

/// <summary>View > Color by Feature: each face's feature found from its name, a distinct color each, drawn run by run.</summary>
static class FeatureColorTests
{
    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Color by feature: a face's feature is the id its name starts with", () =>
        {
            t.Eq(FeatureColors.FeatureOf("F12.S.L5"), "F12");
            t.Eq(FeatureColors.FeatureOf("F14.2.Blend"), "F14");
            t.Eq(FeatureColors.FeatureOf("F20.1.F12.S.L5"), "F20"); // a pattern's copy: the pattern made it
            t.Eq(FeatureColors.FeatureOf("Face"), null);
            t.Eq(FeatureColors.FeatureOf(".x"), null);
        });

        t.Test("Color by feature: colors distinct and kept, each face's feature colored, runs cover the mesh", () =>
        {
            foreach (var file in new[] { "bracket.sprt", "spoked-handwheel.sprt" })
            {
                var doc = CadDocument.Load(Path.Combine(Root(), "samples", file));
                var colors = FeatureColors.Of(doc);
                t.True(colors.Count > 1, $"{file}: {colors.Count} colored features");
                t.True(doc.Features.Where(FeatureColors.Colored).All(f => colors.ContainsKey(f.Id)), $"{file}: every solid feature colored");
                t.True(!doc.Features.OfType<SketchFeature>().Any(f => colors.ContainsKey(f.Id)), $"{file}: sketches not colored");
                // told apart: no two features' colors close together
                var list = colors.Values.ToList();
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var d = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(list[i][k] - list[j][k], 2)));
                        t.True(d > 0.06, $"{file}: colors {i} and {j} {d:0.000} apart");
                    }
                // the same each time (same document, same colors) and in range
                var again = FeatureColors.Of(CadDocument.Load(Path.Combine(Root(), "samples", file)));
                foreach (var (id, c) in colors)
                {
                    t.True(again[id].SequenceEqual(c), $"{file}: {id} keeps its color");
                    t.True(c.All(v => v >= 0 && v <= 1) && FeatureColors.Vivid(c).All(v => v >= 0 && v <= 1), $"{file}: {id} color in 0..1");
                }

                var r = new Regenerator().Rebuild(doc);
                t.True(r.State.Bodies.Count > 0, $"{file}: bodies");
                foreach (var body in r.State.Bodies)
                {
                    var m = body.Mesh;
                    foreach (var fr in m.Faces)
                        t.True(FeatureColors.FeatureOf(fr.Name) is { } id && colors.ContainsKey(id), $"{file}: face {fr.Name} has a feature with a color");
                    var runs = FeatureColors.Runs(m);
                    int at = 0;
                    for (int i = 0; i < runs.Count; i++)
                    {
                        t.Eq(runs[i].start, at);
                        at += runs[i].count;
                        t.True(i == 0 || runs[i].feature != runs[i - 1].feature, $"{file}: runs {i - 1} and {i} of one feature merged");
                    }
                    t.Eq(at, m.Indices.Length);
                    t.True(runs.Count < m.Faces.Count, $"{file}: {runs.Count} runs for {m.Faces.Count} faces");
                    t.True(runs.Select(x => x.feature).Distinct().Count() > 1, $"{file}: faces of more than one feature");
                }
            }
        });
    }
}
