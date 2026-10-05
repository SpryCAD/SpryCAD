namespace FeatherCad.App.Session;

/// <summary>Per-user view preferences (not part of the document), stored as key=value lines in app data.</summary>
public static class UserSettings
{
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherCAD", "settings.txt");

    public static bool ReverseWheel { get; set; }
    public static bool Perspective { get; set; }

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
            });
        }
        catch { /* preferences are a convenience only */ }
    }
}
