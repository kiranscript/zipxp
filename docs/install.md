# Install and uninstall

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
