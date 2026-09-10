namespace UsageLoom.Core;

// No credentials or response bodies: only the fields required for reproducible pairing.
public sealed record QuotaObservation(DateTimeOffset At,string? Account,string? Plan,string PricingVersion,List<QuotaWindow> Windows,bool Barrier=false)
{
    public string? BarrierReason { get; init; }
}
public sealed record CapacityInterval(string Key,DateTimeOffset From,DateTimeOffset To,string Account,string Plan,DateTimeOffset Reset,double Percent,long Tokens,decimal Cost,long Priced,string[] EventIds,string? Exclusion);
public sealed record CapacityInterruption(string Key,DateTimeOffset From,DateTimeOffset To,string Reason);
public sealed record TemporalCapacityResult(CapacityCache? Cache,List<CapacityInterval> Intervals,DateTimeOffset? PendingFrom,List<CapacityCache> History)
{
    // Display-only anchors; unfinished intervals must not enter estimates or archives.
    public Dictionary<string,double> PendingBaselineUsed { get; init; } = [];
    public List<CapacityInterruption> Interruptions { get; init; } = [];
}

public static class TemporalCapacity
{
    public static TemporalCapacityResult CalculateStable(IEnumerable<QuotaObservation> observations,IEnumerable<UsageEvent> events,DateTimeOffset indexedThrough,CancellationToken cancellationToken=default,IReadOnlyList<UsageUncertainty>? uncertainRanges=null)
        =>Calculate(observations,events,cancellationToken,indexedThrough,uncertainRanges);
    public static TemporalCapacityResult Calculate(IEnumerable<QuotaObservation> observations,IEnumerable<UsageEvent> events,CancellationToken cancellationToken=default,DateTimeOffset? indexedThrough=null,IReadOnlyList<UsageUncertainty>? uncertainRanges=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered=observations.OrderBy(o=>o.At).DistinctBy(o=>o.At).ToArray();
        var stable=indexedThrough is not null;var version=stable?4:3;
        var confirmed=new HashSet<(DateTimeOffset,string)>();
        if(stable)
        {
            var future=new Dictionary<string,(QuotaObservation End,QuotaWindow Next)>();QuotaObservation? next=null;
            foreach(var o in ordered.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(o.Barrier||o.Account is null||o.Plan is null||o.PricingVersion!=Pricing.CatalogVersion){future.Clear();next=o;continue;}
                if(next is not null&&(next.Barrier||next.Account!=o.Account||next.Plan!=o.Plan||next.PricingVersion!=o.PricingVersion))future.Clear();
                foreach(var key in future.Keys.Where(k=>!o.Windows.Any(w=>w.Key==k)).ToArray())future.Remove(key);
                foreach(var w in o.Windows.Where(w=>w.IsPrimary&&w.Minutes==10080))
                {
                    if(!double.IsFinite(w.Used)||w.Used<0||w.Used>100||w.ResetsAt is not {} reset||reset<=o.At){future.Remove(w.Key);continue;}
                    if(!future.TryGetValue(w.Key,out var f)||f.Next.ResetsAt is not {} r||Math.Abs((r-reset).TotalMinutes)>2||f.End.At>=reset||f.Next.Used<w.Used)
                        future[w.Key]=(o,w);
                    else
                    {
                        if(f.End.At>=o.At.AddMinutes(2)&&indexedThrough>=o.At.AddMinutes(2))confirmed.Add((o.At,w.Key));
                        future[w.Key]=(f.End,w);
                    }
                }
                next=o;
            }
        }
        var allEvents=events.ToArray();
        var rows=allEvents.Where(e=>e.Timestamp is not null&&!string.Equals(e.Model,"gpt-5.3-codex-spark",StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e=>e.Id).OrderBy(e=>e.Timestamp).ToArray();
        int After(DateTimeOffset at)
        {
            int lo=0,hi=rows.Length;
            while(lo<hi){int mid=lo+(hi-lo)/2;if(rows[mid].Timestamp<=at)lo=mid+1;else hi=mid;}
            return lo;
        }
        var anchors=new Dictionary<string,(QuotaObservation Observation,QuotaWindow Window)>();
        var totals=new Dictionary<string,CapacitySample>();var intervals=new List<CapacityInterval>();
        var history=new Dictionary<string,CapacityCache>();
        var segments=new Dictionary<string,int>();var generation=0;
        // Completed blocks survive transient failures; unfinished blocks require stricter continuity evidence.
        var suspended=new List<(string Key,DateTimeOffset Reset,double Used,CapacitySample Sample,int Segment)>();
        var pendingAnchors=new List<(string Key,QuotaObservation Start,QuotaWindow Window,QuotaObservation End,QuotaWindow EndWindow,bool Timeout,DateTimeOffset InterruptedAt)>();
        void SuspendPending(string key,QuotaObservation end,bool timeout,DateTimeOffset? interruptedAt=null)
        {
            if(!anchors.TryGetValue(key,out var start)||end.Windows.FirstOrDefault(w=>w.Key==key) is not {} endWindow)return;
            pendingAnchors.RemoveAll(p=>p.Key==key&&p.Window.ResetsAt is {} r&&start.Window.ResetsAt is {} s&&Math.Abs((r-s).TotalMinutes)<=2);
            pendingAnchors.Add((key,start.Observation,start.Window,end,endWindow,timeout,interruptedAt??end.At));
        }
        var interruptions=new List<CapacityInterruption>();
        QuotaObservation? last=null;
        foreach(var observation in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var o=observation;
            // An unavailable identity during a transport failure is not evidence
            // of an account switch. Carry comparison context in memory only;
            // successful recovery must still verify the actual identity below.
            if(o.Barrier&&o.BarrierReason=="query-failure"&&last?.Account is not null&&last.Plan is not null&&
                (!last.Barrier||last.BarrierReason=="query-failure")&&o.PricingVersion==last.PricingVersion&&
                (o.Account is null||o.Account==last.Account)&&(o.Plan is null||o.Plan==last.Plan))
                o=o with{Account=o.Account??last.Account,Plan=o.Plan??last.Plan};
            if(o.Barrier&&o.BarrierReason=="query-failure"&&last is not null&&o.Account is not null&&
                o.Account==last.Account&&o.Plan==last.Plan&&o.PricingVersion==last.PricingVersion&&o.PricingVersion==Pricing.CatalogVersion)
            {
                foreach(var key in anchors.Keys.ToArray())SuspendPending(key,last,true,o.At);
                foreach(var pair in totals.Where(p=>p.Value.Samples>0))
                {
                    var used=last.Windows.FirstOrDefault(w=>w.Key==pair.Key)?.Used??pair.Value.LastUsed;
                    suspended.RemoveAll(s=>s.Key==pair.Key&&Math.Abs((s.Reset-pair.Value.ResetsAt).TotalMinutes)<=2);
                    suspended.Add((pair.Key,pair.Value.ResetsAt,used,pair.Value,segments[pair.Key]));
                }
                anchors.Clear();totals.Clear();last=o;continue;
            }
            if(o.Barrier||string.IsNullOrWhiteSpace(o.Account)||string.IsNullOrWhiteSpace(o.Plan)||o.PricingVersion!=Pricing.CatalogVersion)
            {anchors.Clear();totals.Clear();suspended.Clear();pendingAnchors.Clear();interruptions.Clear();last=o;continue;}
            if(last?.Account!=o.Account||last?.Plan!=o.Plan||last?.PricingVersion!=o.PricingVersion){anchors.Clear();totals.Clear();suspended.Clear();pendingAnchors.Clear();interruptions.Clear();}
            var keys=o.Windows.Select(w=>w.Key).ToHashSet();
            foreach(var key in anchors.Keys.Where(k=>!keys.Contains(k)).ToArray()){anchors.Remove(key);totals.Remove(key);pendingAnchors.RemoveAll(p=>p.Key==key);}
            foreach(var w in o.Windows.Where(w=>w.IsPrimary&&w.Minutes==10080))
            {
                if(w.ResetsAt is not {} reset||reset<=o.At||!double.IsFinite(w.Used)||w.Used<0||w.Used>100)
                {anchors.Remove(w.Key);totals.Remove(w.Key);pendingAnchors.RemoveAll(p=>p.Key==w.Key);continue;}
                if(!anchors.TryGetValue(w.Key,out var a)||a.Window.ResetsAt is not {} oldReset||
                    Math.Abs((oldReset-reset).TotalMinutes)>2||o.At>=oldReset||w.Used<a.Window.Used||
                    stable&&last?.Windows.FirstOrDefault(p=>p.Key==w.Key) is {} previousWindow&&w.Used<previousWindow.Used)
                {
                    var previous=last?.Windows.FirstOrDefault(p=>p.Key==w.Key);
                    var differentCycle=previous?.ResetsAt is {} priorReset&&Math.Abs((priorReset-reset).TotalMinutes)>2;
                    if(differentCycle&&last is not null)SuspendPending(w.Key,last,false);
                    if(differentCycle&&totals.TryGetValue(w.Key,out var completed)&&completed.Samples>0)
                    {
                        suspended.RemoveAll(s=>s.Key==w.Key&&Math.Abs((s.Reset-completed.ResetsAt).TotalMinutes)<=2);
                        suspended.Add((w.Key,completed.ResetsAt,previous!.Used,completed,segments[w.Key]));
                    }
                    var resume=suspended.FindLastIndex(s=>s.Key==w.Key&&s.Reset>o.At&&Math.Abs((s.Reset-reset).TotalMinutes)<=2);
                    totals.Remove(w.Key);segments[w.Key]=++generation;
                    if((differentCycle||last?.BarrierReason=="query-failure")&&resume>=0)
                    {
                        var saved=suspended[resume];suspended.RemoveAt(resume);
                        if(w.Used>=saved.Used)
                        {
                            totals[w.Key]=saved.Sample with{LastUsed=w.Used,ResetsAt=reset};
                            segments[w.Key]=saved.Segment;
                        }
                    }
                    anchors[w.Key]=(o,w);
                    var pendingIndex=pendingAnchors.FindLastIndex(p=>p.Key==w.Key&&p.Window.ResetsAt is {} r&&r>o.At&&Math.Abs((r-reset).TotalMinutes)<=2);
                    if((differentCycle||last?.BarrierReason=="query-failure")&&pendingIndex>=0)
                    {
                        var p=pendingAnchors[pendingIndex];pendingAnchors.RemoveAt(pendingIndex);
                        var elapsed=o.At-p.End.At;
                        var verified=rows[After(p.Start.At)..After(o.At)].All(e=>e.AccountScope==o.Account&&e.AccountAttribution!="restart-inferred");
                        var safe=stable&&confirmed.Contains((o.At,w.Key))&&p.Start.Account==o.Account&&p.Start.Plan==o.Plan&&
                            p.Start.PricingVersion==o.PricingVersion&&w.Used==p.EndWindow.Used&&elapsed>TimeSpan.Zero&&
                            elapsed<=TimeSpan.FromMinutes(60)&&(!p.Timeout||elapsed<=TimeSpan.FromMinutes(5)||
                                !allEvents.Any(e=>e.Timestamp>p.InterruptedAt&&e.Timestamp<=o.At))&&verified&&!allEvents.Any(e=>e.Timestamp is null)&&
                            uncertainRanges?.Any(r=>r.Overlaps(p.Start.At,o.At))!=true&&
                            (p.Timeout||!allEvents.Any(e=>e.Timestamp>p.End.At&&e.Timestamp<=o.At));
                        if(safe)anchors[w.Key]=(p.Start,p.Window);
                        else interruptions.Add(new(w.Key,p.Start.At,o.At,
                            !verified?"unverified-ownership":uncertainRanges?.Any(r=>r.Overlaps(p.Start.At,o.At))==true?"uncertain-history":
                            !confirmed.Contains((o.At,w.Key))?"awaiting-confirmation":"continuity-not-proven"));
                    }
                    continue;
                }
                var delta=w.Used-a.Window.Used;
                if(delta<=0)continue; // Keep flat observations, but pair the whole plateau when it moves.
                if(stable&&(delta<3||!confirmed.Contains((o.At,w.Key))))continue;
                var part=rows[After(a.Observation.At)..After(o.At)];
                string? exclusion=part.Length==0?"no-timed-usage":part.Any(e=>e.AccountScope!=o.Account||e.AccountAttribution=="restart-inferred")?"unverified-ownership":null;
                if(uncertainRanges?.Any(r=>r.Overlaps(a.Observation.At,o.At))==true)exclusion="uncertain-history";
                var tokens=part.Sum(e=>e.Tokens.Total);
                if(tokens<=0)exclusion??="no-timed-usage";
                var price=exclusion is null?Pricing.Summarize(part):null;
                intervals.Add(new(w.Key,a.Observation.At,o.At,o.Account!,o.Plan!,reset,delta,tokens,price?.Cost??0,price?.Priced??0,part.Select(e=>e.Id).ToArray(),exclusion));
                var t=totals.GetValueOrDefault(w.Key)??new(w.Key,w.Label,reset,w.Used,0,0,0,0,0,0);
                totals[w.Key]=t with{LastUsed=w.Used,ResetsAt=reset,Percent=t.Percent+(exclusion is null?delta:0),Tokens=checked(t.Tokens+(exclusion is null?tokens:0)),Cost=t.Cost+(price?.Cost??0),Priced=t.Priced+(price?.Priced??0),Samples=t.Samples+(exclusion is null?1:0),Excluded=t.Excluded+(exclusion is null?0:1)};
                if(stable&&price is {Priced:>0})
                {
                    var value=price.Cost*100m/(decimal)delta;
                    totals[w.Key]=totals[w.Key] with{RangeSamples=t.RangeSamples+1,DollarLow=t.DollarLow is {} low?Math.Min(low,value):value,DollarHigh=t.DollarHigh is {} high?Math.Max(high,value):value};
                }
                var updated=totals[w.Key];
                if(updated.Samples>0)history[segments[w.Key]+"|"+w.Key]=new(version,o.Account!,o.Plan!,Pricing.CatalogVersion,o.At,[updated]);
                anchors[w.Key]=(o,w);
            }
            last=o;
        }
        var cache=last is not null&&!last.Barrier&&last.Account is not null&&last.Plan is not null&&last.PricingVersion==Pricing.CatalogVersion?
            new CapacityCache(version,last.Account,last.Plan,Pricing.CatalogVersion,last.At,totals.Values.Where(t=>t.Samples>0).ToList()):null;
        return new(cache,intervals,anchors.Count>0?anchors.Values.Min(a=>a.Observation.At):null,history.Values.ToList())
        {PendingBaselineUsed=anchors.ToDictionary(p=>p.Key,p=>p.Value.Window.Used),Interruptions=interruptions};
    }
}
