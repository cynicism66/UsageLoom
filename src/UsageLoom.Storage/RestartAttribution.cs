using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageLoom.Core;

namespace UsageLoom.Storage;

public sealed record RestartFileBoundary(long Created,int Records,string Prefix);
public sealed record RestartCheckpoint(string Account,string Home,DateTimeOffset Since,DateTimeOffset ClosedAt,Dictionary<string,RestartFileBoundary> Files)
{
    public DateTimeOffset? RecoveryOpenedAt { get; init; }
}
public sealed partial class HistoryStore
{
    public List<RestartCapacityEvidence> ReadRestartCapacityEvidence()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT value FROM metadata WHERE key LIKE 'restart-inference-%'";
        using var reader=command.ExecuteReader();var result=new List<RestartCapacityEvidence>();
        while(reader.Read())
        {
            try{if(JsonSerializer.Deserialize<RestartCapacityEvidence>(reader.GetString(0)) is {} item&&item.Events is not null)result.Add(item);}
            catch(JsonException){} // Missing or malformed proof never authorizes inference.
        }
        return result;
    }
    private static string RecordsHash(IEnumerable<string> records)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',records))));
    public void SaveRestartCheckpoint(string account,string home,DateTimeOffset since,DateTimeOffset closedAt)
    {
        lock(writerGate)
        {
            var boundaries=ReadIndexes().ToDictionary(p=>p.Key,p=>new RestartFileBoundary(p.Value.Created,p.Value.Records.Count,RecordsHash(p.Value.Records)),StringComparer.OrdinalIgnoreCase);
            var checkpoint=new RestartCheckpoint(account,Path.GetFullPath(home),since,closedAt,boundaries);
            using var connection=Open();using var command=connection.CreateCommand();
            command.CommandText="INSERT OR IGNORE INTO metadata(key,value) VALUES('restart_attribution_v1',$value)";
            command.Parameters.AddWithValue("$value",JsonSerializer.Serialize(checkpoint));command.ExecuteNonQuery();
        }
    }
    public RestartCheckpoint? ReadPendingRestart(DateTimeOffset openedAt)
    {
        lock(writerGate)
        {
            using var connection=Open();using var tx=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT value FROM metadata WHERE key='restart_attribution_v1'";
            if(command.ExecuteScalar() is not string json)return null;
            RestartCheckpoint? checkpoint;
            try{checkpoint=JsonSerializer.Deserialize<RestartCheckpoint>(json);}catch(JsonException){return null;}
            if(checkpoint is null||checkpoint.Files is null)return null;
            checkpoint=checkpoint with{RecoveryOpenedAt=checkpoint.RecoveryOpenedAt??openedAt};
            command.CommandText="UPDATE metadata SET value=$value WHERE key='restart_attribution_v1'";
            command.Parameters.AddWithValue("$value",JsonSerializer.Serialize(checkpoint));command.ExecuteNonQuery();tx.Commit();
            return checkpoint;
        }
    }
    public void DiscardPendingRestart()
    {
        lock(writerGate)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            command.CommandText="DELETE FROM metadata WHERE key='restart_attribution_v1'";command.ExecuteNonQuery();
        }
    }
    // Consumed once at startup. A crash or unsuccessful login must not replay an old handoff.
    public RestartCheckpoint? TakeRestartCheckpoint()
    {
        lock(writerGate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="SELECT value FROM metadata WHERE key='restart_attribution_v1'";var json=command.ExecuteScalar() as string;
            command.CommandText="DELETE FROM metadata WHERE key='restart_attribution_v1'";command.ExecuteNonQuery();transaction.Commit();
            if(json is null)return null;
            try{return JsonSerializer.Deserialize<RestartCheckpoint>(json);}catch(JsonException){return null;}
        }
    }
    public int AttributeRestartGap(RestartCheckpoint checkpoint,string account,string home,DateTimeOffset openedAt,CancellationToken cancellationToken=default)
    {
        if(account!=checkpoint.Account||string.IsNullOrWhiteSpace(account)||
           !string.Equals(Path.GetFullPath(home),checkpoint.Home,StringComparison.OrdinalIgnoreCase)||
           checkpoint.Since>checkpoint.ClosedAt||checkpoint.ClosedAt>openedAt)return 0;
        lock(writerGate)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            var candidates=new HashSet<(string Source,DateTimeOffset Timestamp)>();
            var changes=new List<(string Source,IndexedFile Index)>();
            using(var query=connection.CreateCommand())
            {
                query.Transaction=transaction;query.CommandText="SELECT source,payload FROM scan_indexes";using var reader=query.ExecuteReader();
                while(reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index=JsonSerializer.Deserialize<IndexedFile>(reader.GetString(1))!;
                    if(index.Version!=IncrementalHistory.ParserVersion)continue;
                    var relative=Path.GetRelativePath(checkpoint.Home,index.Path);
                    if(Path.IsPathRooted(relative)||relative.StartsWith("..",StringComparison.Ordinal))continue;
                    var first=0;
                    if(checkpoint.Files.TryGetValue(index.Path,out var boundary))
                    {
                        if(index.Created!=boundary.Created||index.Records.Count<boundary.Records||RecordsHash(index.Records.Take(boundary.Records))!=boundary.Prefix)continue;
                        first=boundary.Records;
                    }
                    else if(index.Created<=checkpoint.Since.UtcDateTime.Ticks||index.Created>openedAt.UtcDateTime.Ticks)continue;
                    var source=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(index.Path).ToUpperInvariant())));
                    var changed=false;
                    for(var i=first;i<index.Records.Count;i++)
                    {
                        var row=JsonNode.Parse(index.Records[i])!;
                        if(row["type"]?.GetValue<string>()!="event_msg"||row["payload"]?["type"]?.GetValue<string>()!="token_count"||row["account_scope"] is not null)continue;
                        if(!DateTimeOffset.TryParse(row["timestamp"]?.GetValue<string>(),out var at)||at<=checkpoint.Since||at>openedAt)continue;
                        row["account_scope"]=account;row["account_attribution"]="restart-inferred";
                        index.Records[i]=row.ToJsonString();candidates.Add((source,at));changed=true;
                    }
                    if(changed)changes.Add((reader.GetString(0),index));
                }
            }
            var events=new List<UsageEvent>();
            using(var query=connection.CreateCommand())
            {
                query.Transaction=transaction;query.CommandText="SELECT payload FROM events";using var reader=query.ExecuteReader();
                while(reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();var item=JsonSerializer.Deserialize<UsageEvent>(reader.GetString(0))!;
                    if(item.AccountScope is null&&item.Timestamp is {} at&&candidates.Contains((item.Source,at)))events.Add(item with{AccountScope=account,AccountAttribution="restart-inferred"});
                }
            }
            foreach(var (source,index) in changes)
            {
                using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE scan_indexes SET payload=$value WHERE source=$source";
                update.Parameters.AddWithValue("$value",JsonSerializer.Serialize(index));update.Parameters.AddWithValue("$source",source);update.ExecuteNonQuery();
            }
            foreach(var item in events)
            {
                using var update=connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE events SET payload=$value WHERE id=$id";
                update.Parameters.AddWithValue("$value",JsonSerializer.Serialize(item));update.Parameters.AddWithValue("$id",item.Id);update.ExecuteNonQuery();
            }
            using(var audit=connection.CreateCommand())
            {
                audit.Transaction=transaction;audit.CommandText="INSERT INTO metadata(key,value) VALUES($key,$value)";
                audit.Parameters.AddWithValue("$key","restart-inference-"+Guid.NewGuid().ToString("N"));
                audit.Parameters.AddWithValue("$value",JsonSerializer.Serialize(new{checkpoint.Account,checkpoint.Since,checkpoint.ClosedAt,OpenedAt=openedAt,Events=events.Select(item=>item.Id).ToArray(),Reason="restart-inferred"}));audit.ExecuteNonQuery();
            }
            using(var complete=connection.CreateCommand())
            {
                complete.Transaction=transaction;complete.CommandText="DELETE FROM metadata WHERE key='restart_attribution_v1' AND json_extract(value,'$.Since')=json_extract($checkpoint,'$.Since') AND json_extract(value,'$.Account')=json_extract($checkpoint,'$.Account')";
                complete.Parameters.AddWithValue("$checkpoint",JsonSerializer.Serialize(checkpoint));complete.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();transaction.Commit();return events.Count;
        }
    }
}
