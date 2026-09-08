using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using UsageLoom.Core;

namespace UsageLoom.Storage;
public sealed record CapacityCleanupResult(bool Ran,int Observations,int Intervals,DateTimeOffset? Cutoff);
public sealed partial class HistoryStore
{
    // Keep whole pairing segments. Normally retains 30–37 days for weekly windows.
    public CapacityCleanupResult CleanupCapacity(DateTimeOffset now)
    {
        lock(writerGate)
        {
            using var c=Open();using var tx=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT value FROM metadata WHERE key='capacity_cleanup_at'";
            if(DateTimeOffset.TryParse(command.ExecuteScalar() as string,out var last)&&now-last<TimeSpan.FromDays(1))return new(false,0,0,null);
            command.CommandText="CREATE TABLE IF NOT EXISTS quota_observations(at TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS capacity_intervals(id TEXT PRIMARY KEY,payload TEXT NOT NULL)";command.ExecuteNonQuery();
            command.CommandText="SELECT payload FROM quota_observations ORDER BY at";
            var observations=new List<QuotaObservation>();using(var reader=command.ExecuteReader())while(reader.Read())observations.Add(JsonSerializer.Deserialize<QuotaObservation>(reader.GetString(0))!);
            DateTimeOffset? cutoff=null;QuotaObservation? previous=null;
            foreach(var o in observations)
            {
                if(o.At>now.AddDays(-30))break;
                bool boundary=o.Barrier||previous is not null&&(previous.Barrier||o.Account!=previous.Account||o.Plan!=previous.Plan||o.PricingVersion!=previous.PricingVersion||
                    o.Windows.Count==0||o.Windows.All(w=>previous.Windows.FirstOrDefault(p=>p.Key==w.Key) is not {} old||
                        old.ResetsAt is {} a&&w.ResetsAt is {} b&&(Math.Abs((a-b).TotalMinutes)>2||o.At>=a)||w.Used<old.Used));
                if(boundary)cutoff=o.At;
                previous=o;
            }
            int removedObservations=0,removedIntervals=0;
            if(cutoff is {} before)
            {
                // Preserve final estimates independently before deleting their inputs.
                command.CommandText="SELECT payload FROM temporal_capacity_history WHERE saved_at<$cutoff";command.Parameters.AddWithValue("$cutoff",before.ToUniversalTime().ToString("O"));
                var archives=new List<string>();using(var reader=command.ExecuteReader())while(reader.Read())archives.Add(reader.GetString(0));
                foreach(var payload in archives)
                {
                    var cache=JsonSerializer.Deserialize<CapacityCache>(payload)!;
                    command.Parameters.Clear();command.CommandText="INSERT OR IGNORE INTO temporal_capacity_archive(id,saved_at,payload) VALUES($id,$at,$payload)";
                    command.Parameters.AddWithValue("$id",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));command.Parameters.AddWithValue("$at",cache.SavedAt.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$payload",payload);command.ExecuteNonQuery();
                }
                command.Parameters.Clear();command.Parameters.AddWithValue("$cutoff",before.ToUniversalTime().ToString("O"));
                command.CommandText="DELETE FROM quota_observations WHERE at<$cutoff";removedObservations=command.ExecuteNonQuery();
                command.CommandText="DELETE FROM capacity_intervals WHERE julianday(json_extract(payload,'$.To'))<julianday($cutoff)";removedIntervals=command.ExecuteNonQuery();
                command.CommandText="DELETE FROM temporal_capacity_history WHERE saved_at<$cutoff";command.ExecuteNonQuery();
                // v3 duplicate export snapshots are not used by the history UI; final results above are retained.
                command.CommandText="DELETE FROM capacity_history WHERE json_extract(payload,'$.Version') IN (3,4) AND saved_at<$cutoff";command.ExecuteNonQuery();
            }
            command.Parameters.Clear();command.CommandText="INSERT INTO metadata(key,value) VALUES('capacity_cleanup_at',$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$at",now.ToUniversalTime().ToString("O"));command.ExecuteNonQuery();tx.Commit();
            return new(true,removedObservations,removedIntervals,cutoff);
        }
    }
}
