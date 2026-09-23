namespace UsageLoom.Core;

// A workload comparison, not a subscription token allowance. Claude's percentage includes
// Chat, Cowork, cloud sessions and other devices; local Code transcripts do not.
public sealed record ClaudeWeeklyCapacity(string Status,double PercentagePoints,int Intervals,decimal LocalTokens,
    decimal ProjectedTokens,decimal LocalApiEquivalent,decimal ProjectedApiEquivalent,double PricingCoverage,
    DateTimeOffset? ResetsAt)
{
    public bool Ready=>Status=="ready";
    public bool HasApiEquivalent=>Ready&&PricingCoverage>=95;
    public static ClaudeWeeklyCapacity Unavailable(string status,DateTimeOffset? resetsAt=null,double points=0,int intervals=0)=>
        new(status,points,intervals,0,0,0,0,0,resetsAt);
}

public static class ClaudeWeeklyCapacityEstimator
{
    private const double MinimumPoints=5;
    private const int MinimumIntervals=2;
    private const double MinimumBucketPoints=1;
    private const double MaximumUnmatchedPoints=1;
    private const decimal MaximumRatio=3;

    public static ClaudeWeeklyCapacity Estimate(ClaudeQuotaSnapshot quota,ClaudeCodeSnapshot code,DateTimeOffset now)
    {
        if(quota.Status!="snapshot"||quota.Scope is null||quota.ObservedAt is not {} observed||
            observed>now.AddMinutes(2)||now-observed>TimeSpan.FromMinutes(15))
            return ClaudeWeeklyCapacity.Unavailable("quotaUnavailable");
        var week=quota.Windows.FirstOrDefault(w=>w.Key=="seven_day");
        if(week?.ResetsAt is not {} reset||reset<=now||reset>now.AddDays(7).AddMinutes(5))
            return ClaudeWeeklyCapacity.Unavailable("resetUnavailable");
        if(code.Status is not ("ready" or "partial")||code.Rows.Count==0)
            return ClaudeWeeklyCapacity.Unavailable("codeUnavailable",reset);
        if(code.Status=="partial"||code.Skipped>0)
            return ClaudeWeeklyCapacity.Unavailable("partialLogs",reset);

        var cycleStart=reset.AddDays(-7);
        var points=ClaudeQuotaHistory.Normalize(ClaudeQuotaHistory.Observations(quota).Where(p=>
            p.Scope==quota.Scope&&p.Window=="seven_day"&&p.At>=cycleStart&&p.At<=observed));
        bool SameReset(ClaudeQuotaObservation p)=>p.ResetsAt is null||Math.Abs((p.ResetsAt.Value-reset).TotalSeconds)<1;
        var rows=code.Rows.Where(r=>r.At>cycleStart&&r.At<=observed).OrderBy(r=>r.At).ToArray();
        var selected=new List<ClaudeCodeUsage>();
        var bucketRows=new List<ClaudeCodeUsage>();
        var ratios=new List<decimal>();
        ClaudeQuotaObservation? previous=null;
        double matchedPoints=0,unmatchedPoints=0,bucketPoints=0;
        var rowIndex=0;
        void FlushBucket()
        {
            if(bucketPoints>=MinimumBucketPoints&&bucketRows.Count>0)
            {
                var tokens=bucketRows.Sum(r=>(decimal)r.Total);
                selected.AddRange(bucketRows);
                matchedPoints+=bucketPoints;
                ratios.Add(tokens/(decimal)bucketPoints);
            }
            bucketRows.Clear();bucketPoints=0;
        }
        foreach(var point in points)
        {
            if(previous is not null&&previous.Used is {} old&&point.Used is {} used&&SameReset(previous)&&SameReset(point)&&
                point.At>previous.At&&point.At-previous.At<=ClaudeQuotaHistory.MaximumGap&&used>=old)
            {
                var increase=used-old;
                while(rowIndex<rows.Length&&rows[rowIndex].At<=previous.At)rowIndex++;
                var start=rowIndex;
                while(rowIndex<rows.Length&&rows[rowIndex].At<=point.At)rowIndex++;
                if(increase>0&&start==rowIndex&&bucketRows.Count==0)unmatchedPoints+=increase;
                else
                {
                    bucketPoints+=increase;
                    for(var i=start;i<rowIndex;i++)bucketRows.Add(rows[i]);
                    if(bucketPoints>=MinimumBucketPoints)FlushBucket();
                }
            }
            else FlushBucket();
            previous=point;
        }
        FlushBucket();
        if(unmatchedPoints>MaximumUnmatchedPoints)
            return ClaudeWeeklyCapacity.Unavailable("otherUsage",reset,matchedPoints,ratios.Count);
        if(matchedPoints<MinimumPoints||ratios.Count<MinimumIntervals)
            return ClaudeWeeklyCapacity.Unavailable("insufficient",reset,matchedPoints,ratios.Count);
        if(ratios.Min()<=0||ratios.Max()/ratios.Min()>MaximumRatio)
            return ClaudeWeeklyCapacity.Unavailable("unstable",reset,matchedPoints,ratios.Count);

        var tokens=selected.Sum(r=>(decimal)r.Total);
        var price=ClaudeCodePricing.Summarize(selected);
        return new("ready",matchedPoints,ratios.Count,tokens,tokens*100/(decimal)matchedPoints,
            price.Cost,price.Cost*100/(decimal)matchedPoints,price.Coverage,reset);
    }
}
