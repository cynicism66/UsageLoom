namespace UsageLoom.Core;

public sealed record TrayQuotaWindow(double Remaining,DateTimeOffset? Reset,QuotaPaceKind Pace);
public sealed record TrayQuotaPresentation(string Source,TrayQuotaWindow? FiveHour,TrayQuotaWindow? Weekly)
{
    public TrayQuotaWindow? MostConstrained=>new[]{FiveHour,Weekly}.Where(window=>window is not null).OrderBy(window=>window!.Remaining).FirstOrDefault();
    public int? DisplayRemaining=>MostConstrained is {} window?(int)Math.Clamp(Math.Round(window.Remaining,MidpointRounding.AwayFromZero),0,100):null;
}

public static class TrayQuotaSelection
{
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
    private static TrayQuotaPresentation Claude(ClaudeQuotaSnapshot snapshot,bool enabled,DateTimeOffset now)
    {
        TrayQuotaWindow? Find(string key)
        {
            var age=snapshot.ObservedAt is {} at?now-at:TimeSpan.MaxValue;
            if(!enabled||snapshot.Status!="snapshot"||snapshot.HistoryOnly||string.IsNullOrWhiteSpace(snapshot.Scope)||age<TimeSpan.Zero||age>QuotaPace.ClaudeFreshness)return null;
            var window=snapshot.Windows.FirstOrDefault(item=>item.Key==key);
            if(window is null||!double.IsFinite(window.Used)||window.Used<0||window.Used>100||window.Expired(now))return null;
            return new(Math.Clamp(100-window.Used,0,100),window.ResetsAt,QuotaPace.ForClaude(snapshot,window,enabled,now));
        }
        return new("claude",Find("five_hour"),Find("seven_day"));
    }
}
