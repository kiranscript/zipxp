using System.Runtime.InteropServices;

namespace StorageManager.Core.Persistence;

// Windows' inbox SQLite, with bound UTF-8 parameters and explicit statement lifetimes.
internal sealed class Sqlite : IDisposable
{
    private IntPtr database;
    private readonly object gate = new();
    private const string Library = "winsqlite3.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out IntPtr statement, IntPtr tail);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length, IntPtr destructor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);

    public Sqlite(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var result = sqlite3_open_v2(path, out database, 2 | 4 | 0x10000, IntPtr.Zero);
        if (result != 0) { var error = Error(); sqlite3_close_v2(database); throw error; }
        sqlite3_busy_timeout(database, 5000);
        Query("PRAGMA journal_mode=WAL");
        Query("PRAGMA synchronous=FULL");
    }

    private IOException Error() => new("SQLite: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(database)));

    public List<string?[]> Query(string sql, params string?[] parameters)
    {
        lock (gate)
        {
            if (sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero) != 0) throw Error();
            try
            {
                for (var i = 0; i < parameters.Length; i++)
                    if ((parameters[i] == null ? sqlite3_bind_null(statement, i + 1) : sqlite3_bind_text(statement, i + 1, parameters[i]!, -1, new IntPtr(-1))) != 0) throw Error();
                List<string?[]> rows = [];
                int status;
                while ((status = sqlite3_step(statement)) == 100)
                {
                    var row = new string?[sqlite3_column_count(statement)];
                    for (var c = 0; c < row.Length; c++) row[c] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, c));
                    rows.Add(row);
                }
                if (status != 101) throw Error();
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }
    }

    public void Transaction(Action action)
    {
        lock (gate)
        {
            Query("BEGIN IMMEDIATE");
            try { action(); Query("COMMIT"); }
            catch { Query("ROLLBACK"); throw; }
        }
    }

    public void Dispose() { lock (gate) { if (database != IntPtr.Zero) { sqlite3_close_v2(database); database = IntPtr.Zero; } } }
}
