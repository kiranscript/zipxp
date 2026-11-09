# Test report: C:\Users\kiran\zipxp-test

Date: 2026-11-09. Application: ZipXP Storage Manager, `storage-manager/StorageManager.App` (development build).

## Result

**Automatic compression and decompression work on this folder, and all original contents are intact. One allocation-accounting defect was found and remains open.**

- 35 original files and 3 subdirectories; 34 distinct file identities because two names refer to one hard-linked file.
- All 34 entries in the fixture's original SHA-256 manifest matched at the start. The manifest itself makes the 35th file checked against this run's baseline.
- Read-only analysis: **22 candidates, 0 scan errors, 0 active native directory defaults**.
- Actual GUI cycle: **22/22 compression jobs succeeded; 22/22 decompression jobs succeeded**. The locked file succeeded on its second attempt.
- Allocation fell by **48.16 MiB (70.7%)** during compression on this disposable corpus.
- **35/35 original SHA-256 hashes match after compression and after restoration.** Identity, length, allocated bytes, last-write time, attributes, and link count also match the baseline for all 35 files.
- Original file set, directory-default states, and allocation have been restored. Temporary probe files/directories are removed; test app processes exited normally. The normal app profile was unchanged.

## Measured storage

These values are known primary-stream allocation, deduplicated by file identity. They exclude directory metadata and named streams and do not claim exact whole-volume free-space recovery.

| Stage | Allocated bytes | Allocated size |
|---|---:|---:|
| Before testing | 71,430,496 | 68.12 MiB |
| After original-file compression | 20,930,912 | 19.96 MiB |
| After decompression/restoration | 71,430,496 | 68.12 MiB |

Original unique logical data: **68.10 MiB**. The app's compression-job deltas summed to exactly **50,499,584 bytes**, matching independently queried before/after allocation. The dashboard showed rounded values of **20.0 MiB allocated** and **48.2 MiB measured reduction**, consistent with that compression phase.

| Example | Before allocation (bytes) | Compressed allocation (bytes) | Reduction (bytes) |
|---|---:|---:|---:|
| `repetitive-8MiB.txt` | 8,388,608 | 1,048,576 | 7,340,032 |
| `application-logs-4MiB.log` | 4,194,304 | 524,288 | 3,670,016 |
| `minimum-65536.txt` | 65,536 | 8,192 | 57,344 |
| `above-minimum-65537.txt` | 69,632 | 12,288 | 57,344 |
| `random-8MiB.bin` | 8,388,608 | 8,388,608 | 0 |
| `already-packed-2MiB.zip` | 2,101,248 | 2,101,248 | 0 |

This fixture is intentionally repetitive. The result is not a prediction for media-heavy or other real workloads and is not a repeated performance benchmark.

## Functional coverage

| Test | Result | Evidence |
|---|---|---|
| Read-only recursive analysis | Pass | All 35 files inspected; no scan errors; no initial mutation. |
| Default policy editor and enable/resume | Pass | Changed through native UI Automation in the actual WPF window. |
| Overview, managed folders, insights, activity | Pass | All four navigation controls exercised; dashboard remained responsive. |
| Size boundaries | Pass | 65,535-byte file skipped; 65,536- and 65,537-byte files compressed. |
| Spaces and Unicode name | Pass | File compressed; original path, identity and hash preserved. |
| Hidden/read-only/hard-link exclusions | Pass | Each had a visible reason and retained original metadata/content. |
| Random and prepacked content | Pass | Random file skipped for low estimated benefit; ZIP excluded by policy; allocation unchanged. |
| Locked file and continuation | Pass | Real exclusive lock produced Retry; other jobs succeeded; release allowed success on attempt two. |
| Pause / stop after current | Pass | Completed count stayed unchanged during a two-second paused observation; resume drained the queue. |
| Default quiet period | Pass | New 1 MiB file stayed uncompressed during the initial five seconds. |
| Automatic compression with window closed | Pass | Probe compressed after **61.88 seconds**, with the default 60-second policy and tray worker active. |
| Single-instance activation | Pass | Second launch reopened the same original worker PID. |
| Restart catch-up | Pass | Offline-created file compressed after restarting the app; its last-write time was deliberately aged three minutes to avoid another quiet-period wait. |
| GUI recursive decompression | Pass | Preview displayed; automatic policy disabled; all 22 original compressed files decompressed. |
| Original data preservation and cleanup | Pass | 35/35 hashes and recorded metadata match; file set and directory defaults restored. |
| Round-trip savings accounting | **Fail** | App recorded **−84 KiB** net reduction although actual allocation returned exactly to baseline. |

Initial analysis decisions: 22 candidates; 7 below the size minimum; 2 hard-link names; 1 hidden; 1 read-only; 1 low-benefit random file; 1 ZIP excluded by policy.

## Confirmed defect: accounting reads allocation before handle close

Severity: **medium — metrics/history accuracy; no observed content corruption or persistent allocation growth**. Status: **open**. Application code was left unchanged so this report describes the tested build.

In the successful GUI compression/decompression cycle, compression deltas totaled **+50,499,584 bytes**, but decompression deltas totaled **−50,585,600 bytes**. The persisted daily ledger consequently reports **−86,016 bytes (−84 KiB)** instead of zero.

Three non-aligned files account for the discrepancy:

| File | End allocation implied by recorded delta (bytes) | Actual final allocation (bytes) | Overstatement (bytes) |
|---|---:|---:|---:|
| `above-minimum-65537.txt` | 131,072 | 69,632 | 61,440 |
| `open-in-browser.html` | 524,288 | 503,808 | 20,480 |
| `records-2MiB.json` | 2,555,904 | 2,551,808 | 4,096 |

A separate 65,537-byte temporary-file reproduction confirmed that `FILE_STANDARD_INFO.AllocationSize` read through the still-open mutation handle was **131,072 bytes** immediately after decompression, then **69,632 bytes** after closing it—the original allocation. The hash stayed unchanged. [Reproduction data](evidence/allocation-repro.json).

The current [Mutator implementation](../../StorageManager.Core/Services/Mutator.cs) queries the post-operation metadata while its write handle is still open. That transient value becomes the persisted job delta. The persistent ledger correctly adds the supplied values; those values are premature for these files.

Recommended correction: obtain finalized post-close metadata, revalidate identity and stable content before accepting a measurement, and add round-trip accounting cases for 65,537-byte and other non-aligned lengths. Mark a measurement unavailable if identity/content cannot be safely revalidated. This test report does not include an application-code fix.

## Method and limits

- Executed on Windows build 26300, .NET runtime 10.0.14, local fixed NTFS, without elevation. Actual host battery/resource/space signals were used; none were injected for this run.
- Used the real application worker and native compression service. The actual WPF controls enabled policy, paused/resumed, closed/reopened, restarted, and requested decompression. No independent `compact.exe` command was substituted for app coverage.
- The managed root was initialized through the app's core API in an isolated database outside the test folder; the native folder picker itself was not exercised.
- Separate background/restart probes and a clean GUI round-trip run were performed against the same original corpus. Temporary probes were outside the measured 35-file comparison.
- This host's native message-box choices appeared as UI Automation **panes without InvokePattern**. Automated confirmation used a PID-checked Win32 `WM_COMMAND/IDYES` on the owned preview dialog. Earlier locator-only attempts were stopped and the folder restored before the successful cycle. Raw logs retain those harness limitations; they are not treated as file-compression failures.
- No tests here cover real low-disk conditions, battery transitions, Windows login/startup, other applications' Open dialogs, screen readers, broad compatibility, or sustained CPU/I/O/open/save benchmarks.

## Evidence and reproduction

[Machine-readable summary](report.json), [original baseline](evidence/before.json), [compressed snapshot](evidence/after-compression.json), [restored snapshot](evidence/gui-cycle-after-restoration.json), [successful GUI-cycle jobs/ledger](evidence/gui-cycle-final-ledger-state.json), [GUI-cycle log](evidence/gui-cycle-desktop-test.log), and [background/restart log](evidence/background-run.log).

Reusable test tooling is in `storage-manager/scripts/test-existing-folder.ps1` and `storage-manager/scripts/folder-test/`. Build the helper and app first, then use a **fresh evidence directory** when repeating. The script performs real file mutations, preserves a pre-test baseline, and attempts restoration on exit. Use disposable data, as this folder contains.
