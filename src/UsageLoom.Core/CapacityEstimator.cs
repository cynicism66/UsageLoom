namespace UsageLoom.Core;

public sealed record CapacitySample(string Key,string Label,DateTimeOffset ResetsAt,double LastUsed,double Percent,long Tokens,decimal Cost,long Priced,int Samples,int Excluded);
public sealed record CapacityCache(int Version,string Account,string Plan,string PricingVersion,DateTimeOffset SavedAt,List<CapacitySample> Windows);

public sealed record WeeklyCapacityEstimate(
    string WindowKey,
    string WindowLabel,
    double EstimatedTokens,
    long ObservedTokens,
    double ObservedPercent,
    int Samples,
    int ExcludedIntervals,
    string Confidence,
    DateTimeOffset? ResetsAt)
{
    public decimal? EstimatedDollars { get; init; }
    public double PricingCoverage { get; init; }
    public string DollarDisplay => EstimatedDollars is {} dollars?$"约 ${dollars:N2} / 周"+(PricingCoverage<99.999?"（部分估算）":""):"美元估算暂不可用";
    public string ScopeNote => "按已保存的本机日志与额度配对样本估算；不是官方固定额度";
}

public sealed class WeeklyCapacityEstimator
{
    private sealed class WindowState
    {
        public required string Label { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public double BaselineUsed { get; set; }
        public long BaselineTokens { get; set; }
        public double ObservedPercent { get; set; }
        public long ObservedTokens { get; set; }
        public int Samples { get; set; }
        public int ExcludedIntervals { get; set; }
        public DateTimeOffset? PendingSince { get; set; }
        public double PendingUsed { get; set; }
        public Estimate? BaselinePrice { get; set; }
        public decimal ObservedCost { get; set; }
        public long PricedTokens { get; set; }
    }

    private readonly Dictionary<string,WindowState> states=new(StringComparer.Ordinal);
    private string? accountKey;
    private string? plan;
    private CapacityCache? pendingCache;
    public DateTimeOffset? RestoredAt { get; private set; }
    public void Restore(CapacityCache? cache){Reset();pendingCache=cache;}
    public CapacityCache? Export()=>string.IsNullOrWhiteSpace(accountKey)||string.IsNullOrWhiteSpace(plan)?null:
        new(2,accountKey,plan,Pricing.CatalogVersion,DateTimeOffset.Now,states.Where(pair=>pair.Value.Samples>0&&pair.Value.ResetsAt is not null).Select(pair=>
        {var s=pair.Value;return new CapacitySample(pair.Key,s.Label,s.ResetsAt!.Value,s.BaselineUsed,s.ObservedPercent,s.ObservedTokens,s.ObservedCost,s.PricedTokens,s.Samples,s.ExcludedIntervals);}).ToList());

    public string DescribeProgress(QuotaState quota,long localTokenTotal,bool historyLoaded)
    {
        if(quota.IsLocalAccount)return "本地模式：没有在线周额度，暂不能估算；本机 Token 统计仍可用。";
        if(!quota.Fresh)return "等待成功读取在线额度；暂停估算，不使用过期额度推算。";
        var window=quota.PrimaryWindows.FirstOrDefault(value=>value.Minutes==10080);
        if(window is null)return "服务端未提供主 Codex 周额度，暂不能估算。";
        if(!historyLoaded)return "等待本机日志索引加载完成，再建立采样起点。";
        if(!string.Equals(accountKey,quota.AccountKey,StringComparison.Ordinal)||!states.TryGetValue(window.Key,out var state))return "正在建立本次采样起点。";
        var tokens=Math.Max(0,localTokenTotal-state.BaselineTokens);
        var percent=Math.Max(0,window.Used-state.BaselineUsed);
        var stage=state.Samples>0?$"配对进度：{state.ObservedPercent:0.##}/5 个百分点、{state.Samples}/2 段（达到后显示实验估算）":percent>0?"等待本机 Token 增量与额度变化配对":tokens>0?"已检测到 Token 增量，等待周额度百分比变化":"采样起点已建立，等待新的使用记录和周额度变化";
        return $"{stage}\n待配对：{tokens:N0} Token / {percent:0.####} 个百分点 · 有效样本 {state.Samples} 段 · 已排除 {state.ExcludedIntervals} 段";
    }

    public IReadOnlyList<WeeklyCapacityEstimate> Observe(QuotaState quota,long localTokenTotal,Estimate? price=null)
    {
        if(!quota.Fresh||localTokenTotal<0)return Current;
        var currentPlan=quota.Plan?.Trim().ToLowerInvariant();
        if(!string.Equals(accountKey,quota.AccountKey,StringComparison.Ordinal)||plan!=currentPlan)
        {
            states.Clear();
            accountKey=quota.AccountKey;
            plan=currentPlan;RestoredAt=null;
        }
        var weekly=quota.PrimaryWindows.Where(window=>window.Minutes==10080).ToList();
        var currentKeys=weekly.Select(window=>window.Key).ToHashSet(StringComparer.Ordinal);
        foreach(var key in states.Keys.Where(key=>!currentKeys.Contains(key)).ToList())states.Remove(key);
        foreach(var window in weekly)
        {
            if(!states.TryGetValue(window.Key,out var state)||NewWindow(state.ResetsAt,window.ResetsAt)||window.Used<state.BaselineUsed-.01||localTokenTotal<state.BaselineTokens)
            {
                states[window.Key]=new WindowState{Label=window.Label,ResetsAt=window.ResetsAt,BaselineUsed=window.Used,BaselineTokens=localTokenTotal,BaselinePrice=price};
                var cache=pendingCache;
                var saved=cache?.Windows?.FirstOrDefault(item=>item.Key==window.Key);
                if(cache is {Version:2}&&cache.Account==accountKey&&cache.Plan==plan&&cache.PricingVersion==Pricing.CatalogVersion&&cache.SavedAt<=DateTimeOffset.Now&&
                    saved is not null&&!NewWindow(saved.ResetsAt,window.ResetsAt)&&saved.ResetsAt>DateTimeOffset.Now&&window.Used>=saved.LastUsed-.01&&
                    double.IsFinite(saved.Percent)&&saved.Percent>0&&saved.Percent<=100&&double.IsFinite(saved.LastUsed)&&saved.LastUsed>=0&&saved.LastUsed<=100&&
                    saved.Tokens>0&&saved.Cost>=0&&saved.Priced>=0&&saved.Priced<=saved.Tokens&&saved.Samples>0&&saved.Excluded>=0)
                {
                    var restored=states[window.Key];restored.ObservedPercent=saved.Percent;restored.ObservedTokens=saved.Tokens;
                    restored.ObservedCost=saved.Cost;restored.PricedTokens=saved.Priced;restored.Samples=saved.Samples;restored.ExcludedIntervals=saved.Excluded;
                    RestoredAt=cache.SavedAt;
                }
                continue;
            }
            state.Label=window.Label;state.ResetsAt=window.ResetsAt;
            var percentDelta=window.Used-state.BaselineUsed;
            if(percentDelta<=.0001)continue;
            var tokenDelta=localTokenTotal-state.BaselineTokens;
            if(tokenDelta>0)
            {
                if(state.PendingSince is {} pending&&DateTimeOffset.Now-pending>TimeSpan.FromSeconds(30))
                {
                    state.ExcludedIntervals++;state.BaselineUsed=window.Used;state.BaselineTokens=localTokenTotal;state.BaselinePrice=price;state.PendingSince=null;continue;
                }
                state.ObservedPercent+=percentDelta;
                state.ObservedTokens=checked(state.ObservedTokens+tokenDelta);
                if(price is {} current&&state.BaselinePrice is {} before&&current.Cost>=before.Cost&&current.Priced>=before.Priced&&current.Priced-before.Priced<=tokenDelta)
                {
                    state.ObservedCost+=current.Cost-before.Cost;
                    state.PricedTokens+=current.Priced-before.Priced;
                }
                state.Samples++;
                state.PendingSince=null;
            }
            else
            {
                if(state.PendingSince is null){state.PendingSince=DateTimeOffset.Now;state.PendingUsed=window.Used;continue;}
                if(DateTimeOffset.Now-state.PendingSince<=TimeSpan.FromSeconds(30)&&window.Used<=state.PendingUsed+.0001)continue;
                state.ExcludedIntervals++;state.PendingSince=null;
            }
            state.BaselineUsed=window.Used;state.BaselineTokens=localTokenTotal;state.BaselinePrice=price;
        }
        pendingCache=null;
        return Current;
    }

    public IReadOnlyList<WeeklyCapacityEstimate> Current=>states
        .Where(pair=>pair.Value.Samples>0&&pair.Value.ObservedPercent>0&&pair.Value.ObservedTokens>0)
        .Select(pair=>
        {
            var state=pair.Value;
            var confidence=state.ExcludedIntervals>0?"低":state.ObservedPercent>=10&&state.Samples>=3?"较高":state.ObservedPercent>=5&&state.Samples>=2?"中":"低";
            return new WeeklyCapacityEstimate(pair.Key,state.Label,state.ObservedTokens*100d/state.ObservedPercent,state.ObservedTokens,state.ObservedPercent,state.Samples,state.ExcludedIntervals,confidence,state.ResetsAt){EstimatedDollars=state.PricedTokens>0?state.ObservedCost*100m/(decimal)state.ObservedPercent:null,PricingCoverage=100d*state.PricedTokens/state.ObservedTokens};
        }).OrderByDescending(value=>value.ObservedPercent).ToList();

    public void Reset(){states.Clear();accountKey=null;plan=null;pendingCache=null;RestoredAt=null;}

    private static bool NewWindow(DateTimeOffset? before,DateTimeOffset? after)
    {
        if(before is null||after is null)return before!=after;
        return Math.Abs((after.Value-before.Value).TotalMinutes)>2;
    }
}
