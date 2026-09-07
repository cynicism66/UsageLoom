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
            if (e.ValueKind != JsonValueKind.Object) throw new JsonException("Token 记录不是对象");
            if (!e.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
            if (!value.TryLong(out var number) || number < 0) throw new JsonException("Token 分类必须为非负整数");
            return number;
        }
        var usage = new TokenUsage(Read("input_tokens"), Read("cached_input_tokens", previous?.Cached ?? 0),
            Read("cache_write_input_tokens", previous?.CacheWrite ?? 0), Read("output_tokens"), Read("reasoning_output_tokens", previous?.Reasoning ?? 0));
        if (!usage.Valid) throw new JsonException("Token 分类关系无效");
        if (e.TryGetProperty("total_tokens", out var total) && total.ValueKind != JsonValueKind.Null &&
            (!total.TryLong(out var declared) || declared != usage.Total)) throw new JsonException("Token 总量与分类不一致");
        return usage;
    }
}
public sealed record UsageEvent(string Id, string Session, string Project, string Model, string Agent, DateTimeOffset? Timestamp, string LocalDate, TokenUsage Tokens, bool Unattributed = false, string Source = "", PricingContext? Pricing = null)
{
    public int Segment { get; init; }
    public string? QualityNote { get; init; }
    public string? AccountScope { get; init; }
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
        if(ResetsAt is not {} at)return "重置时间暂不可用";
        var left=at-now;
        if(left<=TimeSpan.Zero)return "已到重置时间，等待刷新确认";
        var minutes=(long)Math.Ceiling(left.TotalMinutes);
        var days=minutes/1440;var hours=minutes%1440/60;var rest=minutes%60;
        return "距离重置 "+(days>0?$"{days} 天 ":"")+(hours>0?$"{hours} 小时 ":"")+(rest>0?$"{rest} 分钟":"").TrimEnd();
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
    public string AccountLabel => IsAuthorizedAccount?"UsageLoom 授权账号":IsCachedAccount?"本机缓存账号":AccountKey is null ? "本地账户" : "当前查询账户";
    public string PlanDisplay => IsLocalAccount?"本地模式 · 无在线套餐":string.IsNullOrWhiteSpace(Plan)?"套餐暂不可用":
        "套餐："+(Plan.Trim().ToLowerInvariant() switch{"free"=>"Free","plus"=>"Plus","prolite"=>"Pro 5X","pro"=>"Pro 20X","team"=>"Team","business"=>"Business","enterprise"=>"Enterprise","edu"=>"Edu",_=>Plan.Trim()+"（后端标识）"})+(Fresh?"":" · 待刷新确认");
    public static QuotaState LocalAccount => new([],null,null,"本地账户 · 未登录，仍可统计本机 Token；在线额度与重置次数暂不可用",false){IsLocalAccount=true};
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
        if(result.ValueKind!=JsonValueKind.Object)return new([],null,null,"额度响应格式无效",false);
        void ReadBucket(string id,JsonElement bucket)
        {
            if(bucket.ValueKind!=JsonValueKind.Object)return;
            foreach(var property in bucket.EnumerateObject())
            {
                var e=property.Value;
                if(e.ValueKind!=JsonValueKind.Object || !e.TryGetProperty("usedPercent",out var used)||used.ValueKind!=JsonValueKind.Number||!used.TryGetDouble(out var percent)||!double.IsFinite(percent)||percent<0||percent>100)continue;
                var duration=e.NullableNumber("windowDurationMins");if(duration is null or <=0 or >int.MaxValue)continue;
                var label=duration switch{300=>"5 小时额度",10080=>"每周额度",1440=>"每日额度",_=>$"{duration} 分钟额度"};
                var name=bucket.Text("limitName");
                // Confirmed by official account/rateLimits/read: this ID names Spark.
                var spark=string.Equals(name,"GPT-5.3-Codex-Spark",StringComparison.OrdinalIgnoreCase)
                    ||string.IsNullOrWhiteSpace(name)&&id=="codex_bengalfox";
                var group=string.IsNullOrWhiteSpace(name)?id+"（用途未确认）":name+"（"+id+"）";
                windows.Add(new(id+":"+property.Name, id=="codex"||spark?label:group+" · "+label,Math.Clamp(percent,0,100),(int)duration,e.Epoch("resetsAt")){IsSpark=spark&&id!="codex"});
            }
        }
        if(result.TryGetProperty("rateLimitsByLimitId",out var byId)&&byId.ValueKind==JsonValueKind.Object)
            foreach(var item in byId.EnumerateObject())ReadBucket(item.Name,item.Value);
        if(windows.Count==0&&result.TryGetProperty("rateLimits",out var primary))ReadBucket(primary.Text("limitId")??"codex",primary);
        int? count=null;
        if(result.TryGetProperty("rateLimitResetCredits",out var resets)&&resets.ValueKind==JsonValueKind.Object&&resets.NullableNumber("availableCount") is >=0 and <=int.MaxValue)
            count=(int)resets.Number("availableCount");
        return new(windows,count,now,windows.Count==0?"服务端未提供额度窗口":"当前 CLI 查询成功",windows.Count>0,accountKey,plan);
    }
}
