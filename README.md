# XIVonLinux

A Linux-native FFXIV launcher targeting the Linux desktop and Steam Deck.

## Goals

- Launch FFXIV on Linux using Proton / umu-launcher (no manual Wine config).
- Inject Dalamud for plugin support on the Linux/Proton game prefix.
- Runs natively on the Linux desktop (X11/Wayland) and the Steam Deck (SteamOS).

## Key components (planned)

- Launcher UI: **Avalonia** (XAML, X11/Wayland native, dark mode, Steam Deck touch). Eto.Forms (XIVLauncher.Core's stack) rejected as primary: its Linux backend has layout/DPI/dark-mode problems (see XIVLauncher.Core experience); it remains the port-source reference for screens.
- Game acquisition / patching (game files, module files, boot).
- Proton prefix management via umu-launcher.
- Dalamud injection path for Proton prefixes.

## Related projects

- XIVLauncher.Core (upstream, Linux-first, Eto.Forms) — reference implementation.
- Dalamud loader (wine-plugin-loader / dalamud loader for Proton).
- umu-launcher (Valve's "Unix Media" launcher wrapper).
