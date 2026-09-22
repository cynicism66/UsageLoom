using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageLoom.Core;

public static class ClaudePlanLabel
{
    public static IReadOnlyList<string> Choices {get;}=Array.AsReadOnly(new[]{"free","pro","max","max5","max20","team","enterprise"});
    public static string? Normalize(string? value)=>value?.Trim().ToLowerInvariant() is {} key&&Choices.Contains(key)?key:null;
    public static string Display(string? value)=>Normalize(value) switch
    {
        "free"=>"Free","pro"=>"Pro","max"=>"Max","max5"=>"Max 5X","max20"=>"Max 20X","team"=>"Team","enterprise"=>"Enterprise",_=>L10n.T("claude.planUnset")
    };
    public static string Badge(string? value)=>Normalize(value) is null?L10n.T("claude.planUnset"):L10n.F("claude.planManual",Display(value));
}

// Deliberately separate from Codex QuotaState and capacity/attribution ledgers.
public sealed record ClaudeQuotaWindow(string Key,double Used,DateTimeOffset? ResetsAt)
{
    public bool Expired(DateTimeOffset now)=>ResetsAt is {} reset&&reset<=now;
    public string RemainingText(DateTimeOffset now)=>Expired(now)?"—":$"{100-Used:0.#}%";
    public string Countdown(DateTimeOffset now)
    {
        if(ResetsAt is not {} reset)return L10n.T("claude.noReset");
        if(reset<=now)return L10n.T("claude.awaitReset");
        var minutes=(long)Math.Ceiling((reset-now).TotalMinutes);
        return L10n.F("claude.countdown",minutes/1440,minutes%1440/60,minutes%60);
    }
}
public sealed record ClaudeQuotaSnapshot(string Status,IReadOnlyList<ClaudeQuotaWindow> Windows,DateTimeOffset? ObservedAt=null,
    string? Scope=null,IReadOnlyList<string>? Scopes=null,bool HistoryOnly=false)
{
    public static ClaudeQuotaSnapshot Empty(string status)=>new(status,[]);
    public string Describe(DateTimeOffset now)
    {
        if(Status!="snapshot")return L10n.T("claude."+Status);
        var age=ObservedAt is {} at?now-at:TimeSpan.MaxValue;
        return L10n.T(HistoryOnly?"claude.history":age>TimeSpan.FromMinutes(15)||age<TimeSpan.Zero?"claude.stale":"claude.snapshot");
    }
    public string TimestampText=>ObservedAt is {} at?L10n.F("claude.observed",at.ToLocalTime()):L10n.T("claude.noSnapshot");
}

public static class ClaudeQuotaParser
{
    public static string ScopeKey(string root,string organization)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()+"|"+organization.ToLowerInvariant())))[..16];

    public static ClaudeQuotaSnapshot Parse(byte[] body,DateTimeOffset at,string scope,DateTimeOffset now)
    {
        if(at>now.AddMinutes(2)||at<now.AddDays(-31))throw new InvalidDataException("Invalid snapshot time");
        using var doc=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=20});
        var windows=new List<ClaudeQuotaWindow>();
        foreach(var (key,duration) in new[]{("five_hour",TimeSpan.FromHours(5)),("seven_day",TimeSpan.FromDays(7))})
        {
            if(!doc.RootElement.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null)continue;
            if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid window");
            if(!value.TryGetProperty("utilization",out var usage)||usage.ValueKind==JsonValueKind.Null)continue;
            if(usage.ValueKind!=JsonValueKind.Number||!usage.TryGetDouble(out var used)||!double.IsFinite(used)||used<0||used>100)throw new InvalidDataException("Invalid utilization");
            DateTimeOffset? reset=null;
            if(value.TryGetProperty("resets_at",out var time)&&time.ValueKind!=JsonValueKind.Null)
            {
                if(time.ValueKind!=JsonValueKind.String||!DateTimeOffset.TryParse(time.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed)
                    ||parsed>at+duration+TimeSpan.FromMinutes(5)||parsed<at.AddDays(-8))throw new InvalidDataException("Invalid reset");
                reset=parsed;
            }
            windows.Add(new(key,used,reset));
        }
        return new("snapshot",windows,at,scope);
    }

    public static ClaudeQuotaSnapshot Select(IReadOnlyList<ClaudeQuotaSnapshot> snapshots,string? scope)
    {
        var scopes=snapshots.Select(s=>s.Scope!).Distinct(StringComparer.Ordinal).Order().ToArray();
        if(scopes.Length==0)return ClaudeQuotaSnapshot.Empty("waiting");
        if(string.IsNullOrEmpty(scope)&&scopes.Length>1)return new("conflict",[],Scopes:scopes);
        var candidates=snapshots.Where(s=>s.Scope==(scope??scopes[0])).OrderByDescending(s=>s.ObservedAt).ToArray();
        if(candidates.Length==0)return new("scopeMissing",[],Scopes:scopes);
        var first=candidates[0];
        // Equal-time, different payloads are ambiguous; never depend on hash-bucket order.
        if(candidates.Any(s=>s.ObservedAt==first.ObservedAt&&!Equivalent(s.Windows,first.Windows)))return new("conflict",[],Scopes:scopes);
        return first with{Scopes=scopes};
    }
    private static bool Equivalent(IReadOnlyList<ClaudeQuotaWindow> a,IReadOnlyList<ClaudeQuotaWindow> b)=>a.Count==b.Count&&a.Zip(b).All(pair=>
        pair.First.Key==pair.Second.Key&&pair.First.Used==pair.Second.Used&&
        (pair.First.ResetsAt==pair.Second.ResetsAt||pair.First.ResetsAt is {} x&&pair.Second.ResetsAt is {} y&&Math.Abs((x-y).TotalSeconds)<1));

    public static ClaudeQuotaSnapshot History(byte[] data,string root,string? selectedScope,DateTimeOffset now)
    {
        using var doc=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=12});
        if(!doc.RootElement.TryGetProperty("version",out var version)||version.GetInt32()!=2)return ClaudeQuotaSnapshot.Empty("unsupported");
        var rows=new List<ClaudeQuotaSnapshot>();var count=0;
        foreach(var row in doc.RootElement.GetProperty("samples").EnumerateArray())
        {
            if(++count>20000)throw new InvalidDataException("Too many samples");
            if(!row.TryGetProperty("org",out var org)||org.ValueKind!=JsonValueKind.String||!Guid.TryParse(org.GetString(),out var id))throw new InvalidDataException("Missing source");
            var at=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("t").GetInt64());
            if(at>now.AddMinutes(2))throw new InvalidDataException("Future sample");
            if(at<now.AddDays(-31))continue;
            var values=row.GetProperty("u");var windows=new List<ClaudeQuotaWindow>();
            foreach(var (name,key) in new[]{("fh","five_hour"),("sd","seven_day")})
            {
                if(!values.TryGetProperty(name,out var item)||item.ValueKind==JsonValueKind.Null)continue;
                if(item.ValueKind!=JsonValueKind.Number||!item.TryGetDouble(out var value)||!double.IsFinite(value)||value<0||value>100)
                    throw new InvalidDataException("Invalid historical utilization");
                windows.Add(new(key,value,null));
            }
            rows.Add(new("snapshot",windows,at,ScopeKey(root,id.ToString()),HistoryOnly:true));
        }
        return Select(rows,selectedScope);
    }

    // History may be newer than the HTTP cache. Never ignore a changed source or
    // copy a previous cycle's reset onto a newer percentage-only observation.
    internal static ClaudeQuotaSnapshot Reconcile(ClaudeQuotaSnapshot cache,ClaudeQuotaSnapshot history,string? scope)
    {
        if(history.Status=="waiting")return cache;
        var scopes=(cache.Scopes??[]).Concat(history.Scopes??[]).Distinct().Order().ToArray();
        if(string.IsNullOrEmpty(scope)&&scopes.Length>1)return new("conflict",[],Scopes:scopes);
        if(history.Status!="snapshot")return history with{Scopes=scopes};
        if(cache.Status!="snapshot")return cache with{Scopes=scopes};
        if(history.ObservedAt>=cache.ObservedAt&&(history.Scope!=cache.Scope||
            history.Windows.Count!=cache.Windows.Count||history.Windows.Any(h=>!cache.Windows.Any(c=>c.Key==h.Key&&c.Used==h.Used))||
            cache.Windows.Any(w=>w.ResetsAt<=history.ObservedAt)))return history with{Scopes=scopes};
        return cache with{Scopes=scopes};
    }
}
