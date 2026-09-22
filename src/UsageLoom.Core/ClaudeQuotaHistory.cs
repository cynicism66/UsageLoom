namespace UsageLoom.Core;

// Observed quota percentages, never tokens, requests or an inferred account identity.
public sealed record ClaudeQuotaObservation(string Scope,DateTimeOffset At,string Window,double? Used,
    DateTimeOffset? ResetsAt,string Origin);
public sealed record ClaudeQuotaStep(ClaudeQuotaObservation Point,bool Connected,double? Increase);

public static class ClaudeQuotaHistory
{
    public static readonly TimeSpan MaximumGap=TimeSpan.FromMinutes(15);
    public static IEnumerable<ClaudeQuotaObservation> Observations(ClaudeQuotaSnapshot snapshot)
    {
        foreach(var point in snapshot.History)yield return point;
        if(snapshot.Status!="snapshot"||snapshot.Scope is not {} scope||snapshot.ObservedAt is not {} at)yield break;
        foreach(var w in snapshot.Windows)
            yield return new(scope,at,w.Key,w.Used,w.ResetsAt,snapshot.HistoryOnly?"history":"cache");
    }

    public static bool Valid(ClaudeQuotaObservation p)=>!string.IsNullOrWhiteSpace(p.Scope)&&p.Scope.Length<=128&&
        p.Window is "five_hour" or "seven_day"&&p.Origin is "history" or "cache" or "conflict"&&
        (p.Used is {} n?double.IsFinite(n)&&n>=0&&n<=100:p.Origin=="conflict")&&
        (p.ResetsAt is not {} reset||reset>=p.At.AddDays(-8)&&reset<=p.At.AddDays(p.Window=="five_hour"?0:7).AddHours(p.Window=="five_hour"?5:0).AddMinutes(5));

    public static IReadOnlyList<ClaudeQuotaObservation> Normalize(IEnumerable<ClaudeQuotaObservation> points)
    {
        var result=new List<ClaudeQuotaObservation>();
        foreach(var group in points.Where(Valid).GroupBy(p=>(p.Scope,At:p.At.ToUniversalTime(),p.Window)))
        {
            var first=group.First();var used=group.Select(p=>p.Used).Distinct().ToArray();
            var resets=group.Where(p=>p.ResetsAt is not null).Select(p=>p.ResetsAt!.Value.ToUniversalTime()).Order().ToArray();
            if(group.Any(p=>p.Origin=="conflict")||used.Length!=1||resets.Length>1&&(resets[^1]-resets[0]).TotalSeconds>=1)
                result.Add(first with{At=group.Key.At,Used=null,ResetsAt=null,Origin="conflict"});
            else result.Add(first with{At=group.Key.At,ResetsAt=resets.Length>0?resets[0]:null,Origin=group.Any(p=>p.Origin=="cache")?"cache":"history"});
        }
        return result.OrderBy(p=>p.At).ThenBy(p=>p.Scope,StringComparer.Ordinal).ThenBy(p=>p.Window,StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<ClaudeQuotaStep> Series(IEnumerable<ClaudeQuotaObservation> points,string scope,string window,
        DateTimeOffset from,DateTimeOffset through)
    {
        var ordered=Normalize(points.Where(p=>p.Scope==scope&&p.Window==window&&p.At>=from&&p.At<=through));
        var result=new List<ClaudeQuotaStep>();ClaudeQuotaObservation? previous=null;
        foreach(var p in ordered)
        {
            var connected=previous is not null&&previous.Used is {} old&&p.Used is {} used&&used>=old&&
                p.At>previous.At&&p.At-previous.At<=MaximumGap&&
                previous.ResetsAt is {} oldReset&&p.ResetsAt is {} reset&&Math.Abs((oldReset-reset).TotalSeconds)<1&&
                previous.At<oldReset&&p.At<reset;
            result.Add(new(p,connected,connected?p.Used-previous!.Used:null));previous=p;
        }
        return result;
    }
}
