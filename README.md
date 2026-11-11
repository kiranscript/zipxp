# ZipXP Storage Manager

The storage-management implementation of [plan.md](../plan.md), the earlier file-explorer alpha has been removed from this repository. Windows 11, C# / WPF, .NET 10, ordinary NTFS compression. Files keep their original names and paths.

## Run from WSL

From the repository root:

```bash
./storage-manager/run-wsl.sh
```

This launches the native Windows desktop app using Windows `dotnet.exe`. It looks on PATH, in `C:\Program Files\dotnet`, and in the local `C:\Temp\zipxp-dotnet` SDK used for development. Set `WINDOWS_DOTNET_EXE` to another executable's WSL path if needed. A Linux SDK cannot run WPF.

From Windows PowerShell in `storage-manager/`:

```powershell
dotnet run --project StorageManager.App/StorageManager.App.csproj
```

## Use it

1. Select **Managed folders → Add folder…**. Choose a local fixed NTFS folder, such as `C:\Users\kiran\zipxp-test`. Adding a folder starts read-only analysis. A WSL/UNC folder is not supported.
2. Inspect the overview and storage insights. Folder rows show their policy, known logical/allocation totals, candidate bytes, status, scan time, and native directory-default count. Double-click a folder in Insights to drill down, or choose **Largest files**.
3. Select **Folder settings** and turn on automatic compression. Subfolder inclusion and battery behavior are on the first screen; file exclusions, size/benefit thresholds, quiet period, cooldown, schedule, and free-space reserve are under **Advanced options**. Previously: exclusions, quiet period, size/benefit thresholds, cooldown, schedule, battery behavior, and free-space reserve.
4. Eligible existing files are queued. New, copied-in, replaced, and changed files are discovered through notifications and periodic scans. The recommended quiet period is 60 seconds; reconciliation runs every two minutes. Busy files retry with backoff. Poor-benefit files stay excluded until their content fingerprint changes or a failed job is explicitly retried.
5. Close the window to keep the tray worker running. Reopen it from the tray. **Start when I sign in** explicitly registers this user's current launch command. **Pause all** / **Pause folder** stops new operations after the current one. **Quit ZipXP** (finishes the file in progress, then) ends the worker and leaves pending work persisted.
6. Use Activity to search/filter outcomes, copy diagnostics, and retry selected failures. Retry does not re-enable disabled automatic policies.

Adding a folder for analysis does not compress it. **Compress now** is an explicit one-off job; it does not turn on future automation. Each recursive action shows a scope preview first. Ordinary automatic files have no per-file confirmation dialog.

### Decompression and stopping management

- **Restore original size…** (Overview for all folders, Managed folders toolbar per folder, or Storage insights for selected files) uncompresses files so they take their full space again; no files are deleted. It first disables automatic compression for that managed root and cancels pending compression. Re-enabling automation while decompression is pending is rejected.
- Decompression does not clear existing NTFS directory defaults. The preview reports those defaults, incomplete coverage, omitted entries, and the largest sequential operation's required headroom.
- **Turn off Windows' own folder compression** (More menu; formerly "Clear native defaults") is a separate recursive directory-only job within the current policy scope. Existing files stay compressed. Excluded/unreadable/unsupported directories may retain defaults; inspect the next scan.
- **Stop managing this folder** (More menu) removes the root, observations, and future jobs. Existing files and native defaults remain unchanged. Historical job measurements are retained.
- Resuming/pausing the selective worker does not change any native directory inheritance settings. Such defaults can compress future files even while the app is closed and can bypass the selective worker's exclusions and size limits.

## What is implemented

- Recursive managed roots with overlap rejection, policy exclusions, no reparse-point traversal, partial scans, and independent read-only analysis.
- A persistent single sequential queue, stable volume/file identities, fresh metadata validation, native handle-based compression, retry/backoff, cancellation between files, and crash reconciliation.
- Automatic existing/new/copied/replaced-file processing, watcher error reconciliation, quiet periods, cooldowns, and poor-result caching.
- Folder schedules (local hours, including overnight), battery control, free-space reserves, and pausing new file operations above 85% sampled total system CPU usage. These are gates between operations, not hard resource caps.
- One below-normal-priority per-user process with a tray, a single-instance activation event, optional sign-in registration, and graceful exit. No service or elevation is needed.
- Overview cards, live volume usage, managed-folder policies/status, largest-file/folder drill-down, scan allocation history, searchable activity, retry, and copyable diagnostics.
- SQLite schema migrations, local observations/jobs/settings, a daily signed allocation ledger that survives detailed-history pruning, and bounded retention.
- A WSL launcher, Windows service/UI checks, self-contained publishing, optional certificate signing, and per-user install/uninstall scripts.

## Storage accounting

All file measurements cover the primary data stream. Directory metadata and named streams are excluded. Unknown sizes display a dash. Scans deduplicate observed hard links within a root; the overview deduplicates known file identities across managed roots. Excluded subtrees are outside the policy scope.

The UI keeps four different quantities separate:

1. **Volume usage/free space:** current drive statistics, including activity from all applications.
2. **Managed allocation:** known primary-stream allocations from each root's latest scan, with scan timestamps and unknown-entry coverage. This is not an atomic volume snapshot.
3. **Estimated opportunity:** three 32 KiB Deflate samples per eligible file, scaled as a heuristic. NTFS uses a different compression algorithm; the estimate is not a promise. Sampling does not mutate files.
4. **Net measured reduction:** signed `before allocation - after allocation` for successful operations with stable identity, length, and last-write time. Zero is valid; decompression growth is negative. Already-compressed/interrupted/unmeasured jobs claim no new bytes. The ledger records each completed job once and survives restart. Period filtering uses UTC days. It is a historical operation total, not a claim of current free-space recovery or savings remaining after deletion.

The history chart plots scan allocations for the selected root and period, at actual scan times. Amber bars indicate incomplete/unknown coverage; timestamps without a scan have no bar. It includes up to 32 recent scans in the period. Detailed jobs show at most 1,000 matching recent rows; up to 10,000 terminal records and 2,000 scan summaries are retained. Ledger totals are separate from that retention.

## Filesystem and resource boundaries

- Local fixed NTFS with file-compression support; no drive-root management, Windows/Program Files locations, UNC/device paths, WSL filesystem roots, or reparse-point ancestors/children.
- Hidden/system, encrypted, sparse, offline/recall, read-only, multiple-hard-link files, and unsupported entries are excluded from mutation. Inspection/access errors are visible rather than silently counted as zero.
- Initial tested manual/automatic compression sizes are 64 KiB–256 MiB. Decompression can include smaller compressed files, up to the policy's tested maximum. Policies cannot enlarge that maximum yet.
- Reserve is at least 256 MiB for compression, and full logical size plus reserve for decompression. The queue waits for space rather than filling a disk in a test. The OS may still reject a previously checked operation.
- Mutation opens one original file handle with no write/delete sharing, checks the resolved path and stable identity, validates content metadata, invokes `FSCTL_SET_COMPRESSION`, and reads back actual state/allocation from that same handle. A replacement or changed file is skipped for a rescan. Existing writers may cause a retryable sharing violation.
- Normal cancellation does not kill in-flight native I/O. Only one file mutation runs at a time, with a 500 ms worker interval. Scanning is sequential, bounded to 100,000 observations per root; automatic event-triggered rescans are spaced by at least 15 seconds. The queue holds at most 2,000 active jobs and refills through reconciliation.
- Very large roots should be split into smaller nonoverlapping managed scopes. Scans reaching the observation bound explicitly report incomplete coverage; this build does not provide resumable pagination past that bound.
- Native operations, file identity checks, and sharing restrictions improve race handling but are not a production data-integrity guarantee against every concurrent filesystem change. Use disposable copies until broader acceptance is complete.
- No recursive deletion/cleanup, deduplication mutation, cloud/network support, WOF, archive replacement, automatic elevation, telemetry upload, or file-manager replacement features.

## State and recovery

Normal state: `%LOCALAPPDATA%\ZipXP\StorageManager\storage.sqlite3` plus SQLite WAL/SHM files. The main UI and tray share one process; SQLite is supplied by Windows' `winsqlite3.dll`. There are no external NuGet or UI packages.

For an isolated instance, pass a Windows path:

```bash
./storage-manager/run-wsl.sh --data-dir 'C:\Temp\ZipXP-isolated'
```

Press Ctrl+C in the terminal to stop a dev run: the launcher asks the app to quit cleanly (stopping the worker and removing the tray icon), and force-ends it after about 10 seconds if it doesn't.

Only one worker runs for a user/data directory. A second launch activates the existing window. The policy database must not be placed inside a managed root. Startup registration is off unless explicitly enabled. Native compression persists independently of app state.

After a crash, previously running jobs become retry records and actual state/identity is rechecked. A file already compressed by an interrupted operation is recorded as unchanged without inventing a savings measurement. Files changed while offline are found on startup. There is no rollback claim.

To back up the policy/history database, exit the tray worker normally and copy the state directory. Keep a backup before replacing a release with an older version; newer schema versions are rejected. Removing the database removes policies/history, not file contents or their native compression state.

## Build and verification

```powershell
# From storage-manager/ on Windows:
.\scripts\check.ps1
# Or select a nonstandard Windows SDK:
.\scripts\check.ps1 -Dotnet C:\Temp\zipxp-dotnet\dotnet.exe
```

The check harness references the actual core library. It creates a unique disposable NTFS run directory in the user's temp folder and retains its files. It uses real compression, hashes, locks, hard links, directory defaults, replacements, and the persistent automatic worker; injected battery/CPU/free-space signals exercise waiting policies without filling a disk. A separate isolated database seeds the actual WPF window for native UI Automation navigation tests. The launch-check instance exits normally on its own and does not register startup.

Validation on the development Windows host is recorded in [VALIDATION.md](VALIDATION.md). Wider compatibility/performance testing, signing with a release certificate, sign-in lifecycle checks, and a private user pilot remain release acceptance work. This is an unsigned development build until published with a real signing certificate; it is not presented as a completed private beta.

## Publish and install

```powershell
.\scripts\publish.ps1
# Optional real code-signing certificate already in CurrentUser\My:
.\scripts\publish.ps1 -SigningThumbprint YOUR_CERTIFICATE_THUMBPRINT -TimestampServer YOUR_TIMESTAMP_SERVER
```

Publishing produces a self-contained Windows x64 app and ZIP under `artifacts/`. A normal installed app does not need a separate .NET SDK/runtime. Without a signing certificate the package is explicitly labeled unsigned. The script does not create or substitute a signing identity.

Run `.\scripts\package-check.ps1` after publishing to verify an isolated install, self-contained dashboard launch, graceful exit, and uninstall that preserves data. It does not create a Start menu shortcut or change login registration. Pass `-Package` for a different extracted package directory.

Extract the ZIP and run its `install.ps1`. It installs for the current user under `%LOCALAPPDATA%\Programs\ZipXP Storage Manager`, creates a Start menu shortcut, and preserves policy/history data. Add `-StartAtLogin` and/or `-Launch` to explicitly enable those actions. For updates, exit the old tray worker before installing over it. The installer never force-kills a process. A custom `-Destination` and `-NoShortcut` support isolated installation checks.

Run the installed `uninstall.ps1` after exiting the tray worker. It verifies its installation record, removes the app's files, matching startup registration and shortcut, and leaves managed files and the local policy/history database intact. Reinstalling can reuse those retained policies. Test data is not deleted automatically.

## Source layout

```text
StorageManager.Core/    Models, native handles/identities, SQLite store, analysis, rules, worker
StorageManager.App/     WPF dashboard, policy editor, tray/single-instance/startup integration
StorageManager.Checks/  Actual-service policy, native, persistence, and automation checks
scripts/               Verification, self-contained publishing, install/uninstall
run-wsl.sh             Windows SDK launcher from WSL
```

Native references: [FSCTL_SET_COMPRESSION](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_compression), [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info), [FILE_BASIC_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info), [SQLite prepared statements](https://www.sqlite.org/c3ref/prepare.html), and [bound parameters](https://www.sqlite.org/c3ref/bind_blob.html).
