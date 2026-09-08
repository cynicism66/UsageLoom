using System.Text.Json;

namespace UsageLoom.Core;

// Evidence is scoped to an explicitly declared parent. Equal counters elsewhere are irrelevant.
internal static class InheritedUsage
{
    private sealed record Counter(TokenUsage? Total,string Signature,DateTimeOffset At);
    private static string Signature(JsonElement info)
    {
        var values=new List<string>();
        foreach(var field in new[]{"total_token_usage","last_token_usage"})
        {
            if(!info.TryGetProperty(field,out var usage)||usage.ValueKind==JsonValueKind.Null){values.Add("missing");continue;}
            if(usage.ValueKind!=JsonValueKind.Object)throw new JsonException();
            foreach(var key in new[]{"input_tokens","cached_input_tokens","cache_write_input_tokens","output_tokens","reasoning_output_tokens","total_tokens"})
            {
                if(!usage.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null){values.Add("missing");continue;}
                if(!value.TryLong(out var number)||number<0)throw new JsonException();
                values.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return string.Join('|',values);
    }
    private sealed record FileEvidence(string Owner,string? Parent,List<Counter?> Counters);
    internal static async Task<Dictionary<string,int>> Prefixes(string home,CancellationToken ct,IReadOnlyDictionary<string,IReadOnlyList<string>>? records)
    {
        var evidence=new Dictionary<string,FileEvidence>(StringComparer.OrdinalIgnoreCase);
        var files=records?.Keys??new[]{"sessions","archived_sessions"}.SelectMany(leaf=>Directory.Exists(Path.Combine(home,leaf))?
            Directory.EnumerateFiles(Path.Combine(home,leaf),"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}):[]);
        foreach(var file in files)
        {
            ct.ThrowIfCancellationRequested();string? owner=null,parent=null;var counters=new List<Counter?>();
            try
            {
                await foreach(var line in records is null?HistoryScanner.ReadRecords(file,ct):Replay(records[file],ct))
                {
                    if(line is null)continue;
                    try
                    {
                        using var doc=JsonDocument.Parse(line);var row=doc.RootElement;
                        if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)continue;
                        if(row.Text("type")=="session_meta"&&owner is null)
                        {owner=payload.Text("id")??payload.Text("session_id");parent=payload.Text("forked_from_id")??payload.Text("parent_thread_id");}
                        if(row.Text("type")!="event_msg"||payload.Text("type")!="token_count"||!payload.TryGetProperty("info",out var info)||info.ValueKind!=JsonValueKind.Object)continue;
                        // Compare every counter field, including inconsistent declared totals.
                        // Such rows are evidence only, never a billable baseline or a skipped gap.
                        Counter? counter=null;
                        try
                        {
                            if(info.TryGetProperty("total_token_usage",out var total)&&total.ValueKind==JsonValueKind.Object&&DateTimeOffset.TryParse(row.Text("timestamp"),out var at))
                            {
                                var signature=Signature(info);TokenUsage? parsed=null;
                                try
                                {
                                    var valid=TokenUsage.Parse(total);
                                    if(info.TryGetProperty("last_token_usage",out var last)&&last.ValueKind==JsonValueKind.Object)TokenUsage.Parse(last);
                                    parsed=valid;
                                }
                                catch(JsonException){}
                                counter=new(parsed,signature,at);
                            }
                        }
                        catch(Exception ex)when(ex is JsonException or OverflowException or InvalidOperationException){}
                        counters.Add(counter);
                    }
                    catch(JsonException){}
                }
                if(owner is not null)evidence[file]=new(owner,parent,counters);
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
        var byOwner=evidence.Values.GroupBy(e=>e.Owner).ToDictionary(g=>g.Key,g=>g.ToList());
        bool ValidChain(FileEvidence child)
        {
            var seen=new HashSet<string>{child.Owner};var next=child.Parent;
            while(next is not null)
            {
                if(!seen.Add(next)||seen.Count>128)return false;
                if(!byOwner.TryGetValue(next,out var ancestors))break;
                var links=ancestors.Select(a=>a.Parent).Distinct().ToArray();
                if(links.Length!=1)return false;
                next=links[0];
            }
            return true;
        }
        var result=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var (file,child) in evidence)
        {
            ct.ThrowIfCancellationRequested();
            if(child.Parent is null||!ValidChain(child)||child.Counters.Count<3||!byOwner.TryGetValue(child.Parent,out var parents))continue;
            var best=0;
            foreach(var parent in parents)
            for(var start=0;start<parent.Counters.Count;start++)
            {
                ct.ThrowIfCancellationRequested();var matched=0;var distinct=new HashSet<TokenUsage>();
                while(matched<child.Counters.Count&&start+matched<parent.Counters.Count&&
                    child.Counters[matched] is {} c&&parent.Counters[start+matched] is {} p&&
                    c.Signature==p.Signature&&p.At<=c.At)
                {
                    if(c.Total is {} valid)distinct.Add(valid);matched++;
                    if(distinct.Count>=3&&c.Total is not null)best=Math.Max(best,matched);
                }
            }
            if(best>0)result[file]=best;
        }
        return result;
    }
    private static async IAsyncEnumerable<string> Replay(IReadOnlyList<string> records,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
    {foreach(var row in records){ct.ThrowIfCancellationRequested();yield return row;}await Task.CompletedTask;}
}
