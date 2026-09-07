namespace UsageLoom.Core;

public static class SamplingSchedule
{
    public static int QuotaPeriod(bool visible,DateTimeOffset now,DateTimeOffset lastUsage,int activeSeconds,int idleSeconds)
        => visible || (now>=lastUsage && now-lastUsage<TimeSpan.FromMinutes(5))
            ? Math.Min(activeSeconds,idleSeconds) : idleSeconds;
}
