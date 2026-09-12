namespace UsageLoom.Core;

public sealed class CapacityBatchSchedule(DateTimeOffset startedAt)
{
    public DateTimeOffset NextAt { get; private set; }=startedAt.AddMinutes(5);
    public bool Dirty { get; private set; }=true;
    public bool RevalidationPending { get; private set; }
    public void RequestRevalidation(DateTimeOffset now){RevalidationPending=true;Dirty=true;NextAt=now;}
    public void CompleteRevalidation()=>RevalidationPending=false;
    public void MarkDirty()=>Dirty=true;
    public void RetrySoon(DateTimeOffset now){Dirty=true;NextAt=now.AddSeconds(5);}
    public bool TryBegin(DateTimeOffset now,bool force=false,bool initialReady=false)
    {
        if(now<NextAt.AddMinutes(-5))NextAt=now; // Clock correction must not postpone retries indefinitely.
        if(!force&&(!Dirty||!initialReady&&now<NextAt))return false;
        Dirty=false;NextAt=now.AddMinutes(5);return true;
    }
}
