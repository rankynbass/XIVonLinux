using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVonLinux;

/// <summary>Per-username uid cache, XIVLauncher.Core's CommonUniqueIdCache shape
/// (System.Text.Json instead of Newtonsoft): caches the game UniqueId, region and
/// max expansion for a username for one day, so XivGame.Login can skip the network
/// login entirely (useCache:true + TryGet hit fabricates the LoginResult). The file
/// lives in ~/.config/xivonlinux/uidcache.json. Cache Add() fires from
/// XIVLauncher.Common Launcher.Login after a real login; Reset() backs the Game tab
/// "Reset UID Cache" button.</summary>
public class UniqueIdCache : IUniqueIdCache
{
    /// <summary>Cache validity window, upstream parity (CommonUniqueIdCache: 1 day).</summary>
    private const int DaysToTimeout = 1;

    private readonly FileInfo? saveFile;
    private List<CacheEntry> cache = new();

    public UniqueIdCache(FileInfo? saveFile)
    {
        this.saveFile = saveFile;
        Load();
    }

    public void Save()
    {
        if (saveFile is null)
            return;
        File.WriteAllText(saveFile.FullName, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Load()
    {
        if (saveFile is null || !File.Exists(saveFile.FullName))
        {
            cache = new List<CacheEntry>();
            return;
        }

        cache = JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(saveFile.FullName)) ?? new List<CacheEntry>();
    }

    public void Reset()
    {
        cache.Clear();
        Save();
    }

    public bool HasValidCache(string userName)
    {
        return cache.Any(entry => IsValidCache(entry, userName));
    }

    public void Add(string userName, string uid, int region, int maxExpansion)
    {
        cache.Add(new CacheEntry
        {
            CreationDate = DateTime.Now,
            UserName = userName,
            UniqueId = uid,
            Region = region,
            ExpansionLevel = maxExpansion
        });
        Save();
    }

    public bool TryGet(string userName, out IUniqueIdCache.CachedUid cached)
    {
        DeleteOldCaches();

        var entry = cache.FirstOrDefault(e => IsValidCache(e, userName));

        if (entry is null)
        {
            cached = default;
            return false;
        }

        cached = new IUniqueIdCache.CachedUid
        {
            UniqueId = entry.UniqueId,
            Region = entry.Region,
            MaxExpansion = entry.ExpansionLevel
        };
        return true;
    }

    private void DeleteOldCaches()
    {
        cache.RemoveAll(entry => (DateTime.Now - entry.CreationDate).TotalDays > DaysToTimeout);
    }

    private static bool IsValidCache(CacheEntry entry, string userName) =>
        entry.UserName == userName && (DateTime.Now - entry.CreationDate).TotalDays <= DaysToTimeout;

    public class CacheEntry
    {
        public string UserName { get; set; } = "";
        public string UniqueId { get; set; } = "";
        public int Region { get; set; }
        public int ExpansionLevel { get; set; }
        public DateTime CreationDate { get; set; }
    }
}
