using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Parts edited in a local working copy (Core/Storage/WorkingCopy): autosave, saving over the file, locks, recovery.</summary>
static class WorkingCopyTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-workingcopy-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    /// <summary>Runs <paramref name="body"/> with working copies kept in a fresh folder (and the lock settings restored after).</summary>
    static void Isolated(string name, Action<string, string> body)
    {
        var (folder, stale) = (WorkingCopy.Folder, WorkingCopy.StaleLock);
        var root = NewDir(name);
        var copies = Path.Combine(root, "working");
        var files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        WorkingCopy.Folder = copies;
        try { body(copies, files); }
        finally { WorkingCopy.Folder = folder; WorkingCopy.StaleLock = stale; }
    }

    static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static long Scalar(string path, string sql, params (string, object)[] ps)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    static long Count(string path, string table) => Scalar(path, $"SELECT COUNT(*) FROM {table}");
    static long WorkingRows(string path) => Scalar(path, "SELECT COUNT(*) FROM Revisions WHERE Kind = 1");

    /// <summary>The part's content as JSON: no identity or revision, name counters sorted.</summary>
    static string Json(CadDocument d)
    {
        var c = CadDocument.FromJson(d.ToJson());
        c.Id = null; c.Revision = null;
        c.NameCounters = c.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        return c.ToJson();
    }

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>A plate with a hole; its depth tells versions apart.</summary>
    static (CadDocument doc, ExtrudeFeature plate) Plate(string depth = "10")
    {
        var doc = new CadDocument();
        doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60, "W") });
        var plate = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = depth });
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(20, 30));
        sk.Add(new SkCircle { Center = c.Id, R = 5 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        return (doc, plate);
    }

    /// <summary>The same part (same identity) with the plate's depth changed.</summary>
    static CadDocument WithDepth(CadDocument doc, string depth)
    {
        var c = CadDocument.FromJson(doc.ToJson());
        c.Id = doc.Id; c.Revision = doc.Revision;
        c.Features.OfType<ExtrudeFeature>().First().Depth = depth;
        return c;
    }

    /// <summary>A saved part file (a database) in <paramref name="dir"/>.</summary>
    static (string path, CadDocument doc) SavedPart(string dir, string name = "part")
    {
        var (doc, _) = Plate();
        var path = Path.Combine(dir, name + CadDocument.FileExtension);
        PartFile.Save(doc, path, "tester");
        return (path, doc);
    }

    static void WriteLock(string source, LockInfo l)
    {
        var p = source + ".lock";
        if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
        File.WriteAllText(p, JsonSerializer.Serialize(l));
    }

    static LockInfo Someone() => new("someone-else", "OTHER-PC", DateTime.UtcNow);

    public static void Run(TestRunner t)
    {
        t.Test("WorkingCopy: Open copies the file into the folder; Open, Load and SaveWorking leave the file untouched", () => Isolated("open", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            var bytes = File.ReadAllBytes(src);
            var time = File.GetLastWriteTimeUtc(src);
            using var wc = WorkingCopy.Open(src);
            t.Eq(wc.Source, Path.GetFullPath(src));
            t.True(PartIndexFolder(wc.Path) == Path.GetFullPath(copies), "copy not in the folder: " + wc.Path);
            t.True(File.Exists(wc.Path) && File.ReadAllBytes(wc.Path).SequenceEqual(bytes), "the copy is the file");
            t.Eq(Json(wc.Load()), Json(doc));
            t.Eq(Json(wc.Load(working: true)), Json(doc));
            wc.SaveWorking(WithDepth(doc, "12"), "tester");
            wc.SaveWorking(WithDepth(doc, "13"), "tester");
            wc.Load(working: true);
            t.True(File.ReadAllBytes(src).SequenceEqual(bytes), "the file changed");
            t.Eq(File.GetLastWriteTimeUtc(src), time);
            t.True(!File.Exists(src + ".lock"), "Open took a lock");
            wc.Close();
        }));

        t.Test("WorkingCopy: SaveWorking keeps one working revision beside the head, its old blobs removed; none when it is the head", () => Isolated("autosave", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            using var wc = WorkingCopy.Open(src);
            var history = PartFile.History(wc.Path);
            var head = PartFile.ReadIdentity(wc.Path);
            var blobs0 = Count(wc.Path, "Blobs");

            var a = WithDepth(doc, "12");
            wc.SaveWorking(a, "tester");
            t.Eq(PartFile.ReadIdentity(wc.Path), head);
            t.True(PartFile.History(wc.Path).Select(r => r.RevisionId).SequenceEqual(history.Select(r => r.RevisionId)), "history changed by autosave");
            t.Eq(Json(wc.Load(working: true)), Json(a));
            t.Eq(Json(wc.Load()), Json(doc));
            t.Eq(wc.Load().Revision, head.revision);
            t.Eq(WorkingRows(wc.Path), 1L);
            t.Eq(Count(wc.Path, "Blobs"), blobs0 + 1);

            var featureA = PartObjects.From(a)[PartObjects.FeatureId(a.Features.OfType<ExtrudeFeature>().First().Id)];
            var hashA = Hash.Of(featureA).ToBytes();
            t.Eq(Scalar(wc.Path, "SELECT COUNT(*) FROM Blobs WHERE Hash = $h", ("$h", hashA)), 1L);

            var b = WithDepth(doc, "13");
            wc.SaveWorking(b, "tester");
            t.Eq(Json(wc.Load(working: true)), Json(b));
            t.Eq(WorkingRows(wc.Path), 1L);
            t.Eq(Count(wc.Path, "Blobs"), blobs0 + 1);
            t.Eq(Scalar(wc.Path, "SELECT COUNT(*) FROM Blobs WHERE Hash = $h", ("$h", hashA)), 0L);
            t.Eq(Count(wc.Path, "Revisions"), (long)history.Count + 1);

            // back to what is saved: no working revision
            wc.SaveWorking(wc.Load(), "tester");
            t.Eq(WorkingRows(wc.Path), 0L);
            t.Eq(Count(wc.Path, "Blobs"), blobs0);
            t.Eq(wc.Load(working: true).Revision, head.revision);
            using (var db = PartDatabase.Open(wc.Path)) t.True(db.GetWorking(Guid.Parse(doc.Id!)) == null, "a working revision equal to the head");
            wc.Close();
        }));

        t.Test("WorkingCopy: Save writes the file with one more revision and drops the working revision", () => Isolated("save", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            using var wc = WorkingCopy.Open(src);
            wc.SaveWorking(WithDepth(doc, "12"), "tester");
            var changed = WithDepth(doc, "14");
            wc.Save(changed, "tester");
            var back = CadDocument.Load(src);
            t.Eq(Json(back), Json(changed));
            t.Eq(back.Revision, changed.Revision);
            t.Eq(back.Id, doc.Id);
            t.Eq(PartFile.History(src).Count, 2);
            t.Eq(PartFile.History(src)[1].RevisionId.ToString("D"), doc.Revision);
            t.Eq(WorkingRows(src), 0L);
            t.Eq(WorkingRows(wc.Path), 0L);
            t.Eq(Json(wc.Load(working: true)), Json(changed));
            t.True(!File.Exists(src + ".saving"), "temporary file left");
            // saving again with no change: no revision, no conflict with our own save
            wc.Save(changed, "tester");
            t.Eq(PartFile.History(src).Count, 2);
            wc.Save(WithDepth(changed, "15"), "tester");
            t.Eq(PartFile.History(src).Count, 3);
            wc.Close();
        }));

        t.Test("WorkingCopy: a file saved by someone else since it was opened is not overwritten (SaveConflictException) unless asked", () => Isolated("conflict", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            using var wc = WorkingCopy.Open(src);
            var theirs = WithDepth(doc, "20");
            PartFile.Save(theirs, src, "someone-else");
            var bytes = File.ReadAllBytes(src);
            var ex = Catch(() => wc.Save(WithDepth(doc, "30"), "tester"));
            t.True(ex is SaveConflictException sc && sc.FilePath == Path.GetFullPath(src), "expected SaveConflictException, got " + (ex?.GetType().Name ?? "none"));
            t.True(File.ReadAllBytes(src).SequenceEqual(bytes), "the file was changed by a refused save");
            t.Eq(Json(CadDocument.Load(src)), Json(theirs));
            // still refused on a second try
            t.True(Catch(() => wc.Save(WithDepth(doc, "31"), "tester")) is SaveConflictException, "second save not refused");
            var mine = WithDepth(doc, "32");
            wc.Save(mine, "tester", overwrite: true);
            t.Eq(Json(CadDocument.Load(src)), Json(mine));
            // then saving again is ours again
            wc.Save(WithDepth(doc, "33"), "tester");
            t.Eq(CadDocument.Load(src).Features.OfType<ExtrudeFeature>().First().Depth, "33");

            // rewritten in place (another program): refused too
            var (src2, doc2) = SavedPart(files, "other");
            using var wc2 = WorkingCopy.Open(src2);
            File.WriteAllBytes(src2, File.ReadAllBytes(src2).Concat(new byte[4096]).ToArray());
            t.True(Catch(() => wc2.Save(WithDepth(doc2, "40"), "tester")) is SaveConflictException, "a rewritten file not detected");
            wc.Close(); wc2.Close();
        }));

        t.Test("WorkingCopy: Save As writes the new file and moves the copy (and its lock) to it", () => Isolated("saveas", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            var bytes = File.ReadAllBytes(src);
            using var wc = WorkingCopy.Open(src);
            wc.TakeLock();
            t.True(File.Exists(src + ".lock"), "no lock");
            var target = Path.Combine(files, "renamed" + CadDocument.FileExtension);
            var changed = WithDepth(doc, "17");
            wc.Save(changed, "tester", target: target);
            t.Eq(wc.Source, Path.GetFullPath(target));
            t.Eq(Json(CadDocument.Load(target)), Json(changed));
            t.True(File.ReadAllBytes(src).SequenceEqual(bytes), "the old file changed");
            t.True(!File.Exists(src + ".lock"), "the old file's lock left");
            wc.TakeLock();
            t.True(File.Exists(target + ".lock"), "TakeLock after Save As: no lock on the new file");
            t.True(!File.Exists(src + ".lock"), "TakeLock after Save As locked the old file");
            // and saving goes to the new file
            wc.Save(WithDepth(doc, "18"), "tester");
            t.Eq(CadDocument.Load(target).Features.OfType<ExtrudeFeature>().First().Depth, "18");
            t.True(File.ReadAllBytes(src).SequenceEqual(bytes), "the old file changed by a later save");
            wc.Close();
            t.True(!File.Exists(target + ".lock"), "Close left the lock");

            // a New() part has no file: Save refuses, Save As makes one
            using var n = WorkingCopy.New();
            var (fresh, _) = Plate("8");
            t.True(Catch(() => n.Save(fresh)) is InvalidOperationException, "Save of a part with no file");
            var p = Path.Combine(files, "new" + CadDocument.FileExtension);
            n.Save(fresh, "tester", target: p);
            t.Eq(n.Source, Path.GetFullPath(p));
            t.Eq(Json(CadDocument.Load(p)), Json(fresh));
            n.Close();
        }));

        t.Test("WorkingCopy: locks: ours ignored, someone else's reported until stale, never removed by us", () => Isolated("locks", (copies, files) =>
        {
            var (src, _) = SavedPart(files);
            var lockPath = src + ".lock";
            var wc = WorkingCopy.Open(src);
            t.True(wc.ForeignLock == null, "a foreign lock on a fresh file");
            wc.TakeLock();
            t.True(File.Exists(lockPath), "TakeLock made no lock file");
            if (OperatingSystem.IsWindows()) t.True(File.GetAttributes(lockPath).HasFlag(FileAttributes.Hidden), "lock file not hidden");
            var mine = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(lockPath))!;
            t.Eq(mine.User, Environment.UserName);
            t.Eq(mine.Machine, Environment.MachineName);
            t.True(WorkingCopy.ReadLock(src) == null, "ReadLock reported our own lock");
            wc.RefreshLock();
            wc.ReleaseLock();
            t.True(!File.Exists(lockPath), "ReleaseLock left our lock");

            // someone else's
            var other = Someone();
            WriteLock(src, other);
            var l = WorkingCopy.ReadLock(src);
            t.True(l != null && l.User == other.User && l.Machine == other.Machine, "someone else's lock not reported");
            using (var wc2 = WorkingCopy.Open(src))
                t.True(wc2.ForeignLock != null && wc2.ForeignLock.User == other.User, "ForeignLock not set");
            // stale
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - WorkingCopy.StaleLock - TimeSpan.FromMinutes(5));
            t.True(WorkingCopy.ReadLock(src) == null, "a stale lock reported");
            WorkingCopy.StaleLock = TimeSpan.FromDays(1);
            t.True(WorkingCopy.ReadLock(src) != null, "not stale under a longer StaleLock");

            // ReleaseLock never removes someone else's lock (written over ours)
            File.Delete(lockPath);
            wc.TakeLock();
            t.True(File.Exists(lockPath), "no lock");
            WriteLock(src, Someone());
            wc.ReleaseLock();
            t.True(File.Exists(lockPath) && JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(lockPath))!.User == "someone-else", "ReleaseLock removed someone else's lock");
            wc.Close();
            t.True(File.Exists(lockPath), "Close removed someone else's lock");
            t.True(WorkingCopy.ReadLock(Path.Combine(files, "missing.sprt")) == null, "a lock on a file with none");
        }));

        t.Test("WorkingCopy: TakeLock over a stale lock file (hidden, left by a crash) takes it", () => Isolated("stalelock", (copies, files) =>
        {
            var (src, _) = SavedPart(files);
            var lockPath = src + ".lock";
            // our own lock left by a crash: a hidden file
            using (var crashed = WorkingCopy.Open(src)) crashed.TakeLock();
            using var a = WorkingCopy.Open(src);
            // the crashed instance released its lock on Dispose; leave one behind as a crash would
            WriteLock(src, new LockInfo(Environment.UserName, Environment.MachineName, DateTime.UtcNow.AddHours(-3)));
            if (OperatingSystem.IsWindows()) File.SetAttributes(lockPath, File.GetAttributes(lockPath) | FileAttributes.Hidden);
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddHours(-3));
            a.TakeLock();
            t.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromMinutes(5), "TakeLock did not rewrite a hidden stale lock file");
            a.ReleaseLock();
            t.True(!File.Exists(lockPath), "ReleaseLock after taking over a stale lock left it");
            a.Close();
        }));

        t.Test("WorkingCopy: TakeLock does not overwrite someone else's live lock", () => Isolated("foreignlock", (copies, files) =>
        {
            var (src, _) = SavedPart(files);
            WriteLock(src, Someone());
            using var wc = WorkingCopy.Open(src);
            t.True(wc.ForeignLock != null, "no ForeignLock");
            wc.TakeLock();
            var l = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(src + ".lock"))!;
            t.Eq(l.User, "someone-else");
            wc.Close();
        }));

        t.Test("WorkingCopy: recovery: copies left with a working revision are found, adopted and discarded; others removed", () => Isolated("recovery", (copies, files) =>
        {
            var (src, doc) = SavedPart(files, "bracket");
            var (src2, doc2) = SavedPart(files, "live");
            var (src3, _) = SavedPart(files, "clean");

            var crashed = WorkingCopy.Open(src);
            var autosaved = WithDepth(doc, "21");
            crashed.SaveWorking(autosaved, "tester");
            crashed.Dispose();
            t.True(File.Exists(crashed.Path), "Dispose removed the copy");

            var live = WorkingCopy.Open(src2);
            live.SaveWorking(WithDepth(doc2, "22"), "tester");

            var clean = WorkingCopy.Open(src3);
            clean.Dispose();

            var unsaved = WorkingCopy.New();
            var (fresh, _) = Plate("23");
            unsaved.SaveWorking(fresh, "tester");
            unsaved.Dispose();

            var found = WorkingCopy.FindRecoverable();
            t.Eq(found.Count, 2);
            var r = found.SingleOrDefault(f => f.WorkingPath == crashed.Path);
            t.True(r != null, "the crashed copy not found");
            t.Eq(r!.Source, Path.GetFullPath(src));
            t.Eq(r.Name, "bracket");
            t.True(DateTime.UtcNow - r.SavedUtc < TimeSpan.FromMinutes(5), "SavedUtc: " + r.SavedUtc);
            t.True(!found.Any(f => f.WorkingPath == live.Path), "a live copy offered for recovery");
            t.True(File.Exists(live.Path), "a live copy removed");
            t.True(!File.Exists(clean.Path), "a copy with nothing to recover kept");
            t.True(!File.Exists(Path.ChangeExtension(clean.Path, ".owner")) && !File.Exists(Path.ChangeExtension(clean.Path, ".json")), "its owner/info files kept");
            var u = found.SingleOrDefault(f => f.WorkingPath == unsaved.Path);
            t.True(u != null && u.Source == null && u.Name == "Unsaved part", "the unsaved new part not found as such");

            var adopted = WorkingCopy.Adopt(r);
            t.Eq(adopted.Source, Path.GetFullPath(src));
            t.Eq(Json(adopted.Load(working: true)), Json(autosaved));
            t.Eq(Json(adopted.Load()), Json(doc));
            // while adopted it is held
            t.True(!WorkingCopy.FindRecoverable().Any(f => f.WorkingPath == r.WorkingPath), "an adopted copy offered again");
            // and saving it works (no conflict: the file is as it was)
            adopted.Save(adopted.Load(working: true), "tester");
            t.Eq(Json(CadDocument.Load(src)), Json(autosaved));
            adopted.Close();
            t.True(!File.Exists(r.WorkingPath), "Close left the adopted copy");

            using (var au = WorkingCopy.Adopt(u!))
            {
                t.True(au.Source == null, "an adopted new part has a file");
                t.Eq(Json(au.Load(working: true)), Json(fresh));
            }
            WorkingCopy.Discard(u!);
            t.True(!File.Exists(u!.WorkingPath), "Discard left the new part's copy");
            live.Close();
            // discard of a copy no one holds
            var crashed2 = WorkingCopy.Open(src);
            crashed2.SaveWorking(WithDepth(CadDocument.Load(src), "24"), "tester");
            crashed2.Dispose();
            var r2 = WorkingCopy.FindRecoverable().Single(f => f.WorkingPath == crashed2.Path);
            WorkingCopy.Discard(r2);
            t.True(!File.Exists(r2.WorkingPath) && !File.Exists(Path.ChangeExtension(r2.WorkingPath, ".owner")) && !File.Exists(Path.ChangeExtension(r2.WorkingPath, ".json")), "Discard left files");
            t.True(!WorkingCopy.FindRecoverable().Any(f => f.WorkingPath == r2.WorkingPath), "a discarded copy found");
        }));

        t.Test("WorkingCopy: Close removes the copy, its owner and info files; a New() part never written leaves nothing", () => Isolated("close", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            var wc = WorkingCopy.Open(src);
            wc.SaveWorking(WithDepth(doc, "25"), "tester");
            var owner = Path.ChangeExtension(wc.Path, ".owner");
            var info = Path.ChangeExtension(wc.Path, ".json");
            t.True(File.Exists(wc.Path) && File.Exists(owner) && File.Exists(info), "copy files missing while open");
            wc.Close();
            t.True(!File.Exists(wc.Path) && !File.Exists(owner) && !File.Exists(info), "Close left files");
            t.Eq(Directory.GetFiles(copies).Length, 0);

            var n = WorkingCopy.New();
            n.Close();
            t.Eq(Directory.GetFiles(copies).Length, 0);

            // a New() part disposed without ever being written: FindRecoverable cleans up after it
            var n2 = WorkingCopy.New();
            n2.Dispose();
            WorkingCopy.FindRecoverable();
            t.True(Directory.GetFiles(copies).Length == 0, "left behind: " + string.Join(", ", Directory.GetFiles(copies).Select(Path.GetFileName)));
        }));

        t.Test("WorkingCopy: an old ZIP or JSON part opens (converted) and saving writes a database", () => Isolated("oldformat", (copies, files) =>
        {
            var models = Path.Combine(Root(), "tests", "SpryCad.Tests", "models");
            int zip = 0, json = 0;
            foreach (var name in new[] { "failed-shell.sprt", "shelled-bracket.sprt" })
            {
                var src = Path.Combine(files, name);
                File.Copy(Path.Combine(models, name), src);
                var head = File.ReadAllBytes(src)[0];
                if (head == (byte)'P') zip++; else if (head == (byte)'{') json++;
                t.True(!PartDatabase.IsFile(src), name + " is a database already");
                var original = CadDocument.Load(src);
                var bytes = File.ReadAllBytes(src);
                using var wc = WorkingCopy.Open(src);
                t.True(PartDatabase.IsFile(wc.Path), name + ": the copy is not a database");
                t.True(File.ReadAllBytes(src).SequenceEqual(bytes), name + ": Open changed the file");
                var doc = wc.Load();
                t.Eq(Json(doc), Json(original));
                wc.Save(doc, "tester");
                t.True(PartDatabase.IsFile(src), name + ": Save did not write a database");
                t.Eq(Json(CadDocument.Load(src)), Json(original));
                t.True(PartFile.History(src).Count >= 1, name + ": no history");
                wc.Close();
            }
            t.True(zip == 1 && json == 1, $"formats tested: {zip} ZIP, {json} JSON");
        }));

        t.Test("WorkingCopy: autosave on another thread while saving: no errors, the file reads back", () => Isolated("threads", (copies, files) =>
        {
            var (src, doc) = SavedPart(files);
            using var wc = WorkingCopy.Open(src);
            Exception? error = null;
            int autosaves = 0;
            var stop = false;
            var json = doc.ToJson();
            var bg = new Thread(() =>
            {
                try
                {
                    for (int i = 0; !Volatile.Read(ref stop); i++)
                    {
                        var d = CadDocument.FromJson(json);
                        d.Id = doc.Id;
                        d.Features.OfType<ExtrudeFeature>().First().Depth = (50 + i % 7).ToString();
                        wc.SaveWorking(d, "autosave");
                        autosaves++;
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            bg.Start();
            CadDocument last = doc;
            for (int i = 0; i < 6; i++)
            {
                last = WithDepth(doc, (11 + i).ToString());
                wc.Save(last, "tester");
                Thread.Sleep(20);
            }
            Volatile.Write(ref stop, true);
            bg.Join();
            t.True(error == null, "autosave thread: " + error);
            t.True(autosaves > 0, "no autosave ran");
            var back = CadDocument.Load(src);
            t.Eq(Json(back), Json(last));
            t.Eq(PartFile.History(src).Count, 7);
            t.Eq(WorkingRows(src), 0L);
            wc.Load(working: true);
            wc.Close();
        }));
    }

    static string PartIndexFolder(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;
}
