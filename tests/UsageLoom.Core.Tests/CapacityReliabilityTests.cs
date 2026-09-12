using System.Text.Json;
using System.Text.Json.Nodes;
using UsageLoom.Core;

internal static class CapacityReliabilityTests
{
    public static void Run(Action<string,Action> test)
    {
        test("容量模式证据按 Token 独立加权，实际档位优先于请求",EvidenceWeights);
        test("混合模型模式与未知字段不切断额度采样，仅有效区间累计证据",MixedAcceptedSamples);
        test("旧模式缓存保留 Token 样本但不得伪装证据已重估",LegacyCacheEvidence);
        test("模式证据序列化合并守恒，破损证据不得显示已核实",ProfileRoundTrip);
    }

    private static UsageEvent Event(string id,long tokens,PricingContext? context,string model="gpt-6-astra",DateTimeOffset? at=null,string account="a")=>
        new(id,"session","project",model,"main",at??DateTimeOffset.UtcNow,"2026-09-12",new(tokens),Pricing:context){AccountScope=account};
    private static PricingContext Actual(string tier,string? effort=null)=>new(100,RegionalProcessing:false,ValuationDate:new(2026,9,7))
        {ActualServiceTier=tier,ReasoningEffort=effort};
    private static PricingContext Requested(string tier,string? effort=null)=>new(100,RegionalProcessing:false,ValuationDate:new(2026,9,7))
        {RequestedServiceTier=tier,ReasoningEffort=effort};
    private static void Check(bool condition,string message="Capacity reliability assertion failed")
    {if(!condition)throw new InvalidOperationException(message);}

    private static void EvidenceWeights()
    {
        var profile=UsagePricingProfile.From([
            Event("actual",40,Actual("default","high") with{RequestedServiceTier="priority",FastMode=true}),
            Event("request",30,Requested("priority","low")),
            Event("unknown",20,null,"gpt-5.6-sol"),
            Event("other",10,Actual("flex","medium"))]);
        Check(profile.IsValidFor(100));
        Check(profile.ActualTokens==50&&profile.RequestedTokens==30&&profile.UnknownTokens==20);
        Check(profile.ActualCoverage==50&&profile.RequestedCoverage==30&&profile.UnknownCoverage==20);
        Check(profile.Modes["standard"]==40&&profile.Modes["fast"]==30&&profile.Modes["unknown"]==20&&profile.Modes["flex"]==10);
        Check(profile.Models["gpt-6-astra"]==80&&profile.Models["gpt-5.6-sol"]==20);
        Check(profile.Reasoning["high"]==40&&profile.Reasoning["low"]==30&&profile.Reasoning["unknown"]==20&&profile.IsMixed);
        Check(profile.ConditionalTokens==50,"仅请求与未知模式不能因100%有价格变为条件已核实");
        var estimatorValue=new WeeklyCapacityEstimate("weekly","weekly",1000,100,10,3,0,"mature",DateTimeOffset.Now.AddDays(6))
            {EstimatedDollars=10,PricingCoverage=100,EvidenceVersion=UsagePricingProfile.CurrentVersion,PricingProfile=profile};
        Check(estimatorValue.HasCurrentPricingEvidence&&estimatorValue.PricingCoverage==100&&profile.ActualCoverage==50);
        var rejectedActual=UsagePricingProfile.From([Event("unsupported",5,Actual("future-tier"))]);
        Check(rejectedActual.ActualTokens==0&&rejectedActual.UnknownTokens==5);
        var actualAuto=UsagePricingProfile.From([Event("auto",5,Actual("auto") with{RequestedServiceTier="fast"})]);
        Check(actualAuto.ActualTokens==0&&actualAuto.RequestedTokens==0&&actualAuto.UnknownTokens==5);
    }

    private static TemporalCapacityResult SampleResult()
    {
        var at=DateTimeOffset.UtcNow.AddHours(-1);var reset=at.AddDays(6);
        QuotaObservation O(int minutes,double used)=>new(at.AddMinutes(minutes),"a","pro",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,reset)]);
        return TemporalCapacity.CalculateStable([O(0,0),O(3,3),O(6,6),O(9,9),O(12,9)],
            [Event("standard",100,Actual("standard","high"),at:at.AddMinutes(1)),
             Event("fast",200,Actual("fast","low"),at:at.AddMinutes(2)),
             Event("unknown",300,null,"gpt-5.6-sol",at.AddMinutes(4)),
             Event("requested",400,Requested("standard","medium"),at:at.AddMinutes(5)),
             Event("wrong-account",500,Actual("fast"),at:at.AddMinutes(8),account:"b")],at.AddMinutes(14));
    }

    private static void MixedAcceptedSamples()
    {
        var result=SampleResult();var sample=result.Cache!.Windows.Single();var profile=sample.PricingProfile!;
        Check(result.Cache.EvidenceVersion==UsagePricingProfile.CurrentVersion);
        Check(sample.Percent==6&&sample.Samples==2&&sample.Tokens==1000&&sample.Excluded==1);
        Check(profile.IsValidFor(1000)&&profile.ActualTokens==300&&profile.RequestedTokens==400&&profile.UnknownTokens==300);
        Check(profile.IsMixed&&profile.Models.Values.Sum()==1000&&profile.Modes.Values.Sum()==1000);
        Check(result.Intervals.Count==3&&result.Intervals.Count(i=>i.Exclusion is null)==2);
        Check(result.Intervals.Single(i=>i.Exclusion is not null).PricingProfile is null);
        Check(result.Intervals.Where(i=>i.Exclusion is null).All(i=>i.PricingProfile?.IsValidFor(i.Tokens)==true));
        Check(result.History.All(c=>c.EvidenceVersion==UsagePricingProfile.CurrentVersion&&c.Windows.All(s=>s.PricingProfile?.IsValidFor(s.Tokens)==true)));
    }

    private static void LegacyCacheEvidence()
    {
        var now=DateTimeOffset.Now;var reset=now.AddDays(6);
        var old=new CapacityCache(4,"a","pro",Pricing.CatalogVersion,now,[new("codex:weekly","weekly",reset,6,6,600,3,600,2,0)]);
        var oldJson=JsonNode.Parse(JsonSerializer.Serialize(old))!.AsObject();oldJson.Remove("EvidenceVersion");
        foreach(var sample in oldJson["Windows"]!.AsArray())sample!.AsObject().Remove("PricingProfile");
        var restored=JsonSerializer.Deserialize<CapacityCache>(oldJson.ToJsonString())!;
        Check(restored.EvidenceVersion==0&&restored.Windows.Single().PricingProfile is null);
        var quota=new QuotaState([new("codex:weekly","weekly",7,10080,reset)],null,now,"ok",true,"a","pro");
        var estimator=new WeeklyCapacityEstimator();estimator.Restore(restored);estimator.InitializeTemporal(quota);
        Check(estimator.Current.Single().ObservedTokens==600&&estimator.Current.Single().ObservedPercent==6);
        Check(!estimator.Current.Single().HasCurrentPricingEvidence&&estimator.Export()!.EvidenceVersion==0);
        estimator.Restore(restored);estimator.InitializeTemporal(quota with{Windows=[quota.Windows[0] with{ResetsAt=reset.AddDays(7),Used=0}]});
        var historical=estimator.DisplayCurrent.Single();
        Check(historical.HistoricalAt is not null&&historical.ObservedTokens==600&&!historical.HasCurrentPricingEvidence);
        foreach(var language in new[]{"zh-CN","en-US"})
        {
            var previous=L10n.Language;try
            {
                L10n.Language=language;
                Check(historical.EvidenceSummary==L10n.T("capacity.evidenceLegacyHistorical"));
                Check(L10n.T("capacity.valuationCoverage")!="capacity.valuationCoverage");
            }finally{L10n.Language=previous;}
        }
    }

    private static void ProfileRoundTrip()
    {
        var cache=SampleResult().Cache!;
        var roundtrip=JsonSerializer.Deserialize<CapacityCache>(JsonSerializer.Serialize(cache))!;
        var profile=roundtrip.Windows.Single().PricingProfile!;
        Check(roundtrip.EvidenceVersion==UsagePricingProfile.CurrentVersion&&profile.IsValidFor(1000));
        Check(cache.Windows.SequenceEqual(roundtrip.Windows),"序列化不应改变采样记录的结构值相等性");
        Check(cache.Windows[0].PricingProfile!.Equals(profile)&&cache.Windows[0].PricingProfile!.GetHashCode()==profile.GetHashCode());
        var reordered=profile with{Modes=profile.Modes.Reverse().ToDictionary(p=>p.Key,p=>p.Value)};
        Check(profile.Equals(reordered)&&profile.GetHashCode()==reordered.GetHashCode(),"模式字典顺序不是证据差异");
        var quota=new QuotaState([new("codex:weekly","weekly",9,10080,roundtrip.Windows[0].ResetsAt)],null,DateTimeOffset.Now,"ok",true,"a","pro");
        var estimator=new WeeklyCapacityEstimator();estimator.Restore(roundtrip);estimator.InitializeTemporal(quota);
        Check(estimator.Current.Single().HasCurrentPricingEvidence&&estimator.Export()!.Windows.Single().PricingProfile!.IsValidFor(1000));
        Check(!(profile with{ActualTokens=profile.ActualTokens+1}).IsValidFor(1000));
        var invalid=profile with{ActualTokens=long.MaxValue,RequestedTokens=long.MaxValue,UnknownTokens=long.MaxValue};
        Check(!invalid.IsValidFor(1000));
        var value=estimator.Current.Single() with{PricingProfile=invalid};Check(!value.HasCurrentPricingEvidence);
        var left=UsagePricingProfile.From([Event("a",25,Actual("standard"))]);
        var right=UsagePricingProfile.From([Event("b",75,Requested("fast"))]);
        var merged=UsagePricingProfile.Merge(left,right);
        Check(merged.IsValidFor(100)&&left.Modes.Count==1&&left.Modes["standard"]==25&&right.Modes.Count==1);
    }
}
