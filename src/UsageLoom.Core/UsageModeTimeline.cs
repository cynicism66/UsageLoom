using System.Text.Json;

namespace UsageLoom.Core;

// Request metadata only. A requested priority tier is not proof of the tier actually served.
// Keep this separate from counters, attribution, and event identity.
internal sealed class UsageModeTimeline
{
    private sealed record Point(DateTimeOffset At,string Model,string? Tier,string? Effort,bool Turn,bool HasTier,bool HasEffort);
    private sealed record Mode(DateTimeOffset At,string Model,string? Tier,string? Effort);
    private readonly Dictionary<string,List<Mode>> modes=new(StringComparer.Ordinal);
    private readonly HashSet<string> untimedOwners=new(StringComparer.Ordinal);

    internal static async Task<UsageModeTimeline> ReadAsync(string home,CancellationToken ct,
        IReadOnlyDictionary<string,IReadOnlyList<string>>? records,IReadOnlyDictionary<string,int> inherited)
    {
        var points=new Dictionary<string,List<Point>>(StringComparer.Ordinal);
        var untimedOwners=new HashSet<string>(StringComparer.Ordinal);
        var paths=records?.Keys??new[]{"sessions","archived_sessions"}.SelectMany(leaf=>Directory.Exists(Path.Combine(home,leaf))?
            Directory.EnumerateFiles(Path.Combine(home,leaf),"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}):[]);
        foreach(var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string? owner=null,parent=null;var tokenOrdinal=0;var hasParentUsage=false;
            DateTimeOffset? childBoundary=null;var boundaryExclusive=false;
            var filePoints=new List<(Point Point,bool NeedsChildBoundary,bool CounterCopy)>();
            var authoritativeTurns=new HashSet<DateTimeOffset>();
            var untimedTurns=new List<(bool CounterCopy,string Stamp)>();
            var authoritativeUntimedTurns=new HashSet<string>(StringComparer.Ordinal);
            void ChildBoundary(JsonElement row,bool exclusive=false)
            {
                if(DateTimeOffset.TryParse(row.Text("timestamp"),out var at)&&(childBoundary is null||at<childBoundary))
                {childBoundary=at;boundaryExclusive=exclusive;}
            }
            var inheritedCount=inherited.GetValueOrDefault(path);
            try
            {
                await foreach(var line in records is null?HistoryScanner.ReadRecords(path,ct):Replay(records[path],ct))
                {
                    if(line is null)continue;
                    try
                    {
                        using var doc=JsonDocument.Parse(line);var row=doc.RootElement;
                        if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)continue;
                        var type=row.Text("type");
                        if(type=="session_meta")
                        {
                            var id=payload.Text("id")??payload.Text("session_id");
                            if(owner is null)
                            {owner=id;parent=payload.Text("forked_from_id")??payload.Text("parent_thread_id");}
                            else if(parent is not null&&id==parent&&hasParentUsage)ChildBoundary(row);
                            continue;
                        }
                        if(owner is null)continue; // A file name is not evidence of a declared thread identity.
                        if(type=="event_msg"&&payload.Text("type")=="token_count")
                        {
                            tokenOrdinal++;
                            if(inheritedCount>0&&tokenOrdinal==inheritedCount)ChildBoundary(row,true);
                            if(payload.TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object&&
                                info.TryGetProperty("total_token_usage",out var total)&&total.ValueKind==JsonValueKind.Object)
                                hasParentUsage|=total.Number("input_tokens")>0||total.Number("output_tokens")>0;
                            continue;
                        }
                        if(type=="event_msg"&&payload.Text("type")=="task_started")
                        {if(IsChildTurn(payload.Text("turn_id"),owner))ChildBoundary(row);continue;}
                        if(!DateTimeOffset.TryParse(row.Text("timestamp"),out var at))
                        {
                            // A setting without an effective time cannot safely be
                            // applied before or after any completion. Do not keep a
                            // previous Fast default merely because this row is untimed.
                            if(type=="event_msg"&&payload.Text("type")=="thread_settings_applied"&&
                                payload.Text("thread_id")==owner&&payload.TryGetProperty("thread_settings",out var untimedSettings)&&
                                untimedSettings.ValueKind==JsonValueKind.Object)untimedOwners.Add(owner);
                            else if(type=="turn_context")
                            {
                                var compact=IsCounterCopy(row,payload,records is not null);
                                var stamp=row.Text("timestamp")??"";
                                if(!compact)authoritativeUntimedTurns.Add(stamp);
                                var thread=payload.Text("thread_id");
                                if((thread is null||thread==owner)&&(parent is null||thread==owner||IsChildTurn(payload.Text("turn_id"),owner)))
                                    untimedTurns.Add((compact,stamp));
                            }
                            continue;
                        }
                        Point? point=null;var needsChildBoundary=false;var counterCopy=false;
                        if(type=="event_msg"&&payload.Text("type")=="thread_settings_applied")
                        {
                            // Forks may replay parent settings; never infer the recipient from file proximity.
                            if(payload.Text("thread_id")!=owner||!payload.TryGetProperty("thread_settings",out var settings)||settings.ValueKind!=JsonValueKind.Object)continue;
                            var model=Text(settings,"model")??"";
                            point=new(at,model,Text(settings,"service_tier"),Text(settings,"reasoning_effort"),false,true,true);
                        }
                        else if(type=="turn_context")
                        {
                            // v7 counter records intentionally retain only model. Their
                            // sidecar retains explicit thread/turn constraints, including
                            // contexts rejected below. A rejected foreign context must
                            // not reappear as an unconstrained model-only counter copy.
                            counterCopy=IsCounterCopy(row,payload,records is not null);
                            if(!counterCopy)authoritativeTurns.Add(at);
                            var thread=payload.Text("thread_id");
                            if(thread is not null&&thread!=owner)continue;
                            needsChildBoundary=parent is not null&&thread is null&&!IsChildTurn(payload.Text("turn_id"),owner);
                            var model=Text(payload,"model")??"";
                            point=new(at,model,Text(payload,"service_tier"),Effort(payload),true,
                                payload.TryGetProperty("service_tier",out _),payload.TryGetProperty("effort",out _)||payload.TryGetProperty("reasoning_effort",out _));
                        }
                        if(point is not null)
                            filePoints.Add((point,needsChildBoundary,counterCopy));
                    }
                    catch(Exception ex)when(ex is JsonException or InvalidOperationException or OverflowException){}
                }
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
            if(owner is not null)
            {
                if(untimedTurns.Any(p=>!p.CounterCopy||!authoritativeUntimedTurns.Contains(p.Stamp)))untimedOwners.Add(owner);
                if(!points.TryGetValue(owner,out var list))points[owner]=list=[];
                list.AddRange(filePoints.Where(p=>(!p.CounterCopy||!authoritativeTurns.Contains(p.Point.At))&&
                    (!p.NeedsChildBoundary||childBoundary is {} boundary&&
                    (boundaryExclusive?p.Point.At>boundary:p.Point.At>=boundary))).Select(p=>
                        // Without the source/sidecar, lost thread constraints cannot
                        // justify inheriting a previous Fast setting.
                        p.CounterCopy?p.Point with{Tier=null,Effort=null,HasTier=true,HasEffort=true}:p.Point));
            }
        }
        var result=new UsageModeTimeline();
        result.untimedOwners.UnionWith(untimedOwners);
        foreach(var (owner,values) in points)
        {
            Mode? defaults=null,activeTurn=null;var history=new List<Mode>();
            foreach(var group in values.Distinct().OrderBy(p=>p.At).GroupBy(p=>p.At))
            {
                ct.ThrowIfCancellationRequested();
                var settings=group.Where(p=>!p.Turn).ToArray();
                if(settings.Length>0)
                {
                    var model=settings.Select(p=>p.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var tier=settings.Select(p=>p.Tier).Distinct().ToArray();
                    var effort=settings.Select(p=>p.Effort).Distinct().ToArray();
                    defaults=model.Length==1?new(group.Key,model[0],tier.Length==1?tier[0]:null,effort.Length==1?effort[0]:null):new(group.Key,"",null,null);
                    // A turn can contain multiple requests. Without a new explicit context we cannot
                    // tell whether the next completion used the old or the newly requested tier.
                    if(activeTurn is not null&&(defaults.Model!=activeTurn.Model||!SameTier(defaults.Tier,activeTurn.Tier)))
                        activeTurn=activeTurn with{Tier=null};
                }
                var turns=group.Where(p=>p.Turn).ToArray();
                if(turns.Length>0)
                {
                    var model=turns.Select(p=>p.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var tier=turns.Where(p=>p.HasTier).Select(p=>p.Tier).Distinct().ToArray();
                    var effort=turns.Where(p=>p.HasEffort).Select(p=>p.Effort).Distinct().ToArray();
                    // A turn override is not a new global setting, but it can disprove
                    // the old fallback. Otherwise priority -> explicit default/null ->
                    // a tier-less context would silently resurrect the old Fast setting.
                    // Only a later settings event may establish that fallback again.
                    if(defaults is not null&&(model.Length!=1||model[0].Length==0||
                        !string.Equals(defaults.Model,model[0],StringComparison.OrdinalIgnoreCase)||
                        tier.Length>0&&(tier.Length!=1||tier[0] is null||!SameTier(tier[0],defaults.Tier))))
                        defaults=defaults with{Tier=null};
                    // Legacy counter records may contain model-only turn_context; sidecar metadata
                    // supplies its missing fields. Conflicting explicit values remain unknown.
                    activeTurn=model.Length==1?new(group.Key,model[0],
                        tier.Length==1?tier[0]:tier.Length>1?null:defaults?.Model==model[0]?defaults.Tier:null,
                        effort.Length==1?effort[0]:null):new(group.Key,"",null,null);
                }
                var effective=activeTurn??defaults;
                if(effective is not null)history.Add(effective with{At=group.Key});
            }
            result.modes[owner]=history;
        }
        return result;
    }

    internal PricingContext? Apply(UsageEvent item)
    {
        // A cumulative correction can span multiple requests/modes. Its last request must not
        // stamp the entire aggregate; preserve unknown instead of inventing a cost allocation.
        var pricing=item.Pricing;
        if(pricing?.RequestInputTokens is null||item.Timestamp is not {} at)return pricing;
        if(untimedOwners.Contains(item.Session))return pricing with{RequestedServiceTier=null,ReasoningEffort=null,ServiceTierEvidence="unknown"};
        if(!modes.TryGetValue(item.Session,out var timeline))return pricing;
        var lo=0;var hi=timeline.Count;
        while(lo<hi){var mid=lo+(hi-lo)/2;if(timeline[mid].At<=at)lo=mid+1;else hi=mid;}
        if(lo==0)return pricing;
        var mode=timeline[lo-1];
        if(!string.Equals(mode.Model,item.Model,StringComparison.OrdinalIgnoreCase))return pricing;
        return pricing with{RequestedServiceTier=mode.Tier,ReasoningEffort=mode.Effort,
            ServiceTierEvidence=mode.Tier is null?"unknown":"request-setting"};
    }
    private static string? Text(JsonElement value,string key)
    {
        var text=value.Text(key)?.Trim().ToLowerInvariant();
        return text is {Length:>0 and <=80}&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')?text:null;
    }
    private static bool IsCounterCopy(JsonElement row,JsonElement payload,bool indexed)=>indexed&&
        row.TryGetProperty("account_scope",out _)&&payload.EnumerateObject().All(field=>field.Name=="model");
    private static bool SameTier(string? a,string? b)
    {
        static string? Canonical(string? tier)=>tier switch{"default"=>"standard","priority"=>"fast",_=>tier};
        return Canonical(a)==Canonical(b);
    }
    private static string? Effort(JsonElement payload)
    {
        var effort=Text(payload,"effort");var alternate=Text(payload,"reasoning_effort");
        return payload.TryGetProperty("effort",out _)&&payload.TryGetProperty("reasoning_effort",out _)?
            effort==alternate?effort:null:effort??alternate;
    }
    private static bool IsChildTurn(string? turn,string owner)=>Guid.TryParse(turn,out _)&&Guid.TryParse(owner,out _)&&
        turn![14]=='7'&&owner[14]=='7'&&string.CompareOrdinal(turn,owner)>=0;
    private static async IAsyncEnumerable<string> Replay(IReadOnlyList<string> records,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
    {foreach(var record in records){ct.ThrowIfCancellationRequested();yield return record;}await Task.CompletedTask;}
}
