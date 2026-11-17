# How ZipXP works

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
