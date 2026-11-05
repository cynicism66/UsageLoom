using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    internal Dashboard? DataSourceTestFlyout=>ClaudeSettingsCheck?flyout:null;
    internal async Task VerifyDisabledSourceAsync()
    {
        if(!ClaudeSettingsCheck||CodexActive)throw new InvalidOperationException("Disabled-source check requires isolated disabled preview");
        var events=Events;var quota=Quota;var names=SessionNames;var observations=store.ReadQuotaObservations().Count;
        await RefreshQuotaAsync(false);await RefreshQuotaAsync(true);await ScanAsync();await ScanAsync(true,true);await RefreshSessionTitlesAsync(true);
        RenewIdentityIfDue();EvaluateNotifications();
        if(!ReferenceEquals(events,Events)||!ReferenceEquals(quota,Quota)||!ReferenceEquals(names,SessionNames)||store.ReadQuotaObservations().Count!=observations||historyWatchers.Count!=0||client.IsConnected||incremental is not null)
            throw new InvalidOperationException("Disabled Codex source performed collection or changed retained data");
        try{await CalculateCapacityNowAsync();throw new Exception("Disabled source calculated capacity");}catch(InvalidOperationException){}
        try{await AuthorizeAsync();throw new Exception("Disabled source began login");}catch(InvalidOperationException){}
        try{await MaintainCapacityAsync("repair");throw new Exception("Disabled source began repair");}catch(InvalidOperationException){}
        if(store.ReadQuotaObservations().LastOrDefault()?.BarrierReason!="explicit-boundary")throw new InvalidOperationException("Source pause boundary missing");
    }
    internal int SourceBoundaryCount=>store.ReadQuotaObservations().Count(item=>item.BarrierReason=="explicit-boundary");
    internal async Task VerifyLocalOnlySourceAsync()
    {
        if(!ClaudeSettingsCheck||!CodexActive||!Config.CodexLocalOnly)throw new InvalidOperationException("Local-only check requires isolated active local-only preview");
        if(store.ReadQuotaObservations().LastOrDefault()?.BarrierReason!="explicit-boundary")throw new InvalidOperationException("Local-only switch boundary missing");
        if(TrayCodexShown)throw new InvalidOperationException("Local-only Codex still drives the tray");
        var observations=store.ReadQuotaObservations().Count;
        // A leftover live snapshot must be replaced, not refreshed, by a quota request.
        Quota=new([new("codex:primary","weekly",13,10080,DateTimeOffset.Now.AddDays(3))],null,DateTimeOffset.Now,"ok",true,"local-only-probe");
        await RefreshQuotaAsync(false);await RefreshQuotaAsync(true);RenewIdentityIfDue();EvaluateNotifications();
        if(!Quota.IsLocalAccount||Quota.Fresh||Quota.AccountKey is not null||Quota.Status!=L10n.T("source.codexLocalOnlyStatus")||
            store.ReadQuotaObservations().Count!=observations||client.IsConnected||identityRefreshing||WeeklyCapacity.Count!=0)
            throw new InvalidOperationException("Local-only Codex queried the account or kept quota state");
        try{await CalculateCapacityNowAsync();throw new Exception("Local-only source calculated capacity");}catch(InvalidOperationException){}
        try{await AuthorizeAsync();throw new Exception("Local-only source began login");}catch(InvalidOperationException){}
        try{await UseCachedLoginAsync(Config.CliPath??"",Config.CodexHome);throw new Exception("Local-only source used a cached login");}catch(InvalidOperationException){}
    }
    internal async Task VerifyPendingSourceBoundaryAsync()
    {
        if(!ClaudeSettingsCheck)throw new InvalidOperationException("Boundary check requires isolated preview");
        var beforeSource=CurrentCapacitySource();
        sourceSettingsBusy=true;
        try
        {
            try{RequireSourceSettingsIdle();throw new Exception("Concurrent global mutation accepted");}catch(InvalidOperationException){}
            try{await ConfigureCodexAsync(true,"",Path.Combine(Program.DataPath,"concurrent-source"));throw new Exception("Concurrent source save accepted");}catch(InvalidOperationException){}
            try{await ConfigureClaudeAsync(new(true,Path.Combine(Program.DataPath,"concurrent-claude"),null));throw new Exception("Concurrent Claude save accepted");}catch(InvalidOperationException){}
            try{await AuthorizeAsync();throw new Exception("Concurrent authorization accepted");}catch(InvalidOperationException){}
            if(!beforeSource.Matches(CurrentCapacitySource()))throw new InvalidOperationException("Concurrent save mutated source fields");
        }
        finally{sourceSettingsBusy=false;}
        // Persisted pending state simulates a crash after settings save and before
        // the boundary transaction. No source may run before it is completed.
        var enabled=Config.CodexEnabled;
        Config.CodexEnabled=true;Config.CodexBoundaryPending=true;Config.Save();
        if(Settings.Load().CodexBoundaryPending!=true||CodexActive)throw new InvalidOperationException("Pending boundary did not survive persistence");
        await VerifyDisabledSourceAsync();
        var temporary=Path.Combine(Program.DataPath,"settings.tmp");Directory.CreateDirectory(temporary);
        try
        {
            try{CompleteSourceBoundary();throw new Exception("Boundary settings failure accepted");}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){if(CodexActive||!Config.CodexBoundaryPending)throw new InvalidOperationException("Failed boundary resumed source");}
        }
        finally{Directory.Delete(temporary);}
        Config.CodexEnabled=enabled;CompleteSourceBoundary();
    }
}
