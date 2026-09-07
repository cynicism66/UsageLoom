namespace UsageLoom.Core;

public static class CapacityUsage
{
    // Never infer ownership of old/unassigned history from the current login.
    public static List<UsageEvent> ForAccount(IEnumerable<UsageEvent> events,string? account) =>
        string.IsNullOrWhiteSpace(account)?[]:events.Where(item=>
            string.Equals(item.AccountScope,account,StringComparison.Ordinal)&&
            !string.Equals(item.Model,"gpt-5.3-codex-spark",StringComparison.OrdinalIgnoreCase)).ToList();
}
