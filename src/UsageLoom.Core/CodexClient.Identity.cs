using System.Text.Json;

namespace UsageLoom.Core;

public sealed partial class CodexClient
{
    // Identity has its own short-lived lease. Quota failures and quota polling
    // backoff must not prevent a read-only account check from renewing it.
    public async Task<bool> RenewAttributionIdentityAsync(string? executable,string home,CancellationToken ct,bool reuseBackend=false,bool managedAccount=false)
    {
        await requests.WaitAsync(ct);
        try
        {
            if(managedAccount&&reuseBackend)throw new InvalidOperationException(L10n.T("sFBEA9728D07D"));
            if(IsConnected&&(proxyConnection!=reuseBackend||managedConnection!=managedAccount||connectedHome!=Path.GetFullPath(home)))await StopAsync();
            if(!IsConnected)
            {
                if(TestProcessFactory is null&&ExecutableResolver(executable) is null)
                {AttributionIdentity.Clear();return false;}
                await StartAsync(executable,home,ct,reuseBackend,managedAccount);
            }
            var known=AttributionIdentity.LastKnown(home);
            var revision=Volatile.Read(ref accountRevision);
            var before=await RequestAsync("account/read",new{refreshToken=false},ct);
            if(!before.TryGetProperty("account",out var first)||first.ValueKind!=JsonValueKind.Object||first.Text("type")!="chatgpt")
            {accountKey=null;InvalidateAccount(first.ValueKind==JsonValueKind.Object);return false;}
            var key=ReadAccountKey(first).Key;
            if(string.IsNullOrWhiteSpace(key))
            {accountKey=null;InvalidateAccount(false);return false;}
            var after=await RequestAsync("account/read",new{refreshToken=false},ct);
            if(revision!=Volatile.Read(ref accountRevision)||!after.TryGetProperty("account",out var second)||second.ValueKind!=JsonValueKind.Object||
                second.Text("type")!="chatgpt"||ReadAccountKey(second).Key!=key)
            {accountKey=null;InvalidateAccount(true);return false;}
            if(known is not null&&known!=key)InvalidateAccount(true);
            ct.ThrowIfCancellationRequested();
            accountKey=key;
            AttributionIdentity.Observe(key,home,DateTimeOffset.UtcNow);
            // account/updated can arrive on the output pump between the check
            // above and Observe. It must always win over a finishing old read.
            if(revision!=Volatile.Read(ref accountRevision))
            {accountKey=null;AttributionIdentity.Clear();return false;}
            log.Write("INFO","AttributionIdentity","Account identity renewed independently of quota polling");
            return true;
        }
        catch(Exception ex)
        {
            if(ex is CodexRpcException rpc&&rpc.Kind==RpcFailureKind.Authentication)InvalidateAccount(false);
            // Transport failure does not prove an account change. Preserve the
            // existing lease, which still expires after five minutes.
            await StopAsync();throw;
        }
        finally{requests.Release();}
    }
}
