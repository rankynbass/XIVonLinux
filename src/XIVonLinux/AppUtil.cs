using System.Reflection;

namespace XIVonLinux;

/// <summary>Application-wide version/util info, copied from XIVLauncher.Core's AppUtil
/// (SteamDeck browser bits dropped). The VersionInfo constructor is generated at build
/// time by the XIVLauncher.VersionGenerator source generator from the csproj Version.</summary>
public static partial class AppUtil
{
    public static byte[] GetEmbeddedResourceBytes(string resourceName)
    {
        var assembly = typeof(AppUtil).Assembly;

        using var s = assembly.GetManifestResourceStream(resourceName);
        if (s == null)
            throw new ArgumentException($"Resource {resourceName} not found", nameof(resourceName));

        var ret = new byte[s.Length];
        s.ReadExactly(ret, 0, (int)s.Length);
        return ret;
    }

    /// <summary>Gets the git hash value from the assembly
    /// or null if it cannot be found.</summary>
    public static string? GetGitHash()
    {
        var asm = typeof(AppUtil).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "GitHash")?.Value;
    }

    /// <summary>Gets the build origin from the assembly
    /// or null if it cannot be found.</summary>
    public static string? GetBuildOrigin()
    {
        var asm = typeof(AppUtil).Assembly;
        var attrs = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "BuildOrigin")?.Value;
    }

    /// <summary>Gets the version from the "assembly". Should never be null.</summary>
    public static string GetAssemblyVersion()
    {
        var fvi = VersionInfo.Instance().Version;
        return fvi.FileVersion.ToString();
    }
}
