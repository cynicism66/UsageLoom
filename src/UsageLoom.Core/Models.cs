using System.Text.Json;

namespace UsageLoom.Core;

public sealed record TokenUsage(long Input = 0, long Cached = 0, long CacheWrite = 0, long Output = 0, long Reasoning = 0)
{
    public long Total => checked(Input + Output);
    public bool Valid => Input >= 0 && Cached >= 0 && CacheWrite >= 0 && Output >= 0 && Input <= long.MaxValue - Output && Reasoning >= 0 && Cached <= Input && CacheWrite <= Input - Cached && Reasoning <= Output;
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(checked(a.Input+b.Input),checked(a.Cached+b.Cached),checked(a.CacheWrite+b.CacheWrite),checked(a.Output+b.Output),checked(a.Reasoning+b.Reasoning));
    public static TokenUsage operator -(TokenUsage a, TokenUsage b) => new(a.Input-b.Input,a.Cached-b.Cached,a.CacheWrite-b.CacheWrite,a.Output-b.Output,a.Reasoning-b.Reasoning);
    public static TokenUsage Parse(JsonElement e, TokenUsage? previous = null)
    {
        long Read(string key, long fallback = 0)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new JsonException(L10n.T("s46DED4B259CF"));
            if (!e.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
            if (!value.TryLong(out var number) || number < 0) throw new JsonException(L10n.T("s454F0C311E2E"));
            return number;
        }
        var usage = new TokenUsage(Read("input_tokens"), Read("cached_input_tokens", previous?.Cached ?? 0),
            Read("cache_write_input_tokens", previous?.CacheWrite ?? 0), Read("output_tokens"), Read("reasoning_output_tokens", previous?.Reasoning ?? 0));
        if (!usage.Valid) throw new JsonException(L10n.T("s67F4E4082B46"));
        if (e.TryGetProperty("total_tokens", out var total) && total.ValueKind != JsonValueKind.Null &&
            (!total.TryLong(out var declared) || declared != usage.Total)) throw new JsonException(L10n.T("sAA365478E46B"));
        return usage;
    }
}
public sealed record UsageEvent(string Id, string Session, string Project, string Model, string Agent, DateTimeOffset? Timestamp, string LocalDate, TokenUsage Tokens, bool Unattributed = false, string Source = "", PricingContext? Pricing = null)
{
    public int Segment { get; init; }
    public string? QualityNote { get; init; }
    public string? AccountScope { get; init; }
    public string? AccountAttribution { get; init; }
}
public sealed record ScanReport(List<UsageEvent> Events, int Files, int Warnings, DateTimeOffset ScannedAt)
{
    public TokenUsage Total => Events.Aggregate(new TokenUsage(), (a,e) => a+e.Tokens);
    public List<string> Sources { get; init; } = [];
    public bool UsedCache { get; init; }
}
public sealed record QuotaWindow(string Key, string Label, double Used, int Minutes, DateTimeOffset? ResetsAt)
{
    public bool IsSpark { get; init; }
    public double Remaining => Math.Clamp(100-Used,0,100);
    public bool IsPrimary => Key.StartsWith("codex:",StringComparison.OrdinalIgnoreCase)||!Key.Contains(':');
    public string RemainingText => Remaining>99.9&&Remaining<100?">99.9%":Remaining>0&&Remaining<.1?"<0.1%":$"{Remaining:0.#}%";
    public string ResetCountdown(DateTimeOffset now)
    {
        if(ResetsAt is not {} at)return L10n.T("sFB4EF6852264");
        var left=at-now;
        if(left<=TimeSpan.Zero)return L10n.T("s8149D5B6846D");
        var minutes=(long)Math.Ceiling(left.TotalMinutes);
        var days=minutes/1440;var hours=minutes%1440/60;var rest=minutes%60;
        return L10n.T("s407FA6B07943")+(days>0?L10n.F("s658760D2AB1E", days):"")+(hours>0?L10n.F("s470A7C92EF5A", hours):"")+(rest>0?L10n.F("sC65627EAD130", rest):"").TrimEnd();
    }
}
public sealed record QuotaState(List<QuotaWindow> Windows, int? ResetCount, DateTimeOffset? FetchedAt, string Status, bool Fresh, string? AccountKey = null, string? Plan = null)
{
    public bool IsLocalAccount { get; init; }
    public bool IsAuthorizedAccount { get; init; }
    public bool IsCachedAccount { get; init; }
    public bool HasQuotaDisplay => !IsLocalAccount&&Windows.Count>0&&(Fresh||AccountKey is not null);
    public bool SnapshotOnly => (IsCachedAccount||IsAuthorizedAccount)&&AccountKey is null;
    public IEnumerable<QuotaWindow> PrimaryWindows => Windows.Where(window=>window.IsPrimary);
    public IEnumerable<QuotaWindow> OtherWindows => Windows.Where(window=>!window.IsPrimary);
    public QuotaState ClearUnverifiedSnapshot(string status)=>SnapshotOnly?this with{Windows=[],ResetCount=null,FetchedAt=null,Fresh=false,Status=status}:this;
    public string AccountLabel => IsAuthorizedAccount?L10n.T("s7FA72B2E0D53"):IsCachedAccount?L10n.T("s4D9071E7F3DD"):AccountKey is null ? L10n.T("s99D2089F4407") : L10n.T("s1ECA54C16740");
    public string PlanDisplay => IsLocalAccount?L10n.T("sE4174722F2CB"):string.IsNullOrWhiteSpace(Plan)?L10n.T("s0607D6675FE0"):
        L10n.T("s63F426BC249E")+(Plan.Trim().ToLowerInvariant() switch{"free"=>"Free","plus"=>"Plus","prolite"=>"Pro 5X","pro"=>"Pro 20X","team"=>"Team","business"=>"Business","enterprise"=>"Enterprise","edu"=>"Edu",_=>Plan.Trim()+L10n.T("s93D0582816C8")})+(Fresh?"":L10n.T("s5B8BFF4DF405"));
    public static QuotaState LocalAccount => new([],null,null,L10n.T("s3EC630C3E092"),false){IsLocalAccount=true};
}

public static class JsonFields
{
    public static string? Text(this JsonElement e, string key) => e.ValueKind==JsonValueKind.Object && e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    public static long Number(this JsonElement e,string key) => e.NullableNumber(key) ?? 0;
    public static long? NullableNumber(this JsonElement e,string key) => e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.TryLong(out var n)?n:null;
    public static bool TryLong(this JsonElement e,out long value) { value=0;return e.ValueKind==JsonValueKind.Number&&e.TryGetInt64(out value); }
    public static DateTimeOffset? Epoch(this JsonElement e,string key)
    {
        var value=e.NullableNumber(key); if(value is null)return null;
        try{return DateTimeOffset.FromUnixTimeSeconds(value.Value);}catch(ArgumentOutOfRangeException){return null;}
    }
}

public static class QuotaParser
{
    public static QuotaState Parse(JsonElement result, DateTimeOffset now, string? accountKey, string? plan)
    {
        var windows=new List<QuotaWindow>();
        if(result.ValueKind!=JsonValueKind.Object)return new([],null,null,L10n.T("s0F0DAC9297DD"),false);
        void ReadBucket(string id,JsonElement bucket)
        {
            if(bucket.ValueKind!=JsonValueKind.Object)return;
            foreach(var property in bucket.EnumerateObject())
            {
                var e=property.Value;
                if(e.ValueKind!=JsonValueKind.Object || !e.TryGetProperty("usedPercent",out var used)||used.ValueKind!=JsonValueKind.Number||!used.TryGetDouble(out var percent)||!double.IsFinite(percent)||percent<0||percent>100)continue;
                var duration=e.NullableNumber("windowDurationMins");if(duration is null or <=0 or >int.MaxValue)continue;
                var label=duration switch{300=>L10n.T("sEE0C10BF45F6"),10080=>L10n.T("s475811D50FA9"),1440=>L10n.T("sDDEA7144CA0D"),_=>L10n.F("sB1294AF90CCA", duration)};
                var name=bucket.Text("limitName");
                // Confirmed by official account/rateLimits/read: this ID names Spark.
                var spark=string.Equals(name,"GPT-5.3-Codex-Spark",StringComparison.OrdinalIgnoreCase)
                    ||string.IsNullOrWhiteSpace(name)&&id=="codex_bengalfox";
                var group=string.IsNullOrWhiteSpace(name)?id+L10n.T("sD06DFAAB2A0F"):name+"（"+id+"）";
                windows.Add(new(id+":"+property.Name, id=="codex"||spark?label:group+" · "+label,Math.Clamp(percent,0,100),(int)duration,e.Epoch("resetsAt")){IsSpark=spark&&id!="codex"});
            }
        }
        if(result.TryGetProperty("rateLimitsByLimitId",out var byId)&&byId.ValueKind==JsonValueKind.Object)
            foreach(var item in byId.EnumerateObject())ReadBucket(item.Name,item.Value);
        if(windows.Count==0&&result.TryGetProperty("rateLimits",out var primary))ReadBucket(primary.Text("limitId")??"codex",primary);
        int? count=null;
        if(result.TryGetProperty("rateLimitResetCredits",out var resets)&&resets.ValueKind==JsonValueKind.Object&&resets.NullableNumber("availableCount") is >=0 and <=int.MaxValue)
            count=(int)resets.Number("availableCount");
        return new(windows,count,now,windows.Count==0?L10n.T("sBB0A9BCC61AF"):L10n.T("s3153571BC640"),windows.Count>0,accountKey,plan);
    }
}
