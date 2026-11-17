# Changelog

All notable changes to ZipXP are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed
- Repository reorganised into `src/`, `tests/`, `scripts/` and `docs/`; projects renamed from `StorageManager.*` to `ZipXP.*`.
- The window, tray icon and sidebar now say "ZipXP" instead of "ZipXP Storage Manager".
- README rewritten as a product front page; the technical detail moved to `docs/`.
- Added MIT license, solution file, `.editorconfig`, CI workflow and issue/PR templates.

### Unchanged on purpose
- Data folder, startup entry, single-instance name, executable name and install folder keep their `StorageManager` identifiers so existing installs and saved policies continue to work.

## [0.2.0]

### Added
- Selective, policy-driven NTFS compression of managed folders: read-only analysis first, then optional automatic compression of existing, new, copied-in and changed files.
- Persistent single-file-at-a-time queue with retry/backoff, quiet periods, cooldowns, schedules, battery, CPU and free-space gates, and crash recovery.
- System-tray worker, optional start at sign-in, and **Restore original size** decompression.
- Overview, managed folders, storage insights and searchable activity views with a signed allocation ledger.
- Self-contained publish package plus per-user install and uninstall scripts.

### Removed
- The earlier file-explorer alpha.
