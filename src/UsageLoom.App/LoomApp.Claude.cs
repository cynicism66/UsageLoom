using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed record ClaudeSettingsInput(bool Enabled,string? Directory,string? Scope,string? ManualPlan=null)
{
    internal ClaudeSettingsInput Validated()
    {
        var directory=Directory?.Trim();
        if(!string.IsNullOrEmpty(directory)&&(!Path.IsPathFullyQualified(directory)||directory.StartsWith(@"\\")))throw new ArgumentException(L10n.T("claude.invalidPath"));
        if(!string.IsNullOrWhiteSpace(ManualPlan)&&ClaudePlanLabel.Normalize(ManualPlan) is null)throw new ArgumentException(L10n.T("claude.invalidPlan"));
        return this with{Directory=string.IsNullOrEmpty(directory)?null:directory,Scope=string.IsNullOrWhiteSpace(Scope)?null:Scope,ManualPlan=ClaudePlanLabel.Normalize(ManualPlan)};
    }
}

public sealed partial class LoomApp
{
    internal ClaudeQuotaSnapshot ClaudeQuota {get;private set;}=ClaudeQuotaSnapshot.Empty("disabled");
    internal event Action? ClaudeChanged;
    private Task? claudeTask;
    private CancellationTokenSource? claudeCancellation;
    private DateTimeOffset claudeLastRead=DateTimeOffset.MinValue;
    private int claudeGeneration;
    internal Task RefreshClaudeAsync(bool manual=false)
    {
        if(quitting||IsDemo||!Config.ClaudeEnabled)return Task.CompletedTask;
        if(claudeTask is {IsCompleted:false})return claudeTask;
        var visible=flyout?.IsPanelVisible==true||dashboard?.IsPanelVisible==true;
        var now=DateTimeOffset.UtcNow;
        if(!manual&&(!Config.AutoRefresh||now>=claudeLastRead&&now-claudeLastRead<TimeSpan.FromSeconds(visible?30:300)))return Task.CompletedTask;
        claudeLastRead=DateTimeOffset.UtcNow;
        claudeCancellation?.Dispose();claudeCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        claudeCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        return claudeTask=ReadClaudeAsync(Config.ClaudeDataDirectory,Config.ClaudeScope,claudeGeneration,claudeCancellation.Token);
    }
    private async Task ReadClaudeAsync(string? directory,string? scope,int generation,CancellationToken token)
    {
        try
        {
            var result=await Task.Run(()=>ClaudeDesktopReader.Read(directory,scope,DateTimeOffset.UtcNow,token),token);
            if(quitting||generation!=claudeGeneration||!Config.ClaudeEnabled)return;
            ClaudeQuota=result;ClaudeChanged?.Invoke();
        }
        catch(OperationCanceledException)
        {
            if(!quitting&&generation==claudeGeneration){ClaudeQuota=ClaudeQuotaSnapshot.Empty("readFailed");ClaudeChanged?.Invoke();}
        }
    }
    private bool PersistClaudeSettings(ClaudeSettingsInput? input)
    {
        var before=new ClaudeSettingsInput(Config.ClaudeEnabled,Config.ClaudeDataDirectory,Config.ClaudeScope,Config.ClaudeManualPlan);
        var next=input?.Validated()??before;
        Config.ClaudeEnabled=next.Enabled;Config.ClaudeDataDirectory=next.Directory;Config.ClaudeScope=next.Scope;Config.ClaudeManualPlan=next.ManualPlan;
        try{Config.Save();}
        catch{Config.ClaudeEnabled=before.Enabled;Config.ClaudeDataDirectory=before.Directory;Config.ClaudeScope=before.Scope;Config.ClaudeManualPlan=before.ManualPlan;throw;}
        return (next with{ManualPlan=before.ManualPlan})!=before;
    }
    private async Task CompleteClaudeSettingsAsync(bool changed,bool forceRead=false)
    {
        if(!changed){ClaudeChanged?.Invoke();if(forceRead)await RefreshClaudeAsync(true);return;}
        claudeGeneration++;claudeCancellation?.Cancel();
        ClaudeQuota=ClaudeQuotaSnapshot.Empty(Config.ClaudeEnabled?"waiting":"disabled");ClaudeChanged?.Invoke();Changed?.Invoke();
        if(claudeTask is {} pending)await pending;
        await RefreshClaudeAsync(true);
    }
    internal async Task ConfigureClaudeAsync(ClaudeSettingsInput input)
    {
        if(sourceSettingsBusy)throw new InvalidOperationException(L10n.T("maintenance.busy"));
        if(IsDemo&&!ClaudeSettingsCheck)return;
        var changed=PersistClaudeSettings(input);
        Message=L10n.T("sBD03C0AAD701");Changed?.Invoke();
        await CompleteClaudeSettingsAsync(changed,true);
    }
    private void ConfigureClaudePreview()
    {
        if(!args.Contains("--preview-claude")){Config.ClaudeEnabled=false;return;}
        Config.ClaudeEnabled=true;
        ClaudeQuota=new("snapshot",[new("five_hour",65,DateTimeOffset.Now.AddHours(1)),new("seven_day",42,DateTimeOffset.Now.AddDays(2))],DateTimeOffset.Now,"preview",["preview"]);
    }
    internal void ConfigureClaudeRefreshPreview(int step)
    {
        if(!CapacityUiCheck||!args.Contains("--preview-claude"))return;
        var now=DateTimeOffset.Now;
        Config.ClaudeManualPlan=(step%4) switch{0=>null,1=>"pro",2=>"max20",_=>"enterprise"};
        ClaudeQuota=(step%6) switch
        {
            0=>ClaudeQuotaSnapshot.Empty("readFailed"),
            1=>new("snapshot",[new("five_hour",100,now.AddMinutes(-1)),new("seven_day",70,now.AddHours(2))],now.AddHours(-1),"preview",["preview"]),
            2=>new("snapshot",[new("five_hour",32,null)],now,"preview",["preview"],true),
            4=>new("snapshot",[new("five_hour",0,now.AddHours(3)),new("seven_day",100,now.AddDays(2))],now,"preview",["preview"]),
            _=>new("snapshot",[new("five_hour",32,now.AddHours(3)),new("seven_day",70,now.AddDays(2))],now,"preview",["preview"])
        };
        ClaudeChanged?.Invoke();
    }
}
