namespace UsageLoom.Core;

public static class LegacyQuotaFailures
{
    // Old barriers contain no reason. Only exact, timestamp-correlated timeout
    // messages provide evidence; unknown barriers remain hard boundaries.
    public static List<QuotaObservation> Recover(IEnumerable<QuotaObservation> observations,IEnumerable<string> logLines)
    {
        var times=new List<DateTimeOffset>();
        foreach(var line in logLines)
        {
            const string marker=" [WARN] Quota ";var split=line.IndexOf(marker,StringComparison.Ordinal);
            if(split<0||!DateTimeOffset.TryParse(line[..split],out var at))continue;
            var message=line[(split+marker.Length)..].Trim();
            if(message is "Codex 额度服务响应超时，请稍后重试" or "request timed out")times.Add(at);
        }
        var result=observations.OrderBy(o=>o.At).ToList();
        for(var i=1;i<result.Count;i++)
        {
            var o=result[i];var prior=result[i-1];
            if(o.Barrier&&o.BarrierReason is null&&o.Account is null&&o.Plan is null&&o.Windows.Count==0&&
                prior.Account is not null&&prior.Plan is not null&&(!prior.Barrier||prior.BarrierReason=="query-failure")&&
                prior.PricingVersion==o.PricingVersion&&times.Any(at=>at>=o.At&&at-o.At<TimeSpan.FromSeconds(1)))
                result[i]=o with{Account=prior.Account,Plan=prior.Plan,BarrierReason="query-failure"};
        }
        return result;
    }
}
