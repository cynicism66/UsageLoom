namespace UsageLoom.Core;

// Presentation evidence only: never use this state to accept a sample or mutate the ledger.
public sealed record CapacityPendingSample(string Key,DateTimeOffset Reset,double BaselineUsed,double Used,
    bool HasThresholdSnapshot,bool HasLaterSnapshot,bool LogsReady);

public static class CapacitySamplingProgress
{
    public const double MinimumBlockPercent=3;

    // A quota response can change the weekly context before the next batch runs.
    // This only invalidates presentation; the ledger decides which samples survive.
    public static bool WeeklyContextChanged(IEnumerable<QuotaWindow> before,IEnumerable<QuotaWindow> after)
    {
        var previous=before.Where(w=>w.IsPrimary&&w.Minutes==10080).ToList();
        var current=after.Where(w=>w.IsPrimary&&w.Minutes==10080).ToList();
        return previous.Count!=current.Count||current.Any(w=>
            previous.FirstOrDefault(p=>p.Key==w.Key) is not {} old||
            (old.ResetsAt is {} a&&w.ResetsAt is {} b?Math.Abs((a-b).TotalMinutes)>2:old.ResetsAt!=w.ResetsAt)||
            !double.IsFinite(w.Used)||w.Used<old.Used);
    }

    public static string Describe(double pending,CapacityPendingSample? evidence)
    {
        if(!double.IsFinite(pending)||pending<0)return L10n.T("capacity.stageChecking");
        if(pending<.0001)return L10n.T("capacity.stageWaitingUsage");
        // A rounded "3/3, 0 more" would contradict the calculation threshold.
        if(pending<MinimumBlockPercent&&MinimumBlockPercent-pending<.1)
            return L10n.T("capacity.stageCollectingFractional");
        if(pending<MinimumBlockPercent)
            return L10n.F("capacity.stageCollecting",pending,MinimumBlockPercent,MinimumBlockPercent-pending);
        if(evidence is not {HasThresholdSnapshot:true})return L10n.T("capacity.stageChecking");
        if(!evidence.HasLaterSnapshot)return L10n.T("capacity.stageWaitingSnapshot");
        if(!evidence.LogsReady)return L10n.T("capacity.stageWaitingLogs");
        return L10n.T("capacity.stageReady");
    }
}
