namespace UsageLoom.Core;

// Normalize only calculation copies. Never synthesize a successful quota response.
public static class CapacityObservations
{
    public static bool Soft(QuotaObservation o)=>o.Barrier&&o.BarrierReason is "query-failure" or "snapshot-unavailable";
    public static QuotaObservation[] Prepare(IEnumerable<QuotaObservation> input)
    {
        var rows=input.OrderBy(o=>o.At).GroupBy(o=>o.At).Select(g=>
        {
            var first=g.First();
            // Conflicting responses at one instant are not independent confirmation.
            return g.All(o=>o.Account==first.Account&&o.Plan==first.Plan&&o.PricingVersion==first.PricingVersion&&
                o.Barrier==first.Barrier&&o.BarrierReason==first.BarrierReason&&o.Windows.SequenceEqual(first.Windows))?
                first:first with{Barrier=true,BarrierReason="conflicting-snapshot",Windows=[]};
        }).ToArray();
        QuotaObservation? previous=null;
        for(var i=0;i<rows.Length;i++)
        {
            var o=rows[i];
            if(o.Barrier){if(!Soft(o))previous=null;continue;}
            var weekly=o.Windows.Where(w=>w.IsPrimary&&w.Minutes==10080).ToArray();
            var invalid=weekly.Length==0||weekly.Any(w=>!double.IsFinite(w.Used)||w.Used<0||w.Used>100||w.ResetsAt is null||w.ResetsAt<=o.At);
            // Isolate a transient regression only if a later same-identity response
            // returns to the old, still-live cycle. The gap itself is not bridged.
            var regression=previous is not null&&SameIdentity(previous,o)&&weekly.Any(w=>previous.Windows.Any(p=>
                p.Key==w.Key&&SameCycle(p,w)&&w.Used<p.Used));
            var recovers=false;var confirmedLower=false;
            if(regression)
                for(var j=i+1;j<rows.Length&&rows[j].At-o.At<=TimeSpan.FromHours(1);j++)
                {
                    var next=rows[j];
                    if(next.Barrier){if(Soft(next))continue;break;}
                    if(!SameIdentity(o,next))break;
                    if(previous!.Windows.All(p=>p.ResetsAt>next.At&&next.Windows.Any(w=>w.Key==p.Key&&SameCycle(p,w)&&w.Used>=p.Used&&w.Used<=100))){recovers=true;break;}
                    if(next.At>=o.At.AddMinutes(2)&&weekly.All(p=>p.ResetsAt>next.At&&next.Windows.Any(w=>w.Key==p.Key&&SameCycle(p,w)&&double.IsFinite(w.Used)&&w.Used>=p.Used&&w.Used<=100)))confirmedLower=true;
                }
            if(invalid||regression&&(!confirmedLower||recovers))rows[i]=o with{Barrier=true,BarrierReason="snapshot-unavailable",Windows=[]};
            else previous=o;
        }
        return rows;
    }
    private static bool SameIdentity(QuotaObservation a,QuotaObservation b)=>a.Account==b.Account&&a.Plan==b.Plan&&a.PricingVersion==b.PricingVersion;
    private static bool SameCycle(QuotaWindow a,QuotaWindow b)=>a.ResetsAt is {} x&&b.ResetsAt is {} y&&Math.Abs((x-y).TotalMinutes)<=2;
}
