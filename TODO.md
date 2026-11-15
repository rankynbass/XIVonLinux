# XIVonLinux TODO list

TODO list for developing XIVonLinux launcher

## Code cleanup
* Organize code into folders to make it a bit more organized. May not be necessary. Priority: Low
* [DONE 2026-11-14 22:00 PT via b8452f7] Clean up code to be more readable. No one or two letter variable names, except for incrementers in for loops. Use protonPath or proton_path instead of pp, for example. Priority: Medium
* [DONE 2026-11-14 15:30 PT] Move Login logic to XIVonLinux instead of XIVLauncher.Common.Linux

## Settings

Changes and additions to Settings window

### Dalamud
* [DONE 2026-11-14 16:15 PT] Match features of XIVLauncher.Core. Priority: Medium
* [DONE 2026-11-14 14:00 PT] Make sure that dalamud is obeying the killswitch (disabled for patch days, etc). Priority: Very High

### Game
* Add search feature to find the game. Search paths ~/.xlcore/ffxiv, ~/.local/share/Steam/steamapps/common, ~/Games. Allow the user to pick one if multiple are found. Priority: Low
* [DONE 2026-11-14 18:03 PT via 8a78b6b] Add features from XIVLauncher.Core Settings->Game tab. Priority: Medium
* Add UID cache function. Priority: Low
* Add Auto-login macro. Priority: Low
* [DONE 2026-11-15 12:20 PT via b1fda3c] Fix the Args field so that it actually passes key=value pairs as environment variables. Priority: Medium

### Patching
* [DONE 2026-11-15 08:30 PT via e6edabe] Move patching to its own tab. Priority: Low
* [DONE 2026-11-15 08:30 PT via e6edabe] Allow user to set limit on download speed. Priority: Low
* [DONE 2026-11-15 08:30 PT via e6edabe] Allow user to set maximum number of connections. Priority: Low

### Proton
* [DONE 2026-11-14 15:00 PT] Have a toggle for Custom proton or Managed proton. Priority: High
* [DONE 2026-11-14 15:00 PT] If Steam-managed proton is selected, enumerate a dropdown list from ~/.local/share/Steam/compatibilitytools.d, ~/.local/share/Steam/steamapps/common, and ~/.xivonlinux/tools/proton. Priority: High
* [DONE 2026-11-14 15:00 PT] Manage umu-launcher versions as well as proton. We should always keep two, the latest version and the previous version, in case there's a bad update pushed. We should download them into ~/.xivonlinux/tools/umu. Priority: High
* [Pointless. Ignored] Add a toggle for DXVK-Nvapi. Priority: Low

### Troubleshooting
* [DONE 2026-11-15 13:56 PT via bb9e57e, 2a79eff] Add a troubleshooting tab with hacks from XIVLauncher.Core. Priority: Low
* Generate tspacks from here. Priority: Low

## Main Page fixes
Changes to the main page

### Accounts
* [DONE 2026-11-14 14:13 PT] OTP code should be a true/false checkbox. If enabled, a popup should prompt for the 6-digit code with Login and Cancel buttons. Priority: High.
* [DONE 2026-11-14 14:21 PT] Usernames should be unique. Do not allow multiple users named "test", for example. Priority: High
* [DONE 2026-11-14 14:21 PT] Switch accounts toml to a sqlite database instead. Priority: Medium
* [DONE 2026-11-14 17:44 PT] Add backends for saving passwords. File/Keychain. Examples in XIVLauncher.Core. Use Simple AED encrypt/decrypt to obfuscate passwords in file. Should also be a sqlite database. Priority: Medium

### UI fixes
* [DONE 2026-11-14 20:55 PT via 554a9cc] Move the Settings/Show logs buttons to the right. So the news frame should be on the left side, with the login on upper right, and settings/log buttons on bottom right. This should allow the whole news frame to show without scroll bar by default. Priority: Medium
* [DONE 2026-11-14 21:50 PT via d6852f5] Fix the download popup. It should always show at least one bar as long as something is downloading. Make 5 bars: The first bar should be the longest, and an overall progress bar. The remaining bars should be for downloading files, min 0 (if nothing downloading), max 4. If there are between 1 and 3 files still downloading, show that many download bars. If 4 or more, show 4. Priority: Medium

## Obey XDG spec
* [DONE 2026-11-14 22:15 PT via caa0eed] Make the main settings files (.toml files, any db files, etc) in ~/.config/xivonlinux. Priority: Low
* [DONE 2026-11-14 22:15 PT via caa0eed] Patches and temporary downloads should be in ~/.cache/xivonlinux. Priority: Low
* [DONE 2026-11-14 22:15 PT via caa0eed] Main storage should be ~/.local/share/xivonlinux. Priority: Medium

### Storage structure
* [DONE 2026-11-14 18:14 PT via 2df9e7d, 28923c0] All logs should go in the STORAGE/logs folder. This includes dalamud logs, launcher logs, wine/proton logs, etc. Priority: Medium, before tspacks.

### Add Troubleshoot button
* [DONE 2026-11-14 21:04 PT via 49bd097] Have a dropdown for disabling dalamud, third party plugins, etc. Refer to XIVLauncher.Core for details. Priority: Medium.
* [DONE 2026-11-15 08:22 PT via c58afcd] Add Repair to the troubleshoot button. Refer to XIVLauncher.Core for details. Priority: Medium.


