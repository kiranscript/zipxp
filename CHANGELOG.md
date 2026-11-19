# Changelog

All notable changes to ZipXP are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.3.0] - 2026-11-19 (pre-release)

### Added
- React + TypeScript desktop interface hosted in WebView2; launch with `--native-ui` for the original WPF screens.
- Suggested folders on first launch, an add-folder dialog with an optional "keep compressed automatically" choice, and per-folder progress for the current run.
- One-line curl installer (`scripts/get.ps1`, attached to each release as `zipxp-get.ps1`): downloads the release ZIP, verifies its SHA-256 and runs the per-user `install.ps1`.
- Release ZIPs get a GitHub build attestation; `get.ps1` verifies it when `gh` is installed and signed in (`-RequireAttestation` makes it mandatory).
- Release workflow: pushing a `v*` tag builds, checks and packages the app and attaches `ZipXP-win-x64.zip`, its `.sha256` and `zipxp-get.ps1` to a GitHub release.

### Fixed
- Allocation is now measured after the mutation handle is closed, so savings after a restore are no longer overstated for file sizes that are not cluster-aligned ([#2](https://github.com/kiranscript/zipxp/issues/2)).
- The documented install command pointed at a website that is no longer deployed; it now downloads `zipxp-get.ps1` from the latest release.
- Background work on large folders. On a 50,000-file test folder, the worker no longer loads every observation for each file it compresses (CPU during a run fell from 70 s to 12 s per 3 minutes, and throughput rose from 69 to 109 files/min). Rescans reuse sampling estimates for unchanged files and write only changed records (a rescan now reads 70 MB instead of about 1 GB and writes nothing instead of 92 MB). The safety rescan runs every 30 minutes instead of every 2, because file-system notifications already trigger a scan within seconds; it stays at 2 minutes for a folder whose watcher can't start.

### Changed
- Repository reorganised into `src/`, `tests/`, `scripts/` and `docs/`; projects renamed from `StorageManager.*` to `ZipXP.*`.
- The window, tray icon and sidebar now say "ZipXP" instead of "ZipXP Storage Manager".
- README rewritten as a product front page; the technical detail moved to `docs/`.
- Added MIT license, solution file, `.editorconfig`, CI workflow and issue/PR templates.
- Plainer wording for compression choices and savings throughout the app.

### Unchanged on purpose
- Data folder, startup entry, single-instance name, executable name and install folder keep their `StorageManager` identifiers so existing installs and saved policies continue to work.

## [0.2.0] (pre-release)

### Added
- Selective, policy-driven NTFS compression of managed folders: read-only analysis first, then optional automatic compression of existing, new, copied-in and changed files.
- Persistent single-file-at-a-time queue with retry/backoff, quiet periods, cooldowns, schedules, battery, CPU and free-space gates, and crash recovery.
- System-tray worker, optional start at sign-in, and **Restore original size** decompression.
- Overview, managed folders, storage insights and searchable activity views with a signed (+/-) allocation ledger.
- Self-contained publish package plus per-user install and uninstall scripts.

### Removed
- The earlier file-explorer alpha.
