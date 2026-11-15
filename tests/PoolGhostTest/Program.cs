// Simulate the Reset Settings crash: pooled connection on a deleted accounts.db.
using Microsoft.Data.Sqlite;

var path = Path.Combine(Path.GetTempPath(), "pool_ghost_demo", "accounts.db");
Directory.CreateDirectory(Path.GetDirectoryName(path)!);

// 1) normal app start: open, create schema, add a row, connection disposed -> pooled
using (var c = new SqliteConnection($"Data Source={path}"))
{
    c.Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "CREATE TABLE IF NOT EXISTS accounts (user_name TEXT PRIMARY KEY);";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO accounts VALUES ('rankynfree');";
    cmd.ExecuteNonQuery();
}

// 2) Reset Settings: delete the file while its connection sits in the pool
File.Delete(path);

// 3) Reinit's Load(): open again on the deleted path
var conn1 = new SqliteConnection($"Data Source={path}");
conn1.Open();
Console.WriteLine($"file recreated? {File.Exists(path)}");
var q = conn1.CreateCommand();
q.CommandText = "SELECT user_name FROM accounts;";
var r = q.ExecuteReader();
var ghost = new List<string>();
while (r.Read()) ghost.Add(r.GetString(0));
Console.WriteLine($"ghost read after delete: [{string.Join(", ", ghost)}]");
conn1.Close(); // pooled again

// 4) login writes: INSERT on the ghost -> crash reproduction
try
{
    var conn2 = new SqliteConnection($"Data Source={path}");
    conn2.Open();
    var ins = conn2.CreateCommand();
    ins.CommandText = "INSERT INTO accounts VALUES ('newuser');";
    ins.ExecuteNonQuery();
    Console.WriteLine("insert succeeded (unexpected)");
    conn2.Close();
}
catch (SqliteException ex)
{
    Console.WriteLine($"REPRODUCED: {ex.Message} (SQLite Error {ex.SqliteErrorCode})");
}

// 5) the fix: ClearAllPools before re-open
SqliteConnection.ClearAllPools();
Console.WriteLine($"file exists after ClearAllPools? {File.Exists(path)}");
var conn3 = new SqliteConnection($"Data Source={path}");
conn3.Open();
// Mirror the app's OpenDb(), which always runs CREATE TABLE IF NOT EXISTS before use.
var mk = conn3.CreateCommand();
mk.CommandText = "CREATE TABLE IF NOT EXISTS accounts (user_name TEXT PRIMARY KEY);";
mk.ExecuteNonQuery();
var q3 = conn3.CreateCommand();
q3.CommandText = "SELECT user_name FROM accounts;";
var r3 = q3.ExecuteReader();
var after = new List<string>();
while (r3.Read()) after.Add(r3.GetString(0));
Console.WriteLine($"read after ClearAllPools: [{string.Join(", ", after)}]");
var ins3 = conn3.CreateCommand();
ins3.CommandText = "INSERT INTO accounts VALUES ('newuser');";
ins3.ExecuteNonQuery();
Console.WriteLine("insert after ClearAllPools: SUCCESS");
conn3.Close();
Console.WriteLine($"final file exists? {File.Exists(path)} size={new FileInfo(path).Length}");
