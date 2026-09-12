namespace UsageLoom.Core;

// Describes only the Token workload of accepted samples. It never assigns a
// model or mode its own share of account-level quota percentage points.
public sealed record UsagePricingProfile(long Tokens,long ActualTokens,long RequestedTokens,long UnknownTokens,
    long ConditionalTokens,Dictionary<string,long> Modes,Dictionary<string,long> Models,Dictionary<string,long> Reasoning)
{
    public const int CurrentVersion=Pricing.EvidenceVersion;
    public int Version { get; init; }=CurrentVersion;
    public double ActualCoverage=>Tokens>0?100d*ActualTokens/Tokens:0;
    public double RequestedCoverage=>Tokens>0?100d*RequestedTokens/Tokens:0;
    public double UnknownCoverage=>Tokens>0?100d*UnknownTokens/Tokens:0;
    public bool IsMixed=>Modes.Count(p=>p.Value>0)>1||Models.Count(p=>p.Value>0)>1||Reasoning.Count(p=>p.Value>0)>1;

    // Cache/sample equality must describe evidence values, not the allocation
    // identity or insertion order of dictionaries after a replay/JSON roundtrip.
    public bool Equals(UsagePricingProfile? other)=>other is not null&&Version==other.Version&&Tokens==other.Tokens&&
        ActualTokens==other.ActualTokens&&RequestedTokens==other.RequestedTokens&&UnknownTokens==other.UnknownTokens&&
        ConditionalTokens==other.ConditionalTokens&&DistributionEquals(Modes,other.Modes)&&
        DistributionEquals(Models,other.Models)&&DistributionEquals(Reasoning,other.Reasoning);
    public override int GetHashCode()
    {
        var hash=new HashCode();hash.Add(Version);hash.Add(Tokens);hash.Add(ActualTokens);hash.Add(RequestedTokens);
        hash.Add(UnknownTokens);hash.Add(ConditionalTokens);
        void AddDistribution(Dictionary<string,long>? values)
        {
            if(values is null){hash.Add(-1);return;}
            hash.Add(values.Count);
            foreach(var pair in values.OrderBy(p=>p.Key,StringComparer.Ordinal)){hash.Add(pair.Key,StringComparer.Ordinal);hash.Add(pair.Value);}
        }
        AddDistribution(Modes);AddDistribution(Models);AddDistribution(Reasoning);return hash.ToHashCode();
    }
    private static bool DistributionEquals(Dictionary<string,long>? a,Dictionary<string,long>? b)=>ReferenceEquals(a,b)||
        a is not null&&b is not null&&a.Count==b.Count&&a.All(p=>b.Any(q=>string.Equals(p.Key,q.Key,StringComparison.Ordinal)&&p.Value==q.Value));

    public bool IsValidFor(long tokens)=>Version==CurrentVersion&&tokens>0&&Tokens==tokens&&
        ActualTokens>=0&&RequestedTokens>=0&&UnknownTokens>=0&&ConditionalTokens>=0&&ConditionalTokens<=tokens&&
        (decimal)ActualTokens+RequestedTokens+UnknownTokens==tokens&&ValidDistribution(Modes,tokens)&&
        ValidDistribution(Models,tokens)&&ValidDistribution(Reasoning,tokens);
    private static bool ValidDistribution(Dictionary<string,long>? values,long tokens)=>values is not null&&
        values.All(p=>!string.IsNullOrWhiteSpace(p.Key)&&p.Value>=0)&&values.Sum(p=>(decimal)p.Value)==tokens;

    public static UsagePricingProfile From(IEnumerable<UsageEvent> events)
    {
        long total=0,actual=0,requested=0,unknown=0,conditional=0;
        Dictionary<string,long> modes=new(StringComparer.Ordinal),models=new(StringComparer.Ordinal),reasoning=new(StringComparer.Ordinal);
        foreach(var e in events)
        {
            if(!e.Tokens.Valid||e.Tokens.Total<=0)continue;
            var tokens=e.Tokens.Total;total=checked(total+tokens);
            var mode=Pricing.ResolveMode(e.Pricing);
            if(mode.Tier=="unknown"||mode.Evidence=="unknown")unknown=checked(unknown+tokens);
            else if(mode.Evidence=="actual-response")actual=checked(actual+tokens);
            else requested=checked(requested+tokens);
            if(mode.Tier=="unknown"||mode.Evidence!="actual-response"||Pricing.Calculate(e).HasConditionalAssumptions)
                conditional=checked(conditional+tokens);
            Add(modes,mode.Tier,tokens);Add(models,e.Model,tokens);Add(reasoning,e.Pricing?.ReasoningEffort,tokens);
        }
        return new(total,actual,requested,unknown,conditional,modes,models,reasoning);
    }

    public static UsagePricingProfile Merge(UsagePricingProfile? before,UsagePricingProfile after)
    {
        if(before is null)return after;
        Dictionary<string,long> Combine(Dictionary<string,long> a,Dictionary<string,long> b)
        {
            var result=new Dictionary<string,long>(a,StringComparer.Ordinal);
            foreach(var pair in b)Add(result,pair.Key,pair.Value);
            return result;
        }
        return new(checked(before.Tokens+after.Tokens),checked(before.ActualTokens+after.ActualTokens),
            checked(before.RequestedTokens+after.RequestedTokens),checked(before.UnknownTokens+after.UnknownTokens),
            checked(before.ConditionalTokens+after.ConditionalTokens),Combine(before.Modes,after.Modes),
            Combine(before.Models,after.Models),Combine(before.Reasoning,after.Reasoning))
            {Version=before.Version==CurrentVersion&&after.Version==CurrentVersion?CurrentVersion:0};
    }

    private static void Add(Dictionary<string,long> values,string? key,long tokens)
    {
        key=string.IsNullOrWhiteSpace(key)?"unknown":key.Trim().ToLowerInvariant();
        values[key]=checked(values.GetValueOrDefault(key)+tokens);
    }
}
