using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private bool capacityMaintenanceBusy;
    private Task<string>? capacityMaintenanceTask;
    internal List<UsageEvent> CapacityRepairCandidates()
    {
        var reset=Quota.PrimaryWindows.FirstOrDefault(w=>w.Minutes==10080)?.ResetsAt;
        return reset is null?[]:Events.Where(e=>e.AccountScope is null&&e.Timestamp>reset.Value.AddDays(-7)&&
            e.Timestamp<=DateTimeOffset.UtcNow&&!string.Equals(e.Model,"gpt-5.3-codex-spark",StringComparison.OrdinalIgnoreCase)).ToList();
    }
    internal Task<string> MaintainCapacityAsync(string action)
    {
        if(capacityMaintenanceBusy)throw new InvalidOperationException(L10n.T("maintenance.busy"));
        return capacityMaintenanceTask=MaintainCapacityCoreAsync(action);
    }
    private async Task<string> MaintainCapacityCoreAsync(string action)
    {
        if(action is not ("repair" or "clear" or "restart"))throw new ArgumentException("Unknown maintenance action");
        if(IsDemo||capacityMaintenanceBusy||scanning||refreshing||attributionBusy||Authorizing||quitting)
            throw new InvalidOperationException(L10n.T("maintenance.busy"));
        if(action!="clear"&&(!Config.CapacityEnabled||!Quota.Fresh||Quota.AccountKey is null||
            !Quota.PrimaryWindows.Any(w=>w.Minutes==10080&&w.ResetsAt>DateTimeOffset.UtcNow)))
            throw new InvalidOperationException(L10n.T("maintenance.needQuota"));
        capacityMaintenanceBusy=true;capacityEpoch++;
        string backup;string detail="";
        try
        {
            if(capacityTask is {} task)await task;
            await capacityWorkGate.WaitAsync(lifetime.Token);
            try
            {
                if(action=="repair")
                {
                    backup=await Task.Run(()=>store.BackupCapacityData(),lifetime.Token);
                    scanning=true;historyRevision++;
                    await ScanCoreAsync(Config.CodexHome,true,false,
                        client.AttributionIdentity.Get(Config.AuthorizedAccount?AuthorizedHome:Config.CodexHome,DateTimeOffset.UtcNow));
                    detail=Message;
                }
                else
                {
                    var at=DateTimeOffset.UtcNow;
                    var baseline=action=="restart"?new QuotaObservation(at,Quota.AccountKey,Quota.Plan?.Trim().ToLowerInvariant(),Pricing.CatalogVersion,
                        Quota.PrimaryWindows.Where(w=>w.Minutes==10080).ToList()):null;
                    backup=await Task.Run(()=>store.MaintainCapacity(action=="clear",at,baseline),lifetime.Token);
                    capacityEstimator.Reset();capacityDisplayIdentity=null;capacityDisplayWindows=[];
                    capacityInterruptionCount=0;capacityInterruptionDetails="";WeeklyCapacity=[];capacityLastCalculated=null;
                    capacityFeedback=L10n.T("maintenance."+action+"Done");
                    if(action=="restart")RestoreCapacity();
                }
                capacityBatch.MarkDirty();
            }
            finally{capacityWorkGate.Release();}
        }
        finally{capacityMaintenanceBusy=false;if(!quitting)Changed?.Invoke();}
        if(action!="clear")await CalculateCapacityNowAsync();
        return L10n.F("maintenance.backup",backup)+(detail.Length>0?"\n"+detail:"")+"\n"+CapacityCalculationStatus;
    }
}
