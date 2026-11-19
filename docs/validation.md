# Validation record

Development host: Windows display version 26H2, build 26300.9711; .NET SDK 10.0.403 / runtime 10.0.14; local fixed NTFS. Tests invoked from WSL using the Windows SDK. The test process runs without administrator elevation. Date: 2026-11-09.

## Executed checks

- Windows core and WPF compilation: passed with zero warnings/errors.
- Final native/core suite: **68 checks passed**, including real allocation reduction, SHA-256 preservation, sharing-failure continuation/retry, quiet periods, schedule/battery/CPU/space gates, exactly-once measurements, stop-after-current, the 2,000-job bound, actual junction exclusion, automatic creation/copy/replacement, rename identity reconciliation, decompression, and offline/restart recovery.
- Actual WPF window launch, all four navigation pages through native UI Automation, read-only seeded analysis, and graceful tray/worker shutdown: passed.
- The exact WSL launcher command, using an isolated Windows state directory, built successfully with zero warnings/errors, launched the app, and exited normally.
- Windows x64 self-contained Release publishing and unsigned ZIP creation: passed.
- Isolated per-user installation, installed self-contained dashboard launch, graceful exit, and uninstall preserving the policy/history database: passed. No test created a shortcut or startup registration.
- Static shell syntax, source whitespace, and local documentation links: passed.

Fixture and package-lifecycle folders from these runs were retained locally as test evidence; they are not required application paths.

Runtime testing found and fixed a launch-check assumption about console-host `WaitForInputIdle`, missing-startup-entry handling in PowerShell uninstall, and an operation-cache edge case that could block explicit decompression. The final executed checks have no unresolved failures.

## React desktop UI verification (2026-11-17)

- React production build and Windows desktop build: passed; Windows build has zero warnings/errors.
- 10 frontend unit tests, 31 browser interaction/audit tests, and 71 Windows core checks: passed. The core suite includes sorting more than 500 storage items before limiting the response.
- Twelve saved accessibility reports (six WCAG audits and six explicit label-in-name audits): zero detected violations.
- Browser regressions cover compact 900px layouts, long folder names, persistent settings actions and validation, background scroll containment, full-width modal dimming, and system-colored primary buttons. Review captures use viewport dimensions and include all main pages in forced-colors emulation.
- Windows UI Automation: both React/WebView2 and native fallback launched, navigated all four pages, preserved analysis-only file attributes, and quit gracefully. A duplicate WPF resource inherited during the merge was found by the launch check and removed. The host reported 120 DPI (125% scaling); the locked Windows desktop prevented useful visual captures, so those captures were discarded.

These are automated development checks. No manual Narrator/NVDA, real Windows contrast-theme, native-dialog visual, or additional-DPI acceptance is claimed. Browser captures use fixture data rather than the live engine. The release acceptance items below remain separate from UI quality.

## Additional real-folder run: accounting defect found (fixed in 0.3.0)

A subsequent manual 35-file test (2026-11-09; its evidence files have since been removed from the repository) exercised the actual app on a local test folder, including the default quiet period, closed-window background work, restart catch-up, and GUI decompression. All original hashes and recorded file metadata were preserved and restored.

That run found an **allocation-accounting defect** not covered by the earlier aligned-size fixtures: reading post-decompression allocation before closing the mutation handle overstates growth for some non-aligned lengths. Three files produced an erroneous **−84 KiB** round-trip ledger total although final allocation exactly matched the baseline. A separate 65,537-byte reproduction confirmed 131,072 allocated bytes while the mutation handle remained open versus 69,632 after close. The recommended correction is to read the allocation after the mutation handle is closed.

**Fixed (2026-11-19).** The mutator now closes the mutation handle and measures on a fresh handle, and claims no saving if the file changed in between. The core suite reproduced the defect first (65,537 bytes failed), then passed 80 checks, including compress-and-restore round trips at 65,537, 100,003 and 262,147 bytes that net to exactly zero and return to the original allocation.

## Large-folder load test (2026-11-19)

The real engine ran against a generated 50,000-file folder (10,000 compressible text files of 100–160 KB, 40,000 of 1–8 KB, 1.4 GB) on a local NTFS SSD, with the battery and CPU gates off so they didn't hold work back. The test data was deleted afterwards.

| | Before | After |
|---|---|---|
| Rescan of an unchanged folder | 10 s CPU, ~1 GB read, 92 MB written | 5 s CPU, 70 MB read, nothing written |
| Rescan interval with a working watcher | 2 minutes | 30 minutes |
| Auto-compression, 3 minutes | 69 files/min, 70 s CPU, 10.3 GB read | 109 files/min, 12 s CPU, 359 MB read |
| Longest wait for a UI command during that run | 8.2 s (during a rescan) | 23 ms (no rescan fell in the window) |

Scans still hold the engine lock, so a UI command can wait for a rescan to finish (about 5 s on this folder). The window's 3-second refresh still loads every observation (about 0.25 s CPU each time on this folder). Throughput is limited by the 500 ms pause between files by design.

## Release acceptance still required

- Broad Windows/NTFS cluster-size, hardware, permissions, antivirus, and backup/restore compatibility matrix.
- Repeated workload CPU/I/O/open/save latency measurements and pilot savings/performance targets.
- Actual Windows sign-out/restart/sign-in validation of optional startup registration and window-to-tray behavior during long operations.
- A release certificate, trusted timestamping, and validation of a signed production package. No certificate is supplied in this workspace.
- Real user pilot, accessibility review, and installation/update recovery on additional machines.

Passing development checks is not proof of production readiness or a guarantee of a particular savings percentage.
