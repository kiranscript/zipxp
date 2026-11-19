using System.Text.Json;

namespace ZipXP.Core.Persistence;

public sealed class Store : IDisposable
{
    private readonly Sqlite db;
    public string DatabasePath { get; }
    public const int QueueLimit = 2000;
    // The latest scan's files by stable identity, so the worker can follow a rename without loading every observation.
    private readonly Dictionary<string, Dictionary<string, Observation>> identities = [];

    public Store(string path)
    {
        DatabasePath = path;
        db = new(path);
        var version = int.Parse(db.Query("PRAGMA user_version")[0][0]!);
        if (version > 2) throw new IOException("This database belongs to a newer version of ZipXP. Upgrade the app.");
        if (version == 0) db.Transaction(() =>
        {
            db.Query("CREATE TABLE roots (id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, data TEXT NOT NULL)");
            db.Query("CREATE TABLE observations (root TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, data TEXT NOT NULL, PRIMARY KEY(root,path))");
            db.Query("CREATE TABLE scans (id INTEGER PRIMARY KEY, root TEXT NOT NULL, time TEXT NOT NULL, data TEXT NOT NULL)");
            db.Query("CREATE TABLE jobs (id TEXT PRIMARY KEY, root TEXT NOT NULL, identity TEXT NOT NULL, operation TEXT NOT NULL, state TEXT NOT NULL, created TEXT NOT NULL, data TEXT NOT NULL)");
            db.Query("CREATE UNIQUE INDEX active_work ON jobs(root,identity,operation) WHERE state IN ('Queued','Retry','Running')");
            db.Query("CREATE TABLE decisions (root TEXT NOT NULL, fingerprint TEXT NOT NULL, message TEXT NOT NULL, time TEXT NOT NULL, PRIMARY KEY(root,fingerprint))");
            db.Query("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            db.Query("PRAGMA user_version=1");
        });
        if (version < 2) db.Transaction(() =>
        {
            db.Query("CREATE TABLE IF NOT EXISTS ledger (root TEXT NOT NULL, day TEXT NOT NULL, delta INTEGER NOT NULL, measured INTEGER NOT NULL, PRIMARY KEY(root,day))");
            db.Query("PRAGMA user_version=2");
        });
        // Running jobs are uncertain after a crash; retry only after checking real state.
        foreach (var job in ActiveJobs().Where(j => j.State == "Running"))
            SaveJob(job with { State = "Retry", DueUtc = DateTime.UtcNow, Message = "Interrupted; reconcile identity and actual state before retry." });
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string? value) => JsonSerializer.Deserialize<T>(value!)!;
    public IReadOnlyList<ManagedFolder> Roots() => db.Query("SELECT data FROM roots ORDER BY path").Select(row => Read<ManagedFolder>(row[0])).ToArray();
    public void SaveRoot(ManagedFolder root) => db.Query("INSERT INTO roots VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET path=excluded.path,data=excluded.data", root.Id, root.Path, Json(root));
    public void RemoveRoot(string id)
    {
        db.Transaction(() =>
        {
            Cancel(id, true);
            db.Query("DELETE FROM roots WHERE id=?", id);
            db.Query("DELETE FROM observations WHERE root=?", id);
            db.Query("DELETE FROM decisions WHERE root=?", id);
        });
        lock (identities) identities.Remove(id);
    }
    public IReadOnlyList<Observation> Observations(string root) => db.Query("SELECT data FROM observations WHERE root=?", root).Select(row => Read<Observation>(row[0])).ToArray();
    public IReadOnlyList<ScanSummary> Scans(string? root = null) => db.Query("SELECT data FROM scans WHERE (? IS NULL OR root=?) ORDER BY id DESC LIMIT 500", root, root).Select(row => Read<ScanSummary>(row[0])).ToArray();

    /// <summary>The latest scan's entry for a file identity, wherever that file has been renamed to.</summary>
    public Observation? Observed(string root, string identity)
    {
        lock (identities)
        {
            if (!identities.TryGetValue(root, out var map)) identities[root] = map = Index(Observations(root));
            return map.GetValueOrDefault(identity);
        }
    }

    private static Dictionary<string, Observation> Index(IEnumerable<Observation> observations)
    {
        var map = new Dictionary<string, Observation>(StringComparer.Ordinal);
        foreach (var observation in observations) if (observation.File != null) map.TryAdd(observation.File.Identity, observation);
        return map;
    }

    public void SaveScan(string root, IEnumerable<Observation> observations, ScanSummary summary)
    {
        var entries = observations.ToList();
        db.Transaction(() =>
        {
            // Write only what changed: an idle folder's rescan should not rewrite its whole history.
            var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in db.Query("SELECT path,data FROM observations WHERE root=?", root)) stored[row[0]!] = row[1]!;
            foreach (var observation in entries)
            {
                var data = Json(observation);
                if (stored.Remove(observation.Path, out var previous) && previous == data) continue;
                db.Query("INSERT INTO observations VALUES(?,?,?) ON CONFLICT(root,path) DO UPDATE SET path=excluded.path,data=excluded.data", root, observation.Path, data);
            }
            foreach (var gone in stored.Keys) db.Query("DELETE FROM observations WHERE root=? AND path=?", root, gone);
            db.Query("INSERT INTO scans(root,time,data) VALUES(?,?,?)", root, summary.Utc.ToString("O"), Json(summary));
            db.Query("DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY id DESC LIMIT 2000)");
            db.Query("DELETE FROM decisions WHERE time < ?", DateTime.UtcNow.AddDays(-90).ToString("O"));
            db.Query("DELETE FROM jobs WHERE state NOT IN ('Queued','Retry','Running') AND id NOT IN (SELECT id FROM jobs ORDER BY created DESC LIMIT 10000)");
        });
        lock (identities) identities[root] = Index(entries);
    }

    public IReadOnlyList<WorkItem> Jobs() => db.Query("SELECT data FROM jobs ORDER BY created DESC").Select(row => Read<WorkItem>(row[0])).ToArray();
    public DateTime? RunStart(string root) =>
        DateTime.TryParse(Setting("runStart:" + root), null, System.Globalization.DateTimeStyles.RoundtripKind, out var start) ? start.ToUniversalTime() : null;
    public bool HasActiveJobs() => db.Query("SELECT 1 FROM jobs WHERE state IN ('Queued','Retry','Running') LIMIT 1").Count != 0;
    public IReadOnlyList<WorkItem> ActiveJobs() => db.Query("SELECT data FROM jobs WHERE state IN ('Queued','Retry','Running') ORDER BY created DESC").Select(row => Read<WorkItem>(row[0])).ToArray();
    public void SaveJob(WorkItem job) => db.Transaction(() => SaveJobInternal(job));
    private void SaveJobInternal(WorkItem job)
    {
        var previous = db.Query("SELECT data FROM jobs WHERE id=?", job.Id).FirstOrDefault();
        var old = previous == null ? null : Read<WorkItem>(previous[0]);
        if (job.Delta.HasValue && job.State == "Succeeded" && old?.Delta == null)
            db.Query("INSERT INTO ledger VALUES(?,?,?,1) ON CONFLICT(root,day) DO UPDATE SET delta=delta+excluded.delta,measured=measured+1", job.RootId, (job.FinishedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd"), job.Delta.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        db.Query("INSERT INTO jobs VALUES(?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET state=excluded.state,data=excluded.data", job.Id, job.RootId, job.Expected.Identity, job.Operation, job.State, job.CreatedUtc.ToString("O"), Json(job));
    }
    public (long Delta, int Measured) MeasuredSince(DateTime since)
    {
        var row = db.Query("SELECT COALESCE(SUM(delta),0),COALESCE(SUM(measured),0) FROM ledger WHERE day>=?", since.ToString("yyyy-MM-dd"))[0];
        return (long.Parse(row[0]!), int.Parse(row[1]!));
    }
    public bool Enqueue(ManagedFolder root, FileRecord file, string operation, bool automatic)
    {
        var inserted = false;
        db.Transaction(() =>
        {
            if (int.Parse(db.Query("SELECT COUNT(*) FROM jobs WHERE state IN ('Queued','Retry','Running')")[0][0]!) >= QueueLimit ||
                db.Query("SELECT id FROM jobs WHERE root=? AND identity=? AND operation=? AND state IN ('Queued','Retry','Running') LIMIT 1", root.Id, file.Identity, operation).Count != 0) return;
            var now = DateTime.UtcNow;
            // The first job queued while a folder is idle starts a new run, so the UI can show "12 of 40 files".
            if (db.Query("SELECT 1 FROM jobs WHERE root=? AND state IN ('Queued','Retry','Running') LIMIT 1", root.Id).Count == 0)
                Setting("runStart:" + root.Id, now.ToString("O"));
            SaveJobInternal(new(Guid.NewGuid().ToString("N"), root.Id, file, operation, automatic, "Queued", 0, now, now));
            inserted = true;
        });
        return inserted;
    }

    public void Cancel(string root, bool all = false)
    {
        foreach (var job in ActiveJobs().Where(j => j.RootId == root && j.State is "Queued" or "Retry" && (all || j.Operation == "Compress")))
            SaveJobInternal(job with { State = "Skipped", Message = "Cancelled by management/policy change.", FinishedUtc = DateTime.UtcNow });
    }
    public void Remember(string root, string fingerprint, string message) => db.Query("INSERT INTO decisions VALUES(?,?,?,?) ON CONFLICT(root,fingerprint) DO UPDATE SET message=excluded.message,time=excluded.time", root, fingerprint, message, DateTime.UtcNow.ToString("O"));
    public (string Message, DateTime Time)? Decision(string root, string fingerprint)
    {
        var rows = db.Query("SELECT message,time FROM decisions WHERE root=? AND fingerprint=?", root, fingerprint);
        return rows.Count == 0 ? null : (rows[0][0]!, DateTime.Parse(rows[0][1]!).ToUniversalTime());
    }
    public void Forget(string root, string fingerprint) => db.Query("DELETE FROM decisions WHERE root=? AND fingerprint=?", root, fingerprint);
    public void ForgetDecisions(string root) => db.Query("DELETE FROM decisions WHERE root=?", root);
    public string? Setting(string key) => db.Query("SELECT value FROM settings WHERE key=?", key).FirstOrDefault()?[0];
    public void Setting(string key, string value) => db.Query("INSERT INTO settings VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    public void Dispose() => db.Dispose();
}
