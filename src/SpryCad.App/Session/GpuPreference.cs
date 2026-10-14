using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SpryCad.App.Session;

/// <summary>
/// Windows' own graphics preference for this program, as Settings > System > Display > Graphics sets it: kept per user
/// under HKCU\Software\Microsoft\DirectX\UserGpuPreferences by the program's path ("GpuPreference=2;" among the
/// value's other settings). On a laptop with two graphics chips it says which one draws for the program. Read by
/// Windows when the program starts.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GpuPreference
{
    public enum Choice { LetWindowsDecide = 0, PowerSaving = 1, HighPerformance = 2 }

    const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string Name = "GpuPreference=";

    public static Choice Get()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (Environment.ProcessPath is not { } exe || k?.GetValue(exe) is not string v) return Choice.LetWindowsDecide;
            foreach (var part in v.Split(';'))
                if (part.StartsWith(Name, StringComparison.Ordinal) && int.TryParse(part[Name.Length..], out var n) && n is >= 0 and <= 2) return (Choice)n;
        }
        catch { /* unreadable: as if unset */ }
        return Choice.LetWindowsDecide;
    }

    /// <summary>Sets the preference, keeping the value's other settings (Let Windows Decide removes it, as Settings does).</summary>
    public static void Set(Choice c)
    {
        if (Environment.ProcessPath is not { } exe) return;
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        var others = (k.GetValue(exe) as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(Name, StringComparison.Ordinal)).ToList();
        if (c != Choice.LetWindowsDecide) others.Insert(0, Name + (int)c);
        if (others.Count == 0) k.DeleteValue(exe, false);
        else k.SetValue(exe, string.Join("", others.Select(p => p + ";")));
    }
}
