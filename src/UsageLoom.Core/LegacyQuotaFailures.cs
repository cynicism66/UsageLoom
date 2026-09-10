namespace UsageLoom.Core;

public static class LegacyQuotaFailures
{
    // Exact, timestamp-correlated known failures provide evidence. Old disconnects
    // were mislabeled explicit; unrelated explicit boundaries remain untouched.
    public static List<QuotaObservation> Recover(IEnumerable<QuotaObservation> observations,IEnumerable<string> logLines)
    {
        var times=new List<(DateTimeOffset At,bool Disconnected)>();
        foreach(var line in logLines)
        {
            const string marker=" [WARN] Quota ";var split=line.IndexOf(marker,StringComparison.Ordinal);
            if(split<0||!DateTimeOffset.TryParse(line[..split],out var at))continue;
            var message=line[(split+marker.Length)..].Trim();
            if(message is "Codex 额度服务响应超时，请稍后重试" or "request timed out")times.Add((at,false));
            if(message is "app-server 连接已断开" or "app-server disconnected")times.Add((at,true));
        }
        var result=observations.OrderBy(o=>o.At).ToList();
        for(var i=1;i<result.Count;i++)
        {
            var o=result[i];var prior=result[i-1];
            if(o.Barrier&&o.BarrierReason is null or "explicit-boundary"&&o.Account is null&&o.Plan is null&&o.Windows.Count==0&&
                prior.Account is not null&&prior.Plan is not null&&(!prior.Barrier||prior.BarrierReason=="query-failure")&&
                prior.PricingVersion==o.PricingVersion&&times.Any(t=>t.At>=o.At&&t.At-o.At<TimeSpan.FromSeconds(1)&&(o.BarrierReason is null||t.Disconnected)))
                result[i]=o with{Account=prior.Account,Plan=prior.Plan,BarrierReason="query-failure"};
        }
        return result;
    }
}
