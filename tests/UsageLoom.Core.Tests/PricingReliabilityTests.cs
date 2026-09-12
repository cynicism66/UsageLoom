using System.Text.Json;
using UsageLoom.Core;

internal static class PricingReliabilityTests
{
    private static void Check(bool condition,string message="Pricing reliability assertion failed")
    {
        if(!condition)throw new InvalidOperationException(message);
    }
    private static PricingContext Context(string? actual=null,string? requested=null)=>
        new(RequestInputTokens:100,RegionalProcessing:false,ValuationDate:new(2026,9,12))
        {ActualServiceTier=actual,RequestedServiceTier=requested};

    public static void Run(Action<string,Action> test)
    {
        test("服务模式实际响应优先且 Fast 与 Priority 只计一次倍率",()=>
        {
            foreach(var actual in new[]{"default","standard"," DEFAULT "})
            {
                var context=Context(actual,"priority") with{ServiceTier="flex",FastMode=true};
                Check(Pricing.ResolveMode(context)==("standard","actual-response"));
                var result=Pricing.Calculate("gpt-6-astra",new(100),context:context);
                Check(result.Cost==.001m&&result.Priced==100&&result.Unpriced==0);
                Check(result.EffectiveServiceTier=="standard"&&result.ServiceTierEvidence=="actual-response"&&!result.HasConditionalAssumptions);
            }
            foreach(var actual in new[]{"fast","priority"," PRIORITY "})
            {
                var context=Context(actual,"fast") with{FastMode=true,ServiceTier="priority"};
                Check(Pricing.ResolveMode(context)==("fast","actual-response"));
                var result=Pricing.Calculate("gpt-6-astra",new(100),context:context);
                Check(result.Cost==.002m&&result.Notes.Count(n=>n==L10n.T("s84151728B0A6"))==1);
                Check(!result.HasConditionalAssumptions,"已核实 Fast 倍率说明不是模式假设");
                Check(result.Status==L10n.T("s387A9E6023DB"));
            }
        });
        test("请求档位仅作为设置证据，不冒充实际处理模式",()=>
        {
            var fast=Pricing.Calculate("gpt-6-astra",new(100),context:Context(requested:"priority"));
            Check(fast.Cost==.002m&&fast.EffectiveServiceTier=="fast"&&fast.ServiceTierEvidence=="request-setting");
            Check(fast.HasConditionalAssumptions&&fast.Notes.Contains(L10n.T("pricing.modeRequested")));
            var standard=Context(requested:"default") with{FastMode=true};
            Check(Pricing.ResolveMode(standard)==("standard","request-setting"));
            Check(Pricing.Calculate("gpt-6-astra",new(100),context:standard).Cost==.001m);
            var projected=Context() with{ServiceTier="priority",ServiceTierEvidence="actual-response"};
            Check(Pricing.ResolveMode(projected)==("fast","actual-response"));
            Check(Pricing.ResolveMode(projected with{ServiceTierEvidence="request-setting"})==("fast","request-setting"));
        });
        test("Batch Flex 与 Fast 为互斥档位，旧矛盾设置不能叠乘",()=>
        {
            foreach(var tier in new[]{"batch","flex"})
            {
                var actual=Context(tier,"priority") with{FastMode=true};
                Check(Pricing.Calculate("gpt-6-astra",new(100),context:actual).Cost==.0005m);
                var requested=Context(requested:tier) with{FastMode=true};
                Check(Pricing.Calculate("gpt-6-astra",new(100),context:requested).Cost==.0005m);
                var contradictory=Context() with{ServiceTier=tier,FastMode=true};
                Check(Pricing.ResolveMode(contradictory)==("unknown","conflicting-settings"));
                var fallback=Pricing.Calculate("gpt-6-astra",new(100),context:contradictory);
                Check(!fallback.HasAmount&&fallback.Cost==0&&fallback.Unpriced==100&&fallback.HasConditionalAssumptions&&fallback.Notes.Contains(L10n.T("pricing.modeConflict")));
                Check(fallback.EffectiveServiceTier=="unknown"&&fallback.Priced+fallback.Unpriced==100);
            }
        });
        test("旧缺失模式保持未知并明确基础价假设，序列化兼容旧上下文",()=>
        {
            foreach(var context in new PricingContext?[]{null,new(),JsonSerializer.Deserialize<PricingContext>("{}"),
                Context(requested:"auto"),Context(actual:"unknown",requested:"priority"),Context(actual:"",requested:"priority")})
            {
                Check(Pricing.ResolveMode(context).Tier=="unknown");
                var result=Pricing.Calculate("gpt-6-astra",new(100),context:context);
                Check(result.Cost==.001m&&result.Priced==100&&result.HasConditionalAssumptions);
                Check(result.Notes.Contains(L10n.T("pricing.modeUnknown")));
            }
            foreach(var tier in new string?[]{null,""," "})
            {
                var result=Pricing.Calculate("gpt-6-astra",new(100),context:Context() with{ServiceTier=tier!});
                Check(!result.HasAmount&&result.Unpriced==100,"显式损坏的旧档位不应悄悄计价");
            }
            foreach(var actual in new[]{""," ","auto","unknown","garbage-mode"})
            {
                var context=Context(actual,"priority") with{FastMode=true};
                Check(Pricing.ResolveMode(context).Evidence=="unknown","非有效实际档位不能冒充响应核实");
                var estimate=Pricing.Calculate("gpt-6-astra",new(100),context:context);
                Check(estimate.ServiceTierEvidence=="unknown"&&estimate.HasConditionalAssumptions&&estimate.Priced+estimate.Unpriced==100);
                Check(estimate.EffectiveServiceTier!="fast","未知实际字段不能回退到旧请求 Fast");
            }
            var legacy=new PricingContext(100,false,"priority",true,false,new(2026,9,12));
            Check(Pricing.ResolveMode(legacy)==("fast","legacy-explicit"));
            Check(Pricing.Calculate("gpt-6-astra",new(100),context:legacy).Cost==.002m);
            var modern=Context("default","priority") with{ReasoningEffort="ultra",ServiceTierEvidence="actual-response"};
            var restored=JsonSerializer.Deserialize<PricingContext>(JsonSerializer.Serialize(modern));
            Check(restored==modern&&Pricing.ResolveMode(restored)==("standard","actual-response"));
        });
        test("Ultra 思考强度没有附加倍率，长上下文和 Fast 分别应用一次",()=>
        {
            var usage=new TokenUsage(100,20,10,20,5);
            var standard=Context("default") with{RequestInputTokens=272000};
            var fast=standard with{ActualServiceTier="priority",RequestedServiceTier="fast",FastMode=true,RequestInputTokens=272001};
            Check(Pricing.Calculate("gpt-6-astra",usage,context:standard).Cost==.001845m);
            var expected=Pricing.Calculate("gpt-6-astra",usage,context:fast);
            Check(expected.Cost==.00638m&&expected.Priced==120&&expected.Unpriced==0);
            foreach(var effort in new[]{"low","high","max","ultra","ULTRA",null})
            {
                var result=Pricing.Calculate("gpt-6-astra",usage,context:fast with{ReasoningEffort=effort});
                Check(result.Cost==expected.Cost&&result.Priced==usage.Total&&!result.HasConditionalAssumptions);
            }
            var flex=fast with{ActualServiceTier="flex"};
            Check(Pricing.Calculate("gpt-6-astra",usage,context:flex).Cost==.001595m);
        });
        test("其他模型未知 Fast 倍率不套用 Astra 价格，Token 数保持完整",()=>
        {
            var usage=new TokenUsage(100,20,10,20,5);
            foreach(var model in new[]{"gpt-5.6-sol","gpt-5.6-terra","gpt-5.6-luna","unknown-model"})
            {
                var result=Pricing.Calculate(model,usage,context:Context("priority"));
                Check(!result.HasAmount&&result.Priced==0&&result.Unpriced==usage.Total);
                Check(result.EffectiveServiceTier=="fast"&&result.ServiceTierEvidence=="actual-response");
            }
            var unknown=Pricing.Calculate("gpt-6-astra",usage,context:Context("ultrafast"));
            Check(!unknown.HasAmount&&unknown.Unpriced==usage.Total&&unknown.EffectiveServiceTier=="ultrafast"&&unknown.ServiceTierEvidence=="unknown"&&unknown.HasConditionalAssumptions);
            var partial=Pricing.Calculate("gpt-5.6-sol",usage,context:Context("default") with{RequestInputTokens=300000});
            Check(partial.Priced==90&&partial.Unpriced==30&&partial.Priced+partial.Unpriced==usage.Total);
            Check(usage==new TokenUsage(100,20,10,20,5));
        });
        test("价格汇总保留混合模式和条件估算信息，不影响总 Token",()=>
        {
            UsageEvent Event(string id,PricingContext? context,string model="gpt-6-astra")=>
                new(id,"s","p",model,"main",null,"",new(100),Pricing:context);
            var rows=new[]{Event("actual",Context("priority")),Event("requested",Context(requested:"priority")),
                Event("unknown",Context()),Event("unpriced",Context("priority"),"unknown-model")};
            var result=Pricing.Summarize(rows);
            Check(result.Cost==.005m&&result.Priced==300&&result.Unpriced==100);
            Check(result.Priced+result.Unpriced==rows.Sum(e=>e.Tokens.Total));
            Check(result.EffectiveServiceTier=="mixed"&&result.ServiceTierEvidence=="mixed"&&result.HasConditionalAssumptions);
            Check(result.Notes.Contains(L10n.T("pricing.modeRequested"))&&result.Notes.Contains(L10n.T("pricing.modeUnknown")));
            Check(Pricing.Summarize([]) is {EffectiveServiceTier:"unknown",ServiceTierEvidence:"unknown",HasConditionalAssumptions:false});
        });
    }
}
