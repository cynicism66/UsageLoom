namespace UsageLoom.Core;

// No credentials or response bodies: only the fields required for reproducible pairing.
public sealed record QuotaObservation(DateTimeOffset At,string? Account,string? Plan,string PricingVersion,List<QuotaWindow> Windows,bool Barrier=false);
public sealed record CapacityInterval(string Key,DateTimeOffset From,DateTimeOffset To,string Account,string Plan,DateTimeOffset Reset,double Percent,long Tokens,decimal Cost,long Priced,string[] EventIds,string? Exclusion);
public sealed record TemporalCapacityResult(CapacityCache? Cache,List<CapacityInterval> Intervals,DateTimeOffset? PendingFrom,List<CapacityCache> History);

public static class TemporalCapacity
{
    public static TemporalCapacityResult Calculate(IEnumerable<QuotaObservation> observations,IEnumerable<UsageEvent> events)
    {
        var rows=events.Where(e=>e.Timestamp is not null&&!string.Equals(e.Model,"gpt-5.3-codex-spark",StringComparison.OrdinalIgnoreCase))
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
        QuotaObservation? last=null;
        foreach(var o in observations.OrderBy(o=>o.At).DistinctBy(o=>o.At))
        {
            if(o.Barrier||string.IsNullOrWhiteSpace(o.Account)||string.IsNullOrWhiteSpace(o.Plan)||o.PricingVersion!=Pricing.CatalogVersion)
            {anchors.Clear();totals.Clear();last=o;continue;}
            if(last?.Account!=o.Account||last?.Plan!=o.Plan||last?.PricingVersion!=o.PricingVersion){anchors.Clear();totals.Clear();}
            var keys=o.Windows.Select(w=>w.Key).ToHashSet();
            foreach(var key in anchors.Keys.Where(k=>!keys.Contains(k)).ToArray()){anchors.Remove(key);totals.Remove(key);}
            foreach(var w in o.Windows.Where(w=>w.IsPrimary&&w.Minutes==10080))
            {
                if(w.ResetsAt is not {} reset||reset<=o.At||!double.IsFinite(w.Used)||w.Used<0||w.Used>100)
                {anchors.Remove(w.Key);totals.Remove(w.Key);continue;}
                if(!anchors.TryGetValue(w.Key,out var a)||a.Window.ResetsAt is not {} oldReset||
                    Math.Abs((oldReset-reset).TotalMinutes)>2||o.At>=oldReset||w.Used<a.Window.Used)
                {anchors[w.Key]=(o,w);totals.Remove(w.Key);segments[w.Key]=++generation;continue;}
                var delta=w.Used-a.Window.Used;
                if(delta<=0)continue; // Keep flat observations, but pair the whole plateau when it moves.
                var part=rows[After(a.Observation.At)..After(o.At)];
                string? exclusion=part.Length==0?"no-timed-usage":part.Any(e=>e.AccountScope!=o.Account||e.AccountAttribution=="restart-inferred")?"unverified-ownership":null;
                var tokens=part.Sum(e=>e.Tokens.Total);
                if(tokens<=0)exclusion??="no-timed-usage";
                var price=exclusion is null?Pricing.Summarize(part):null;
                intervals.Add(new(w.Key,a.Observation.At,o.At,o.Account!,o.Plan!,reset,delta,tokens,price?.Cost??0,price?.Priced??0,part.Select(e=>e.Id).ToArray(),exclusion));
                var t=totals.GetValueOrDefault(w.Key)??new(w.Key,w.Label,reset,w.Used,0,0,0,0,0,0);
                totals[w.Key]=t with{LastUsed=w.Used,ResetsAt=reset,Percent=t.Percent+(exclusion is null?delta:0),Tokens=checked(t.Tokens+(exclusion is null?tokens:0)),Cost=t.Cost+(price?.Cost??0),Priced=t.Priced+(price?.Priced??0),Samples=t.Samples+(exclusion is null?1:0),Excluded=t.Excluded+(exclusion is null?0:1)};
                var updated=totals[w.Key];
                if(updated.Samples>0)history[segments[w.Key]+"|"+w.Key]=new(3,o.Account!,o.Plan!,Pricing.CatalogVersion,o.At,[updated]);
                anchors[w.Key]=(o,w);
            }
            last=o;
        }
        var cache=last is not null&&!last.Barrier&&last.Account is not null&&last.Plan is not null&&last.PricingVersion==Pricing.CatalogVersion?
            new CapacityCache(3,last.Account,last.Plan,Pricing.CatalogVersion,last.At,totals.Values.Where(t=>t.Samples>0).ToList()):null;
        return new(cache,intervals,anchors.Count>0?anchors.Values.Min(a=>a.Observation.At):null,history.Values.ToList());
    }
}
