using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// No-op unique-ID cache (Launcher's SE-login uid cache; not touched by LaunchGame).
/// Replaced by a real config-backed cache in Milestone 4 (SE login).
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
