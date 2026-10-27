namespace UsageLoom.Core;

public sealed record TrayQuotaWindow(double Remaining,DateTimeOffset? Reset,QuotaPaceKind Pace);
// StaleSince is the observation time of a snapshot older than the freshness limit; its values are the last known ones.
public sealed record TrayQuotaPresentation(string Source,TrayQuotaWindow? FiveHour,TrayQuotaWindow? Weekly,DateTimeOffset? StaleSince=null)
{
    public TrayQuotaWindow? MostConstrained=>new[]{FiveHour,Weekly}.Where(window=>window is not null).OrderBy(window=>window!.Remaining).FirstOrDefault();
    public int? DisplayRemaining=>MostConstrained is {} window?(int)Math.Clamp(Math.Round(window.Remaining,MidpointRounding.AwayFromZero),0,100):null;
    public bool DisplayStale=>StaleSince is not null&&MostConstrained is not null;
}

public static class TrayQuotaSelection
{
    // Beyond one weekly cycle every window has reset, so a kept value would only be an inferred 100%.
    public static readonly TimeSpan ClaudeStaleLimit=TimeSpan.FromDays(7);
    public static string Normalize(string? value)=>value is "codex" or "claude" or "tightest"?value:"auto";
    public static TrayQuotaPresentation Select(string? choice,bool codexEnabled,bool claudeEnabled,
        QuotaState codex,ClaudeQuotaSnapshot claude,DateTimeOffset now)
    {
        var fromCodex=Codex(codex,codexEnabled,now);
        var fromClaude=Claude(claude,claudeEnabled,now);
        return Normalize(choice) switch
        {
            "codex"=>fromCodex,
            "claude"=>fromClaude,
            "tightest"=>new[]{fromCodex,fromClaude}.Where(item=>item.MostConstrained is not null)
                .OrderBy(item=>item.MostConstrained!.Remaining).FirstOrDefault()??new("none",null,null),
            _=>codexEnabled?fromCodex:fromClaude
        };
    }
    private static TrayQuotaPresentation Codex(QuotaState state,bool enabled,DateTimeOffset now)
    {
        TrayQuotaWindow? Find(int minutes)
        {
            if(!enabled||!state.Fresh||state.IsLocalAccount||string.IsNullOrWhiteSpace(state.AccountKey))return null;
            var window=state.PrimaryWindows.FirstOrDefault(item=>item.Minutes==minutes);
            if(window is null||!double.IsFinite(window.Used)||window.Used<0||window.Used>100||window.ResetsAt is {} reset&&reset<=now)return null;
            return new(window.Remaining,window.ResetsAt,QuotaPace.ForCodex(state,window,enabled,now));
        }
        return new("codex",Find(300),Find(10080));
    }
    // Claude snapshots only refresh while Claude itself runs, so an older snapshot keeps its last values
    // (shown dimmed) and a window whose reset time has passed counts as fully remaining.
    private static TrayQuotaPresentation Claude(ClaudeQuotaSnapshot snapshot,bool enabled,DateTimeOffset now)
    {
        var age=snapshot.ObservedAt is {} at?now-at:(TimeSpan?)null;
        var usable=enabled&&snapshot.Status=="snapshot"&&!snapshot.HistoryOnly&&!string.IsNullOrWhiteSpace(snapshot.Scope)&&age>=TimeSpan.Zero&&age<=ClaudeStaleLimit;
        TrayQuotaWindow? Find(string key)
        {
            if(!usable)return null;
            var window=snapshot.Windows.FirstOrDefault(item=>item.Key==key);
            if(window is null||!double.IsFinite(window.Used)||window.Used<0||window.Used>100)return null;
            if(window.Expired(now))return new(100,null,QuotaPaceKind.Unavailable);
            return new(Math.Clamp(100-window.Used,0,100),window.ResetsAt,QuotaPace.ForClaude(snapshot,window,enabled,now));
        }
        return new("claude",Find("five_hour"),Find("seven_day"),usable&&age>QuotaPace.ClaudeFreshness?snapshot.ObservedAt:null);
    }
}
