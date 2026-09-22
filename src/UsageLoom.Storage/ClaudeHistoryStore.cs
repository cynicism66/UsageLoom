using Microsoft.Data.Sqlite;
using UsageLoom.Core;

namespace UsageLoom.Storage;

// Separate database: Codex repair/reset operations cannot affect Claude observations.
public sealed class ClaudeHistoryStore(string directory)
{
    private readonly object gate=new();
    public IReadOnlyList<ClaudeQuotaObservation> Merge(ClaudeQuotaSnapshot snapshot,DateTimeOffset now,CancellationToken cancellation=default)
    {
        if(snapshot.Status!="snapshot"||string.IsNullOrWhiteSpace(snapshot.Scope))return [];
        lock(gate)
        {
            cancellation.ThrowIfCancellationRequested();Directory.CreateDirectory(directory);
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"claude-quota-history.db"),DefaultTimeout=2,Pooling=false}.ToString());
            connection.Open();
            using(var schema=connection.CreateCommand())
            {
                schema.CommandText="CREATE TABLE IF NOT EXISTS observations(scope TEXT NOT NULL, at INTEGER NOT NULL, window TEXT NOT NULL, used REAL, resets INTEGER, origin TEXT NOT NULL, PRIMARY KEY(scope,at,window));";
                schema.ExecuteNonQuery();
            }
            using var transaction=connection.BeginTransaction();
            using(var prune=connection.CreateCommand())
            {
                prune.Transaction=transaction;prune.CommandText="DELETE FROM observations WHERE at<$cutoff OR at>$future";
                prune.Parameters.AddWithValue("$cutoff",now.AddDays(-31).UtcTicks);prune.Parameters.AddWithValue("$future",now.AddMinutes(2).UtcTicks);prune.ExecuteNonQuery();
            }
            var points=new List<ClaudeQuotaObservation>();
            using(var read=connection.CreateCommand())
            {
                read.Transaction=transaction;read.CommandText="SELECT at,window,used,resets,origin FROM observations WHERE scope=$scope ORDER BY at DESC LIMIT 40000";read.Parameters.AddWithValue("$scope",snapshot.Scope);
                using var rows=read.ExecuteReader();
                while(rows.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    points.Add(new(snapshot.Scope,new DateTimeOffset(rows.GetInt64(0),TimeSpan.Zero),rows.GetString(1),rows.IsDBNull(2)?null:rows.GetDouble(2),rows.IsDBNull(3)?null:new DateTimeOffset(rows.GetInt64(3),TimeSpan.Zero),rows.GetString(4)));
                }
            }
            var previous=points.OrderBy(p=>p.At).ThenBy(p=>p.Scope,StringComparer.Ordinal).ThenBy(p=>p.Window,StringComparer.Ordinal).ToArray();
            points.AddRange(ClaudeQuotaHistory.Observations(snapshot));
            var merged=ClaudeQuotaHistory.Normalize(points.Where(p=>p.Scope==snapshot.Scope&&p.At>=now.AddDays(-31)&&p.At<=now.AddMinutes(2))).TakeLast(40000).ToArray();
            if(previous.SequenceEqual(merged)){cancellation.ThrowIfCancellationRequested();transaction.Commit();return merged;}
            using(var clear=connection.CreateCommand())
            {
                clear.Transaction=transaction;clear.CommandText="DELETE FROM observations WHERE scope=$scope";clear.Parameters.AddWithValue("$scope",snapshot.Scope);clear.ExecuteNonQuery();
            }
            using(var write=connection.CreateCommand())
            {
                write.Transaction=transaction;write.CommandText="INSERT INTO observations(scope,at,window,used,resets,origin) VALUES($scope,$at,$window,$used,$resets,$origin)";
                foreach(var key in new[]{"$scope","$at","$window","$used","$resets","$origin"})write.Parameters.Add(new SqliteParameter(key,null));
                foreach(var p in merged)
                {
                    cancellation.ThrowIfCancellationRequested();
                    write.Parameters["$scope"].Value=p.Scope;write.Parameters["$at"].Value=p.At.UtcTicks;write.Parameters["$window"].Value=p.Window;
                    write.Parameters["$used"].Value=(object?)p.Used??DBNull.Value;write.Parameters["$resets"].Value=(object?)p.ResetsAt?.UtcTicks??DBNull.Value;write.Parameters["$origin"].Value=p.Origin;
                    write.ExecuteNonQuery();
                }
            }
            cancellation.ThrowIfCancellationRequested();transaction.Commit();return merged;
        }
    }
}
