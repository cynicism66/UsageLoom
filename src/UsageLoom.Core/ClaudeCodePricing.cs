namespace UsageLoom.Core;

// Current first-party API list prices are a comparison only: Claude Pro is not billed this way.
// Unknown models are never silently assigned another model's rate.
public static class ClaudeCodePricing
{
    public const string VerifiedDate="2026-11-15";
    public const string Source="https://platform.claude.com/docs/en/about-claude/pricing";
    public sealed record Rates(decimal Input,decimal CacheWrite5m,decimal CacheWrite1h,decimal CacheRead,decimal Output)
    {
        // Some models price a request by its prompt length: input, cache reads and cache writes together.
        public long? LongPromptAbove { get; init; }
        public Rates? LongPrompt { get; init; }
        public Rates For(ClaudeCodeUsage row)=>LongPromptAbove is {} limit&&LongPrompt is {} rates&&row.Input+row.CacheRead+row.CacheWrite>limit?rates:this;
    }
    public sealed record Result(decimal Cost,long Priced,long Unpriced,bool Assumed5m)
    {
        public double Coverage=>Priced+Unpriced>0?100d*Priced/(Priced+Unpriced):0;
    }
    private static readonly Dictionary<string,Rates> Catalog=new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-2"]=new(8,10,16,.20m,40),
        ["claude-mythos-5-2"]=new(8,10,16,.20m,40),
        ["claude-fable-5-1"]=new(10,12.5m,20,.25m,50),
        ["claude-mythos-5-1"]=new(10,12.5m,20,.25m,50),
        ["claude-fable-5"]=new(10,12.5m,20,1,50),
        ["claude-mythos-5"]=new(10,12.5m,20,1,50),
        ["claude-opus-5-5"]=new(4,5,8,.20m,20),
        ["claude-opus-5"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-8"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-7"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-6"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-5"]=new(5,6.25m,10,.50m,25),
        ["claude-opus-4-1"]=new(15,18.75m,30,1.5m,75),
        ["claude-opus-4"]=new(15,18.75m,30,1.5m,75),
        ["claude-sonnet-5-5"]=new(2,2.5m,4,.10m,10),
        ["claude-sonnet-5"]=new(2,2.5m,4,.20m,10),
        ["claude-sonnet-4-6"]=new(3,3.75m,6,.30m,15),
        ["claude-sonnet-4-5"]=new(3,3.75m,6,.30m,15),
        ["claude-sonnet-4"]=new(3,3.75m,6,.30m,15),
        ["claude-haiku-5-5"]=new(.10m,.125m,.20m,.01m,.50m){LongPromptAbove=100_000,LongPrompt=new(.50m,.625m,1,.05m,2.50m)},
        ["claude-haiku-4-5"]=new(1,1.25m,2,.10m,5),
        ["claude-haiku-3-5"]=new(.8m,1,1.6m,.08m,4)
    };
    private static readonly string[] Models=Catalog.Keys.OrderByDescending(x=>x.Length).ToArray();
    public static Rates? Find(string model)
    {
        if(string.IsNullOrWhiteSpace(model))return null;
        foreach(var key in Models)
            if(model.Equals(key,StringComparison.OrdinalIgnoreCase)||DatedSnapshot(model,key))return Catalog[key];
        return null;
    }
    // Only a date suffix names the same model (claude-sonnet-5-20260901); claude-sonnet-5-5 is another model.
    private static bool DatedSnapshot(string model,string key)=>model.Length>=key.Length+9&&model.StartsWith(key+"-",StringComparison.OrdinalIgnoreCase)&&
        model.AsSpan(key.Length+1).Length==8&&model.AsSpan(key.Length+1).IndexOfAnyExceptInRange('0','9')<0;
    public static Result Summarize(IEnumerable<ClaudeCodeUsage> rows)
    {
        decimal cost=0;long priced=0,unpriced=0;bool assumed5m=false;
        foreach(var row in rows)
        {
            var rates=Find(row.Model)?.For(row);
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
