using Microsoft.Data.Sqlite;
using System.Text.Json;
using UsageLoom.Core;

namespace UsageLoom.Storage;

public sealed partial class HistoryStore
{
    public string BackupCapacityData()
    {
        lock(writerGate)
        {
            using var source=Open();
            var path=Path.Combine(dataDirectory,"backups","capacity-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());
            backup.Open();source.BackupDatabase(backup);
            using var check=backup.CreateCommand();check.CommandText="PRAGMA quick_check";
            if(check.ExecuteScalar() as string!="ok")throw new InvalidDataException("Capacity backup verification failed");
            return path;
        }
    }
    // Preserve source observations and Token data. A durable floor prevents old
    // observations from recreating estimates explicitly cleared by the user.
    public string MaintainCapacity(bool clearHistory,DateTimeOffset at,QuotaObservation? baseline=null)
    {
        if(!clearHistory&&(baseline is null||baseline.Barrier||string.IsNullOrWhiteSpace(baseline.Account)||
            string.IsNullOrWhiteSpace(baseline.Plan)||!baseline.Windows.Any(w=>w.IsPrimary&&w.Minutes==10080&&w.ResetsAt>at&&double.IsFinite(w.Used)&&w.Used>=0&&w.Used<=100)))
            throw new InvalidOperationException("Fresh weekly quota required");
        lock(writerGate)
        {
            var backup=BackupCapacityData();
            using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="CREATE TABLE IF NOT EXISTS quota_observations(at TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS capacity_intervals(id TEXT PRIMARY KEY,payload TEXT NOT NULL)";cmd.ExecuteNonQuery();
            if(clearHistory)
            {
                cmd.CommandText="DELETE FROM capacity_history; DELETE FROM temporal_capacity_history; DELETE FROM temporal_capacity_archive; DELETE FROM capacity_intervals; INSERT INTO metadata(key,value) VALUES('capacity_observation_floor',$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                cmd.Parameters.AddWithValue("$at",at.ToUniversalTime().ToString("O"));cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            }
            cmd.CommandText="DELETE FROM metadata WHERE key='weekly_capacity_v1'";cmd.ExecuteNonQuery();
            var boundary=new QuotaObservation(at,null,null,Pricing.CatalogVersion,[],true){BarrierReason="explicit-boundary"};
            cmd.CommandText="INSERT INTO quota_observations(at,payload) VALUES($at,$payload)";
            cmd.Parameters.AddWithValue("$at",at.ToUniversalTime().ToString("O"));cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(boundary));cmd.ExecuteNonQuery();
            if(!clearHistory&&baseline is not null)
            {
                var start=baseline with{At=at.AddTicks(1)};
                cmd.Parameters["$at"].Value=start.At.ToUniversalTime().ToString("O");cmd.Parameters["$payload"].Value=JsonSerializer.Serialize(start);cmd.ExecuteNonQuery();
            }
            tx.Commit();return backup;
        }
    }
}
