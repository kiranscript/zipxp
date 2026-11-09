# Storage Explorer alpha

A small C# / WPF explorer for **Windows 11 and local fixed NTFS drives**. Uses ordinary NTFS compression; files keep their names, paths, and normal application access. Implementation of [the alpha specification](../alpha.md).

## Build and run

From this repository's root in **WSL**, run:

```bash
./gui/run-wsl.sh
```

This builds and opens the native app on the Windows desktop using the Windows SDK. It checks `dotnet.exe` on PATH, the standard Windows install, then the local `C:\Temp\zipxp-dotnet` SDK used for validation. For another location, set `WINDOWS_DOTNET_EXE` to the executable's WSL path. Installing Linux `dotnet` alone cannot run WPF. Choose test folders on a Windows NTFS drive (such as `C:\Users\kiran\...`); the WSL repository's UNC path is not a supported test folder.

Install the .NET 10 SDK on Windows, then run these commands from `gui/` in PowerShell, without administrator elevation:

```powershell
dotnet build StorageExplorer/StorageExplorer.csproj
dotnet run --project StorageExplorer/StorageExplorer.csproj
```

There are no third-party packages. On Linux, `EnableWindowsTargeting` permits a cross-build if the Windows desktop reference pack is available. WPF and native operations still require Windows.

Choose a **disposable copy** of a test folder. Ctrl/Shift-select files and choose **Compress selected** or **Decompress selected**. Double-click folders to enter them; **Up** stops at the chosen root. **Open** and double-clicking a file send its original path to its registered application, without extraction. Missing associations and operation failures appear in each row; select a row to read/copy its diagnostics. **Refresh** rescans actual filesystem state and clears selection.

The folder checkbox sets the current directory's native default. Its state is always read from the filesystem; scanning does not change it. Enabling or disabling the default leaves existing files and existing child directory defaults unchanged. New files and new subfolders inherit it, including while the app is closed. Same-volume moves may keep their previous state. This native default applies to future files broadly and does **not** enforce the manual size/exclusion policy.

## Checks and disposable demo fixtures

```powershell
# Deterministic policy/process-contract checks using injectable metadata, runner and free space.
dotnet run --project StorageExplorer.Checks/StorageExplorer.Checks.csproj

# Creates a unique NTFS fixture, builds WPF, and exercises the actual app service sources.
.\scripts\smoke-test.ps1

# Shows the actual WPF window, checks startup, and requests a normal idle-window close.
.\scripts\launch-check.ps1

# A fresh fixture for manual UI testing; no service operations are performed yet.
.\scripts\smoke-test.ps1 -PrepareOnly
```

Both scripts accept `-Dotnet 'C:\path\to\dotnet.exe'`. The smoke script accepts `-FixtureParent 'D:\test'`, which must have directory compression disabled. It creates a unique `StorageExplorer-alpha-<guid>` folder with `fixture/`, a baseline SHA-256 manifest, and a service-check transcript. It never overwrites an existing fixture or deletes test files. The service harness also hashes all original files before and after its cycle; PowerShell independently verifies the persisted baseline afterward. Retained files can be deleted by deleting that one generated run folder when finished.

Fixtures: 8 MiB repetitive text, 8 MiB random binary, 8 MiB files for locking/continuation, an 8 MiB filename containing spaces and Japanese characters, small text, and a child directory with nested text. The service harness acquires an exclusive lock during the partial-failure test. To hold a lock during manual UI testing, use another PowerShell window:

```powershell
$locked = [System.IO.File]::Open('C:\path\to\fixture\locked.txt', 'Open', 'ReadWrite', 'None')
# Select locked.txt and after-lock.txt in the app, and compress both.
# Release the lock when done:
$locked.Dispose()
```

Low-space checks inject free-space values and never fill a disk. Tests compile the same `Models/`, `Native/`, and `Services/` sources used by WPF. They do not substitute an independent invocation of `compact` for application coverage.

## Verified execution

Tested on 2026-11-09 on the reachable Windows host from WSL, with .NET SDK **10.0.403**, runtime **10.0.14**, and local fixed NTFS. Windows display version: **26H2**, build **26300.9711**; runtime-reported OS version: **10.0.26300** (Windows uses a 10.0 kernel version for Windows 11). The test process was **not elevated**.

- WPF build: **passed**, zero warnings/errors.
- Actual WPF window startup and normal idle-window shutdown: **passed**, exit code zero.
- Service/policy/native checks: **54 passed**. Includes exact size/space thresholds, compression queue stopping on low headroom, metadata-change rejection, signed/zero allocation deltas, stale measurement exclusion, sequential partial failure, cancellation between files, rejected paths, and interop structure layout.
- Native cycle: repetitive text compression → read original path and hash → random/Unicode files → locked-file failure with next-file success → enable inheritance → create compressed file and subfolder → disable inheritance → verify existing children unchanged → decompress text → verify every original hash. **Passed**.
- Measured primary-stream allocation reduction for 8 MiB repetitive text: **7,340,032 bytes (7 MiB)**. For 8 MiB random data: **0 bytes**. These are one fixture run, not performance or savings guarantees.
- Independent PowerShell verification of all baseline SHA-256 hashes: **passed**.

No unresolved failures in the executed service checks. The interactive application-opening demo and the manual UI checks below must still be completed on the Windows desktop; a service test does not establish those results.

## Manual UI acceptance and final demo

Use a fresh `-PrepareOnly` fixture. Record the Windows edition/build, SDK version, app revision, results, and any failures alongside the generated transcript.

- [ ] Launch without elevation. Choose the fixture; enter `child`, go Up, Refresh, and confirm a responsive shallow listing and cleared selection. Check an empty child and an unreadable/deleted directory with Refresh as a retry path.
- [ ] Select repetitive, random, small, and Unicode files; compress. Confirm actual attributes, honest measured allocations, and the small-file reason. Inspect the selected row's full diagnostics.
- [ ] Hold the exclusive lock above; compress the locked and another eligible file. Confirm the failed row and successful continuation.
- [ ] Queue several eligible files and press **Stop after current file**. Confirm the current file finishes and remaining queued files are untouched. Close during a job and confirm the window waits for the current operation before exiting.
- [ ] Open compressed text from this app, from Explorer, and from an application's Open dialog. Close this app and open the same original path again from Explorer.
- [ ] Enable the folder default, close the app, and create new repetitive text with PowerShell/Explorer. Confirm the compression attribute. Reopen the app and confirm the observed checkbox state. Existing child directory defaults must be unchanged.
- [ ] Disable the default, create another file, and confirm it is uncompressed while existing compressed files and child directory defaults remain unchanged. Refresh alone must never change the default.
- [ ] Decompress selected text; confirm the attribute clears and its hash matches the persisted baseline. Recheck all original hashes:

```powershell
$manifest = Get-Content -Raw 'C:\path\to\run-folder\baseline-sha256.json' | ConvertFrom-Json
$manifest | ForEach-Object {
    if ((Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash) {
        throw "Hash mismatch: $($_.Path)"
    }
}
```

Demo order: choose folder → compress text → show measured allocation reduction → open original path → close app and open from Explorer → enable default and create a compressed file while closed → reopen → decompress selected text → verify original SHA-256.

## Boundaries and accounting

- Immediate children only. No recursion, background services/watchers, WOF, ZIP, cloud integration, elevation, installers, signing, rollback, persistent history, or resume.
- Ready fixed NTFS with `FILE_FILE_COMPRESSION` is required. UNC/device paths, drive-root selections, Windows/Program Files locations, and reparse points in the chosen root's ancestor chain are rejected. Operations revalidate these boundaries for each file.
- Reparse-point children are never followed. Hidden/system, encrypted, sparse, offline/recall, read-only, multiple-hard-link and inaccessible files are skipped or fail visibly. Unknown allocation is a dash, never zero. Inaccessible/vanished entries do not abort other entries; enumeration failures are explicit and retryable.
- Manual compression supports 64 KiB–256 MiB; decompression supports files up to 256 MiB, including small compressed files. Already-compressed files produce no newly reclaimed bytes.
- Compression requires at least 256 MiB free headroom and stops the remaining compression queue when that threshold fails. Decompression requires the full file logical size plus 256 MiB. These conservative checks cannot guarantee an operation will succeed; actual OS errors are retained.
- `GetFileInformationByHandleEx(FileStandardInfo)` supplies primary-stream allocation and link count using a disposed metadata handle with read/write/delete sharing. Directory sizes are not recursively calculated. Named streams and directory metadata are excluded. Allocation changes do not promise exact whole-volume free-space recovery.
- Each operation rechecks selected metadata and reads back actual attributes/allocation afterward. Signed allocation reduction is `before - after`; zero is valid and a negative number indicates growth. Only successful files with stable logical size and last-write time contribute measurements. Unmeasured successes are excluded from the measured-file count.
- This alpha does not protect against every rename/replacement race or concurrent writer. Metadata checks are best effort, not atomic identity guarantees. Use disposable copies. After abnormal termination, reopen and refresh filesystem state; no rollback or resume is claimed.
- `compact.exe` is resolved only from `Environment.SystemDirectory`. It receives `/C` or `/U`, `/A`, and one literal absolute path through `ArgumentList`. No shell, wildcards, recursive flags, or localized output parsing. Output/error are drained concurrently, retained as diagnostics, and success requires exit code zero plus the expected actual attribute.
- Normal cancellation/closing never kills an in-flight `compact` process. The window disables navigation/mutation jobs until it exits and stops before another file starts. An indeterminate indicator describes the current operation; the footer supplies real completed/queued counts.

Implementation references: [Microsoft compact documentation](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact), [FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info), and [GetVolumeInformationW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw).
