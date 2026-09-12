using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private Task? identityTask;
    private bool identityRefreshing;
    private DateTimeOffset nextIdentityAttemptAt=DateTimeOffset.MinValue;

    private void RenewIdentityIfDue()
    {
        if(IsDemo||quitting||!Config.AutoRefresh||identityRefreshing||refreshing||Authorizing||attributionBusy||capacityMaintenanceBusy)return;
        var source=CurrentCapacitySource();
        if(!appliedCapacitySource.Matches(source))return;
        var home=Config.AuthorizedAccount?AuthorizedHome:Config.CodexHome;
        var now=DateTimeOffset.UtcNow;
        if(now<nextIdentityAttemptAt&&nextIdentityAttemptAt-now<=TimeSpan.FromMinutes(1)||!client.AttributionIdentity.NeedsRenewal(home,now))return;
        identityRefreshing=true;nextIdentityAttemptAt=now.AddMinutes(1);
        identityTask=RenewIdentityCoreAsync(source,home,configurationGeneration,quotaCancellation.Token);
    }
    private async Task RenewIdentityCoreAsync(CapacitySource source,string home,int generation,CancellationToken ct)
    {
        try
        {
            // Only account/read: do not reset quota backoff or manufacture a quota snapshot.
            await client.RenewAttributionIdentityAsync(Config.CliPath,home,ct,Config.ReuseBackend&&!Config.AuthorizedAccount,Config.AuthorizedAccount);
            if(quitting||ct.IsCancellationRequested||generation!=configurationGeneration||!source.Matches(CurrentCapacitySource()))
                client.AttributionIdentity.Clear();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Program.Log.Write("WARN","AttributionRenewal",ex.Message);}
        finally{identityRefreshing=false;}
    }
}
