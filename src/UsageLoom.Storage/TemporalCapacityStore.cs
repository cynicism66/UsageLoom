using System.Text.Json;
using UsageLoom.Core;

namespace UsageLoom.Storage;
public sealed partial class HistoryStore
{
    public void SaveQuotaObservation(QuotaObservation observation)
    {
        lock(writerGate)
        {
            using var c=Open();using var command=c.CreateCommand();
            command.CommandText="CREATE TABLE IF NOT EXISTS quota_observations(at TEXT PRIMARY KEY,payload TEXT NOT NULL); INSERT OR IGNORE INTO quota_observations(at,payload) VALUES($at,$payload)";
            command.Parameters.AddWithValue("$at",observation.At.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(observation));command.ExecuteNonQuery();
        }
    }
    public List<QuotaObservation> ReadQuotaObservations()
    {
        using var c=Open();using var command=c.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS quota_observations(at TEXT PRIMARY KEY,payload TEXT NOT NULL)";command.ExecuteNonQuery();
        command.CommandText="SELECT payload FROM quota_observations ORDER BY at";using var reader=command.ExecuteReader();var result=new List<QuotaObservation>();
        while(reader.Read())if(JsonSerializer.Deserialize<QuotaObservation>(reader.GetString(0)) is {} o)result.Add(o);
        // Preserve only timeout evidence so log rotation cannot undo recovery.
        // The original observation ledger remains unchanged.
        reader.Close();
        command.CommandText="SELECT value FROM metadata WHERE key='legacy_quota_timeout_evidence_v1'";
        var savedEvidence=command.ExecuteScalar() as string;
        var evidence=savedEvidence is null?new List<string>():JsonSerializer.Deserialize<List<string>>(savedEvidence)??[];
        var lines=new List<string>();
        foreach(var name in new[]{"runtime.log","runtime.1.log","runtime.2.log"})
        {
            try
            {
                using var stream=new FileStream(Path.Combine(dataDirectory,"logs",name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var log=new StreamReader(stream);
                while(log.ReadLine() is {} line)if(line.Contains(" [WARN] Quota ",StringComparison.Ordinal))lines.Add(line);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
        lines.AddRange(evidence);
        var recovered=LegacyQuotaFailures.Recover(result,lines);
        var needed=recovered.Where(o=>o.BarrierReason=="query-failure"&&result.Any(old=>old.At==o.At&&old.BarrierReason is null)).Select(o=>o.At).ToList();
        var retained=lines.Distinct().Where(line=>
        {
            var split=line.IndexOf(" [WARN] Quota ",StringComparison.Ordinal);
            return split>0&&DateTimeOffset.TryParse(line[..split],out var at)&&needed.Any(start=>at>=start&&at-start<TimeSpan.FromSeconds(1));
        }).OrderBy(line=>line,StringComparer.Ordinal).ToList();
        var payload=JsonSerializer.Serialize(retained);
        if(payload!=savedEvidence&&retained.Count>0)
        {
            command.CommandText="INSERT INTO metadata(key,value) VALUES('legacy_quota_timeout_evidence_v1',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$value",payload);command.ExecuteNonQuery();
        }
        return recovered;
    }
    public void SaveTemporalIntervals(IReadOnlyList<CapacityInterval> intervals,IReadOnlyList<CapacityCache>? history=null,int algorithmVersion=3)
    {
        lock(writerGate)
        {
            using var c=Open();using var transaction=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=transaction;
            command.CommandText="CREATE TABLE IF NOT EXISTS capacity_intervals(id TEXT PRIMARY KEY,payload TEXT NOT NULL)";command.ExecuteNonQuery();
            var existing=new Dictionary<string,string>();command.CommandText="SELECT id,payload FROM capacity_intervals";
            using(var reader=command.ExecuteReader())while(reader.Read())existing[reader.GetString(0)]=reader.GetString(1);
            command.CommandText="INSERT INTO capacity_intervals(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
            foreach(var interval in intervals)
            {
                var id=interval.Key+"|"+interval.From.ToUniversalTime().ToString("O")+"|"+interval.To.ToUniversalTime().ToString("O");var payload=JsonSerializer.Serialize(interval);
                if(existing.Remove(id,out var old)&&old==payload)continue;
                command.Parameters.Clear();command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$payload",payload);command.ExecuteNonQuery();
            }
            command.CommandText="DELETE FROM capacity_intervals WHERE id=$id";
            foreach(var id in existing.Keys){command.Parameters.Clear();command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();}
            if(history is not null)
            {
                // Retain the previous algorithm's final results before rebuilding with larger blocks.
                if(algorithmVersion==4)
                {
                    command.Parameters.Clear();command.CommandText="INSERT OR IGNORE INTO temporal_capacity_archive(id,saved_at,payload) SELECT 'legacy-v3-' || saved_at || '-' || id,saved_at,payload FROM temporal_capacity_history WHERE json_extract(payload,'$.Version')=3";command.ExecuteNonQuery();
                }
                command.Parameters.Clear();command.CommandText="DELETE FROM temporal_capacity_history";command.ExecuteNonQuery();
                command.CommandText="INSERT INTO temporal_capacity_history(id,saved_at,payload) VALUES($id,$at,$payload)";
                for(var i=0;i<history.Count;i++)
                {
                    command.Parameters.Clear();command.Parameters.AddWithValue("$id",i.ToString());command.Parameters.AddWithValue("$at",history[i].SavedAt.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(history[i]));command.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
    }
}
