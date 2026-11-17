# Development

## React desktop interface

The default interface uses React + TypeScript + Vite in `src/ZipXP.UI`, hosted locally by WebView2 in the WPF shell. The C# engine, SQLite data, tray icon, single-instance handling and sign-in registration stay in `ZipXP.App` and `ZipXP.Core`. Closing the window keeps the tray worker running.

The React interface uses soft black, white and neutral grays. Status is conveyed with explicit labels, icons and outlines; color is never the only signal. Compact desktop tables move metadata below filenames so failure details and recovery controls remain visible. Browser checks enforce the neutral palette and cover settings/list scroll cues. They also run the label-in-name accessibility rule explicitly and verify that failed-file counts are announced in navigation and attention controls, with correct singular and plural wording.

Install Node.js 22.12+ (or a supported newer LTS), the .NET 10 Windows SDK, and the Microsoft Edge WebView2 Evergreen Runtime. Windows builds and `scripts/publish.ps1` build and copy the React bundle into the app's `UI` folder automatically; packaged users need WebView2 but do not need Node.js or npm. The runtime is not bundled by the publisher yet.

Frontend work:

```bash
cd src/ZipXP.UI
npm ci
npm run dev       # browser layout development; no desktop file access
npm test          # bridge and interaction checks
npm run build    # TypeScript check + production bundle
npx playwright install chromium  # once, for browser checks
npm run test:ui  # interaction tests, accessibility audits, rendered screenshots
```

Browser checks route the compiled bundle into an isolated headless browser and provide fixture data through a mock desktop bridge; they never access managed folders. Screenshots and accessibility reports are saved under `artifacts/ui-review`. Set `ZIPXP_REVIEW_ROUND` to choose a report subfolder, or `ZIPXP_CHROMIUM` to use an existing Chromium executable. The Windows UI Automation checks in `scripts/ui-check.ps1` additionally verify real engine data and navigation in both desktop interfaces.

Browser development shows the connection state; real data and actions require the Windows app. Rebuild/relaunch the desktop app to pick up frontend changes. It serves the bundled files without a local HTTP server and accepts only explicit, typed commands from its own `https://zipxp.internal/index.html` document. External navigation, frames, popups and host objects are blocked. Folder pickers and previews for compression/restore use native dialogs.

Launch with `--native-ui` to use the original WPF screens, including if WebView2 is unavailable. If the React host fails, its error screen also offers **Open native interface**. Saved folder policies and history are shared between both interfaces.

## Run from WSL

From the repository root:

```bash
./scripts/dev.sh
```

Install Node.js in WSL first. The launcher checks PATH and NVM installations for Linux Node.js 22.12+ and uses npm from the same installation. Set `ZIPXP_NODE_EXE` to an explicit Linux Node executable if needed. The launcher builds React with Linux npm and then launches the Windows desktop app using Windows `dotnet.exe`. It looks on PATH, in `C:\Program Files\dotnet`, and in the local `C:\Temp\zipxp-dotnet` SDK used for development. Set `WINDOWS_DOTNET_EXE` to another executable's WSL path if needed. A Linux SDK cannot run WPF.

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

The check script runs frontend tests, builds the bundle and desktop app, then runs the core harness and both desktop navigation checks. From WSL, run `npm test` and `npm run build` in `src/ZipXP.UI` first, then call the Windows check script with `-SkipReactBuild` to avoid mixing Windows and Linux npm dependencies.

The check harness references the actual core library. It creates a unique disposable NTFS run directory in the user's temp folder and retains its files. It uses real compression, hashes, locks, hard links, directory defaults, replacements, and the persistent automatic worker; injected battery/CPU/free-space signals exercise waiting policies without filling a disk. A separate isolated database seeds the React/WebView2 window and the original WPF window for Windows UI Automation navigation tests. The launch-check instance exits normally on its own and does not register startup.

Validation on the development Windows host is recorded in [validation.md](validation.md). Wider compatibility/performance testing, signing with a release certificate, sign-in lifecycle checks, and a private user pilot remain release acceptance work. This is an unsigned development build until published with a real signing certificate; it is not presented as a completed private beta.

## Source layout

```text
src/ZipXP.Core/          Models, native handles/identities, SQLite store, analysis, rules, worker
src/ZipXP.UI/            React screens, styles, typed bridge, interaction tests
src/ZipXP.App/           WebView2 host and desktop bridge (Web/), WPF fallback (Views/), controls, tray/single-instance/startup (Shell/), plain-language text (Text/)
tests/ZipXP.Checks/      Real-service policy, native, persistence and automation checks
tests/ZipXP.FolderTest/  Helper used by scripts/test-existing-folder.ps1
scripts/                 dev.sh (WSL launcher), check, publish, install, uninstall
docs/                    Usage, how it works, safety, install, development, validation
```

The `.slnx` solution at the repository root opens all four projects.

### Names that must not change

The product is ZipXP. A few internal identifiers still say `StorageManager` on purpose, because renaming them would orphan existing users' data or startup entries: the data folder `%LOCALAPPDATA%\ZipXP\StorageManager`, the `ZipXP.StorageManager` executable/assembly, the sign-in registry value, the single-instance name, and the install folder.

Native references: [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression), [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info), [FILE_BASIC_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info), [SQLite prepared statements](https://www.sqlite.org/c3ref/prepare.html), and [bound parameters](https://www.sqlite.org/c3ref/bind_blob.html).
