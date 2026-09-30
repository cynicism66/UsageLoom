namespace UsageLoom.Core;

public enum QuotaPaceKind { Unavailable, Normal, Fast, Exhausted }

// A comparison of a local quota snapshot with elapsed window time, not a forecast.
public static class QuotaPace
{
    public const double FastThresholdPoints=20;
    public static readonly TimeSpan ClaudeFreshness=TimeSpan.FromMinutes(15);

    public static QuotaPaceKind ForCodex(QuotaState state,QuotaWindow window,bool enabled,DateTimeOffset now)=>
        Evaluate(window.Used,window.Minutes,window.ResetsAt,enabled&&state.Fresh&&
            !state.IsLocalAccount&&!string.IsNullOrWhiteSpace(state.AccountKey),now);

    public static QuotaPaceKind ForClaude(ClaudeQuotaSnapshot snapshot,ClaudeQuotaWindow window,bool enabled,DateTimeOffset now)
    {
        var minutes=window.Key switch{"five_hour"=>300,"seven_day"=>10080,_=>0};
        var age=snapshot.ObservedAt is {} at?now-at:TimeSpan.MaxValue;
        var identified=!string.IsNullOrWhiteSpace(snapshot.Scope)&&snapshot.Status=="snapshot"&&!snapshot.HistoryOnly;
        return Evaluate(window.Used,minutes,window.ResetsAt,enabled&&identified&&age>=TimeSpan.Zero&&age<=ClaudeFreshness,now);
    }

    public static QuotaPaceKind Evaluate(double used,int minutes,DateTimeOffset? resetsAt,bool trustworthy,DateTimeOffset now)
    {
        if(!trustworthy||minutes<=0||resetsAt is not {} reset||reset<=now||!double.IsFinite(used)||used<0||used>100)
            return QuotaPaceKind.Unavailable;
        var rawProgress=1-(reset-now).TotalMinutes/minutes;
        // Values outside the window imply an uncertain period, not a normal pace.
        if(!double.IsFinite(rawProgress)||rawProgress<0||rawProgress>1)return QuotaPaceKind.Unavailable;
        var progress=Math.Clamp(rawProgress,0,1);
        if(used>=100)return QuotaPaceKind.Exhausted;
        return used-progress*100>FastThresholdPoints?QuotaPaceKind.Fast:QuotaPaceKind.Normal;
    }
}
