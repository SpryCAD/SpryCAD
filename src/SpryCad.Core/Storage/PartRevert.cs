using SpryCad.Core.Model;

namespace SpryCad.Core.Storage;

/// <summary>One object a revert changes: what it is called, what reverting does to it, and whether it changed since.</summary>
public sealed record RevertItem(ObjectId Id, ChangeKind Kind, string Name, string Description, bool ChangedSince);

/// <summary>
/// What a revert would do (the file format spec, section 7), for the user to confirm: the part it makes, what it
/// changes, the features that use what it removes but are not reverted (they would fail to rebuild), and the objects
/// changed again since the revision (their later changes would be lost).
/// </summary>
public sealed record RevertPlan(CadDocument Result, IReadOnlyList<RevertItem> Items, IReadOnlyList<string> Dependents, IReadOnlyList<string> ChangedSince, string Message);

/// <summary>
/// Going back (section 7), always as a new state of the part, never by rewriting its history: so a revert can itself
/// be reverted. Worked out on objects: against the part as it is now (unsaved changes included), from the revisions
/// in the file.
/// </summary>
public static class PartRevert
{
    /// <summary>The part as it was in <paramref name="revisionId"/>: a full revert (made the part's state by the caller).</summary>
    public static CadDocument RevertTo(PartDatabase db, Guid revisionId) =>
        PartObjects.ToDocument(db.GetObjects(revisionId).ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value)));

    /// <summary>
    /// Undoes what revision <paramref name="revisionId"/> changed (or, with <paramref name="only"/>, those of its changes),
    /// keeping everything else of <paramref name="current"/>: what it changed goes back as it was before it, what it
    /// added goes, what it removed comes back. With <paramref name="withDependents"/>, the features using what goes are
    /// put back as they were before it too (or removed, when they came after it), and theirs, and so on.
    /// </summary>
    public static RevertPlan PlanSelective(PartDatabase db, CadDocument current, Guid revisionId, IReadOnlySet<ObjectId>? only = null, bool withDependents = false)
    {
        var rev = db.GetRevision(revisionId) ?? throw new ArgumentException("The revision is not in the file.");
        var before = rev.ParentId is { } p ? Load(db, p) : new Dictionary<ObjectId, byte[]>();
        var after = Load(db, revisionId);
        var now = PartObjects.From(current);
        var result = new Dictionary<ObjectId, byte[]>(now);
        var orderId = PartObjects.Single(ObjectKind.FeatureOrder);

        // what the revision changed (the name counters follow features: never reverted)
        var changes = PartHistory.Diff(db, rev.ParentId, revisionId)
            .Where(c => c.ObjectId.Kind != ObjectKind.NameCounters && (only == null || only.Contains(c.ObjectId))).ToList();
        var items = new List<RevertItem>();
        var names = FileNames(before, after, now);
        foreach (var c in changes)
        {
            var id = c.ObjectId;
            if (id.Equals(orderId)) { items.Add(new(id, ChangeKind.Reordered, "Feature order", "Features put back in their earlier order", !Same(now, after, id))); continue; }
            var name = NameOf(id, before, after, names);
            bool since = !Same(now, after, id);
            switch (c.Kind)
            {
                case ChangeKind.Modified:
                    result[id] = before[id];
                    items.Add(new(id, c.Kind, name, PartDescribe.Describe(id.Kind, after[id], before[id], name), since));
                    break;
                case ChangeKind.Added:
                    result.Remove(id);
                    items.Add(new(id, c.Kind, name, $"{name} removed (the revision added it)", since));
                    break;
                case ChangeKind.Removed:
                    result[id] = before[id];
                    items.Add(new(id, c.Kind, name, $"{name} put back (the revision removed it)", since));
                    break;
            }
        }

        // features using what goes: listed, or (with dependents) reverted with it
        var dependents = new List<string>();
        var reverted = changes.Select(c => c.ObjectId).ToHashSet();
        while (true)
        {
            var doc = Assemble(result, now, before, after, changes.Any(c => c.ObjectId.Equals(orderId)), names);
            var gone = now.Keys.Where(k => k.Kind == ObjectKind.Feature && !result.ContainsKey(k)).Select(k => PartObjects.FeatureOf(now[k]).id).ToList();
            var users = gone.Count == 0 ? new List<Feature>() : doc.DependentsOf(gone).Where(f => !reverted.Contains(PartObjects.FeatureId(f.Id))).ToList();
            if (users.Count == 0 || !withDependents)
            {
                dependents.AddRange(users.Select(f => f.Name));
                var changedSince = items.Where(i => i.ChangedSince).Select(i => i.Name).ToList();
                var message = only == null ? $"Reverted the changes of {When(db, rev)}" : $"Reverted {string.Join(", ", items.Select(i => i.Name))} as before {When(db, rev)}";
                return new RevertPlan(doc, items, dependents, changedSince, message);
            }
            foreach (var f in users)
            {
                var id = PartObjects.FeatureId(f.Id);
                reverted.Add(id);
                if (before.TryGetValue(id, out var b))
                {
                    result[id] = b;
                    items.Add(new(id, ChangeKind.Modified, f.Name, $"{f.Name} put back as before the revision (it uses what is removed)", !Same(now, before, id)));
                }
                else
                {
                    result.Remove(id);
                    items.Add(new(id, ChangeKind.Added, f.Name, $"{f.Name} removed (it uses what is removed)", true));
                }
            }
        }
    }

    /// <summary>
    /// The part the objects make, its features in order: the earlier order when the revision reordered them (with the
    /// features made since kept where they are), else the present one with features put back where they were before.
    /// The embedded files listed are those it has.
    /// </summary>
    static CadDocument Assemble(Dictionary<ObjectId, byte[]> result, Dictionary<ObjectId, byte[]> now, Dictionary<ObjectId, byte[]> before,
        Dictionary<ObjectId, byte[]> after, bool reordered, Dictionary<ObjectId, string> fileNames)
    {
        var orderId = PartObjects.Single(ObjectKind.FeatureOrder);
        var have = result.Where(kv => kv.Key.Kind == ObjectKind.Feature).Select(kv => PartObjects.FeatureOf(kv.Value).id).ToHashSet();
        var nowOrder = PartObjects.OrderOf(now[orderId]);
        var beforeOrder = before.TryGetValue(orderId, out var bo) ? PartObjects.OrderOf(bo) : new List<string>();
        List<string> order;
        if (reordered)
        {
            order = beforeOrder.Where(have.Contains).ToList();
            for (int i = 0; i < nowOrder.Count; i++)
                if (have.Contains(nowOrder[i]) && !order.Contains(nowOrder[i])) order.Insert(Math.Min(i, order.Count), nowOrder[i]);
        }
        else
        {
            order = nowOrder.Where(have.Contains).ToList();
            // features put back: after the feature that came before them then
            foreach (var id in beforeOrder.Where(id => have.Contains(id) && !order.Contains(id)))
            {
                int k = beforeOrder.IndexOf(id), at = 0;
                for (int j = k - 1; j >= 0; j--) { int w = order.IndexOf(beforeOrder[j]); if (w >= 0) { at = w + 1; break; } }
                order.Insert(at, id);
            }
        }
        // features neither order knows (should not happen): at the end, so nothing is lost
        order.AddRange(have.Where(id => !order.Contains(id)));
        var objects = new Dictionary<ObjectId, byte[]>(result) { [orderId] = PartObjects.OrderObject(order) };
        var settingsId = PartObjects.Single(ObjectKind.Settings);
        objects[settingsId] = PartObjects.WithFiles(objects[settingsId],
            objects.Keys.Where(k => k.Kind == ObjectKind.File).Select(k => fileNames.TryGetValue(k, out var n) ? n : throw new InvalidDataException("An embedded file's name is not known.")));
        return PartObjects.ToDocument(objects);
    }

    static Dictionary<ObjectId, byte[]> Load(PartDatabase db, Guid revisionId) =>
        db.GetObjects(revisionId).ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value));

    /// <summary>Whether the object is the same in both (absent from both counts as the same).</summary>
    static bool Same(Dictionary<ObjectId, byte[]> a, Dictionary<ObjectId, byte[]> b, ObjectId id) =>
        a.TryGetValue(id, out var x) ? b.TryGetValue(id, out var y) && x.AsSpan().SequenceEqual(y) : !b.ContainsKey(id);

    /// <summary>Embedded files' names by their object ids, from every state involved.</summary>
    static Dictionary<ObjectId, string> FileNames(params Dictionary<ObjectId, byte[]>[] states)
    {
        var d = new Dictionary<ObjectId, string>();
        var settingsId = PartObjects.Single(ObjectKind.Settings);
        foreach (var s in states)
            if (s.TryGetValue(settingsId, out var b))
                foreach (var n in PartObjects.FileNamesOf(b)) d.TryAdd(PartObjects.FileId(n), n);
        return d;
    }

    static string NameOf(ObjectId id, Dictionary<ObjectId, byte[]> before, Dictionary<ObjectId, byte[]> after, Dictionary<ObjectId, string> files) => id.Kind switch
    {
        ObjectKind.Feature => PartObjects.FeatureOf(after.TryGetValue(id, out var a) ? a : before[id]).name,
        ObjectKind.File => files.TryGetValue(id, out var n) ? n : "an embedded file",
        ObjectKind.Variables => "Equations",
        ObjectKind.Material => "Material",
        ObjectKind.Settings => "Part settings",
        ObjectKind.LibrarySnapshot => "Library data " + PartObjects.SnapshotName(after.TryGetValue(id, out var s) ? s : before[id]),
        ObjectKind.SheetMetalBody => "Sheet metal body " + System.Text.Encoding.ASCII.GetString(id.Bytes[1..]).TrimEnd('\0'),
        ObjectKind.SheetMetalOverride => "Sheet metal override",
        ObjectKind.SheetProfile => "Shop profile rows",
        _ => id.Kind.ToString(),
    };

    static string When(PartDatabase db, RevisionInfo rev)
    {
        var tag = db.GetTags(rev.PartId).Where(t => t.RevisionId == rev.RevisionId).Select(t => t.Name).FirstOrDefault();
        return tag ?? rev.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }
}
