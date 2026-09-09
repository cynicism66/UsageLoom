using System.Text.Json;

namespace UsageLoom.Storage;

// Pagination may prune intermediate records and retimestamp the closing snapshot.
// Keep the original replay ledger; reconcile the physical cursor, never sum two histories.
internal static class PaginatedContinuation
{
    private static string? Owner(JsonElement row)=>row.GetProperty("payload").TryGetProperty("id",out var id)?id.GetString():row.GetProperty("payload").TryGetProperty("session_id",out id)?id.GetString():null;
    private static bool Is(JsonElement row,string type)=>row.TryGetProperty("type",out var t)&&t.GetString()==type;
    private static string? Counter(JsonElement row)
    {
        if(!Is(row,"event_msg")||!row.TryGetProperty("payload",out var p)||!p.TryGetProperty("type",out var t)||t.GetString()!="token_count"||!p.TryGetProperty("info",out var info)||info.ValueKind!=JsonValueKind.Object)return null;
        var values=new List<string>();
        foreach(var kind in new[]{"total_token_usage","last_token_usage"})
        {
            if(!info.TryGetProperty(kind,out var usage)||usage.ValueKind!=JsonValueKind.Object)return null;
            foreach(var key in new[]{"input_tokens","cached_input_tokens","cache_write_input_tokens","output_tokens","reasoning_output_tokens","total_tokens"})
                values.Add(usage.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.GetRawText():"missing");
        }
        return string.Join('|',values);
    }
    private static DateTimeOffset? Time(JsonElement row)=>row.TryGetProperty("timestamp",out var t)&&DateTimeOffset.TryParse(t.GetString(),out var at)?at:null;
    public static bool TryJoin(IReadOnlyList<string> previous,IReadOnlyList<string> physical,out List<string> joined,out bool retainedAhead)
    {
        joined=[];retainedAhead=false;
        try
        {
            var old=previous.Select(r=>JsonSerializer.Deserialize<JsonElement>(r)).ToArray();
            var current=physical.Select(r=>JsonSerializer.Deserialize<JsonElement>(r)).ToArray();
            var first=old.FirstOrDefault(r=>Is(r,"session_meta"));var next=current.FirstOrDefault(r=>Is(r,"session_meta"));
            if(first.ValueKind!=JsonValueKind.Object||next.ValueKind!=JsonValueKind.Object||Owner(first) is not {} owner||owner!=Owner(next)||
                !next.GetProperty("payload").TryGetProperty("history_mode",out var mode)||mode.GetString()!="paginated")return false;
            long ordinal=-1;
            foreach(var row in current)
            {if(!row.TryGetProperty("ordinal",out var n)||!n.TryGetInt64(out var value)||value<=ordinal)return false;ordinal=value;}
            var oldTokens=old.Where(r=>Counter(r)!=null).ToArray();var indexes=Enumerable.Range(0,current.Length).Where(i=>Counter(current[i])!=null).ToArray();
            if(oldTokens.Length==0){if(indexes.Length!=0)return false;joined=previous.ToList();return true;}
            if(indexes.Length==0)return false;
            var tail=oldTokens[^1];var signature=Counter(tail);var at=Time(tail);if(at is null)return false;
            var exact=indexes.Where(i=>Counter(current[i])==signature&&Time(current[i])==at).ToArray();
            int anchor;
            if(exact.Length==1)anchor=exact[0];
            else
            {
                // A retimestamped *final* snapshot is only cursor evidence, not new usage.
                anchor=indexes[^1];
                if(Counter(current[anchor])!=signature||indexes.Any(i=>Time(current[i]) is not {} time||time>at))
                {
                    // A pruned physical history can end BEFORE our retained ledger.
                    // Require the complete ordered counter sequence, not isolated equal totals.
                    if(current.Any(r=>Is(r,"event_msg")&&r.GetProperty("payload").TryGetProperty("type",out var k)&&k.GetString()=="token_count"&&
                        r.GetProperty("payload").TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object&&Counter(r)==null))return false;
                    if(indexes.Length<2||indexes.Any(i=>Time(current[i]) is not {} time||time>at)||
                        indexes.Select(i=>Counter(current[i])).Distinct().Count()<2)return false;
                    var position=0;
                    foreach(var i in indexes)
                    {
                        var counter=Counter(current[i]);
                        while(position<oldTokens.Length&&Counter(oldTokens[position])!=counter)position++;
                        if(position==oldTokens.Length)return false;
                        position++;
                    }
                    joined=previous.ToList();retainedAhead=true;return true;
                }
            }
            if(indexes.Any(i=>i>anchor&&(Time(current[i]) is not {} time||time<=at)))return false;
            joined=previous.Concat(physical.Skip(anchor+1).Where(r=>!Is(JsonSerializer.Deserialize<JsonElement>(r),"session_meta"))).ToList();
            return true;
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException){return false;}
    }
}
