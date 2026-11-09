using System.Text.Json;

namespace StorageManager.Core.Persistence;

public sealed class Store : IDisposable
{
    private readonly Sqlite db;
    public string DatabasePath { get; }
    public const int QueueLimit = 2000;

    public Store(string path)
    {
        DatabasePath = path;
        db = new(path);
        var version = int.Parse(db.Query("PRAGMA user_version")[0][0]!);
        if (version > 2) throw new IOException("This database belongs to a newer Storage Manager. Upgrade the app.");
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
    public void RemoveRoot(string id) => db.Transaction(() =>
    {
        Cancel(id, true);
        db.Query("DELETE FROM roots WHERE id=?", id);
        db.Query("DELETE FROM observations WHERE root=?", id);
        db.Query("DELETE FROM decisions WHERE root=?", id);
    });
    public IReadOnlyList<Observation> Observations(string root) => db.Query("SELECT data FROM observations WHERE root=?", root).Select(row => Read<Observation>(row[0])).ToArray();
    public IReadOnlyList<ScanSummary> Scans(string? root = null) => db.Query("SELECT data FROM scans WHERE (? IS NULL OR root=?) ORDER BY id DESC LIMIT 500", root, root).Select(row => Read<ScanSummary>(row[0])).ToArray();

    public void SaveScan(string root, IEnumerable<Observation> observations, ScanSummary summary) => db.Transaction(() =>
    {
        db.Query("DELETE FROM observations WHERE root=?", root);
        foreach (var observation in observations) db.Query("INSERT INTO observations VALUES(?,?,?)", root, observation.Path, Json(observation));
        db.Query("INSERT INTO scans(root,time,data) VALUES(?,?,?)", root, summary.Utc.ToString("O"), Json(summary));
        db.Query("DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY id DESC LIMIT 2000)");
        db.Query("DELETE FROM decisions WHERE time < ?", DateTime.UtcNow.AddDays(-90).ToString("O"));
        db.Query("DELETE FROM jobs WHERE state NOT IN ('Queued','Retry','Running') AND id NOT IN (SELECT id FROM jobs ORDER BY created DESC LIMIT 10000)");
    });

    public IReadOnlyList<WorkItem> Jobs() => db.Query("SELECT data FROM jobs ORDER BY created DESC").Select(row => Read<WorkItem>(row[0])).ToArray();
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
