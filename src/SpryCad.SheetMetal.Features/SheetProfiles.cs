using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.SheetMetal.Features;

/// <summary>
/// How a part's sheet metal gets its rules: from the part's copy of the shop profile rows (<see cref="CadDocument.SheetProfile"/>)
/// so it rebuilds the same with no library, else from the library set's profile. The copy is kept to the rows the
/// part's materials and gauges can use, and opening a part says when the profile in use has changed for it.
/// </summary>
public static class SheetProfiles
{
    /// <summary>The profile a part's sheets resolve against: its copy, or the library set's when it has none.</summary>
    public static ShopProfile ProfileOf(CadDocument doc) => doc.SheetProfile?.Profile() ?? LibraryLink.Current.Profile;

    /// <summary>The rules of a sheet body; a profile miss is the feature's error.</summary>
    public static SheetRules RulesOf(CadDocument doc, SheetMetalBody body)
    {
        try { return SheetRules.Resolve(ProfileOf(doc), body.Material, body.Gauge, body.Overrides); }
        catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { throw new FeatureException(ex.Message); }
    }

    /// <summary>A bend's rules: the body's, with the bend's own overrides on top.</summary>
    public static SheetRules RulesOf(CadDocument doc, SheetRules body, BendId bend)
    {
        var o = doc.SheetOverrides?.FirstOrDefault(x => x.Target == bend.Value);
        if (o == null || o.Values.Count == 0) return body;
        try { return body.WithOverrides(o.Values); }
        catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { throw new FeatureException($"{bend}: {ex.Message}"); }
    }

    static IEnumerable<(string, string)> Sheets(CadDocument doc) =>
        (doc.SheetBodies ?? new()).Select(b => (b.Material, b.Gauge)).Distinct().OrderBy(x => x.Material, StringComparer.Ordinal).ThenBy(x => x.Gauge, StringComparer.Ordinal);

    static HashSet<(string, string)> Covered(ProfileSnapshot s) => s.Tables.FirstOrDefault(t => t.Key == ProfileTables.Gauges)?.Rows
        .Where(r => r.Keys.Count == 2).Select(r => (r.Keys[0], r.Keys[1])).ToHashSet() ?? new();

    /// <summary>
    /// Keeps the part's copy of the profile in step with its sheet bodies (as part of the edit that changed them): made
    /// from the library set's profile when a body's material or gauge is not in it yet; dropped with the last body.
    /// The rows already copied are not changed here (that is <see cref="Update"/>, on opening). True when it changed.
    /// </summary>
    public static bool Sync(CadDocument doc, LibrarySet? set = null)
    {
        var sheets = Sheets(doc).ToList();
        if (sheets.Count == 0)
        {
            if (doc.SheetProfile == null) return false;
            doc.SheetProfile = null;
            return true;
        }
        if (doc.SheetProfile != null && sheets.All(Covered(doc.SheetProfile).Contains)) return false;
        doc.SheetProfile = ProfileSnapshot.Of((set ?? LibraryLink.Current).Profile, sheets);
        return true;
    }

    /// <summary>
    /// The part's copy of the profile as the library set's profile has it now, and the differences in words (null: the
    /// same, or no sheet metal). Taking it in is one undoable edit (sheet metal parts take profile changes on opening).
    /// </summary>
    public static (ProfileSnapshot Fresh, List<string> Changes)? Update(CadDocument doc, LibrarySet? set = null)
    {
        if (doc.SheetProfile == null) return null;
        var sheets = Sheets(doc).ToList();
        if (sheets.Count == 0) return null;
        var fresh = ProfileSnapshot.Of((set ?? LibraryLink.Current).Profile, sheets);
        var changes = doc.SheetProfile.Differences(fresh);
        return changes.Count == 0 ? null : (fresh, changes);
    }

    /// <summary>What a sheet metal feature's result depends on beyond the features: its body and the profile copy.</summary>
    public static string KeyOf(CadDocument doc, string body)
    {
        var b = doc.SheetBody(body);
        var overrides = doc.SheetOverrides is { Count: > 0 } o ? Json(o) : "";
        var profile = doc.SheetProfile != null ? Json(doc.SheetProfile) : "current:" + LibraryLink.Current.Profile.Name;
        return (b == null ? "" : Json(b)) + "|" + overrides + "|" + profile;
    }

    static string Json<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v, Serialization.Compact);
}
