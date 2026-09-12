using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    internal bool CapacityUiCheck=>IsDemo&&args.Contains("--smoke-test")&&args.Contains("--capacity-ui-check");

    internal void ConfigureCapacityUiPreview(bool historical=false,bool empty=false,bool revalidating=false)
    {
        if(!CapacityUiCheck)throw new InvalidOperationException("Capacity UI fixtures require isolated smoke-test mode");
        Config.CapacityEnabled=true;Config.AutoRefresh=false;
        var now=DateTimeOffset.UtcNow;var reset=now.AddDays(6);
        Quota=new([new("codex:weekly",L10n.T("s475811D50FA9"),8,10080,reset)],1,now,"Isolated capacity UI fixture",true,"capacity-ui-fixture","pro");
        PricingContext Context(string? actual,string? requested,string? effort)=>new(RequestInputTokens:100,RegionalProcessing:false,ValuationDate:new(2026,9,12))
            {ActualServiceTier=actual,RequestedServiceTier=requested,ReasoningEffort=effort};
        Events=[
            new("ui-standard","fixture","fixture","gpt-6-astra","main",now.AddMinutes(-12),DateTime.Today.ToString("yyyy-MM-dd"),new(400000),Pricing:Context("standard",null,"high")),
            new("ui-fast","fixture","fixture","gpt-6-astra","main",now.AddMinutes(-9),DateTime.Today.ToString("yyyy-MM-dd"),new(250000),Pricing:Context("fast",null,"low")),
            new("ui-request","fixture","fixture","gpt-5.6-sol","main",now.AddMinutes(-6),DateTime.Today.ToString("yyyy-MM-dd"),new(200000),Pricing:Context(null,"standard","medium")),
            new("ui-unknown","fixture","fixture","gpt-5.6-luna","main",now.AddMinutes(-3),DateTime.Today.ToString("yyyy-MM-dd"),new(150000))];
        Events=Events.Select(e=>e with{AccountScope=Quota.AccountKey}).ToList();historyLoaded=true;capacityHistoryReady=true;capacityTokenTotal=1000000;
        var profile=UsagePricingProfile.From(Events);
        var sample=new CapacitySample("codex:weekly",L10n.T("s475811D50FA9"),reset,6,6,1000000,79,1000000,2,0){PricingProfile=profile};
        var cache=new CapacityCache(4,Quota.AccountKey!,"pro",Pricing.CatalogVersion,now.AddMinutes(-3),[sample]){EvidenceVersion=UsagePricingProfile.CurrentVersion};
        capacityEstimator.Reset();capacityBatch.CompleteRevalidation();capacityFeedback="";
        capacityInterruptions=[];
        if(empty)
        {
            capacityEstimator.ApplyTemporal(Quota,cache with{Windows=[]},1000000,null);
            capacityInterruptions=[new("codex:weekly",now.AddMinutes(-30),now.AddMinutes(-15),"unverified-ownership")
                {Account=Quota.AccountKey,Plan="pro",PricingVersion=Pricing.CatalogVersion,Reset=reset}];
        }
        else if(historical)
        {
            var old=cache with{EvidenceVersion=0,Windows=[sample with{ResetsAt=reset.AddDays(-7),PricingProfile=null}]};
            capacityEstimator.Restore(old);
            capacityEstimator.InitializeTemporal(Quota);
            capacityEstimator.ApplyTemporal(Quota,cache with{Windows=[]},1000000,null,new Dictionary<string,double>{{"codex:weekly",6}});
        }
        else capacityEstimator.ApplyTemporal(Quota,cache,1000000,null,new Dictionary<string,double>{{"codex:weekly",6}});
        WeeklyCapacity=capacityEstimator.DisplayCurrent;
        if(revalidating){capacityBatch.RequestRevalidation(now);capacityFeedback=L10n.T("capacity.revalidating");}
        Changed?.Invoke();
    }
}
