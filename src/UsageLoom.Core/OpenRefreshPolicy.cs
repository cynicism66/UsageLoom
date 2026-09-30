namespace UsageLoom.Core;

public static class OpenRefreshPolicy
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromSeconds(60);
    public static bool ShouldRefresh(bool enabled,bool snapshotFresh,DateTimeOffset? observedAt,
        DateTimeOffset lastAttempt,TimeSpan retryDelay,DateTimeOffset now)
    {
        if(!enabled||retryDelay<TimeSpan.Zero||now-lastAttempt<retryDelay)return false;
        if(!snapshotFresh||observedAt is not {} at)return true;
        var age=now-at;
        return age<TimeSpan.Zero||age>StaleAfter;
    }
}
