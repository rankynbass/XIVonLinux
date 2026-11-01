using System;
using System.Runtime.InteropServices;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Proton/umu path has no Wine-architecture checks to run (the prefix is managed by
/// umu and the game runs on Proton); kept as the launcher-facing compatibility gate.
/// </summary>
public sealed class LinuxDalamudCompatibilityCheck : IDalamudCompatibilityCheck
{
    public void EnsureCompatibility()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("XIVLauncher.Common.Linux runs on Linux only.");

        // Proton prefixes handle x86/x64 natively; nothing else to gate.
    }
}
