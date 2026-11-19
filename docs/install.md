# Install and uninstall

## Install with curl

From Windows PowerShell (Windows 11 ships `curl.exe`):

```powershell
curl.exe -fsSL https://github.com/kiranscript/zipxp/releases/latest/download/zipxp-get.ps1 -o "$env:TEMP\zipxp-get.ps1"; powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\zipxp-get.ps1"
```

The script downloads the latest release ZIP from GitHub, checks it against the release's SHA-256 file, extracts it to a temporary folder, runs the package's own `install.ps1`, and deletes the temporary files. It needs no administrator rights and installs per user, exactly as described below. If ZipXP is running, quit it from the tray first; nothing is force-killed.

Options go after `-File`, and are passed to `install.ps1`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\zipxp-get.ps1" -StartAtLogin -Launch
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\zipxp-get.ps1" -Version 0.3.0
```

`-Version` pins a release, `-Destination` and `-NoShortcut` are passed through, and the `ZIPXP_VERSION` environment variable works like `-Version`. Read the script before running it: [scripts/get.ps1](../scripts/get.ps1).

The checksum is published next to the ZIP, so on its own it catches a damaged or truncated download, not a tampered release. Each release ZIP also has a GitHub build attestation proving it was built by this repository's release workflow from the tagged commit. If the [GitHub CLI](https://cli.github.com/) is installed and signed in (`gh auth login`), the script verifies it automatically and aborts on failure; add `-RequireAttestation` to refuse to install when it can't check. You can verify by hand with `gh attestation verify ZipXP-win-x64.zip --repo kiranscript/zipxp`.

The build is not Authenticode-signed. Files fetched by `curl.exe` aren't tagged as downloaded from the internet, so Windows usually won't show a SmartScreen prompt on this path, but that also means SmartScreen isn't checking it. Defender still scans the files. A ZIP downloaded in a browser will show the unsigned-publisher warning. Releases are created from `v*` tags by the release workflow; until the first release exists, build from source (below).

## Publish a package

```powershell
.\scripts\publish.ps1
# Optional real code-signing certificate already in CurrentUser\My:
.\scripts\publish.ps1 -SigningThumbprint YOUR_CERTIFICATE_THUMBPRINT -TimestampServer YOUR_TIMESTAMP_SERVER
```

Publishing produces a self-contained Windows x64 app and ZIP under `artifacts/`. A normal installed app does not need a separate .NET SDK/runtime. Without a signing certificate the package is explicitly labeled unsigned. The script does not create or substitute a signing identity.

Run `.\scripts\package-check.ps1` after publishing to verify an isolated install, self-contained dashboard launch, graceful exit, and uninstall that preserves data. It does not create a Start menu shortcut or change login registration. Pass `-Package` for a different extracted package directory.

Extract the ZIP and run its `install.ps1`. It installs for the current user under `%LOCALAPPDATA%\Programs\ZipXP Storage Manager`, creates a Start menu shortcut, and preserves policy/history data. Add `-StartAtLogin` and/or `-Launch` to explicitly enable those actions. For updates, exit the old tray worker before installing over it. The installer never force-kills a process. A custom `-Destination` and `-NoShortcut` support isolated installation checks.

Run the installed `uninstall.ps1` after exiting the tray worker. It verifies its installation record, removes the app's files, matching startup registration and shortcut, and leaves managed files and the local policy/history database intact. Reinstalling can reuse those retained policies. Test data is not deleted automatically.
