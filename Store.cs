using System.Runtime.InteropServices;

namespace Whit;

sealed partial class Store : IDisposable
{
    readonly nint db;
    public readonly record struct Page(long Id, string Url, string Title, long Stamp);

    public Store(string path)
    {
        var result = sqlite3_open(path, out db);
        try
        {
            Check(result);
            Check(sqlite3_busy_timeout(db, 100));
            Check(sqlite3_exec(db, """
                PRAGMA journal_mode=WAL;
                PRAGMA secure_delete=ON;
                CREATE TABLE IF NOT EXISTS bookmarks(
                    id INTEGER PRIMARY KEY, url TEXT UNIQUE NOT NULL, title TEXT NOT NULL, stamp INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS history(
                    id INTEGER PRIMARY KEY, url TEXT NOT NULL, title TEXT NOT NULL, stamp INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS settings(
                    key TEXT PRIMARY KEY, value INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TRIGGER IF NOT EXISTS trim_history AFTER INSERT ON history BEGIN
                    DELETE FROM history WHERE id <= NEW.id - 50000;
                END;
                """, 0, 0, 0));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Setting(string key) => Scalar("SELECT value FROM settings WHERE key=?1", key);
    public bool Bookmarked(string url) => Scalar("SELECT 1 FROM bookmarks WHERE url=?1", url);
    public void SetPrivacy(bool https, bool strict) =>
        Write("INSERT OR REPLACE INTO settings VALUES('https',?1),('strict',?2)", https ? 1L : 0L, strict ? 1L : 0L);
    public void Bookmark(string url, string title) =>
        Write("INSERT OR IGNORE INTO bookmarks(url,title,stamp) VALUES(?1,?2,?3)", url, title, Now());
    public void Unbookmark(string url) => Write("DELETE FROM bookmarks WHERE url=?1", url);
    public void Visit(string url, string title) =>
        Write("INSERT INTO history(url,title,stamp) VALUES(?1,?2,?3)", url, title, Now());
    public void Forget(long id)
    {
        Write("DELETE FROM history WHERE id=?1", id);
        Write("PRAGMA wal_checkpoint(TRUNCATE)");
    }
    public void ClearHistory()
    {
        Write("DELETE FROM history");
        Write("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    public List<Page> Browse(bool bookmarks, long before, string search)
    {
        var table = bookmarks ? "bookmarks" : "history";
        var statement = Prepare($"""
            SELECT id,url,title,stamp FROM {table} WHERE id < ?1
                AND (?2='' OR instr(lower(title),lower(?2)) OR instr(lower(url),lower(?2)))
                ORDER BY id DESC LIMIT 41
            """, before, search);
        var pages = new List<Page>();
        try
        {
            while (Step(statement))
                pages.Add(new(sqlite3_column_int64(statement, 0), Text(statement, 1),
                    Text(statement, 2), sqlite3_column_int64(statement, 3)));
        }
        finally { sqlite3_finalize(statement); }
        return pages;
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string Text(nint statement, int column) =>
        Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";

    bool Scalar(string sql, string key)
    {
        var statement = Prepare(sql, key);
        try { return Step(statement) && sqlite3_column_int64(statement, 0) != 0; }
        finally { sqlite3_finalize(statement); }
    }

    void Write(string sql, params object[] values)
    {
        var statement = Prepare(sql, values);
        try { while (Step(statement)) { } }
        finally { sqlite3_finalize(statement); }
    }

    nint Prepare(string sql, params object[] values)
    {
        Check(sqlite3_prepare_v2(db, sql, -1, out var statement, 0));
        try
        {
            for (var i = 0; i < values.Length; i++)
                Check(values[i] is string text
                    ? sqlite3_bind_text(statement, i + 1, text, -1, -1)
                    : sqlite3_bind_int64(statement, i + 1, (long)values[i]));
            return statement;
        }
        catch
        {
            sqlite3_finalize(statement);
            throw;
        }
    }

    bool Step(nint statement)
    {
        var result = sqlite3_step(statement);
        if (result == 100) return true;
        if (result != 101) Check(result);
        return false;
    }

    void Check(int result)
    {
        if (result != 0) throw new IOException(Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
    }

    public void Dispose() { if (db != 0) sqlite3_close(db); }

    const string Sqlite = "winsqlite3.dll";
    [LibraryImport(Sqlite, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_open(string path, out nint db);
    [LibraryImport(Sqlite)] private static partial int sqlite3_close(nint db);
    [LibraryImport(Sqlite)] private static partial int sqlite3_busy_timeout(nint db, int milliseconds);
    [LibraryImport(Sqlite, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_exec(nint db, string sql, nint callback, nint state, nint error);
    [LibraryImport(Sqlite, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_prepare_v2(nint db, string sql, int bytes, out nint statement, nint tail);
    [LibraryImport(Sqlite, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_bind_text(nint statement, int index, string value, int bytes, nint destructor);
    [LibraryImport(Sqlite)] private static partial int sqlite3_bind_int64(nint statement, int index, long value);
    [LibraryImport(Sqlite)] private static partial int sqlite3_step(nint statement);
    [LibraryImport(Sqlite)] private static partial int sqlite3_finalize(nint statement);
    [LibraryImport(Sqlite)] private static partial long sqlite3_column_int64(nint statement, int index);
    [LibraryImport(Sqlite)] private static partial nint sqlite3_column_text(nint statement, int index);
    [LibraryImport(Sqlite)] private static partial nint sqlite3_errmsg(nint db);
}
