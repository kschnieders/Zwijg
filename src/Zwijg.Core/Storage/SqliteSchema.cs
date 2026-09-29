using Microsoft.Data.Sqlite;

namespace Zwijg.Core.Storage;

// Versionen für SQLite Dateien über PRAGMA user_version.
// Jede Änderung am Aufbau kommt als neuer Schritt ans Ende, alte Schritte werden nie geändert.
// Eine Datei läuft beim Öffnen alle Schritte ab ihrer Version durch, jeder in einer eigenen Transaktion.
public static class SqliteSchema
{
    // Gibt die Version zurück, die die Datei vorher hatte
    public static int Migrate(SqliteConnection con, params string[] steps)
    {
        int before;
        using (var read = con.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            before = Convert.ToInt32(read.ExecuteScalar());
        }

        for (var v = before; v < steps.Length; v++)
        {
            using var tx = con.BeginTransaction();
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = steps[v] + $";\nPRAGMA user_version = {v + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        return before;
    }
}
