namespace UsageLoom.Core;

[Flags]
public enum QuotaConsumptionMissing { None=0,Barrier=1,Gap=2,ResetTail=4,ResetUnknown=8,Regression=16 }
public sealed record QuotaConsumptionBucket(double? Points,QuotaConsumptionMissing Missing)
{
    public bool Partial=>Missing!=QuotaConsumptionMissing.None;
}

// This is quota percentage-point evidence, independent of the local Token ledger.
public static class QuotaConsumption
{
    private sealed record Point(DateTimeOffset At,double? Used,DateTimeOffset? Reset,bool Barrier,string? Identity);
    private sealed class Total {internal bool Seen;internal double Points;internal QuotaConsumptionMissing Missing;}

    public static IReadOnlyList<QuotaConsumptionBucket> Codex(IEnumerable<QuotaObservation> observations,string? account,
        int minutes,IReadOnlyList<HistoryTrendBucket> buckets)
    {
        if(string.IsNullOrWhiteSpace(account)||minutes<=0)return Empty(buckets);
        var rows=CapacityObservations.Prepare(observations,minutes);
        var points=rows.Select(row=>
        {
            var candidates=row.Windows.Where(window=>window.IsPrimary&&window.Minutes==minutes).ToArray();
            var window=candidates.Length==1?candidates[0]:null;
            var identity=row.Account==account?row.Account+"|"+row.Plan+"|"+row.PricingVersion:null;
            return new Point(row.At,window?.Used,window?.ResetsAt,row.Barrier||identity is null||window is null,identity);
        });
        return Calculate(points,TimeSpan.FromMinutes(minutes),null,buckets);
    }

    public static IReadOnlyList<QuotaConsumptionBucket> Claude(IEnumerable<ClaudeQuotaObservation> observations,string? scope,
        string window,IReadOnlyList<HistoryTrendBucket> buckets)
    {
        if(string.IsNullOrWhiteSpace(scope))return Empty(buckets);
        var duration=window switch{"five_hour"=>TimeSpan.FromHours(5),"seven_day"=>TimeSpan.FromDays(7),_=>TimeSpan.Zero};
        if(duration<=TimeSpan.Zero)return Empty(buckets);
        // Include other scopes as explicit boundaries; never join two same-scope
        // points across an intervening source switch.
        var points=ClaudeQuotaHistory.Normalize(observations).Where(point=>point.Window==window).Select(point=>
            new Point(point.At,point.Used,point.ResetsAt,point.Scope!=scope||point.Used is null,point.Scope));
        return Calculate(points,duration,ClaudeQuotaHistory.MaximumGap,buckets);
    }

    private static IReadOnlyList<QuotaConsumptionBucket> Empty(IReadOnlyList<HistoryTrendBucket> buckets)=>
        buckets.Select(_=>new QuotaConsumptionBucket(null,QuotaConsumptionMissing.None)).ToArray();

    private static IReadOnlyList<QuotaConsumptionBucket> Calculate(IEnumerable<Point> points,TimeSpan duration,TimeSpan? maximumGap,
        IReadOnlyList<HistoryTrendBucket> buckets)
    {
        var totals=Enumerable.Range(0,buckets.Count).Select(_=>new Total()).ToArray();
        int Index(DateTimeOffset at)
        {
            var local=at.LocalDateTime;var day=DateOnly.FromDateTime(local);
            for(var i=0;i<buckets.Count;i++)
                if(day>=buckets[i].From&&day<=buckets[i].Through&&
                    (buckets[i].Hour is null||buckets[i].Hour==local.Hour))return i;
            return -1;
        }
        Point? previous=null;var interrupted=false;
        foreach(var point in points.OrderBy(p=>p.At))
        {
            var index=Index(point.At);
            if(point.Barrier||point.Used is not {} used||!double.IsFinite(used)||used<0||used>100||
                point.Reset is not {} reset||reset<=point.At)
            {
                if(index>=0)totals[index].Missing|=QuotaConsumptionMissing.Barrier;
                previous=null;interrupted=true;continue;
            }
            if(index>=0)
            {
                totals[index].Seen=true;
                if(interrupted)totals[index].Missing|=QuotaConsumptionMissing.Barrier;
            }
            interrupted=false;
            if(previous is not {} prior){previous=point;continue;}
            if(prior.Identity!=point.Identity)
            {
                if(index>=0)totals[index].Missing|=QuotaConsumptionMissing.Barrier;
                previous=point;continue;
            }
            if(maximumGap is {} gap&&point.At-prior.At>gap)
            {
                if(index>=0)totals[index].Missing|=QuotaConsumptionMissing.Gap;
                previous=point;continue;
            }
            if(prior.Reset==reset)
            {
                var increase=used-prior.Used!.Value;
                if(increase<0)
                {
                    if(index>=0)totals[index].Missing|=QuotaConsumptionMissing.Regression;
                    previous=null;interrupted=true;continue;
                }
                // A pair spanning buckets belongs wholly to the later observation's bucket.
                if(index>=0)totals[index].Points+=increase;
            }
            else if(prior.Reset is {} oldReset&&reset==oldReset+duration&&prior.At<oldReset&&point.At>=oldReset)
            {
                // Adjacent cycles alone justify a zero baseline for the new cycle.
                if(index>=0)
                {
                    totals[index].Points+=used;
                    totals[index].Missing|=QuotaConsumptionMissing.ResetTail;
                }
                var oldIndex=Index(oldReset.AddTicks(-1));
                if(oldIndex>=0)totals[oldIndex].Missing|=QuotaConsumptionMissing.ResetTail;
            }
            else if(index>=0)totals[index].Missing|=QuotaConsumptionMissing.ResetUnknown;
            previous=point;
        }
        return totals.Select(total=>new QuotaConsumptionBucket(total.Seen?total.Points:null,total.Missing)).ToArray();
    }
}
