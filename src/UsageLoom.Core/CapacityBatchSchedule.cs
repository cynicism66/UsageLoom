namespace UsageLoom.Core;

public sealed class CapacityBatchSchedule(DateTimeOffset startedAt)
{
    public DateTimeOffset NextAt { get; private set; }=startedAt.AddMinutes(5);
    public bool Dirty { get; private set; }=true;
    public void MarkDirty()=>Dirty=true;
    public bool TryBegin(DateTimeOffset now,bool force=false)
    {
        if(!force&&(!Dirty||now<NextAt))return false;
        Dirty=false;NextAt=now.AddMinutes(5);return true;
    }
}
