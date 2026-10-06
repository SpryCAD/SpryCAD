namespace FeatherCad.App.Session;

/// <summary>What the view rotates about when no rotation center has been set by a middle-click.</summary>
public enum RotateCenter
{
    /// <summary>The middle of the model (SolidWorks' default).</summary>
    ModelCenter,
    /// <summary>The most recently selected vertex, edge or face.</summary>
    Selection,
    /// <summary>The point on the model under the cursor when the rotation starts (as Onshape/Fusion).</summary>
    UnderCursor,
}

/// <summary>Per-user view preferences (not part of the document), stored as key=value lines in app data.</summary>
public static class UserSettings
{
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD", "settings.txt");

    public static bool ReverseWheel { get; set; }
    public static bool Perspective { get; set; }
    /// <summary>Save every part as a container (ZIP with a preview image), not only parts with embedded files.</summary>
    public static bool SaveContainer { get; set; }
    public static RotateCenter RotateAbout { get; set; } = RotateCenter.ModelCenter;

    public static void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            foreach (var line in File.ReadAllLines(StorePath))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
                if (k == nameof(ReverseWheel)) ReverseWheel = v == "1";
                else if (k == nameof(Perspective)) Perspective = v == "1";
                else if (k == nameof(SaveContainer)) SaveContainer = v == "1";
                else if (k == nameof(RotateAbout) && Enum.TryParse<RotateCenter>(v, out var rc)) RotateAbout = rc;
            }
        }
        catch { /* defaults */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllLines(StorePath, new[]
            {
                $"{nameof(ReverseWheel)}={(ReverseWheel ? 1 : 0)}",
                $"{nameof(Perspective)}={(Perspective ? 1 : 0)}",
                $"{nameof(SaveContainer)}={(SaveContainer ? 1 : 0)}",
                $"{nameof(RotateAbout)}={RotateAbout}",
            });
        }
        catch { /* preferences are a convenience only */ }
    }
}
