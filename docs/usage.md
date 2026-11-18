# Using ZipXP

## Getting started

1. Select **Add folder** and choose a folder on a local NTFS drive, such as `C:\zipxp-test`. ZipXP selects it in **Managed folders** and scans it without changing files.
2. Check the folder’s estimated savings. **Compress once** previews a one-time job. **Turn on auto-compression** asks you to confirm before ZipXP starts handling eligible existing, new, copied and changed files in the background.
3. In **Overview**, **Estimated savings** comes from the latest scans. **Space saved by ZipXP** counts the net disk space change ZipXP measured from its own compression and restores. **Folder size on disk** shows how much space the measured files currently use.
4. Use **Folder settings** to choose subfolders, exclusions and when ZipXP can work. Use **Browse files** to inspect a folder and **Activity** to track progress or retry failed work.
5. Close the window to keep ZipXP running in the tray. Enable **Start when I sign in** if you want ZipXP to start with Windows. **Pause all folders** or **More actions → Pause folder** pauses work after the current file. **Quit ZipXP** stops background work and keeps the queue for next time.

### Decompression and stopping management

- **Restore files** in Managed folders or **Restore selected** in Storage insights uncompresses files so they take their full space again; no files are deleted. It first disables automatic compression for that managed root and cancels pending compression. Re-enabling automation while decompression is pending is rejected.
- Decompression does not clear existing NTFS directory defaults. The preview reports those defaults, incomplete coverage, omitted entries, and the largest sequential operation's required headroom.
- **Turn off Windows' own folder compression** (More menu) is a separate recursive directory-only job within the current policy scope. Existing files stay compressed. Excluded/unreadable/unsupported directories may retain defaults; inspect the next scan.
- **Stop managing this folder** (More menu) removes the root, observations, and future jobs. Existing files and native defaults remain unchanged. Historical job measurements are retained.
- Resuming/pausing the selective worker does not change any native directory inheritance settings. Such defaults can compress future files even while the app is closed and can bypass the selective worker's exclusions and size limits.
