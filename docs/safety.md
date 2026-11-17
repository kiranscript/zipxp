# Safety, limits and recovery

ZipXP asks Windows to compress your files in place. It does not delete, move or rename them, and files keep their names and paths. This is still a pre-release: use disposable copies until broader acceptance testing is complete.

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

Only one worker runs for a user/data directory. A second launch activates the existing window. The policy database must not be placed inside a managed root. Startup registration is off unless explicitly enabled. Native compression persists independently of app state.

After a crash, previously running jobs become retry records and actual state/identity is rechecked. A file already compressed by an interrupted operation is recorded as unchanged without inventing a savings measurement. Files changed while offline are found on startup. There is no rollback claim.

To back up the policy/history database, exit the tray worker normally and copy the state directory. Keep a backup before replacing a release with an older version; newer schema versions are rejected. Removing the database removes policies/history, not file contents or their native compression state.

## Known issues

After decompression, allocation can be measured before the file handle is closed, which overstates growth for some file sizes and can show a small negative savings total. Files themselves are unaffected. Tracked in [#2](https://github.com/kiranscript/zipxp/issues/2); see [validation](validation.md).
