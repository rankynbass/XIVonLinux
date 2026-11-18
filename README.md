# XIVonLinux

A Linux-native FFXIV launcher targeting the Linux desktop and Steam Deck.

## Goals

- Launch FFXIV on Linux using Proton / umu-launcher (no manual Wine config). **DONE**
- Inject Dalamud for plugin support on the Linux/Proton game prefix. **DONE**
- Runs natively on the Linux desktop (X11/Wayland) and the Steam Deck (SteamOS). **X11/Wayland, not tested on deck**

## Key components (planned)

- Launcher UI: **Avalonia** (XAML, X11/Wayland native, dark mode, Steam Deck touch). Eto.Forms rejected as primary: its Linux backend has layout/DPI/dark-mode problems, and it has a terrible file/folder picker control. 
- Game acquisition / patching (game files, module files, boot). **DONE**
- Proton prefix management via umu-launcher. **DONE**
- Dalamud injection path for Proton prefixes. **DONE - with killswitch and various safe modes**

## Related projects

- XIVLauncher.Core — reference implementation.
- Dalamud loader (wine-plugin-loader / dalamud loader for Proton).
- umu-launcher (Valve's "Unix Media" launcher wrapper).
