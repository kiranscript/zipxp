# Development

## Run from WSL

From the repository root:

```bash
./scripts/dev.sh
```

This launches the native Windows desktop app using Windows `dotnet.exe`. It looks on PATH, in `C:\Program Files\dotnet`, and in the local `C:\Temp\zipxp-dotnet` SDK used for development. Set `WINDOWS_DOTNET_EXE` to another executable's WSL path if needed. A Linux SDK cannot run WPF.

From Windows PowerShell at the repository root:

```powershell
dotnet run --project src/ZipXP.App/ZipXP.App.csproj
```

### Isolated and clean runs

For an isolated instance, pass a Windows path:

```bash
./scripts/dev.sh --data-dir 'C:\Temp\ZipXP-isolated'
```

Starting clean: the app keeps its folders, settings and history in `%LOCALAPPDATA%\ZipXP\StorageManager`, so a rebuild still shows old data. Launcher options (others are passed to the app):

```bash
./scripts/dev.sh --fresh     # empty throwaway data; your real data is untouched
./scripts/dev.sh --rebuild   # delete bin/ and obj/ first, so nothing stale is reused
./scripts/dev.sh --reset     # erase your real saved data (asks for confirmation), then start empty
./scripts/dev.sh --rebuild --fresh
```

Erasing data does not undo compression; files already compressed stay compressed.

Press Ctrl+C in the terminal to stop a dev run: the launcher asks the app to quit cleanly (stopping the worker and removing the tray icon), and force-ends it after about 10 seconds if it doesn't.

## Build and verification

```powershell
# From the repository root on Windows:
.\scripts\check.ps1
# Or select a nonstandard Windows SDK:
.\scripts\check.ps1 -Dotnet C:\Temp\zipxp-dotnet\dotnet.exe
```

The check harness references the actual core library. It creates a unique disposable NTFS run directory in the user's temp folder and retains its files. It uses real compression, hashes, locks, hard links, directory defaults, replacements, and the persistent automatic worker; injected battery/CPU/free-space signals exercise waiting policies without filling a disk. A separate isolated database seeds the actual WPF window for native UI Automation navigation tests. The launch-check instance exits normally on its own and does not register startup.

Validation on the development Windows host is recorded in [validation.md](validation.md). Wider compatibility/performance testing, signing with a release certificate, sign-in lifecycle checks, and a private user pilot remain release acceptance work. This is an unsigned development build until published with a real signing certificate; it is not presented as a completed private beta.

## Source layout

```text
src/ZipXP.Core/          Models, native handles/identities, SQLite store, analysis, rules, worker
src/ZipXP.App/           WPF dashboard (Views/), controls, tray/single-instance/startup (Shell/), plain-language text (Text/)
tests/ZipXP.Checks/      Real-service policy, native, persistence and automation checks
tests/ZipXP.FolderTest/  Helper used by scripts/test-existing-folder.ps1
scripts/                 dev.sh (WSL launcher), check, publish, install, uninstall
docs/                    Usage, how it works, safety, install, development, validation
```

The `.slnx` solution at the repository root opens all four projects.

### Names that must not change

The product is ZipXP. A few internal identifiers still say `StorageManager` on purpose, because renaming them would orphan existing users' data or startup entries: the data folder `%LOCALAPPDATA%\ZipXP\StorageManager`, the `ZipXP.StorageManager` executable/assembly, the sign-in registry value, the single-instance name, and the install folder.

Native references: [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression), [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info), [FILE_BASIC_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info), [SQLite prepared statements](https://www.sqlite.org/c3ref/prepare.html), and [bound parameters](https://www.sqlite.org/c3ref/bind_blob.html).
