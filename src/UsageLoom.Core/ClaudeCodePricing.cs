namespace UsageLoom.Core;

// Current first-party API list prices are a comparison only: Claude Pro is not billed this way.
// Unknown models are never silently assigned another model's rate.
public static class ClaudeCodePricing
{
    public const string VerifiedDate="2026-09-23";
    public const string Source="https://platform.claude.com/docs/en/about-claude/pricing";
    public sealed record Rates(decimal Input,decimal CacheWrite5m,decimal CacheWrite1h,decimal CacheRead,decimal Output);
    public sealed record Result(decimal Cost,long Priced,long Unpriced,bool Assumed5m)
    {
        public double Coverage=>Priced+Unpriced>0?100d*Priced/(Priced+Unpriced):0;
    }
    private static readonly Dictionary<string,Rates> Catalog=new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"]=new(4,5,8,.20m,20),
        ["claude-opus-5"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-8"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-7"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-6"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-5"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-1"]=new(15,18.75m,30,1.5m,75),
        ["claude-opus-4"]=new(15,18.75m,30,1.5m,75),
        ["claude-sonnet-5"]=new(2,2.5m,4,.20m,10),
        ["claude-sonnet-4-6"]=new(3,3.75m,6,.30m,15),
        ["claude-sonnet-4-5"]=new(3,3.75m,6,.30m,15),
        ["claude-sonnet-4"]=new(3,3.75m,6,.30m,15),
        ["claude-haiku-4-5"]=new(1,1.25m,2,.10m,5),
        ["claude-haiku-3-5"]=new(.8m,1,1.6m,.08m,4)
    };
    private static readonly string[] Models=Catalog.Keys.OrderByDescending(x=>x.Length).ToArray();
    public static Rates? Find(string model)
    {
        if(string.IsNullOrWhiteSpace(model))return null;
        foreach(var key in Models)
            if(model.Equals(key,StringComparison.OrdinalIgnoreCase)||model.StartsWith(key+"-",StringComparison.OrdinalIgnoreCase))return Catalog[key];
        return null;
    }
    public static Result Summarize(IEnumerable<ClaudeCodeUsage> rows)
    {
        decimal cost=0;long priced=0,unpriced=0;bool assumed5m=false;
        foreach(var row in rows)
        {
            var rates=Find(row.Model);
            if(rates is null){unpriced=checked(unpriced+row.Total);continue;}
            var hour=row.CacheWrite1h??0;
            if(hour<0||hour>row.CacheWrite){unpriced=checked(unpriced+row.Total);continue;}
            if(row.CacheWrite1h is null&&row.CacheWrite>0)assumed5m=true;
            cost+=(row.Input*rates.Input+row.Output*rates.Output+row.CacheRead*rates.CacheRead+
                (row.CacheWrite-hour)*rates.CacheWrite5m+hour*rates.CacheWrite1h)/1_000_000m;
            priced=checked(priced+row.Total);
        }
        return new(cost,priced,unpriced,assumed5m);
    }
}
