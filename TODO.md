# XIVonLinux TODO list

TODO list for developing XIVonLinux launcher

## Code cleanup
* Organize code into folders to make it a bit more organized. May not be necessary. Priority: Low
* Clean up code to be more readable. No one or two letter variable names, except for incrementers in for loops. Use protonPath or proton_path instead of pp, for example. Priority: Medium
* Move Login logic to XIVonLinux instead of XIVLauncher.Common.Linux

## Settings

Changes and additions to Settings window

### Dalamud
* Match features of XIVLauncher.Core. Priority: Medium
* Make sure that dalamud is obeying the killswitch (disabled for patch days, etc). Priority: Very High

### Game
* Add search feature to find the game. Search paths ~/.xlcore/ffxiv, ~/.local/share/Steam/steamapps/common, ~/Games. Allow the user to pick one if multiple are found. Priority: Low
* Add features from XIVLauncher.Core Settings->Game tab. Priority: Medium

### Patching
* Move patching to its own tab. Priority: Low
* Allow user to set limit on download speed. Priority: Low
* Allow user to set maximum number of connections. Priority: Low

### Proton
* Have a toggle for Custom proton or Managed proton 
* If Steam-managed proton is selected, enumerate a dropdown list from ~/.local/share/Steam/compatibilitytools.d, ~/.local/share/Steam/steamapps/common, and ~/.xivonlinux/tools/proton. Priority: High
* Manage umu-launcher versions as well as proton. We should always keep two, the latest version and the previous version, in case there's a bad update pushed. We should download them into ~/.xivonlinux/tools/umu. Priority: High
* Add a toggle for DXVK-Nvapi. Priority: Low

### Troubleshooting
* Add a troubleshooting tab with hacks from XIVLauncher.Core. Priority: Low
* Generate tspacks from here.

## Main Page fixes
Changes to the main page

### Accounts
* OTP code should be a true/false checkbox. If enabled, a popup should prompt for the 6-digit code with Login and Cancel buttons. Priority: High.
* Usernames should be unique. Do not allow multiple users named "test", for example. Priority: High
* Switch accounts toml to a sqlite database instead. Priority: Medium
* Add backends for saving passwords. File/Keychain. Examples in XIVLauncher.Core. Use Simple AED encrypt/decrypt to obfuscate passwords in file. Should also be a sqlite database. Priority: Medium

### UI fixes
* Move the Settings/Show logs buttons to the right. So the news frame should be on the left side, with the login on upper right, and settings/log buttons on bottom right. This should allow the whole news frame to show without scroll bar by default. Priority: Medium
* Fix the download popup. It should always show at least one bar as long as something is downloading. Make 5 bars: The first bar should be the longest, and an overall progress bar. The remaining bars should be for downloading files, min 0 (if nothing downloading), max 4. If there are between 1 and 3 files still downloading, show that many download bars. If 4 or more, show 4. Priority: Low

## Obey XDG spec
* Make the main settings files (.toml files, any db files, etc) in ~/.config/xivonlinux. Priority: Low
* Patches and temporary downloads should be in ~/.cache/xivonlinux. Priority: Low
* Main storage should be ~/.local/share/xivonlinux. Priority: Medium


