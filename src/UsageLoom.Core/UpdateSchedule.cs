namespace UsageLoom.Core;

public static class UpdateSchedule
{
    public static int NormalizeHours(int hours) => hours is 1 or 6 or 12 or 24 or 72 or 168 ? hours : 24;

    public static bool IsDue(bool enabled, int hours, DateTimeOffset? lastCheck, DateTimeOffset now)
        => enabled && (lastCheck is null || now - lastCheck.Value >= TimeSpan.FromHours(NormalizeHours(hours)));
}
