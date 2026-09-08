using System.Text.Json;
using UsageLoom.Core;
using Microsoft.Data.Sqlite;

namespace UsageLoom.Storage;

public sealed partial class HistoryStore
{
    // A preview is an exact event snapshot, not a date-wide UPDATE. Never overwrite another account.
    public string ConfirmAttribution(IReadOnlyList<UsageEvent> preview,string account)
    {
        if(string.IsNullOrWhiteSpace(account)||preview.Count==0||preview.Any(e=>e.AccountScope is not null||e.Timestamp is null))
            throw new InvalidOperationException("Invalid attribution preview");
        lock(writerGate)
        {
            using var c=Open();
            var existing=Read().ToDictionary(e=>e.Id);
            if(preview.Any(e=>!existing.TryGetValue(e.Id,out var current)||current!=e))
                throw new InvalidOperationException("Attribution preview changed; refresh and try again");
            var backupPath=Path.Combine(dataDirectory,"backups","attribution-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            using(var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backupPath}.ToString()))
            {backup.Open();c.BackupDatabase(backup);}
            using var tx=c.BeginTransaction();
            foreach(var item in preview)
            {
                var confirmed=item with{AccountScope=account,AccountAttribution="user-confirmed"};
                using var cmd=c.CreateCommand();cmd.Transaction=tx;
                cmd.CommandText="UPDATE events SET payload=$payload WHERE id=$id; INSERT INTO attribution_confirmations(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                cmd.Parameters.AddWithValue("$id",item.Id);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(confirmed));cmd.ExecuteNonQuery();
            }
            tx.Commit();return backupPath;
        }
    }
    private static UsageEvent ApplyConfirmation(SqliteConnection c,SqliteTransaction tx,UsageEvent item)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT payload FROM attribution_confirmations WHERE id=$id";cmd.Parameters.AddWithValue("$id",item.Id);
        if(cmd.ExecuteScalar() is string json&&JsonSerializer.Deserialize<UsageEvent>(json) is {} old&&
            old.Source==item.Source&&old.Timestamp==item.Timestamp&&old.Tokens==item.Tokens&&
            (item.AccountScope is null||item.AccountScope==old.AccountScope))
            return item with{AccountScope=old.AccountScope,AccountAttribution="user-confirmed"};
        return item;
    }
}
