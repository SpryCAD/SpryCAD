using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;
using static SpryCad.Tests.NestedAssemblyTests;

namespace SpryCad.Tests;

/// <summary>
/// Assembly tools: sub-assemblies embedded, kept as a copy, configured and overridden per instance, made and
/// dissolved, components moved between levels; configurations, broken references, replacing; patterns, exploded views;
/// bills of materials, mass properties, interference, Pack and Go.
/// </summary>
static class AssemblyToolTests
{
    const double PinVolume = Math.PI * 25 * 10, BaseVolume = Math.PI * 400 * 5;

    static bool Near(Vec3 a, Vec3 b, double tol = 1e-6) => Vec3.Distance(a, b) < tol;

    public static void Run(TestRunner t)
    {
        t.Test("Assembly tools: embedded sub-assemblies; a linked one gone missing shows from the assembly's copy", () =>
        {
            var dir = NewDir("tools-embed");
            var subPath = PinSleeve(dir);
            var m = AssemblyModel.New();
            var e = m.InsertAssembly(subPath, Transform.Identity, embed: true);
            var l = m.InsertAssembly(subPath, Transform.Translation(new Vec3(50, 0, 0)));
            t.True(e.Part.Embedded == "subs/PinSleeve.sasm" && m.SubOf(e)!.Source == SubSource.Embedded && m.SubOf(l)!.Source == SubSource.File, "one embedded, one linked");
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            t.True(m.Doc.Caches.Values.Any(v => v.EndsWith(".sasm")), "the linked one's file kept as a copy");
            // the sub-assembly's file (and its parts) gone: both still show
            Directory.Delete(Path.Combine(dir, "sub"), true);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            var se = back.SubOf(back.Doc.Components[0])!; var sl = back.SubOf(back.Doc.Components[1])!;
            t.True(se.Source == SubSource.Embedded && sl.Source == SubSource.Cache && sl.Problem != null, $"embedded, and from the copy ({sl.Source}: {sl.Problem})");
            t.Eq(back.PlacedParts().Count(p => p.pd.Mesh.Indices.Length > 0), 4);

            // an embedded sub-assembly edited (saved again into the assembly)
            var sd = AssemblyContainer.Load(back.Doc.Files[e.Part.Embedded!], "x");
            sd.Components[1].Suppressed = true;
            back.UpdateEmbedded(e.Part.Embedded!, AssemblyContainer.ToBytes(sd));
            t.True(back.Find("C1/C2")!.Suppressed && back.Doc.Components[0].Part.Revision == sd.Revision, "the edit shows");
        });

        t.Test("Assembly tools: an embedded part edited, put back and the assembly saved: opened again as edited", () =>
        {
            var dir = NewDir("tools-embedded-part");
            var pp = Path.Combine(dir, "Pin.sprt");
            Cylinder(pp, 5, 10);
            var m = AssemblyModel.New();
            var c = m.Insert(pp, Transform.Identity, embed: true);
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            File.Delete(pp);
            // edited as the app does: the embedded copy written out, changed, saved, its bytes put back
            var copy = Path.Combine(NewDir("tools-embedded-copy"), "Pin.sprt");
            File.WriteAllBytes(copy, m.Doc.Files[c.Part.Embedded!]);
            var doc = CadDocument.Load(copy);
            ((SpryCad.Core.Sketching.SkCircle)doc.Features.OfType<SketchFeature>().Single().Sketch.Entities.Single(e => e is SpryCad.Core.Sketching.SkCircle)).R = 8;
            doc.Save(copy);
            // the assembly opened again from its file (its tab closed meanwhile), the edit put in, saved
            var again = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            again.UpdateEmbedded(c.Part.Embedded!, File.ReadAllBytes(copy), defer: true);
            again.Save(path);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            var shown = back.DisplayOf(back.Doc.Components[0])!;
            t.True(shown.Source == PartSource.Embedded && Math.Abs(shown.Mesh.Bounds.Max.X - 8) < 1e-3, $"the edit kept ({shown.Source}, {shown.Mesh.Bounds.Max.X:0.###})");
            t.True(back.Doc.Components[0].Part.Revision == doc.Revision, "its revision noted");
        });

        t.Test("Assembly tools: sub-assembly configurations per instance; overrides of its components per instance", () =>
        {
            var dir = NewDir("tools-config");
            var subPath = PinSleeve(dir);
            var sm = AssemblyModel.Open(AssemblyDocument.Load(subPath), subPath);
            t.True(sm.Doc.AddConfiguration("PinOnly"), "a configuration added");
            sm.Doc.Components[1].Suppressed = true;
            t.True(sm.ActivateConfiguration(AssemblyDocument.DefaultConfiguration) && !sm.Doc.Components[1].Suppressed, "Default: the sleeve back");
            sm.Save(subPath);

            var m = AssemblyModel.New();
            var a = m.InsertAssembly(subPath, Transform.Identity);
            var b = m.InsertAssembly(subPath, Transform.Translation(new Vec3(50, 0, 0)));
            t.True(m.ConfigurationsOf(a).SequenceEqual(new[] { "Default", "PinOnly" }), $"its configurations ({string.Join(", ", m.ConfigurationsOf(a))})");
            m.SetSubConfiguration(b, "PinOnly");
            t.True(m.Subs.Count == 2 && m.PlacedParts().Count() == 3 && m.Find("C2/C2")!.Suppressed && !m.Find("C1/C2")!.Suppressed, "one instance in each");

            // overrides: hidden, suppressed, colored for one instance; the file untouched
            m.SetHidden("C1/C1", true);
            m.SetColor("C1/C2", new[] { 1.0, 0, 0 });
            t.True(m.Find("C1/C1")!.Hidden && m.PlacedParts().All(p => p.o.Path != "C1/C1") && m.PlacedParts(hidden: true).Any(p => p.o.Path == "C1/C1"), "hidden (still counted)");
            t.True(m.Find("C1/C2")!.Color![0] == 1 && a.Overrides!.Count == 2, "colored");
            m.SetHidden("C1/C1", false);
            t.True(a.Overrides!.Count == 1, "an override back to the sub-assembly's state is dropped");
            m.SetSuppressed("C1/C2", true);
            t.True(m.Find("C1/C2")!.Suppressed && !AssemblyDocument.Load(subPath).Components[1].Suppressed, "suppressed here only");
            m.SetHidden("C2", true);
            t.True(m.Find("C2/C1")!.Hidden, "a hidden sub-assembly hides what it holds");
        });

        t.Test("Assembly tools: configurations (suppressed components and mates, parts' configurations)", () =>
        {
            var (m, sub, dir, _) = WithSub("tools-asmconfig", flexible: true);
            var mate = m.AddMate(MateType.Coincident, m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!, m.RefTo(null, "Front")!, 7);
            t.True(m.Doc.ConfigurationNames.SequenceEqual(new[] { "Default" }), "one, as the components are");
            t.True(m.Doc.AddConfiguration("NoBase") && m.Doc.ActiveConfigurationName == "NoBase", "added and active");
            m.Doc.Components[0].Suppressed = true;
            mate.Suppressed = true;
            t.True(m.ActivateConfiguration("Default") && !m.Doc.Components[0].Suppressed && !mate.Suppressed, "Default as it was");
            t.True(m.ActivateConfiguration("NoBase") && m.Doc.Components[0].Suppressed && mate.Suppressed && m.PlacedParts().Count() == 2, "NoBase as it was set");
            t.True(!m.Doc.DeleteConfiguration("NoBase") && m.Doc.RenameConfiguration("NoBase", "Bare") && m.Doc.ActiveConfigurationName == "Bare", "the active one renamed, not deleted");
            var path = Path.Combine(dir, "Top.sasm");
            m.Doc.RecordActiveConfiguration();
            m.Save(path);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            t.True(back.Doc.ConfigurationNames.SequenceEqual(new[] { "Default", "Bare" }) && back.ActivateConfiguration("Default") && !back.Doc.Components[0].Suppressed, "saved with them");
            t.True(back.Doc.DeleteConfiguration("Bare") && back.Doc.Configurations == null, "deleted down to one");
        });

        t.Test("Assembly tools: broken references; replacing a component (its mates kept where the faces are)", () =>
        {
            var (m, sub, dir, subPath) = WithSub("tools-broken", flexible: true);
            sub.Fixed = true;
            var mate = m.AddMate(MateType.Coincident, m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!, m.RefTo(null, "Front")!, 7);
            t.True(m.BrokenMates().Count == 0, "none broken");
            t.True(m.Broken(new MateRef { Component = "C2/C2", Face = "NoSuchFace" }) != null, "a face that is not there");
            // the sleeve taken out of the sub-assembly's file: the mate's component is gone
            var sd = AssemblyDocument.Load(subPath);
            sd.Components.RemoveAt(1); sd.Mates.Clear();
            sd.Save(subPath);
            m.ReloadChanged(new[] { subPath }, defer: false);
            t.True(m.BrokenMates() is [var (bm, why)] && bm == mate && why.Contains("gone"), "reported");

            // replaced by a bigger cylinder of the same make: the mates on it hold
            var (m2, pin, sleeve, d2) = (AssemblyModel.New(), (Component?)null, (Component?)null, NewDir("tools-replace"));
            string pp = Path.Combine(d2, "Pin.sprt"), sp = Path.Combine(d2, "Sleeve.sprt"), bp = Path.Combine(d2, "Big.sprt");
            Cylinder(pp, 5, 10); Cylinder(sp, 5, 10); Cylinder(bp, 8, 10);
            pin = m2.Insert(pp, Transform.Identity);
            sleeve = m2.Insert(sp, Transform.Translation(new Vec3(30, 0, 0)));
            m2.AddMate(MateType.Concentric, m2.RefTo(sleeve.Id, FaceAt(m2, sleeve.Id, "side"))!, m2.RefTo(pin.Id, FaceAt(m2, pin.Id, "side"))!);
            m2.AddMate(MateType.Coincident, m2.RefTo(sleeve.Id, FaceAt(m2, sleeve.Id, "start"))!, m2.RefTo(pin.Id, FaceAt(m2, pin.Id, "end"))!);
            var replaced = m2.Replace(sleeve, bp);
            m2.SolveMates();
            t.True(replaced.Single() == sleeve && sleeve.Name == "Big<1>" && m2.DisplayOf(sleeve)!.Mesh.Bounds.Max.X > 7.9, "the same component, the new part");
            t.True(m2.LastSolve!.Satisfied && m2.BrokenMates().Count == 0, $"its mates hold ({string.Join(", ", m2.BrokenMates().Select(x => x.why))})");
        });

        t.Test("Assembly tools: Form New Sub-assembly and Dissolve (places and mates kept); components moved between levels", () =>
        {
            var dir = NewDir("tools-form");
            string bp = Path.Combine(dir, "Base.sprt"), pp = Path.Combine(dir, "Pin.sprt"), sp = Path.Combine(dir, "Sleeve.sprt");
            Cylinder(bp, 20, 5); Cylinder(pp, 5, 10); Cylinder(sp, 5, 10);
            var m = AssemblyModel.New();
            var bas = m.Insert(bp, Transform.Identity);
            var pin = m.Insert(pp, Transform.Translation(new Vec3(40, 0, 0)));
            var sleeve = m.Insert(sp, Transform.Translation(new Vec3(80, 0, 0)));
            MateRef R(string id, string w) => m.RefTo(id, FaceAt(m, id, w))!;
            m.AddMate(MateType.Coincident, R(pin.Id, "start"), R(bas.Id, "end"));                 // pin on the base
            m.AddMate(MateType.Concentric, R(sleeve.Id, "side"), R(pin.Id, "side"));              // sleeve on the pin
            m.AddMate(MateType.Coincident, R(sleeve.Id, "start"), m.RefTo(null, "Front")!, 12);   // sleeve 12 up
            t.True(m.LastSolve!.Satisfied, "mated");
            var was = m.PlacedParts().ToDictionary(p => p.o.Component.Name, p => p.o.World.T);
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);

            var sub = m.FormSubAssembly(new[] { pin, sleeve }, Path.Combine(dir, "PinSleeve.sasm"), flexible: true);
            t.True(File.Exists(Path.Combine(dir, "PinSleeve.sasm")) && m.Doc.Components.Count == 2 && sub.Flexible, "a sub-assembly of two");
            var sd = AssemblyDocument.Load(Path.Combine(dir, "PinSleeve.sasm"));
            t.True(sd.Mates.Count == 1 && sd.Mates[0].Type == MateType.Concentric && sd.Components[0].Fixed, "the mate between them went with them; its first fixed");
            t.True(m.Doc.Mates.Count == 2 && m.Doc.Mates.All(x => x.A.Component?.StartsWith(sub.Id + "/") == true), $"the others on the new paths ({string.Join(", ", m.Doc.Mates.Select(x => x.A.Component))})");
            m.SolveMates();
            var now = m.PlacedParts().ToDictionary(p => p.o.Component.Name, p => p.o.World.T);
            t.True(m.LastSolve!.Satisfied && now.All(kv => Near(kv.Value, was.Values.First(v => Near(v, kv.Value, 1e-5)), 1e-5)), "everything where it was, satisfied");

            // a component moved into it and out again
            var basPath = m.MoveInto(bas, sub);
            t.True(basPath == sub.Id + "/C3" && m.Doc.Components.Count == 1 && AssemblyDocument.Load(Path.Combine(dir, "PinSleeve.sasm")).Components.Count == 3, $"moved in ({basPath})");
            t.True(Near(m.WorldOf(basPath).T, was["Base<1>"], 1e-6), "where it was");
            var outComp = m.MoveOut(basPath);
            t.True(m.Doc.Components.Count == 2 && AssemblyDocument.Load(Path.Combine(dir, "PinSleeve.sasm")).Components.Count == 2 && Near(outComp.Placement.T, was["Base<1>"], 1e-6), "moved out");
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied, $"its mates follow ({string.Join(", ", m.LastSolve.Failed())})");

            // dissolved: three components again, where they were, the mates satisfied
            var made = m.Dissolve(sub);
            m.SolveMates();
            t.True(made.Count == 2 && m.Doc.Components.Count == 3 && m.Subs.Count == 0, "dissolved");
            t.True(m.LastSolve!.Satisfied && m.Doc.Mates.Count == 3 && m.Doc.Mates.All(x => x.A.Component?.Contains('/') != true && x.B.Component?.Contains('/') != true), "mates on the components again");
            foreach (var (o, _) in m.PlacedParts()) t.True(was.Values.Any(v => Near(v, o.World.T, 1e-5)), $"{o.Component.Name} where it was ({o.World.T})");
        });

        t.Test("Assembly tools: linear, circular and mirror patterns (copies follow their seeds); exploded views", () =>
        {
            var dir = NewDir("tools-pattern");
            string bp = Path.Combine(dir, "Base.sprt"), pp = Path.Combine(dir, "Pin.sprt");
            Cylinder(bp, 20, 5); Cylinder(pp, 5, 10);
            var m = AssemblyModel.New();
            var bas = m.Insert(bp, Transform.Identity);
            var pin = m.Insert(pp, Transform.Translation(new Vec3(10, 0, 5)));
            var lin = m.AddPattern(PatternKind.Linear, new[] { pin }, m.RefTo(null, "Right")!, count: 3, spacing: 30);
            t.True(lin.Instances.Count == 2 && m.Doc.Components.Count == 4, "two copies");
            t.True(Near(m.Doc.Find(lin.Instances[0].Component)!.Placement.T, new Vec3(40, 0, 5)) && Near(m.Doc.Find(lin.Instances[1].Component)!.Placement.T, new Vec3(70, 0, 5)), "30 apart along X");
            t.True(m.Movable(lin.Instances[0].Component) == null, "a copy is not dragged");
            // the seed moved: the copies follow
            pin.Placement = Placement.From(Transform.Translation(new Vec3(10, 3, 5)));
            m.SolveMates();
            t.True(Near(m.Doc.Find(lin.Instances[1].Component)!.Placement.T, new Vec3(70, 3, 5)), "followed");
            lin.Count = 2; m.UpdatePattern(lin);
            t.True(lin.Instances.Count == 1 && m.Doc.Components.Count == 3, "fewer");
            m.RemovePattern(lin);
            t.True(m.Doc.Components.Count == 2 && m.Doc.Patterns == null, "removed with its copies");

            var cir = m.AddPattern(PatternKind.Circular, new[] { pin }, m.RefTo(bas.Id, FaceAt(m, bas.Id, "side"))!, count: 4, spacing: 90);
            var c2 = m.Doc.Find(cir.Instances[1].Component)!.Placement.T;
            t.True(Near(c2, new Vec3(-10, -3, 5)), $"half way round ({c2})");
            m.RemovePattern(cir, keepCopies: true);
            t.True(m.Doc.Components.Count == 5 && m.Doc.Components.All(c => c.Pattern == null), "kept as components");

            var mir = m.AddPattern(PatternKind.Mirror, new[] { pin }, m.RefTo(null, "Right")!);
            var mc = m.Doc.Find(mir.Instances[0].Component)!.Placement.ToTransform();
            t.True(Near(mc.T, new Vec3(-10, 3, 5)) && Math.Abs(mc.Determinant - 1) < 1e-9, $"mirrored across Right, turned not reflected ({mc.T}, det {mc.Determinant})");

            // exploded views: made from where the components are; played part way
            var v = m.AutoExplode();
            t.True(v.Steps.Count == m.Doc.Components.Count - 1 && v.Steps.All(s => !s.Components.Contains(bas.Id)), "every component but the fixed one");
            var half = AssemblyModel.ExplodeOffsets(v, 0.5);
            var full = AssemblyModel.ExplodeOffsets(v, 1);
            t.True(full.Count == v.Steps.Count && half.Count < full.Count && half.Count > 0, "half way: the first steps");
            t.True(Near(AssemblyModel.ExplodeOffsetOf(pin.Id + "/X", full), full[pin.Id]), "what a component holds moves with it");
        });

        t.Test("Assembly tools: bills of materials (top level, indented, parts only), mass properties, interference", () =>
        {
            var (m, sub, dir, subPath) = WithSub("tools-bom", flexible: false);
            var sub2 = m.InsertAssembly(subPath, Transform.Translation(new Vec3(-100, 0, 0)));
            var top = Bom.Build(m, BomKind.TopLevel);
            t.True(top.Count == 2 && top[0].Name == "Base" && top[0].Quantity == 1 && top[1].Name == "PinSleeve" && top[1].Quantity == 2 && top[1].IsAssembly, string.Join("; ", top));
            var ind = Bom.Build(m, BomKind.Indented);
            t.True(ind.Count == 4 && ind[2].Item == "2.1" && ind[2].Level == 1 && ind[2].Quantity == 1 && ind[3].Name == "Sleeve", string.Join("; ", ind));
            var parts = Bom.Build(m, BomKind.PartsOnly);
            t.True(parts.Count == 3 && parts.Single(r => r.Name == "Pin").Quantity == 2, string.Join("; ", parts));
            t.True(Bom.ToCsv(ind).Split('\n')[0] == "Item,Part,Configuration,Quantity,Material,File" && Bom.ToCsv(ind).Contains("2.1,  Pin,"), Bom.ToCsv(ind));

            var mp = AssemblyMass.Of(m);
            var vol = BaseVolume + 4 * PinVolume;
            t.True(mp.Parts == 5 && Math.Abs(mp.Volume - vol) < 1e-3 * vol && Math.Abs(mp.Mass - vol * 7.85e-3) < 1e-3 * mp.Mass, $"volume {mp.Volume:0.##} (want {vol:0.##}), mass {mp.Mass:0.###} g");
            // symmetric about x = 0 (the base at the origin, one pin and sleeve pair each side)
            t.True(Math.Abs(mp.Center.X) < 1e-6 && mp.Principal[0] > 0 && mp.Approximate.Count == 0, $"centered ({mp.Center})");

            // each pin and its sleeve share their volume (the sleeve sits on the pin)
            var notes = new List<string>();
            var clashes = Interference.Find(Interference.Items(m), notes);
            t.True(clashes.Count == 2 && clashes.All(c => Math.Abs(c.Volume - PinVolume) < 1e-3 * PinVolume) && notes.Count == 0, $"{clashes.Count} clashes: {string.Join(", ", clashes)} {string.Join("; ", notes)}");
            t.True(Interference.Find(Interference.Items(m), notes, withinSubAssemblies: false).Count == 0, "none between sub-assemblies' own parts when asked");
        });

        t.Test("Assembly tools: Pack and Go (every file copied, renamed, the copies refer to the copies)", () =>
        {
            var (m, sub, dir, subPath) = WithSub("tools-pack", flexible: true);
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            var dest = Path.Combine(NewDir("tools-pack-out"), "Packed");
            var plan = PackAndGo.Plan(m, dest, prefix: "X-");
            t.True(plan.Count == 5 && plan.All(i => Path.GetFileName(i.To).StartsWith("X-")), string.Join("; ", plan.Select(i => i.To)));
            t.True(plan.Any(i => i.To.EndsWith(Path.Combine("sub", "X-Pin.sprt"))), "the folders kept");
            PackAndGo.Run(plan);
            // the originals gone: the copy opens from the copies
            Directory.Delete(dir, true);
            var top = plan.First(i => i.From == Path.GetFullPath(path)).To;
            var back = AssemblyModel.Open(AssemblyDocument.Load(top), top);
            t.True(back.Resolution.Parts.Values.All(r => r.Status == PartStatus.Found && r.Path!.StartsWith(dest)), string.Join("; ", back.Resolution.Parts.Values));
            var inner = back.Subs.Values.Single().Model!;
            t.True(inner.Resolution.Parts.Values.All(r => r.Status == PartStatus.Found && r.Path!.StartsWith(dest)) && back.PlacedParts().Count() == 3, "the sub-assembly's parts too");
            var zip = Path.Combine(Path.GetDirectoryName(dest)!, "Packed.zip");
            PackAndGo.Zip(dest, zip);
            t.True(new FileInfo(zip).Length > 1000, "zipped");
        });
    }
}
