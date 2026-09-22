using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private bool sourceSettingsBusy;
    private DateTimeOffset nextSourceBoundaryRetry;
    internal bool CodexActive=>Config.CodexEnabled&&!Config.CodexBoundaryPending;
    private void CompleteSourceBoundary()
    {
        if(!Config.CodexBoundaryPending)return;
        // Persist a hard boundary before any new observation. A failed write must
        // leave the source gated, including after process restart.
        if(!IsDemo||ClaudeSettingsCheck)store.SaveQuotaObservation(new(DateTimeOffset.UtcNow,null,null,Pricing.CatalogVersion,[],true){BarrierReason="explicit-boundary"});
        Config.CodexBoundaryPending=false;
        try{Config.Save();}catch{Config.CodexBoundaryPending=true;throw;}
    }
    private void RetrySourceBoundary()
    {
        if(!Config.CodexBoundaryPending||sourceSettingsBusy||DateTimeOffset.UtcNow<nextSourceBoundaryRetry)return;
        nextSourceBoundaryRetry=DateTimeOffset.UtcNow.AddSeconds(30);
        try{CompleteSourceBoundary();WatchHistory();historyDirty=true;Changed?.Invoke();}
        catch(Exception ex){Message=L10n.T("source.boundaryFailed");Program.Log.Write("WARN","DataSource",ex.Message);}
    }
    internal async Task ConfigureCodexAsync(bool enabled,string executable,string home)
    {
        if(!Path.IsPathFullyQualified(home.Trim()))throw new ArgumentException(L10n.T("s772A83DE6A61"));
        if(sourceSettingsBusy)throw new InvalidOperationException(L10n.T("maintenance.busy"));
        var oldPath=Config.CliPath;var oldHome=Config.CodexHome;
        Config.CliPath=executable.Trim();Config.CodexHome=home.Trim();
        try{await SaveSettingsAsync(codexEnabled:enabled);}
        catch
        {
            // Restore drafts only if persistence itself failed, not after a
            // committed enable/disable transition with a pending safe boundary.
            var saved=Settings.Load();
            if(saved.CliPath!=Config.CliPath||saved.CodexHome!=Config.CodexHome){Config.CliPath=oldPath;Config.CodexHome=oldHome;}
            throw;
        }
    }
    private void RequireCodexSource()
    {
        if(!CodexActive)throw new InvalidOperationException(L10n.T("source.codexDisabled"));
    }
}
