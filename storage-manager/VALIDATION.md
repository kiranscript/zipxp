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

The final core fixture run was retained at `C:\Users\kiran\AppData\Local\Temp\ZipXP-Manager-checks-082943c57d3341af9af25c26fa92066d`. The successful package lifecycle state was retained at `C:\Users\kiran\AppData\Local\Temp\ZipXP-package-check-6aee2ef8f445402da8ece8c04a655598`. The development package is `C:\Temp\ZipXP-StorageManager-package.zip`. These paths are local test evidence, not required application paths.

Runtime testing found and fixed a launch-check assumption about console-host `WaitForInputIdle`, missing-startup-entry handling in PowerShell uninstall, and an operation-cache edge case that could block explicit decompression. The final executed checks have no unresolved failures.

## Release acceptance still required

- Broad Windows/NTFS cluster-size, hardware, permissions, antivirus, and backup/restore compatibility matrix.
- Repeated workload CPU/I/O/open/save latency measurements and the plan's pilot savings/performance targets.
- Actual Windows sign-out/restart/sign-in validation of optional startup registration and window-to-tray behavior during long operations.
- A release certificate, trusted timestamping, and validation of a signed production package. No certificate is supplied in this workspace.
- Real user pilot, accessibility review, and installation/update recovery on additional machines.

Passing development checks is not proof of production readiness or a guarantee of a particular savings percentage.
