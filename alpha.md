# Alpha: a small Windows file explorer with native compression

Status: implementation-ready specification; no code implemented yet.

Timebox: 4–6 focused hours for a developer comfortable with C# and Windows, with the SDK and a Windows test environment already available. Setup, signing, installer work, and production hardening are outside this estimate.

## 1. What must work

The user selects a local NTFS test folder, sees its immediate files and folders, compresses selected existing files, and opens those files in their normal applications without extraction. They can enable automatic compression for newly created files in the current directory. They can decompress selected files and turn off the directory default separately.

Use standard NTFS compression, not ZIP or WOF. Native inheritance is the alpha's automatic behavior. Intelligent background selection is deferred.

Files retain their original names and locations. Windows handles decompression during reads. The app does not delete originals, create archive replacements, or intercept file opens.

## 2. Scope contract

Required:

- Windows 11, local fixed NTFS volume with file compression support.
- One window; one current directory; Choose folder, Up, and Refresh.
- Immediate children only; entering a child folder performs another shallow scan.
- Multi-select files; Compress selected, Decompress selected, and Open.
- "Compress newly created files in this folder" control reflecting the current directory's actual compression attribute.
- File logical size, allocated bytes, compressed state, and operation status.
- Progress by files completed; stop after the current file; per-file errors.
- A README with build/run steps, limitations, and a verified demo.

Excluded:

- Recursive bulk jobs, watchers, a background service, persistent job history, predictions, WOF, ZIP export, deduplication, thumbnails, tabs, shell replacement, cloud integration, automatic elevation, and macOS/Linux support.
- Production guarantees for concurrent rename/replacement races. This alpha is for disposable copies in an ordinary local test directory.

## 3. Stack and suggested files

Use C#, WPF, and .NET 10 (`net10.0-windows`). Use built-in controls and APIs, with no third-party UI packages. Simple code-behind is acceptable; filesystem and process logic must be independently callable.

```text
StorageExplorer/
  StorageExplorer.csproj
  App.xaml
  App.xaml.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  Models/FileEntry.cs
  Services/FolderScanner.cs
  Services/CompressionService.cs
  Services/FileLauncher.cs
  Native/FileMetadata.cs
scripts/
  smoke-test.ps1
README.md
```

On Windows, scaffold with `dotnet new wpf -n StorageExplorer -f net10.0`. Build with `dotnet build StorageExplorer/StorageExplorer.csproj`; run with `dotnet run --project StorageExplorer/StorageExplorer.csproj`.

Linux build success, if cross-targeting is configured, does not satisfy Windows runtime acceptance.

## 4. User interface and behavior

Top row: Choose folder, Up, Refresh, and a read-only current path.

Second row: Compress selected, Decompress selected, Open, and Stop after current file.

Directory setting: "Compress newly created files in this folder." Show explanatory text: "Applies to new files and new subfolders. Existing files and existing subfolders are unchanged. Files moved here may retain their previous setting. This setting remains active when the app is closed."

Grid columns: Name, Kind, Logical size, Allocated size, Compression, Result. Folder sizes display a dash; do not calculate recursive totals.

Footer: processed/queued counts, current file, succeeded/skipped/failed counts, and measured allocation change for successful stable files in the current job. Use indeterminate progress for the current file; do not invent byte-level progress.

Double-click a folder to navigate and a file to open its original path. Clear selection on navigation. Disable navigation and new mutation jobs while a job runs. Keep the UI responsive. An empty folder is a valid state; an unreadable folder is an error with a retry path.

The directory control is a direct, user-initiated operation. Reading or refreshing its state must never trigger a mutation through a checkbox event. Disabling it does not decompress existing files or clear defaults in child directories.

## 5. File inspection and supported boundaries

Use `DriveInfo` to require a ready, fixed NTFS drive, and verify `FILE_FILE_COMPRESSION` through `GetVolumeInformation`. Reject UNC paths and drive roots for mutations. Reject Windows and Program Files locations. Only operate within the chosen folder; Up must stop at that selected root.

Check the selected root and its ancestors for reparse points. Skip reparse-point children rather than following them. Skip hidden/system, encrypted, sparse, offline/recall-on-access files, read-only files, and files with more than one hard link. Treat inaccessible or vanished entries as per-entry failures rather than terminating the entire scan. WOF files are excluded by the reparse-point restriction.

For manual compression, limit files to 64 KiB–256 MiB in this alpha. This bounds individual wait times and testing scope; it is not an OS limit. Decompression can include smaller supported compressed files, up to 256 MiB. Directory inheritance is an OS default and does not enforce these size limits on future files; explain that distinction.

`FileEntry` needs: full path, name, kind, logical bytes, nullable allocated bytes, last-write time, attributes, eligibility reason, and job result. Read compression state from the filesystem, never an app-maintained Boolean.

For allocation and link count, use `GetFileInformationByHandleEx` with `FileStandardInfo` and a correctly marshalled `FILE_STANDARD_INFO`. Open a metadata handle without requesting write access, and permit ordinary read/write/delete sharing. Use `SafeFileHandle` and dispose it. `AllocationSize` supplies allocated bytes; `NumberOfLinks` supports the hard-link exclusion. Handle native errors explicitly and display unknown size as a dash, never zero.

`GetCompressedFileSizeW` is an alternative compression-size query, but on uncompressed files it can return logical size; do not silently treat that as a uniformly accurate allocation baseline. Metrics cover the primary data stream and exclude directory metadata and named streams. Label them accordingly; do not advertise exact whole-volume free-space recovery.

## 6. Compression operations

Resolve `compact.exe` from `Environment.SystemDirectory`, not the current directory or PATH. Use `ProcessStartInfo` with `UseShellExecute = false`, `CreateNoWindow = true`, redirected output/error, and `ArgumentList`. Never pass a shell command string through `cmd.exe` or PowerShell.

For a single file, arguments are `/C`, `/A`, then its full path. To decompress, use `/U`, `/A`, then the full path. For a directory default, use `/C` or `/U`, `/A`, then the current directory's full path. Do not use `/S`, `/EXE`, `/CompactOS`, wildcards, or any recursive invocation.

Drain stdout and stderr concurrently and await process exit. Retain output for diagnostics, but do not parse localized prose as machine-readable state. After completion, re-read attributes and sizes. Success requires an acceptable exit result and the expected attribute; a compression attribute does not guarantee meaningful savings.

Process selected files sequentially:

1. Revalidate eligibility, path boundaries, and existence immediately before running.
2. Capture fresh logical size, allocation, and last-write time. If metadata changed since selection, skip as changed and ask for Refresh through the UI status.
3. For decompression, require available space of at least the file's full logical size plus 256 MiB. This is deliberately conservative, not a guarantee; still handle an OS failure. For compression, stop starting jobs below 256 MiB free space.
4. Execute one operation; record errors without suppressing them.
5. Refresh metadata. If size or last-write changed during the job, mark the savings measurement unavailable because another writer may have changed it.
6. Compute signed allocation delta for valid measurements. Zero is valid; an increase is reported honestly. Previously compressed files skipped by a compress job contribute no newly reclaimed bytes.
7. Stop before the next file if cancellation was requested.

Do not forcibly kill an in-flight `compact` process for normal cancellation. Closing during a job requests cancellation and keeps the window open until the current process exits. After an abnormal app termination, refresh filesystem state on next launch; do not claim rollback or resume support.

No per-file confirmation dialogs are needed. Selecting files and pressing the action explicitly starts the job. Failures such as access denied remain visible and actionable.

## 7. Opening files

Launch the full original path using `ProcessStartInfo` with `UseShellExecute = true`. This shell usage is only for the user-requested Open action, not compression commands. Let Windows use the registered application. Handle missing associations, deleted files, and launch failures with a visible error. Do not decompress to disk before opening.

## 8. Implementation sequence

1. **0:00–0:30:** create the WPF project and window; build and run on Windows.
2. **0:30–1:15:** folder selection/navigation, shallow scan, exclusions, and metadata wrapper.
3. **1:15–2:00:** compression process wrapper, selected-file actions, verified state refresh.
4. **2:00–2:30:** directory default control and normal file opening.
5. **2:30–3:15:** sequential queue, cancellation, errors, space checks, and honest metrics.
6. **3:15–4:30:** fixture script, manual acceptance, bug fixes, and README.
7. **4:30–6:00:** contingency for Windows interop or UI defects. Polish only after core checks pass.

If the timebox slips, cut visual styling and convenience sorting first. Do not cut error handling, correct subprocess arguments, inheritance semantics, or content verification. Stop at a working alpha and document deferred items.

## 9. Acceptance checks and demo

Create fixtures under a new disposable NTFS directory: an 8 MiB repetitive text file, 8 MiB random binary file, small text file, a filename containing spaces and Unicode, a child directory, and a file opened with an exclusive lock. Record SHA-256 hashes before operations. The fixture script must invoke the app's service code or support manual UI testing; it must not merely test an independent `compact` invocation and claim app coverage.

- [ ] Build succeeds and the app launches on Windows 11 without administrator elevation.
- [ ] Browse the fixture directory, enter its child, go Up, and Refresh without freezing.
- [ ] Repetitive text compresses, its compression attribute is set, and allocated bytes decrease.
- [ ] Random data may show little/no improvement; the UI reports the measured result without promising savings.
- [ ] Small files and unsupported entries have clear skip reasons.
- [ ] A path with spaces and Unicode is passed intact and operates on the intended file.
- [ ] A locked file fails visibly while another selected eligible file can complete.
- [ ] Stop after current file prevents remaining queued operations.
- [ ] Open a compressed file in its default application, through Explorer, and through that application's Open dialog.
- [ ] Close the app; the compressed file still opens normally.
- [ ] Enable the folder default, close the app, create a new repetitive file, and verify it inherits compression.
- [ ] Disable the default, create another new file, and verify it is uncompressed. Existing compressed files remain compressed. Existing child directories were not changed by either action.
- [ ] Decompress a selected file; its attribute clears and its SHA-256 matches the original.
- [ ] All untouched fixture contents retain their baseline hashes after the complete cycle.
- [ ] Low-space policy rejects insufficient headroom using an injectable free-space value or disposable constrained test volume; never fill a real system disk.
- [ ] README records tested Windows/SDK versions, checks passed, unresolved failures, and the primary-stream accounting and concurrent-change limitations.

Final demo: choose folder → compress text → show measured allocation reduction → open original path → close app and open from Explorer → create a new compressed file through inheritance → reopen app → decompress selected file → verify original hash.

## 10. Authoritative implementation references

- [compact syntax and directory behavior](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact)
- [Compression attribute and volume capability](https://learn.microsoft.com/en-us/windows/win32/fileio/compression-attribute)
- [Compression state: file versus directory](https://learn.microsoft.com/en-us/windows/win32/fileio/compression-state)
- [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex)
- [FILE_STANDARD_INFO allocation and link count](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info)
- [GetCompressedFileSizeW behavior](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew)
- [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
