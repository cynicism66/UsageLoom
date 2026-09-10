namespace UsageLoom.Core;

// Compared only on the UI thread. An event-list identity is an immutable scan snapshot.
public sealed record CapacityInputStamp(object Events,int Epoch,int Configuration,string? Account,string? Plan,DateTimeOffset? FetchedAt)
{
    public IReadOnlyList<QuotaWindow>? Windows { get; init; }
    public bool Matches(object events,int epoch,int configuration,QuotaState quota,bool enabled,bool quitting)=>
        !quitting&&enabled&&quota.Fresh&&ReferenceEquals(Events,events)&&Epoch==epoch&&Configuration==configuration&&
        Account==quota.AccountKey&&Plan==quota.Plan&&(FetchedAt==quota.FetchedAt||
            Windows is not null&&Windows.SequenceEqual(quota.PrimaryWindows));
}
