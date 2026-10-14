using System.Text.RegularExpressions;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.Tests;

/// <summary>Random feature and sketch ids, and parts and assemblies saved before them (counted ids) re-id'd on opening.</summary>
static class IdTests
{
    static readonly Regex NewFeature = new(@"^F[0-9a-z]{12}$");
    const string F = "F[0-9a-z]{12}";

    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Ids: new feature and sketch ids are well formed, hold a letter and do not repeat", () =>
        {
            var features = new HashSet<string>();
            var sketch = new HashSet<long>();
            for (int i = 0; i < 1000; i++)
            {
                var f = Ids.NewFeatureId();
                t.True(NewFeature.IsMatch(f) && f.Skip(1).Any(char.IsLetter), $"feature id {f}");
                features.Add(f);
                var s = Ids.NewSketchId();
                t.True(s > 0 && s < 1L << 53, $"sketch id {s} in (0, 2^53)");
                t.True(Ids.Name(s).Any(char.IsLetter), $"sketch id {s} written {Ids.Name(s)}");
                sketch.Add(s);
            }
            t.Eq(features.Count, 1000);
            t.Eq(sketch.Count, 1000);
            // what a document and a sketch hand out
            var doc = new CadDocument();
            var sk = new SpryCad.Core.Sketching.Sketch();
            var p = sk.AddPoint(new SpryCad.Kernel.Vec2(1, 2));
            var feat = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            t.True(NewFeature.IsMatch(feat.Id), $"added feature {feat.Id}");
            t.True(p.Id > 0 && p.Id < 1L << 53 && p.Id != SpryCad.Core.Sketching.Sketch.OriginId, $"added point {p.Id}");
        });

        t.Test("Ids: a part saved before random ids is re-id'd on opening, the same every time and kept when saved", () =>
        {
            var file = Path.Combine(Root(), "tests", "SpryCad.Tests", "models", "shelled-bracket.sprt");
            t.True(File.ReadAllText(file).Contains("\"formatVersion\": 1"), "the fixture is format 1");
            var doc = CadDocument.Load(file);
            t.Eq(doc.FormatVersion, CadDocument.CurrentFormatVersion);
            t.True(doc.Features.Count > 0, "features");
            t.True(doc.Features.All(f => !Regex.IsMatch(f.Id, @"^F\d+$")), "no counted feature ids: " + string.Join(",", doc.Features.Select(f => f.Id)));
            t.True(doc.Features.All(f => NewFeature.IsMatch(f.Id)), "new-form feature ids: " + string.Join(",", doc.Features.Select(f => f.Id)));
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.True(r.State.Bodies.Count > 0, "a body");

            var again = CadDocument.Load(file);
            t.True(doc.Features.Select(f => f.Id).SequenceEqual(again.Features.Select(f => f.Id)), "the same ids opened twice");
            t.Eq(again.Id, doc.Id);
            t.Eq(again.ToJson(), doc.ToJson());

            var dir = Path.Combine(Path.GetTempPath(), "sprycad-tests", "ids");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shelled-bracket.sprt");
            doc.Save(path);
            var back = CadDocument.Load(path);
            t.Eq(back.FormatVersion, CadDocument.CurrentFormatVersion);
            t.True(doc.Features.Select(f => f.Id).SequenceEqual(back.Features.Select(f => f.Id)), "ids kept through save and open");
            doc.NameCounters = doc.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(); // (the file keeps them sorted)
            t.Eq(back.ToJson(), doc.ToJson());
            var r2 = new Regenerator().Rebuild(back);
            foreach (var f in back.Features) t.True(r2.Status[f.Id].Ok, $"reopened {f.Name}: {r2.Status[f.Id].Error}");
        });

        t.Test("Ids: a loft saved before random ids keeps its sections and guides (lists of sketch ids)", () =>
        {
            // (SectionSketchIds, GuideSketchIds: kept as "F1", "F2", the loft lost its sketches on opening)
            var doc = new CadDocument { Id = "loft-part" };
            doc.Features.Add(new SketchFeature { Id = "F1", Name = "Section 1" });
            doc.Features.Add(new SketchFeature { Id = "F2", Name = "Section 2" });
            doc.Features.Add(new SketchFeature { Id = "F3", Name = "Guide" });
            doc.Features.Add(new SketchFeature { Id = "F4", Name = "Centerline" });
            var loft = new LoftFeature { Id = "F5", SectionSketchIds = { "F1", "F2" }, GuideSketchIds = { "F3" }, CenterlineSketchId = "F4" };
            doc.Features.Add(loft);
            LegacyIds.Upgrade(doc, doc.ToJson());
            var ids = doc.Features.Select(f => f.Id).ToList();
            t.True(loft.SectionSketchIds.SequenceEqual(ids.Take(2)), $"sections {string.Join(",", loft.SectionSketchIds)} for {string.Join(",", ids)}");
            t.True(loft.GuideSketchIds.SequenceEqual(new[] { ids[2] }), $"guides {string.Join(",", loft.GuideSketchIds)}");
            t.Eq(loft.CenterlineSketchId, ids[3]);
        });

        t.Test("Ids: old face names translated once (idempotent), their structure kept", () =>
        {
            const string seed = "6a1f0c2e-1111-4222-8333-944455556666";
            foreach (var x in new[] { "F2.S.L6", "F9.I123", "F3.Turn", "F5.2.F2.End", "F9.I.F8.1.F7.0.R.CB", "F4-2", "F1.H.C3", "Box.End", "", "F2.Q.L6" })
            {
                var once = LegacyIds.Name(seed, x);
                t.Eq(LegacyIds.Name(seed, once), once);
            }
            t.True(Regex.IsMatch(LegacyIds.Name(seed, "F9.I123"), $@"^{F}\.I123$"), LegacyIds.Name(seed, "F9.I123"));
            t.True(Regex.IsMatch(LegacyIds.Name(seed, "F3.Turn"), $@"^{F}\.Turn$"), LegacyIds.Name(seed, "F3.Turn"));
            var nested = LegacyIds.Name(seed, "F5.2.F2.End");
            var m = Regex.Match(nested, $@"^({F})\.2\.({F})\.End$");
            t.True(m.Success && m.Groups[1].Value == LegacyIds.Feature(seed, "F5") && m.Groups[2].Value == LegacyIds.Feature(seed, "F2"), nested);
            var side = LegacyIds.Name(seed, "F2.S.L6");
            t.Eq(side, $"{LegacyIds.Feature(seed, "F2")}.S.L{Ids.Name(LegacyIds.Sketch(seed, 6))}");
            t.True(Regex.IsMatch(LegacyIds.Name(seed, "F1.H.C3"), $@"^{F}\.H\.C[0-9a-z]+$") && LegacyIds.Name(seed, "F1.H.C3") != LegacyIds.Feature(seed, "F1") + ".H.C3", LegacyIds.Name(seed, "F1.H.C3"));
            // sketch entities only after a side marker; names that are not ids left alone
            t.Eq(LegacyIds.Name(seed, "F2.Q.L6"), LegacyIds.Feature(seed, "F2") + ".Q.L6");
            t.True(Regex.IsMatch(LegacyIds.Name(seed, "F4-2"), $@"^{F}-2$"), LegacyIds.Name(seed, "F4-2"));
            t.Eq(LegacyIds.Name(seed, "Box.End"), "Box.End");
            t.Eq(LegacyIds.Name(seed, "F9.I.F8.1.F7.0.R.CB"), $"{LegacyIds.Feature(seed, "F9")}.I.{LegacyIds.Feature(seed, "F8")}.1.{LegacyIds.Feature(seed, "F7")}.0.R.CB");
            // another part (seed) gives other ids; origin stays
            t.True(LegacyIds.Feature(seed, "F2") != LegacyIds.Feature(seed + "x", "F2"), "seeded");
            t.Eq(LegacyIds.Sketch(seed, 0), 0L);
        });

        t.Test("Ids: an assembly saved before random ids opens with its mates on the re-id'd faces", () =>
        {
            var path = Path.Combine(Root(), "samples", "demos", "hinge.sasm");
            var asm = AssemblyDocument.Load(path);
            var m = AssemblyModel.Open(asm, path, new PartIndex());
            t.True(m.Doc.Mates.Count > 0, "mates");
            var faces = m.Doc.Mates.SelectMany(x => new[] { x.A.Face, x.B.Face }).ToList();
            t.True(faces.All(x => !Regex.IsMatch(x, @"^F\d+[.-]")), "mate faces renamed: " + string.Join(", ", faces));
            t.True(m.Doc.Components.All(c => m.DisplayOf(c) is { Problem: null }), "parts shown: " + string.Join("; ", m.Doc.Components.Select(c => m.DisplayOf(c)?.Problem).OfType<string>()));
            var broken = m.BrokenMates();
            t.True(broken.Count == 0, "broken mates: " + string.Join("; ", broken.Select(b => b.why)));
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied, "the mates hold");
        });
    }
}
