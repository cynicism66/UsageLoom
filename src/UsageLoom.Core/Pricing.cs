namespace UsageLoom.Core;

public sealed record Price(decimal Input,decimal? Cached,decimal? Write,decimal Output);
public sealed record PricingContext(long? RequestInputTokens = null, bool? SessionLongContext = null,
    string ServiceTier = "standard", bool? FastMode = null, bool? RegionalProcessing = null, DateOnly? ValuationDate = null);
public sealed record ModelPrice(string Model, Price Rates, Uri Source, DateOnly CheckedOn, DateOnly? EffectiveFrom,
    DateOnly? EffectiveUntil, string LongContextScope = "none", bool CacheLongMultiplierKnown = false,
    bool AstraServiceTiers = false, bool RegionalSurcharge = false, DateOnly? PromotionAtLeastThrough = null);
public sealed record Estimate(decimal Cost,long Priced,long Unpriced,string? Reason)
{
    public ModelPrice? Basis { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool HasAmount => Priced > 0;
    public string Status => !HasAmount ? "无法定价" : Unpriced > 0 ? "部分估算" : Notes.Count > 0 ? "有条件估算" : "标准 API 等价估算";
    public string DisplayAmount => HasAmount ? "$" + Cost.ToString("N4", System.Globalization.CultureInfo.InvariantCulture) : "暂不可估算";
}
public static class Pricing
{
    // 有日期的离线目录；未覆盖的模型/子分类保持未知，不推断订阅账单。
    public const string VerifiedDate="2026-09-07";
    public const string CatalogVersion="2026-09-07.1";
    private static ModelPrice Entry(string model,Price price,string scope="none",bool cache=false,bool regional=false,bool tiers=false,DateOnly? promo=null) =>
        new(model,price,new Uri("https://developers.openai.com/api/docs/models/"+model),new(2026,9,7),null,null,scope,cache,tiers,regional,promo);
    private static readonly Dictionary<string,ModelPrice> Catalog=new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.4-mini"]=Entry("gpt-5.4-mini",new(.75m,.075m,null,4.5m),regional:true),
        ["gpt-5.4"]=Entry("gpt-5.4",new(2.5m,.25m,null,15m),"session",regional:true),
        ["gpt-5.3-codex"]=Entry("gpt-5.3-codex",new(1.75m,.175m,null,14m)),
        ["gpt-5.2-codex"]=Entry("gpt-5.2-codex",new(1.75m,.175m,null,14m)),
        ["gpt-5.5"]=Entry("gpt-5.5",new(5m,.5m,null,30m),"session",regional:true),
        ["gpt-5.6-sol"]=Entry("gpt-5.6-sol",new(4m,.4m,5m,20m),"request",promo:new(2026,11,21)),
        ["gpt-5.6-terra"]=Entry("gpt-5.6-terra",new(2m,.2m,2.5m,12m),"request"),
        ["gpt-5.6-luna"]=Entry("gpt-5.6-luna",new(.2m,.02m,.25m,1.2m),"request"),
        ["gpt-6-astra"]=Entry("gpt-6-astra",new(10m,1m,12.5m,50m),"request",cache:true,tiers:true)
    };
    private static readonly Dictionary<string,string> Aliases=new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6"]="gpt-5.6-sol",["gpt-5.4-mini-2026-03-17"]="gpt-5.4-mini",
        ["gpt-5.4-2026-03-05"]="gpt-5.4",["gpt-5.5-2026-04-23"]="gpt-5.5"
    };
    public static IReadOnlyList<ModelPrice> Entries => Catalog.Values.ToList();
    public static ModelPrice? Find(string model) => Catalog.GetValueOrDefault(Aliases.GetValueOrDefault(model.Trim())??model.Trim());
    public static Estimate Calculate(UsageEvent item) => Calculate(item.Model,item.Tokens,context:item.Pricing);
    public static Estimate Calculate(string model,TokenUsage usage,Price? testPrice=null,PricingContext? context=null)
    {
        if(!usage.Valid)return new(0,0,0,"分类关系无效");
        var basis=testPrice is null?Find(model):null;
        if(testPrice is null&&basis is null)return new(0,0,usage.Total,"模型未定价");
        var price=testPrice??basis!.Rates;
        if(price.Input<0||price.Output<0||price.Cached<0||price.Write<0)return new(0,0,usage.Total,"价格参数无效");
        var notes=new List<string>();context??=new();
        decimal inputMultiplier=1,outputMultiplier=1,cacheMultiplier=1,tierMultiplier=1,regionalMultiplier=1;
        bool cachedKnown=price.Cached is not null,writeKnown=price.Write is not null;
        if(basis is not null)
        {
            var date=context.ValuationDate??DateOnly.FromDateTime(DateTime.UtcNow);
            if(basis.EffectiveFrom is {} from&&date<from||basis.EffectiveUntil is {} until&&date>until)
                return new(0,0,usage.Total,"价格不在有效区间"){Basis=basis};
            if(basis.PromotionAtLeastThrough is {} promotion&&date>promotion)
                return new(0,0,usage.Total,"促销保证期已过，需重新核对价格"){Basis=basis};
            if(date<basis.CheckedOn)notes.Add("按所选目录价格重估历史，不代表历史账单");
            if(date.DayNumber-basis.CheckedOn.DayNumber>30)notes.Add("价格核对已超过 30 天，建议复核");
            bool? longContext=basis.LongContextScope switch
            {
                "request"=>context.RequestInputTokens is {} tokens?tokens>272000:null,
                "session"=>context.SessionLongContext,
                _=>false
            };
            if(context.RequestInputTokens<0)return new(0,0,usage.Total,"请求输入计数无效"){Basis=basis};
            if(longContext is null)notes.Add("缺少可靠的"+(basis.LongContextScope=="session"?"全会话":"单请求")+"输入条件，按基础价格估算；未核实长上下文倍率");
            if(longContext==true)
            {
                inputMultiplier=2;outputMultiplier=1.5m;
                if(basis.CacheLongMultiplierKnown)cacheMultiplier=2;
                else {cachedKnown=false;writeKnown=false;notes.Add("长上下文缓存倍率尚未核实，缓存部分暂不计价");}
                notes.Add("长上下文：输入 ×2、输出 ×1.5"+(basis.CacheLongMultiplierKnown?"、缓存 ×2":""));
            }
            if(string.IsNullOrWhiteSpace(context.ServiceTier))
                return new(0,0,usage.Total,"服务档位缺失，无法确认适用价格"){Basis=basis};
            var tier=context.ServiceTier.Trim().ToLowerInvariant();
            if(tier is not ("standard" or "default"))
            {
                if(basis.AstraServiceTiers&&tier is "batch" or "flex")tierMultiplier=.5m;
                else return new(0,0,usage.Total,"该模型服务档位尚未核实"){Basis=basis};
                notes.Add(tier+" 档位 ×0.5");
            }
            if(context.FastMode==true)
            {
                if(!basis.AstraServiceTiers)return new(0,0,usage.Total,"该模型 Fast 倍率尚未核实"){Basis=basis};
                tierMultiplier*=2;notes.Add("Fast 模式 ×2");
            }
            else if(context.FastMode is null&&basis.AstraServiceTiers)notes.Add("未核实 Fast 模式，按非 Fast 估算");
            if(context.RegionalProcessing==true)
            {
                if(!basis.RegionalSurcharge)return new(0,0,usage.Total,"该模型区域处理倍率尚未核实"){Basis=basis};
                regionalMultiplier=1.1m;notes.Add("区域处理 ×1.1");
            }
            else if(context.RegionalProcessing is null&&basis.RegionalSurcharge)notes.Add("未核实区域处理，未加区域附加费");
        }
        var regular=usage.Input-usage.Cached-usage.CacheWrite;
        var unpriced=(!cachedKnown?usage.Cached:0)+(!writeKnown?usage.CacheWrite:0);
        try
        {
            var cost=(regular*price.Input*inputMultiplier+usage.Cached*(cachedKnown?price.Cached??0:0)*cacheMultiplier+usage.CacheWrite*(writeKnown?price.Write??0:0)*cacheMultiplier+usage.Output*price.Output*outputMultiplier)*tierMultiplier*regionalMultiplier/1_000_000m;
            return new(cost,usage.Total-unpriced,unpriced,unpriced>0?"部分子分类缺价或倍率未知":null){Basis=basis,Notes=notes};
        }
        catch(OverflowException){return new(0,0,usage.Total,"费用计算溢出"){Basis=basis};}
    }
    public static Estimate Summarize(IEnumerable<UsageEvent> events)
    {
        var values=events.Select(Calculate).ToList();
        return new(values.Sum(v=>v.Cost),values.Sum(v=>v.Priced),values.Sum(v=>v.Unpriced),
            string.Join("；",values.Select(v=>v.Reason).Where(r=>r is not null).Distinct()))
            {Notes=values.SelectMany(v=>v.Notes).Distinct().ToList()};
    }
}
