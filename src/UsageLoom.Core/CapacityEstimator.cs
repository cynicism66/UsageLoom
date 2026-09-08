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
    public DateTimeOffset? HistoricalAt { get; init; }
    public decimal? EstimatedDollars { get; init; }
    public double PricingCoverage { get; init; }
    public string DollarDisplay => EstimatedDollars is {} dollars?L10n.F("s1F2CD6B8A261", dollars)+(PricingCoverage<99.999?L10n.T("sD08FAC75B598"):""):L10n.T("s5D0032761903");
    public string ScopeNote => L10n.T("sAF8F04571120");
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
    private bool temporal;
    public DateTimeOffset? RestoredAt { get; private set; }
    private readonly List<CapacityCache> displayHistory=[];
    public void Restore(CapacityCache? cache,IEnumerable<CapacityCache>? history=null)
    {
        Reset();pendingCache=cache;
        if(history is not null)displayHistory.AddRange(history);
        if(cache is not null)displayHistory.Add(cache);
    }
    public CapacityCache? Export()=>string.IsNullOrWhiteSpace(accountKey)||string.IsNullOrWhiteSpace(plan)?null:
        new(temporal?3:2,accountKey,plan,Pricing.CatalogVersion,DateTimeOffset.Now,states.Where(pair=>pair.Value.Samples>0&&pair.Value.ResetsAt is not null).Select(pair=>
        {var s=pair.Value;return new CapacitySample(pair.Key,s.Label,s.ResetsAt!.Value,s.BaselineUsed,s.ObservedPercent,s.ObservedTokens,s.ObservedCost,s.PricedTokens,s.Samples,s.ExcludedIntervals);}).ToList());

    public string DescribeProgress(QuotaState quota,long localTokenTotal,bool historyLoaded)
    {
        if(quota.IsLocalAccount)return L10n.T("s94FCDB5088E1");
        if(!quota.Fresh)return L10n.T("s0F5B641F7850");
        var window=quota.PrimaryWindows.FirstOrDefault(value=>value.Minutes==10080);
        if(window is null)return L10n.T("s9FA7AD37DD0D");
        if(!historyLoaded)return L10n.T("s7C0D2C76FB51");
        if(!string.Equals(accountKey,quota.AccountKey,StringComparison.Ordinal)||!states.TryGetValue(window.Key,out var state))return L10n.T("sBD4D57CD3F7F");
        var tokens=Math.Max(0,localTokenTotal-state.BaselineTokens);
        var percent=Math.Max(0,window.Used-state.BaselineUsed);
        var stage=state.Samples>0?L10n.F("s4C059D23076E", state.ObservedPercent, state.Samples):percent>0?L10n.T("sBBBF41C18FDD"):tokens>0?L10n.T("s56E6E4044346"):L10n.T("s374E804CF4EB");
        return L10n.F("s62ED6CB79078", stage, tokens, percent, state.Samples, state.ExcludedIntervals);
    }

    public IReadOnlyList<WeeklyCapacityEstimate> Observe(QuotaState quota,long localTokenTotal,Estimate? price=null)
    {
        if(!quota.Fresh||localTokenTotal<0)return Current;
        if(Export() is {} previous&&previous.Windows.Any(w=>w.Percent>=5&&w.Samples>=2))
        {
            displayHistory.RemoveAll(c=>c.Account==previous.Account&&c.Plan==previous.Plan&&c.PricingVersion==previous.PricingVersion);
            displayHistory.Add(previous);
        }
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
            var confidence=state.ExcludedIntervals>0?L10n.T("sAA9E366F68D3"):state.ObservedPercent>=10&&state.Samples>=3?L10n.T("sDFBAD24E7F4A"):state.ObservedPercent>=5&&state.Samples>=2?L10n.T("sA567BDAA1136"):L10n.T("sAA9E366F68D3");
            return new WeeklyCapacityEstimate(pair.Key,state.Label,state.ObservedTokens*100d/state.ObservedPercent,state.ObservedTokens,state.ObservedPercent,state.Samples,state.ExcludedIntervals,confidence,state.ResetsAt){EstimatedDollars=state.PricedTokens>0?state.ObservedCost*100m/(decimal)state.ObservedPercent:null,PricingCoverage=100d*state.PricedTokens/state.ObservedTokens};
        }).OrderByDescending(value=>value.ObservedPercent).ToList();

    // Display-only fallback: never feed an old window into Observe or Export.
    public IReadOnlyList<WeeklyCapacityEstimate> DisplayCurrent
    {
        get
        {
            var current=Current.ToList();
            foreach(var key in states.Keys)
            {
                if(current.Any(e=>e.WindowKey==key&&e.ObservedPercent>=5&&e.Samples>=2))continue;
                var old=displayHistory.Where(c=>c.Version is 2 or 3&&c.Account==accountKey&&c.Plan==plan&&c.PricingVersion==Pricing.CatalogVersion&&c.SavedAt<=DateTimeOffset.Now)
                    .OrderByDescending(c=>c.SavedAt).SelectMany(c=>c.Windows.Select(w=>(Cache:c,Sample:w)))
                    .FirstOrDefault(p=>p.Sample.Key==key&&double.IsFinite(p.Sample.Percent)&&p.Sample.Percent>=5&&p.Sample.Percent<=100&&p.Sample.Samples>=2&&p.Sample.Tokens>0&&p.Sample.Cost>=0&&p.Sample.Priced>=0&&p.Sample.Priced<=p.Sample.Tokens);
                if(old.Sample is not {} s)continue;
                current.RemoveAll(e=>e.WindowKey==key);
                current.Add(new(key,s.Label,s.Tokens*100d/s.Percent,s.Tokens,s.Percent,s.Samples,s.Excluded,L10n.T("capacity.previous"),s.ResetsAt)
                {HistoricalAt=old.Cache.SavedAt,EstimatedDollars=s.Priced>0?s.Cost*100m/(decimal)s.Percent:null,PricingCoverage=100d*s.Priced/s.Tokens});
            }
            return current;
        }
    }

    public void ApplyTemporal(QuotaState quota,CapacityCache? cache,long localTotal,Estimate? price)
    {
        if(!quota.Fresh)return;
        if(Export() is {} previous&&previous.Windows.Any(w=>w.Percent>=5&&w.Samples>=2))
        {
            displayHistory.RemoveAll(c=>c.Account==previous.Account&&c.Plan==previous.Plan&&c.Version==previous.Version);
            displayHistory.Add(previous);
        }
        temporal=true;states.Clear();accountKey=quota.AccountKey;plan=quota.Plan?.Trim().ToLowerInvariant();pendingCache=null;RestoredAt=null;
        foreach(var window in quota.PrimaryWindows.Where(w=>w.Minutes==10080))
        {
            var s=cache is {Version:3}&&cache.Account==accountKey&&cache.Plan==plan?cache.Windows.FirstOrDefault(w=>w.Key==window.Key):null;
            states[window.Key]=new WindowState{Label=window.Label,ResetsAt=window.ResetsAt,BaselineUsed=window.Used,BaselineTokens=localTotal,BaselinePrice=price,
                ObservedPercent=s?.Percent??0,ObservedTokens=s?.Tokens??0,ObservedCost=s?.Cost??0,PricedTokens=s?.Priced??0,Samples=s?.Samples??0,ExcludedIntervals=s?.Excluded??0};
        }
    }

    public void Reset(){states.Clear();displayHistory.Clear();accountKey=null;plan=null;pendingCache=null;RestoredAt=null;temporal=false;}

    private static bool NewWindow(DateTimeOffset? before,DateTimeOffset? after)
    {
        if(before is null||after is null)return before!=after;
        return Math.Abs((after.Value-before.Value).TotalMinutes)>2;
    }
}
