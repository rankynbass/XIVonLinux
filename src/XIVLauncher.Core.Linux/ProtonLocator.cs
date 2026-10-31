using System;
using System.IO;

namespace XIVLauncher.Core.Linux;

/// <summary>
/// Finds a GE-Proton build in Steam's compatibilitytools.d. The launcher pins the Milestone-1
/// verified GE-Proton 11-6; umu auto-selects the latest UMU-Proton when PROTONPATH is unset,
/// which reports "Prefix has an invalid version" and upgrades a GE prefix — always pass PROTONPATH.
/// </summary>
public sealed class ProtonLocator
{
    /// <summary>Returns the full path of GE-Proton 11-6 (x86_64 variant as fallback), or null.</summary>
    public static string? FindGeProton(int major = 11, int minor = 6)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d");
        if (!Directory.Exists(dir))
            return null;

        var exact = $"GE-Proton{major}-{minor}";
        var variant = $"{exact}-x86_64";

        foreach (var entry in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(entry);
            if (name == exact)
                return entry;
        }

        foreach (var entry in Directory.GetDirectories(dir))
        {
            if (Path.GetFileName(entry) == variant)
                return entry;
        }

        return null;
    }
}
