# XIVonLinux

A Linux-native FFXIV launcher targeting the Linux desktop and Steam Deck.

## Goals

- Launch FFXIV on Linux using Proton / umu-launcher (no manual Wine config).
- Inject Dalamud for plugin support on the Linux/Proton game prefix.
- Runs natively on the Linux desktop (X11/Wayland) and the Steam Deck (SteamOS).

## Key components (planned)

- Launcher UI (TBD: Eto.Forms vs GTK — see XIVLauncher.Core experience on Linux).
- Game acquisition / patching (game files, module files, boot).
- Proton prefix management via umu-launcher.
- Dalamud injection path for Proton prefixes.

## Related projects

- XIVLauncher.Core (upstream, Linux-first, Eto.Forms) — reference implementation.
- Dalamud loader (wine-plugin-loader / dalamud loader for Proton).
- umu-launcher (Valve's "Unix Media" launcher wrapper).
