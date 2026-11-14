using XIVLauncher.Common.PlatformAbstractions;

namespace XIVonLinux;

/// <summary>
/// App-layer no-op unique-ID cache, passed to Launcher/XivGame for the SE-credential
/// path (Launcher's uid cache is not consulted by SE login nor by LaunchGame).
/// </summary>
public sealed class NullUniqueIdCache : IUniqueIdCache
{
    public bool HasValidCache(string name) => false;

    public void Add(string name, string uid, int region, int maxExpansion) { }

    public bool TryGet(string userName, out IUniqueIdCache.CachedUid cached)
    {
        cached = default;
        return false;
    }

    public void Reset() { }
}
