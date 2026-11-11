using System.Text.RegularExpressions;
using StorageManager.Core;

namespace StorageManager.App;

/// <summary>
/// Turns the engine's precise, technical wording into everyday language for the window.
/// The engine's own strings are stored in the database and compared by the checks, so they stay as they are;
/// anything not listed here is shown unchanged.
/// </summary>
internal static partial class Plain
{
    public const string BuiltIn = "Windows' own folder compression";

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        // Worker state
        ["Ready"] = "Ready.",
        ["Paused; the current operation will finish"] = "Paused. The file in progress will finish first.",
        ["Paused; no new operations start. Native directory inheritance remains active."] = "Paused. ZipXP won't start anything new until you resume.",
        ["Add a folder for read-only analysis to begin."] = "Add a folder to get started.",
        ["Watching managed folders; reconciliation is active."] = "Up to date. Watching your folders for changes.",

        // Folder state
        ["Waiting for scan"] = "Waiting for its first scan",
        ["Scanning primary streams…"] = "Scanning…",
        ["Complete primary-stream scan"] = "Up to date",
        ["Folder paused"] = "Paused",
        ["Outside schedule"] = "Waiting for its scheduled hours",
        ["On battery"] = "Waiting until the PC is plugged in",
        ["High system CPU load"] = "Waiting until the PC is less busy",
        ["Low free space"] = "Waiting: the drive is too full to work safely",
        ["Watcher overflow/error; reconciliation requested."] = "Catching up on recent changes",

        // Why a file is or isn't compressed
        ["Candidate"] = "Can be compressed",
        ["Folder"] = "",
        ["Already compressed"] = "Already compressed",
        ["Excluded by policy"] = "Excluded in this folder's settings",
        ["Reparse point"] = "A link to another location (skipped)",
        ["Reparse point; not followed"] = "A link to another location (skipped)",
        ["Hidden"] = "Hidden item (skipped)",
        ["System"] = "Windows system item (skipped)",
        ["Encrypted"] = "Encrypted (can't also be compressed)",
        ["Sparse"] = "Not a regular file (skipped)",
        ["Offline"] = "Not stored on this PC",
        ["Recall-on-open"] = "Online-only cloud file",
        ["Recall-on-access"] = "Online-only cloud file",
        ["Read-only"] = "Read-only (skipped)",
        ["Directory default only"] = "Folders themselves aren't compressed",
        ["Multiple/unknown hard links"] = "Shared under more than one file name (skipped)",
        ["Above tested maximum size"] = "Larger than the size limit",
        ["Below minimum size"] = "Too small to be worth compressing",
        ["File still changing / quiet period"] = "Changed recently; waiting for it to settle",
        ["Changed during sampling; rescan pending"] = "Changed while being checked; will look again",
        ["Cooldown after operation"] = "Handled recently; will be checked again later",
        ["low estimated benefit until content changes"] = "Wouldn't save much space",

        // What happened to a file
        ["In progress; stop waits for this file."] = "Working on it now",
        ["State verified; allocation measurement unavailable."] = "Done (the space saved couldn't be measured)",
        ["Native default cleared; existing file contents unchanged."] = BuiltIn + " turned off here. Existing files unchanged.",
        ["Actual compression state already matches; no new measurement."] = "Nothing to do; already in the requested state",
        ["Outside the current policy scope."] = "Skipped: excluded in this folder's settings",
        ["Resolved path changed; no mutation performed."] = "Skipped: the file was moved",
        ["File identity, length or last-write time changed; rescan required."] = "Skipped: the file changed, so it will be checked again",
        ["Unsupported directory default."] = "Skipped: this kind of folder isn't supported",
        ["Expected compression state was not observed."] = "Windows didn't apply the change",
        ["Folder is no longer managed."] = "Skipped: the folder is no longer managed",
        ["Automation disabled."] = "Skipped: automatic compression was turned off",
        ["Cancelled by management/policy change."] = "Cancelled because the folder's settings changed",
        ["Interrupted; reconcile identity and actual state before retry."] = "Interrupted; the file will be checked and tried again",

        // Things that can go wrong when adding or changing a folder
        ["Choose an absolute local Windows folder, not a WSL/UNC/device path."] = "Choose a folder on this PC, such as one in Documents. Network and Linux folders aren't supported.",
        ["Drive roots cannot be managed."] = "Choose a folder rather than a whole drive.",
        ["Choose a ready local fixed NTFS drive."] = "That drive isn't supported. Choose a folder on an internal drive; USB, network and non-Windows drives can't be used.",
        ["NTFS with file compression support is required."] = "That drive doesn't support Windows file compression.",
        ["Windows and Program Files locations are excluded."] = "Windows and Program Files folders are protected and can't be added.",
        ["A parent directory is a reparse point."] = "That folder sits behind a link to another location. Choose the real folder instead.",
        ["Reparse points are not followed."] = "That folder is a link to another location. Choose the real folder instead.",
        ["The root folder is unsupported or unavailable."] = "That folder can't be used. It may be hidden, read-only, encrypted or unavailable.",
        ["Path is outside the managed folder."] = "That item is outside the folder.",
        ["The app data directory cannot be inside a managed root."] = "That folder contains ZipXP's own data, so it can't be added.",
        ["This folder overlaps an existing managed folder. Use one root with exclusions."] = "That folder is inside (or contains) a folder you've already added. Keep the outer folder and exclude parts of it in its settings.",
        ["This folder is no longer managed."] = "That folder is no longer managed.",
        ["Wait for pending decompression to finish before re-enabling automatic compression."] = "This folder is still being decompressed. Wait for that to finish before turning automatic compression back on.",
        ["Wait for the current folder operation to finish before starting a different operation."] = "This folder is still busy with another task. Wait for it to finish first.",
        ["Wait for native-default clearing to finish first."] = "This folder is still busy with another task. Wait for it to finish first.",
    };

    private static readonly (string Prefix, string Replacement)[] Prefixes =
    [
        ("Scan failed: ", "Couldn't scan: "),
        ("Volume unavailable: ", "Drive unavailable: "),
        ("Watcher unavailable; periodic scan remains active: ", "Can't watch for live changes, so checking every few minutes: "),
        ("Worker error: ", "Something went wrong: "),
        ("Inspection failed: ", "Couldn't read this item: "),
        ("Listing failed: ", "Couldn't open this folder: "),
        ("Compress: ", "Compressing "),
        ("Decompress: ", "Decompressing "),
        ("ClearDefault: ", "Updating "),
    ];

    [GeneratedRegex(@"^Verified\. Signed allocation reduction: (-?)(.+)\.$")] private static partial Regex Verified();
    [GeneratedRegex(@"^Low free space; requires (.+) headroom\.$")] private static partial Regex LowSpace();
    [GeneratedRegex(@"^Partial · (\d+) unknown · (\d+) errors$")] private static partial Regex Partial();

    /// <summary>Plain-language version of an engine status, reason, result or error.</summary>
    public static string Message(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (Exact.TryGetValue(text, out var plain)) return plain;
        if (text.StartsWith("Permanent: ", StringComparison.Ordinal)) return Message(text["Permanent: ".Length..]);
        foreach (var (prefix, replacement) in Prefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return replacement + text[prefix.Length..];
        var match = Verified().Match(text);
        if (match.Success) return match.Groups[1].Length == 0 ? $"Done. Saved {match.Groups[2].Value}." : $"Done. The file now uses {match.Groups[2].Value} more.";
        match = LowSpace().Match(text);
        if (match.Success) return $"Waiting: needs {match.Groups[1].Value} of free space on the drive";
        match = Partial().Match(text);
        if (match.Success) return $"Scanned, but {Count(int.Parse(match.Groups[1].Value), "item")} couldn't be read";
        return text;
    }

    public static string Action(string operation) => operation switch
    {
        "Compress" => "Compress", "Decompress" => "Decompress", "ClearDefault" => "Turn off Windows compression", _ => operation
    };

    public static (string Text, string Tone) JobState(string state) => state switch
    {
        "Queued" => ("Waiting", "Info"), "Running" => ("Working", "Info"), "Retry" => ("Will retry", "Warn"),
        "Succeeded" => ("Done", "Good"), "Failed" => ("Failed", "Bad"), "Skipped" => ("Skipped", "Neutral"),
        "Unchanged" => ("No change", "Neutral"), _ => (state, "Neutral")
    };

    /// <summary>"Today 15:35", "Yesterday 09:10", "3 Nov 15:35": unambiguous for any date format.</summary>
    public static string When(DateTime utc, DateTime? after = null)
    {
        var local = utc.ToLocalTime(); var today = DateTime.Now.Date;
        // In a range such as "Today 16:17 to 16:19" the second half only needs the time.
        if (after is { } first && first.ToLocalTime().Date == local.Date) return local.ToString("t");
        return local.Date == today ? $"Today {local:t}" : local.Date == today.AddDays(-1) ? $"Yesterday {local:t}" : local.ToString("d MMM") + $" {local:t}";
    }

    /// <summary>Shortens a long path from the middle so the start and the folder's own name both stay visible.</summary>
    public static string MiddleTrim(string path, int max)
    {
        if (path.Length <= max) return path;
        var root = System.IO.Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return path;
        return root + parts[0] + "\\…\\" + string.Join("\\", parts[^2..]);
    }

    /// <summary>"1 file", "2 files", "1,204 files".</summary>
    public static string Count(long number, string noun) => number == 1 ? $"1 {noun}" : $"{number:N0} {noun}s";

    /// <summary>A size change where positive means space was saved.</summary>
    public static string Saved(long? bytes) => bytes switch
    {
        null => "—",
        0 => "Nothing yet",
        < 0 => "−" + Display.Bytes(-bytes.Value),
        _ => Display.Bytes(bytes)
    };
}
