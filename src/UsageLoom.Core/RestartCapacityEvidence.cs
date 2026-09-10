namespace UsageLoom.Core;

public sealed record RestartCapacityEvidence(string Account,DateTimeOffset Since,DateTimeOffset ClosedAt,DateTimeOffset OpenedAt,string[] Events,string Reason);

public static class RestartCapacityVerification
{
    // Upgrade only the calculation copy. Persisted ownership remains explicitly inferred.
    public static UsageEvent[] Verify(IEnumerable<UsageEvent> events,IEnumerable<QuotaObservation> observations,
        IEnumerable<RestartCapacityEvidence> evidence,DateTimeOffset indexedThrough)
    {
        var rows=events.ToArray();var ledger=observations.OrderBy(o=>o.At).ToArray();var accepted=new HashSet<string>();
        foreach(var proof in evidence)
        {
            if(proof.Reason!="restart-inferred"||string.IsNullOrWhiteSpace(proof.Account)||proof.Events.Length==0||
                proof.Since>proof.ClosedAt||proof.ClosedAt>proof.OpenedAt||proof.OpenedAt-proof.Since>TimeSpan.FromMinutes(15))continue;
            var before=ledger.LastOrDefault(o=>o.At<=proof.Since&&!o.Barrier);
            var after=ledger.FirstOrDefault(o=>o.At>=proof.OpenedAt&&!o.Barrier);
            if(before is null||after is null||proof.Since-before.At>TimeSpan.FromMinutes(5)||after.At-proof.OpenedAt>TimeSpan.FromMinutes(60)||
                indexedThrough<after.At.AddMinutes(2)||before.Account!=proof.Account||after.Account!=proof.Account||
                before.Plan!=after.Plan||before.PricingVersion!=Pricing.CatalogVersion||after.PricingVersion!=before.PricingVersion)continue;
            var window=before.Windows.FirstOrDefault(w=>w.IsPrimary&&w.Minutes==10080);
            bool Matches(QuotaObservation o)=>o.Account==proof.Account&&o.Plan==before.Plan&&o.PricingVersion==before.PricingVersion&&
                window?.ResetsAt is {} reset&&reset>o.At&&o.Windows.Any(w=>w.Key==window.Key&&w.ResetsAt is {} r&&Math.Abs((r-reset).TotalMinutes)<=2&&
                    double.IsFinite(w.Used)&&w.Used>=window.Used&&w.Used<=100);
            if(window is null||!double.IsFinite(window.Used)||window.Used<0||!Matches(after))continue;
            bool Soft(QuotaObservation o)=>o.Barrier&&o.BarrierReason=="query-failure"&&
                (o.Account is null||o.Account==proof.Account)&&(o.Plan is null||o.Plan==before.Plan)&&o.PricingVersion==before.PricingVersion;
            if(ledger.Any(o=>o.At>before.At&&o.At<=after.At&&!Soft(o)&&(o.Barrier||!Matches(o))))continue;
            var confirmation=ledger.FirstOrDefault(o=>o.At>=after.At.AddMinutes(2)&&!o.Barrier&&Matches(o));
            if(confirmation is null||indexedThrough<confirmation.At||ledger.Any(o=>o.At>after.At&&o.At<=confirmation.At&&!Soft(o)&&(o.Barrier||!Matches(o))))continue;
            var used=window.Used;var rollback=false;
            foreach(var o in ledger.Where(o=>o.At>before.At&&o.At<=confirmation.At&&!Soft(o)))
            {
                var current=o.Windows.First(w=>w.Key==window.Key).Used;
                if(current<used){rollback=true;break;}used=current;
            }
            if(rollback)continue;
            var ids=proof.Events.ToHashSet();var selected=rows.Where(e=>ids.Contains(e.Id)).ToArray();
            if(selected.Select(e=>e.Id).Distinct().Count()!=ids.Count||selected.Any(e=>e.AccountScope!=proof.Account||e.Timestamp is null||e.Timestamp<=proof.Since||e.Timestamp>proof.OpenedAt))continue;
            foreach(var e in selected.Where(e=>e.AccountAttribution=="restart-inferred"))accepted.Add(e.Id);
        }
        return rows.Select(e=>accepted.Contains(e.Id)?e with{AccountAttribution="restart-verified"}:e).ToArray();
    }
}
