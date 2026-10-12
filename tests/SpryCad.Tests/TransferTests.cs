using System.Globalization;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Moving parts between an assembly and their own files (Core/Assembly/AssemblyModel.Transfer, Core/Storage/Supersede):
/// Make External and Make Virtual with their histories, the mark left on an original made virtual, the assemblies still
/// using it, and changes made to it afterwards pulled in.
/// </summary>
static class TransferTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-transfer-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Checks gathered and reported together at the end of a test (so the checks after one that fails still run).</summary>
    sealed class Soft
    {
        readonly List<string> _failed = new();
        public void True(bool cond, string msg) { if (!cond) _failed.Add(msg); }
        public void Done(TestRunner t) => t.True(_failed.Count == 0, string.Join(" | ", _failed));
    }

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>Bytes written to a new file in <paramref name="dir"/> (a document's database, to open as a file).</summary>
    static string ToFile(string dir, byte[] bytes, string ext)
    {
        var p = Path.Combine(dir, "x-" + Guid.NewGuid().ToString("N")[..8] + ext);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>A cylinder part (radius 5, height <paramref name="h"/>).</summary>
    static CadDocument Cylinder(double h)
    {
        var d = new CadDocument();
        var sk = new SpryCad.Core.Sketching.Sketch();
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = SpryCad.Core.Sketching.Sketch.OriginId, R = 5 });
        var s = d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        d.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = h.ToString(CultureInfo.InvariantCulture) });
        return d;
    }

    static void SetDepth(CadDocument d, double h) => ((ExtrudeFeature)d.Features[1]).Depth = h.ToString(CultureInfo.InvariantCulture);

    /// <summary>A cylinder part saved <paramref name="revisions"/> times (taller each time) at <paramref name="path"/>, its first revision tagged "v1".</summary>
    static CadDocument PartWithHistory(string path, int revisions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var d = Cylinder(10);
        for (int i = 0; i < revisions; i++) { SetDepth(d, 10 + i); d.Save(path); }
        using (var db = PartDatabase.Open(path, readOnly: false))
            db.SetTag(Guid.Parse(d.Id!), "v1", db.GetHistory(Guid.Parse(d.Id!))[^1].RevisionId);
        return d;
    }

    /// <summary>
    /// A document's history as compared: each revision (id, parent, root hash, kind, message, author) with the blob
    /// hashes of its objects, and its tags.
    /// </summary>
    static string Snapshot(string path, string id)
    {
        using var db = PartDatabase.Open(path);
        var pid = Guid.Parse(id);
        var sb = new System.Text.StringBuilder();
        foreach (var r in db.GetHistory(pid))
        {
            sb.Append($"rev {r.RevisionId} parent {r.ParentId} root {r.RootHash} {r.Kind} '{r.Message}' {r.Author}\n");
            foreach (var (o, h) in db.GetObjects(r.RevisionId).OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)) sb.Append($"  {o} {h}\n");
        }
        foreach (var (name, rev) in db.GetTags(pid).OrderBy(x => x.Name, StringComparer.Ordinal)) sb.Append($"tag {name} {rev}\n");
        return sb.ToString();
    }

    static List<Guid> Revisions(string path) => PartFile.History(path).Select(r => r.RevisionId).ToList();
    static List<Guid> Revisions(string dir, byte[] bytes) => Revisions(ToFile(dir, bytes, ".sprt"));

    /// <summary>The assembly file's head revision (the assembly's own history).</summary>
    static RevisionInfo AsmHead(string path) => PartFile.History(path)[0];

    static string Problems(AssemblyModel m) =>
        string.Join("; ", m.Doc.Components.Where(c => !c.IsAssembly).Select(c => (c, d: m.DisplayOf(c))).Where(x => x.d == null || x.d.Problem != null || x.d.Source == PartSource.None || x.d.Source == PartSource.Cache)
            .Select(x => $"{x.c.Name}: {(x.d == null ? "no display" : x.d.Source + " " + x.d.Problem)}")
            .Concat(m.Doc.Components.Where(c => c.IsAssembly).Select(c => (c, s: m.SubOf(c))).Where(x => x.s?.Model == null || x.s.Problem != null).Select(x => $"{x.c.Name}: {x.s?.Problem ?? "no sub-assembly"}")));

    /// <summary>
    /// An assembly at <paramref name="dir"/>/A.sasm with a part file parts/P.sprt (<paramref name="revisions"/> revisions)
    /// linked twice, saved, opened again from disk.
    /// </summary>
    static (AssemblyModel m, string asmPath, string partPath, CadDocument part) Linked(string dir, int revisions = 2)
    {
        var partPath = Path.Combine(dir, "parts", "P.sprt");
        var part = PartWithHistory(partPath, revisions);
        var m = AssemblyModel.New();
        m.Insert(partPath, Transform.Identity);
        m.Insert(partPath, Transform.Translation(new Vec3(30, 0, 0)));
        var asmPath = Path.Combine(dir, "A.sasm");
        m.Save(asmPath);
        return (AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex()), asmPath, partPath, part);
    }

    /// <summary>An assembly at <paramref name="asmPath"/> linking the part file once, saved.</summary>
    static void LinkingAssembly(string asmPath, string partPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(asmPath)!);
        var m = AssemblyModel.New();
        m.Insert(partPath, Transform.Identity);
        m.Save(asmPath);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Make External: an embedded part's history goes to its file (ids, tags, root hashes); every component linked to it, shown; saved as a Conversion; found on reopening", () =>
        {
            var dir = NewDir("external");
            var src = Path.Combine(dir, "src", "Bracket.sprt");
            var part = PartWithHistory(src, 3);
            var m0 = AssemblyModel.New();
            m0.Insert(src, Transform.Identity, embed: true);
            m0.Insert(src, Transform.Translation(new Vec3(30, 0, 0)), embed: true);
            var asmPath = Path.Combine(dir, "A.sasm");
            m0.Save(asmPath);
            File.Delete(src); // (only the assembly has it now)
            var soft = new Soft();

            var m = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var c = m.Doc.Components[0];
            var name = c.Part.Embedded!;
            t.True(m.Doc.Components.All(x => x.Part.Embedded == name), "both components share one embedded copy");
            var embeddedFile = ToFile(dir, m.Doc.Files[name], ".sprt");
            var before = Snapshot(embeddedFile, part.Id!);
            var history = PartFile.History(embeddedFile);
            t.Eq(history.Count, 3);

            var target = Path.Combine(dir, "parts", "Bracket.sprt");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            m.MakeExternal(c, target);
            t.True(File.Exists(target), "the file written");
            t.True(!File.Exists(target + ".saving"), "no temporary file left");
            var written = PartFile.History(target);
            t.True(written.Select(r => (r.RevisionId, r.RootHash)).SequenceEqual(history.Select(r => (r.RevisionId, r.RootHash))), "the same revision ids and root hashes");
            using (var db = PartDatabase.Open(target))
            {
                t.True(db.GetTags(Guid.Parse(part.Id!)).Select(x => x.Name).SequenceEqual(new[] { "v1" }), "its tag");
                t.Eq(db.Manifest("RootKind"), AssemblyFile.RootKindPart);
                t.True(Supersede.Read(db) == null, "no mark on a part made external");
            }
            t.Eq(Snapshot(target, part.Id!), before);
            t.True(DocumentContainer.ReadIdentity(target).id == part.Id, "its identity");

            foreach (var x in m.Doc.Components)
                t.True(x.Part.Embedded == null && x.Part.AbsolutePath != null && PartIndex.SamePath(x.Part.AbsolutePath, target) && x.Part.Path == "parts/Bracket.sprt" && x.Part.Revision == written[0].RevisionId.ToString("D"),
                    $"{x.Name} linked: embedded {x.Part.Embedded}, path {x.Part.Path}, absolute {x.Part.AbsolutePath}, revision {x.Part.Revision}");
            t.True(!m.Doc.Files.ContainsKey(name), "the embedded copy dropped from the assembly");
            foreach (var x in m.Doc.Components)
            {
                var d = m.DisplayOf(x);
                soft.True(d != null && d.Problem == null && d.Source == PartSource.File && d.Mesh.Volume > 0,
                    $"{x.Name} shown from its file after Make External: {(d == null ? "no display" : $"{d.Source}, problem '{d.Problem}', volume {d.Mesh.Volume}")}");
            }

            m.Save(asmPath);
            var head = AsmHead(asmPath);
            t.True(head.Kind == RevisionKind.Conversion && head.Message == "Made Bracket external", $"the assembly's revision: {head.Kind} '{head.Message}'");
            t.True(m.Doc.SaveMessage == null && m.Doc.SaveKind == RevisionKind.Save, "the save message cleared by the save");
            // the next save is an ordinary one
            m.Doc.Components[1].Placement = Placement.From(Transform.Translation(new Vec3(40, 0, 0)));
            m.Save(asmPath);
            var next = AsmHead(asmPath);
            t.True(next.Kind == RevisionKind.Save && next.Message == "", $"the next revision: {next.Kind} '{next.Message}'");

            var back = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            t.True(back.Doc.Files.Keys.All(k => k.StartsWith("cache/", StringComparison.Ordinal)), "no embedded part in the saved assembly: " + string.Join(", ", back.Doc.Files.Keys));
            foreach (var x in back.Doc.Components)
            {
                var r = back.Resolution.Of(x);
                t.True(r.Status == PartStatus.Found && r.Path != null && PartIndex.SamePath(r.Path, target), $"reopened, {x.Name} found: {r.Status} {r.Path}");
            }
            t.Eq(Problems(back), "");

            // refused: a linked component, a wrong extension
            t.True(Catch(() => back.MakeExternal(back.Doc.Components[0], Path.Combine(dir, "again.sprt"))) is InvalidOperationException, "a linked part made external");
            soft.Done(t);
        });

        t.Test("Make External then Make Virtual: the part's revision ids, root hashes, tags and blob hashes unchanged, its history the same (spec)", () =>
        {
            var dir = NewDir("round-trip");
            var src = Path.Combine(dir, "src", "Pin.sprt");
            var part = PartWithHistory(src, 3);
            var m0 = AssemblyModel.New();
            m0.Insert(src, Transform.Identity, embed: true);
            var asmPath = Path.Combine(dir, "A.sasm");
            m0.Save(asmPath);
            File.Delete(src);

            var m = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var c = m.Doc.Components[0];
            var start = Snapshot(ToFile(dir, m.Doc.Files[c.Part.Embedded!], ".sprt"), part.Id!);

            var target = Path.Combine(dir, "Pin.sprt");
            m.MakeExternal(c, target);
            t.Eq(Snapshot(target, part.Id!), start);
            m.Save(asmPath);

            // made virtual again, in the same session (as the user would)
            var soft = new Soft();
            var e = Catch(() => m.MakeVirtual(c, "tester"));
            soft.True(e == null, "Make Virtual right after Make External (same session) failed: " + e?.Message);
            if (e != null)
            {
                // (carried on from the assembly as saved, to check the history)
                m = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
                c = m.Doc.Components[0];
                m.MakeVirtual(c, "tester");
            }
            t.True(c.Part.Embedded != null && m.Doc.Files.ContainsKey(c.Part.Embedded), "embedded again");
            t.Eq(Snapshot(ToFile(dir, m.Doc.Files[c.Part.Embedded!], ".sprt"), part.Id!), start);
            m.Save(asmPath);
            var head = AsmHead(asmPath);
            t.True(head.Kind == RevisionKind.Conversion && head.Message == "Made Pin virtual", $"the assembly's revision: {head.Kind} '{head.Message}'");

            // through the assembly's database and out again: the same
            var back = AssemblyDocument.Load(asmPath);
            var bc = back.Components[0];
            t.True(bc.Part.Embedded != null && back.Files.ContainsKey(bc.Part.Embedded), "embedded in the saved assembly");
            t.Eq(Snapshot(ToFile(dir, back.Files[bc.Part.Embedded!], ".sprt"), part.Id!), start);
            // and the file made external is as it was (a mark beside its history, nothing else)
            t.Eq(Snapshot(target, part.Id!), start);
            soft.Done(t);
        });

        t.Test("Make External on an embedded sub-assembly with its own embedded part: the written assembly file has the part, with its history (spec)", () =>
        {
            var dir = NewDir("external-sub");
            var pinPath = Path.Combine(dir, "src", "Pin.sprt");
            var pin = PartWithHistory(pinPath, 2);
            var sm = AssemblyModel.New();
            sm.Insert(pinPath, Transform.Identity, embed: true);
            sm.Insert(pinPath, Transform.Translation(new Vec3(10, 0, 0)), embed: true);
            var subPath = Path.Combine(dir, "src", "PinPair.sasm");
            sm.Save(subPath);
            var subId = sm.Doc.Id!;
            File.Delete(pinPath);

            var top = AssemblyModel.New();
            top.InsertAssembly(subPath, Transform.Identity, embed: true);
            var asmPath = Path.Combine(dir, "Top.sasm");
            top.Save(asmPath);
            File.Delete(subPath);

            var m = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var c = m.Doc.Components.Single();
            t.True(c.IsAssembly && c.Part.Embedded != null, "an embedded sub-assembly");
            var name = c.Part.Embedded!;

            t.True(Catch(() => m.MakeExternal(c, Path.Combine(dir, "PinPair.sprt"))) is ArgumentException, "a sub-assembly made a part file: refused");
            var target = Path.Combine(dir, "PinPair.sasm");
            m.MakeExternal(c, target);
            t.True(File.Exists(target) && AssemblyFile.IsAssembly(target), "an assembly file written");
            var written = AssemblyDocument.Load(target);
            t.True(written.Id == subId, "the sub-assembly's identity");
            var pn = written.Components.Select(x => x.Part.Embedded).Distinct().ToList();
            t.True(pn.Count == 1 && pn[0] != null && written.Files.ContainsKey(pn[0]!), "its part embedded in it: " + string.Join(", ", written.Files.Keys));
            var pf = ToFile(dir, written.Files[pn[0]!], ".sprt");
            t.True(PartFile.History(pf).Count == 2 && CadDocument.Load(pf).Id == pin.Id, "its part with its two revisions");
            using (var db = PartDatabase.Open(pf)) t.True(db.GetTags(Guid.Parse(pin.Id!)).Select(x => x.Name).SequenceEqual(new[] { "v1" }), "its part's tag");
            using (var db = PartDatabase.Open(target)) t.Eq(db.GetHistory(Guid.Parse(subId)).Count, 1);

            t.True(c.Part.Embedded == null && c.Part.AbsolutePath != null && PartIndex.SamePath(c.Part.AbsolutePath, target), "the component linked to the file");
            t.True(!m.Doc.Files.ContainsKey(name), "the embedded copy dropped");
            var soft = new Soft();
            var sub = m.SubOf(c);
            soft.True(sub?.Model != null && sub.Problem == null && sub.Source == SubSource.File,
                $"the sub-assembly shown from its file after Make External: {(sub == null ? "none" : $"{sub.Source}, problem '{sub.Problem}'")}");

            m.Save(asmPath);
            var head = AsmHead(asmPath);
            t.True(head.Kind == RevisionKind.Conversion && head.Message == "Made PinPair external", $"the assembly's revision: {head.Kind} '{head.Message}'");
            var back = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var bs = back.SubOf(back.Doc.Components.Single());
            t.True(bs?.Model != null && bs.Problem == null && bs.Source == SubSource.File, "reopened: the sub-assembly from its file");
            t.Eq(Problems(bs!.Model!), "");
            soft.Done(t);
        });

        t.Test("Make Virtual marks the original (assembly id, user, relative path that resolves); another assembly linking it opens it as before; not marked when read-only or old format (spec)", () =>
        {
            var dir = NewDir("virtual-mark");
            var (m, asmPath, partPath, part) = Linked(dir);
            var other = Path.Combine(dir, "other", "B.sasm");
            LinkingAssembly(other, partPath);
            var history = Revisions(partPath);
            var bytesBefore = Snapshot(partPath, part.Id!);

            var c = m.Doc.Components[0];
            var mv = m.MakeVirtual(c, "kurt");
            t.True(PartIndex.SamePath(mv.OriginalPath, partPath) && mv.Marked, $"marked: {mv.Marked}, original {mv.OriginalPath}");
            t.True(m.Doc.Components.All(x => x.Part.Embedded == c.Part.Embedded && x.Part.Embedded != null), "every component of it embedded");
            t.True(m.Doc.Files.TryGetValue(c.Part.Embedded!, out var eb) && Revisions(dir, eb).SequenceEqual(history), "the embedded copy has its history");
            foreach (var x in m.Doc.Components)
            {
                var d = m.DisplayOf(x);
                t.True(d != null && d.Problem == null && d.Source == PartSource.Embedded, $"{x.Name} shown from the embedded copy: {(d == null ? "no display" : $"{d.Source} '{d.Problem}'")}");
            }
            t.True(File.Exists(partPath), "the original kept");
            var mark = Supersede.Read(partPath);
            t.True(mark != null && mark.AssemblyId == m.Doc.Id && mark.User == "kurt", $"the mark: {mark}");
            t.True(mark!.AssemblyPath == "../A.sasm", "kept relative: " + mark.AssemblyPath);
            t.True(mark.AssemblyFile(partPath) is { } af && PartIndex.SamePath(af, asmPath), "resolves to the assembly: " + mark.AssemblyFile(partPath));
            t.True(Math.Abs((DateTime.UtcNow - mark.Utc).TotalMinutes) < 5, "when: " + mark.Utc);
            t.True(Revisions(partPath).SequenceEqual(history) && Snapshot(partPath, part.Id!) == bytesBefore, "the original's history unchanged by the mark");

            m.Save(asmPath);
            var head = AsmHead(asmPath);
            t.True(head.Kind == RevisionKind.Conversion && head.Message == "Made P virtual", $"the assembly's revision: {head.Kind} '{head.Message}'");

            // another assembly using the original: opens it from its file, no problems
            var b = AssemblyModel.Open(AssemblyDocument.Load(other), other, new PartIndex());
            var bc = b.Doc.Components.Single();
            t.True(b.Resolution.Of(bc).Status == PartStatus.Found && b.DisplayOf(bc)?.Source == PartSource.File, "the other assembly finds the original");
            t.Eq(Problems(b), "");
            t.True(b.DisplayOf(bc)!.Mesh.Volume > 0, "and shows it");

            // read-only original: not marked, made virtual all the same
            var dir2 = NewDir("virtual-readonly");
            var (m2, _, p2, _) = Linked(dir2);
            File.SetAttributes(p2, File.GetAttributes(p2) | FileAttributes.ReadOnly);
            try
            {
                var mv2 = m2.MakeVirtual(m2.Doc.Components[0], "kurt");
                t.True(!mv2.Marked, "a read-only original reported marked");
                t.True(m2.Doc.Components[0].Part.Embedded != null, "made virtual all the same");
                t.True(Supersede.Read(p2) == null, "a read-only original has a mark");
            }
            finally { File.SetAttributes(p2, File.GetAttributes(p2) & ~FileAttributes.ReadOnly); }

            // an old part (its JSON, as saved before containers and databases): not marked, the file left as it was
            var dir3 = NewDir("virtual-old");
            var p3 = Path.Combine(dir3, "Old.sprt");
            var old = Cylinder(12);
            old.Id = CadDocument.NewGuid(); old.Revision = CadDocument.NewGuid();
            File.WriteAllText(p3, old.ToJson());
            var oldBytes = File.ReadAllBytes(p3);
            var m3 = AssemblyModel.New();
            m3.Insert(p3, Transform.Identity);
            var a3 = Path.Combine(dir3, "A.sasm");
            m3.Save(a3);
            t.True(File.ReadAllBytes(p3).SequenceEqual(oldBytes), "the old part left as it was by inserting it");
            var mv3 = m3.MakeVirtual(m3.Doc.Components[0], "kurt");
            t.True(!mv3.Marked, "an old-format original reported marked");
            t.True(File.ReadAllBytes(p3).SequenceEqual(oldBytes), "the old part changed by Make Virtual");
            t.True(m3.DisplayOf(m3.Doc.Components[0]) is { Problem: null, Source: PartSource.Embedded }, "the old part shown embedded");
            m3.Save(a3);
            var back3 = AssemblyModel.Open(AssemblyDocument.Load(a3), a3, new PartIndex());
            t.Eq(Problems(back3), "");
        });

        t.Test("Make Virtual: UsedBy lists another assembly in the folder tree linking the part (delete original disabled), empty when none does (spec)", () =>
        {
            var dir = NewDir("used-by");
            var (m, asmPath, partPath, part) = Linked(dir);
            var other = Path.Combine(dir, "deep", "er", "B.sasm");
            LinkingAssembly(other, partPath);
            // one embedding it (not a user of the file), one of another part
            var embedding = Path.Combine(dir, "E.sasm");
            var me = AssemblyModel.New(); me.Insert(partPath, Transform.Identity, embed: true); me.Save(embedding);
            var otherPart = Path.Combine(dir, "parts", "Q.sprt");
            PartWithHistory(otherPart, 1);
            LinkingAssembly(Path.Combine(dir, "Q.sasm"), otherPart);

            var direct = m.UsedBy(part.Id, partPath);
            t.True(direct.Count == 1 && PartIndex.SamePath(direct[0], other), "UsedBy: " + string.Join(", ", direct));
            var mv = m.MakeVirtual(m.Doc.Components[0], "kurt");
            t.True(mv.UsedBy.Count == 1 && PartIndex.SamePath(mv.UsedBy[0], other), "MadeVirtual.UsedBy: " + string.Join(", ", mv.UsedBy));

            // none other
            var dir2 = NewDir("used-by-none");
            var (m2, _, p2, part2) = Linked(dir2);
            t.True(m2.UsedBy(part2.Id, p2).Count == 0, "UsedBy with no other assembly: " + string.Join(", ", m2.UsedBy(part2.Id, p2)));
            var mv2 = m2.MakeVirtual(m2.Doc.Components[0], "kurt");
            t.True(mv2.UsedBy.Count == 0, "MadeVirtual.UsedBy with no other assembly: " + string.Join(", ", mv2.UsedBy));
            // (this assembly itself, saved still linking it, is not counted)
            t.True(m2.UsedBy(part2.Id, p2).Count == 0, "the assembly itself counted");
        });

        t.Test("Make Virtual: changes made to the original afterwards detected (FileAhead) and pulled in; both changed: Both; unmarked or only the virtual copy changed: not reported (spec)", () =>
        {
            var dir = NewDir("changed-originals");
            var (m, asmPath, partPath, part) = Linked(dir);
            var soft = new Soft();
            var c = m.Doc.Components[0];
            m.MakeVirtual(c, "kurt");
            m.Save(asmPath);
            var name = c.Part.Embedded!;
            t.Eq(m.ChangedOriginals().Count, 0);

            // the original edited (elsewhere): FileAhead
            var p = CadDocument.Load(partPath);
            SetDepth(p, 50);
            p.Save(partPath);
            t.True(Supersede.Read(partPath)?.AssemblyId == m.Doc.Id, "the mark kept by saving the part");
            var ch = m.ChangedOriginals();
            t.True(ch.Count == 1 && ch[0].Kind == Divergence.FileAhead && ch[0].Embedded == name && PartIndex.SamePath(ch[0].File, partPath) && ch[0].PartName == "P",
                "changed: " + string.Join(", ", ch));
            // (also seen by the assembly opened again)
            var reopened = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            t.True(reopened.ChangedOriginals() is [{ Kind: Divergence.FileAhead }], "reopened: " + string.Join(", ", reopened.ChangedOriginals()));

            m.TakeOriginal(ch[0]);
            t.True(Revisions(dir, m.Doc.Files[name]).SequenceEqual(Revisions(partPath)), "the embedded history now the file's");
            var d = m.DisplayOf(c);
            t.True(d != null && d.Problem == null && d.Source == PartSource.Embedded && d.Revision == p.Revision, $"shown as the file has it: {d?.Source} '{d?.Problem}' revision {d?.Revision} (file {p.Revision})");
            t.Near(d!.Mesh.Volume, Math.PI * 25 * 50, 0.02 * Math.PI * 25 * 50);
            m.Save(asmPath);
            var head = AsmHead(asmPath);
            soft.True(head.Kind == RevisionKind.Conversion && head.Message == "Pulled in the changes made to P's file", $"pulled in and saved, the assembly's revision: {head.Kind} '{head.Message}'");
            t.Eq(m.ChangedOriginals().Count, 0);
            var back = AssemblyDocument.Load(asmPath);
            t.True(Revisions(dir, back.Files[name]).SequenceEqual(Revisions(partPath)), "saved: the embedded history the file's");

            // only the virtual copy changed: not reported
            var ef = ToFile(dir, m.Doc.Files[name], ".sprt");
            var e = CadDocument.Load(ef);
            SetDepth(e, 60);
            e.Save(ef);
            m.Doc.Files[name] = File.ReadAllBytes(ef);
            t.True(m.ChangedOriginals().Count == 0, "only the virtual copy changed, reported: " + string.Join(", ", m.ChangedOriginals()));

            // both changed: Both
            p = CadDocument.Load(partPath);
            SetDepth(p, 70);
            p.Save(partPath);
            ch = m.ChangedOriginals();
            t.True(ch.Count == 1 && ch[0].Kind == Divergence.Both && ch[0].Embedded == name, "both changed: " + string.Join(", ", ch));

            // not marked by this assembly: not reported
            using (var db = PartDatabase.Open(partPath, readOnly: false)) Supersede.Clear(db);
            t.True(m.ChangedOriginals().Count == 0, "an unmarked original reported: " + string.Join(", ", m.ChangedOriginals()));
            // marked by another assembly: not reported either
            t.True(Supersede.Mark(partPath, Path.Combine(dir, "Other.sasm"), CadDocument.NewGuid(), "someone"), "marked for another assembly");
            t.True(m.ChangedOriginals().Count == 0, "an original marked by another assembly reported: " + string.Join(", ", m.ChangedOriginals()));
            // marked by this one again: reported
            t.True(Supersede.Mark(partPath, asmPath, m.Doc.Id!, "kurt"), "marked again");
            t.True(m.ChangedOriginals() is [{ Kind: Divergence.Both }], "marked again: " + string.Join(", ", m.ChangedOriginals()));
            // taken from the file when both changed
            var both = m.ChangedOriginals()[0];
            m.TakeOriginal(both);
            t.True(Revisions(dir, m.Doc.Files[name]).SequenceEqual(Revisions(partPath)), "taken: the embedded history the file's");
            m.Save(asmPath);
            head = AsmHead(asmPath);
            soft.True(head.Kind == RevisionKind.Conversion && head.Message == "Took P from its file", $"taken and saved, the assembly's revision: {head.Kind} '{head.Message}'");
            soft.Done(t);
        });

        t.Test("Supersede: Mark on a database part leaves its history as it was; Read gives it back; Clear removes it", () =>
        {
            var dir = NewDir("supersede");
            var path = Path.Combine(dir, "parts", "P.sprt");
            var part = PartWithHistory(path, 2);
            var before = PartFile.History(path);
            var snap = Snapshot(path, part.Id!);
            t.True(Supersede.Read(path) == null, "no mark at first");
            var asmId = CadDocument.NewGuid();
            var asmPath = Path.Combine(dir, "sub", "A.sasm");
            t.True(Supersede.Mark(path, asmPath, asmId, "kurt"), "marked");
            var after = PartFile.History(path);
            t.True(after.Count == before.Count && after[0].RevisionId == before[0].RevisionId && Snapshot(path, part.Id!) == snap, "the history unchanged by the mark");
            var mark = Supersede.Read(path);
            t.True(mark != null && mark.AssemblyId == asmId && mark.User == "kurt" && mark.AssemblyPath == "../sub/A.sasm", $"read back: {mark}");
            t.True(PartIndex.SamePath(mark!.AssemblyFile(path)!, asmPath), "resolves: " + mark.AssemblyFile(path));
            using (var db = PartDatabase.Open(path)) t.True(Supersede.Read(db)?.AssemblyId == asmId, "read from the database");
            // the part still opens as it was
            t.True(CadDocument.Load(path).Revision == part.Revision, "the part opens at its head");
            // without the assembly's place
            t.True(Supersede.Mark(path, null, asmId, "kurt") && Supersede.Read(path) is { AssemblyPath: null } m2 && m2.AssemblyFile(path) == null, "marked without a place");

            using (var db = PartDatabase.Open(path, readOnly: false)) Supersede.Clear(db);
            t.True(Supersede.Read(path) == null, "cleared");
            using (var db = PartDatabase.Open(path)) t.True(db.Manifest("SupersededBy") == null, "the manifest key gone");
            var cleared = PartFile.History(path);
            t.True(cleared.Count == before.Count && cleared[0].RevisionId == before[0].RevisionId && Snapshot(path, part.Id!) == snap, "the history unchanged by clearing");

            // not a database, or not there: none, and Mark says so
            var json = Path.Combine(dir, "old.sprt");
            File.WriteAllText(json, Cylinder(5).ToJson());
            t.True(!Supersede.Mark(json, asmPath, asmId, "kurt") && Supersede.Read(json) == null, "an old part");
            t.True(!Supersede.Mark(Path.Combine(dir, "none.sprt"), asmPath, asmId, "kurt") && Supersede.Read(Path.Combine(dir, "none.sprt")) == null, "a missing file");
            t.True(!File.Exists(Path.Combine(dir, "none.sprt")), "Mark made a file");
        });
    }

    /// <summary>Pinned components: a revision (or a tag) of the part shown, whatever the part becomes.</summary>
    public static void RunPins(TestRunner t)
    {
        static double Depth(PartDisplay? pd) => double.Parse(((ExtrudeFeature)pd!.Part!.Features[1]).Depth, CultureInfo.InvariantCulture);

        t.Test("Pinned component keeps showing its revision after the part gets new ones; the others follow the latest", () =>
        {
            var dir = NewDir("pin");
            var (m, asmPath, partPath, part) = Linked(dir, revisions: 2);
            var first = PartFile.History(partPath)[^1].RevisionId;
            var c1 = m.Doc.Components[0]; var c2 = m.Doc.Components[1];
            m.Pin(c1, first);
            t.True(Depth(m.DisplayOf(c1)) == 10 && Depth(m.DisplayOf(c2)) == 11, $"pinned {Depth(m.DisplayOf(c1))}, latest {Depth(m.DisplayOf(c2))}");
            t.True(m.DisplayOf(c1)!.Revision == first.ToString("D"), "the pinned display is that revision");
            m.Save(asmPath);
            // the part changes again; the assembly opened again
            SetDepth(part, 20); part.Save(partPath);
            var back = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            t.True(Depth(back.DisplayOf(back.Doc.Components[0])) == 10, "still the pinned revision after the part changed");
            t.True(Depth(back.DisplayOf(back.Doc.Components[1])) == 20, "the other follows the latest");
            t.Eq(Problems(back), "");
            // following the latest again
            back.Pin(back.Doc.Components[0], null);
            t.True(Depth(back.DisplayOf(back.Doc.Components[0])) == 20, "unpinned: the latest");
        });

        t.Test("Pinned by a tag: the revision the tag names when opened; a pin changes the assembly's revision", () =>
        {
            var dir = NewDir("pin-tag");
            var (m, asmPath, partPath, part) = Linked(dir, revisions: 3);
            var before = AsmHead(asmPath).RevisionId;
            m.Pin(m.Doc.Components[0], PartFile.History(partPath)[^1].RevisionId, "v1");
            t.True(Depth(m.DisplayOf(m.Doc.Components[0])) == 10, "v1 shown");
            m.Save(asmPath);
            t.True(AsmHead(asmPath).RevisionId != before, "pinning is a change of the assembly");
            // the tag moved to the newest revision
            using (var db = PartDatabase.Open(partPath, readOnly: false)) db.SetTag(Guid.Parse(part.Id!), "v1", db.GetHistory(Guid.Parse(part.Id!))[0].RevisionId);
            var back = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            t.True(Depth(back.DisplayOf(back.Doc.Components[0])) == 12, "follows the tag to the revision it names now");
            // the tag removed: said, not shown wrong
            using (var db = PartDatabase.Open(partPath, readOnly: false)) db.DeleteTag(Guid.Parse(part.Id!), "v1");
            var gone = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var pd = gone.DisplayOf(gone.Doc.Components[0]);
            t.True(pd?.Problem?.Contains("tag") == true, "a missing tag reported: " + pd?.Problem);
        });

        t.Test("A pinned embedded part shows its revision from the embedded history; a purged revision is reported", () =>
        {
            var dir = NewDir("pin-embedded");
            var partPath = Path.Combine(dir, "P.sprt");
            PartWithHistory(partPath, 3);
            var m = AssemblyModel.New();
            m.Insert(partPath, Transform.Identity, embed: true);
            var c = m.Doc.Components[0];
            var oldest = m.HistoryOf(c)[^1].Revision.RevisionId;
            t.Eq(m.HistoryOf(c).Count, 3);
            m.Pin(c, oldest);
            t.True(Depth(m.DisplayOf(c)) == 10, "the embedded part's first revision shown");
            // the pinned revision purged from the part's file (linked): reported
            var dir2 = NewDir("pin-purged");
            var (m2, asm2, part2, _) = Linked(dir2, revisions: 3);
            var first = PartFile.History(part2)[^1].RevisionId;
            m2.Pin(m2.Doc.Components[0], first);
            m2.Save(asm2);
            using (var db = PartDatabase.Open(part2, readOnly: false)) PartHistory.Purge(db, PartFile.History(part2)[0].RevisionId);
            var back = AssemblyModel.Open(AssemblyDocument.Load(asm2), asm2, new PartIndex());
            t.True(back.DisplayOf(back.Doc.Components[0])?.Problem?.Contains("no longer") == true, "purged revision reported: " + back.DisplayOf(back.Doc.Components[0])?.Problem);
        });
    }
}
