using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>SpryCAD's own solid files (.sbrep): solids written and read back the same, and STEP assemblies kept in them.</summary>
static class NBrepFileTests
{
    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    // a body's display, bit for bit (its tessellation follows every curve and surface)
    static ulong MeshHash(NBody b)
    {
        ulong h = 14695981039346656037;
        void Mix(ulong v) => h = (h ^ v) * 1099511628211;
        var m = DisplayMesh.Build(NTess.ToSolid(b.Clone()));
        foreach (var x in m.Positions) Mix(BitConverter.SingleToUInt32Bits(x));
        foreach (var x in m.Normals) Mix(BitConverter.SingleToUInt32Bits(x));
        foreach (var x in m.Indices) Mix((ulong)x);
        foreach (var f in m.Faces) foreach (var ch in f.Name) Mix(ch);
        return h;
    }

    // a STEP file's instances as text, in their order (ids, types, arguments)
    static string Dump(StepFile f)
    {
        var sb = new System.Text.StringBuilder();
        void Arg(object? a)
        {
            switch (a)
            {
                case List<object?> l: sb.Append('('); foreach (var x in l) { Arg(x); sb.Append(','); } sb.Append(')'); break;
                case double d: sb.Append(BitConverter.DoubleToInt64Bits(d)); break;
                case StepTyped ty: sb.Append(ty.Type).Append('('); Arg(ty.Value); sb.Append(')'); break;
                case null: sb.Append('$'); break;
                default: sb.Append(a); break;
            }
        }
        foreach (var (type, args) in f.Header) { sb.Append(type); Arg(args); sb.Append('\n'); }
        foreach (var i in f.Instances.Values) { sb.Append(i.Id).Append('='); foreach (var (type, args) in i.Parts) { sb.Append(type); Arg(args); } sb.Append('\n'); }
        return sb.ToString();
    }

    public static void Run(TestRunner t)
    {
        t.Test("Part and assembly files made in memory read back as those saved to disk (every demo; an assembly's embedded parts)", () =>
        {
            var demos = Path.Combine(Root(), "samples", "demos");
            var tmp = Path.Combine(Path.GetTempPath(), "sprycad-mem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            static bool Same(Dictionary<SpryCad.Core.Storage.ObjectId, byte[]> a, Dictionary<SpryCad.Core.Storage.ObjectId, byte[]> b) =>
                a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && o.AsSpan().SequenceEqual(kv.Value));
            try
            {
                foreach (var file in Directory.GetFiles(demos, "*.sprt"))
                {
                    var onDisk = Path.Combine(tmp, "a.sprt");
                    DocumentContainer.Save(CadDocument.Load(file), onDisk);
                    var a = CadDocument.Load(onDisk);
                    var b = DocumentContainer.Load(DocumentContainer.ToNewBytes(CadDocument.Load(file), "b"), "b.sprt");
                    t.True(Same(SpryCad.Core.Storage.PartObjects.From(a), SpryCad.Core.Storage.PartObjects.From(b)), Path.GetFileName(file) + " made in memory differs");
                }
                foreach (var file in Directory.GetFiles(demos, "*.sasm"))
                {
                    var onDisk = Path.Combine(tmp, "a.sasm");
                    AssemblyContainer.Save(AssemblyContainer.Load(file), onDisk);
                    var a = AssemblyContainer.Load(onDisk);
                    var b = AssemblyContainer.Load(AssemblyContainer.ToBytes(AssemblyContainer.Load(file)), "b.sasm");
                    // (each save its own revision: the rest the same)
                    a.Revision = b.Revision = null;
                    t.True(a.ToJson() == b.ToJson(), Path.GetFileName(file) + " made in memory: its document differs");
                    t.True(a.Files.Keys.OrderBy(k => k).SequenceEqual(b.Files.Keys.OrderBy(k => k)), Path.GetFileName(file) + " made in memory: its files differ");
                    foreach (var name in a.Files.Keys.Where(k => k.EndsWith(CadDocument.FileExtension)))
                        t.True(Same(SpryCad.Core.Storage.PartObjects.From(DocumentContainer.Load(a.Files[name], name)), SpryCad.Core.Storage.PartObjects.From(DocumentContainer.Load(b.Files[name], name))),
                            $"{Path.GetFileName(file)} made in memory: {name} differs");
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        });


        t.Test("STEP files parsed in pieces at once read as parsed in order (every STEP model and demo export)", () =>
        {
            var root = Root();
            var files = Directory.GetFiles(Path.Combine(root, "tests", "SpryCad.Tests", "models", "step"), "*.step")
                .Concat(Directory.GetFiles(Path.Combine(root, "samples", "demos"), "*.step")).ToList();
            var was = StepFile.ParallelMin;
            try
            {
                foreach (var file in files)
                {
                    var data = File.ReadAllBytes(file);
                    StepFile.ParallelMin = int.MaxValue;
                    var inOrder = Dump(StepFile.Parse(data));
                    StepFile.ParallelMin = 0;
                    t.True(Dump(StepFile.Parse(data)) == inOrder, Path.GetFileName(file) + " parsed in pieces differs");
                }
            }
            finally { StepFile.ParallelMin = was; }
            t.True(files.Count > 5, "the STEP files found");
        });


        t.Test("Solid files: every STEP model and demo export written and read back the same (names, geometry, display)", () =>
        {
            var root = Root();
            var files = Directory.GetFiles(Path.Combine(root, "tests", "SpryCad.Tests", "models", "step"), "*.step")
                .Concat(Directory.GetFiles(Path.Combine(root, "samples", "demos"), "*.step")).ToList();
            int solids = 0;
            foreach (var file in files)
            {
                StepImportResult r;
                try { r = StepImport.Read(File.ReadAllBytes(file), "F1."); }
                catch (InvalidDataException) { continue; } // (not a part: an assembly, or not importable)
                var bytes = NBrepFile.Write(r.Solids, "F1.", r.Warnings);
                t.True(NBrepFile.Is(bytes), "the file says what it is");
                var back = NBrepFile.Read(bytes, "F1.");
                t.Eq(back.Solids.Count, r.Solids.Count);
                t.True(back.Warnings.SequenceEqual(r.Warnings), "notes kept");
                for (int i = 0; i < r.Solids.Count; i++)
                {
                    var (a, b) = (r.Solids[i].Body, back.Solids[i].Body);
                    var name = $"{Path.GetFileName(file)} solid {i + 1}";
                    t.True(a.Faces.Select(f => f.Tag.Name).SequenceEqual(b.Faces.Select(f => f.Tag.Name)), name + ": face names");
                    t.Eq(NHash.Of(b), NHash.Of(a));
                    t.Eq(MeshHash(b), MeshHash(a));
                    t.Eq(b.Validate(), null);
                    solids++;
                }
            }
            t.True(solids >= 10, $"only {solids} solids round tripped");
        });

        t.Test("Solid files: names take the reader's prefix; anything else refused, not written wrong", () =>
        {
            var r = StepImport.Read(File.ReadAllBytes(Path.Combine(Root(), "samples", "demos", "hand-wheel.step")), "F1.");
            var back = NBrepFile.Read(NBrepFile.Write(r.Solids, "F1."), "F7.");
            t.True(back.Solids[0].Body.Faces.All(f => f.Tag.Name.StartsWith("F7.", StringComparison.Ordinal)), "renamed");
            t.True(r.Solids[0].Body.Faces.Select(f => f.Tag.Name[3..]).SequenceEqual(back.Solids[0].Body.Faces.Select(f => f.Tag.Name[3..])), "same names after it");
            // a body with a pipe (a rolling-ball fillet): not a kind the files hold
            var wheel = new Regenerator().Rebuild(Demos.All.First(d => d.file == "spoked-handwheel.sprt").build()).State.Bodies[0].Solid.Native!;
            t.True(wheel.Faces.Any(f => f.Surface.Kind == SurfaceKind.Pipe || f.Surface.Kind == SurfaceKind.BSpline), "blends");
            if (wheel.Faces.Any(f => f.Surface.Kind == SurfaceKind.Pipe))
            {
                bool refused = false;
                try { NBrepFile.Write(new[] { new StepSolid("wheel", wheel) }, "x."); } catch (NotSupportedException) { refused = true; }
                t.True(refused, "pipe surfaces refused");
            }
        });

        t.Test("STEP assemblies: parts kept as solid files, read once (same names and geometry as the assembly file gave)", () =>
        {
            var step = Demos.AssemblyStep(Demos.Assemblies.First(d => d.file == "linkage.sasm").build(), "linkage");
            var m = AssemblyModel.FromStep(step, "linkage")!;
            t.True(m.Doc.Components.Count >= 2, "components");
            foreach (var pd in m.Parts.Values)
            {
                var im = pd.Doc!.Features.OfType<Core.Model.ImportFeature>().Single();
                t.True(im.File.EndsWith(".sbrep", StringComparison.Ordinal), im.File);
                t.True(NBrepFile.Is(pd.Doc.Files[im.File]), "a solid file");
                t.True(pd.Mesh.Indices.Length > 0, "built");
            }
        });
    }
}
