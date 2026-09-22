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
