using UsageLoom.Core;
using UsageLoom.Storage;

namespace UsageLoom.App;

internal sealed record ClaudeSettingsInput(bool Enabled,string? Directory,string? Scope,string? ManualPlan=null,bool CodeEnabled=false,string? CodeHome=null)
{
    internal ClaudeSettingsInput Validated()
    {
        var directory=Directory?.Trim();
        if(!string.IsNullOrEmpty(directory)&&(!Path.IsPathFullyQualified(directory)||directory.StartsWith(@"\\")))throw new ArgumentException(L10n.T("claude.invalidPath"));
        if(!string.IsNullOrWhiteSpace(ManualPlan)&&ClaudePlanLabel.Normalize(ManualPlan) is null)throw new ArgumentException(L10n.T("claude.invalidPlan"));
        var codeHome=CodeHome?.Trim();
        if(!string.IsNullOrEmpty(codeHome)&&(!Path.IsPathFullyQualified(codeHome)||codeHome.StartsWith(@"\\")))throw new ArgumentException(L10n.T("claude.invalidPath"));
        return this with{Directory=string.IsNullOrEmpty(directory)?null:directory,Scope=string.IsNullOrWhiteSpace(Scope)?null:Scope,ManualPlan=ClaudePlanLabel.Normalize(ManualPlan),CodeHome=string.IsNullOrEmpty(codeHome)?null:codeHome};
    }
}

public sealed partial class LoomApp
{
    internal ClaudeQuotaSnapshot ClaudeQuota {get;private set;}=ClaudeQuotaSnapshot.Empty("disabled");
    internal string? ClaudeHistoryError {get;private set;}
    internal ClaudeCodeSnapshot ClaudeCode {get;private set;}=ClaudeCodeSnapshot.Empty("disabled");
    private readonly ClaudeCodeReader claudeCodeReader=new();
    private readonly ClaudeHistoryStore claudeHistoryStore=new(Program.DataPath);
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
        claudeCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        return claudeTask=ReadClaudeAsync(Config.ClaudeDataDirectory,Config.ClaudeScope,claudeGeneration,claudeCancellation.Token,manual);
    }
    private async Task ReadClaudeAsync(string? directory,string? scope,int generation,CancellationToken token,bool fullRead=false)
    {
        var quotaRead=false;
        try
        {
            var codeEnabled=Config.ClaudeCodeEnabled;var codeHome=Config.ClaudeCodeHome;
            string? historyError=null;
            var result=await Task.Run(()=>
            {
                var now=DateTimeOffset.UtcNow;
                var snapshot=ClaudeDesktopReader.Read(directory,scope,now,token);
                if(snapshot.Status!="snapshot")return snapshot;
                try{return snapshot with{History=claudeHistoryStore.Merge(snapshot,now,token)};}
                catch(OperationCanceledException){throw;}
                catch(Exception ex)when(ex is not OutOfMemoryException)
                {historyError=L10n.T("claude.historySaveFailed");return snapshot;}
            },token);
            if(quitting||generation!=claudeGeneration||!Config.ClaudeEnabled)return;
            ClaudeQuota=result;quotaRead=true;ClaudeHistoryError=historyError;ClaudeChanged?.Invoke();
            if(codeEnabled)
            {
                var code=await Task.Run(()=>{if(fullRead)claudeCodeReader.Clear();return claudeCodeReader.Read(codeHome,token);},token);
                if(quitting||generation!=claudeGeneration||!Config.ClaudeEnabled)return;
                if(ClaudeCode.Status!=code.Status||ClaudeCode.Files!=code.Files||ClaudeCode.Skipped!=code.Skipped||!ClaudeCode.Rows.SequenceEqual(code.Rows))
                {ClaudeCode=code;ClaudeChanged?.Invoke();}
            }
        }
        catch(OperationCanceledException)
        {
            if(!quitting&&generation==claudeGeneration)
            {
                if(!quotaRead){ClaudeQuota=ClaudeQuotaSnapshot.Empty("readFailed");ClaudeHistoryError=null;}
                if(Config.ClaudeCodeEnabled)ClaudeCode=ClaudeCodeSnapshot.Empty("readFailed");ClaudeChanged?.Invoke();
            }
        }
    }
    private bool PersistClaudeSettings(ClaudeSettingsInput? input)
    {
        var before=new ClaudeSettingsInput(Config.ClaudeEnabled,Config.ClaudeDataDirectory,Config.ClaudeScope,Config.ClaudeManualPlan,Config.ClaudeCodeEnabled,Config.ClaudeCodeHome);
        var next=input?.Validated()??before;
        Config.ClaudeEnabled=next.Enabled;Config.ClaudeDataDirectory=next.Directory;Config.ClaudeScope=next.Scope;Config.ClaudeManualPlan=next.ManualPlan;
        Config.ClaudeCodeEnabled=next.CodeEnabled;Config.ClaudeCodeHome=next.CodeHome;
        try{Config.Save();}
        catch{Config.ClaudeEnabled=before.Enabled;Config.ClaudeDataDirectory=before.Directory;Config.ClaudeScope=before.Scope;Config.ClaudeManualPlan=before.ManualPlan;Config.ClaudeCodeEnabled=before.CodeEnabled;Config.ClaudeCodeHome=before.CodeHome;throw;}
        return (next with{ManualPlan=before.ManualPlan})!=before;
    }
    private async Task CompleteClaudeSettingsAsync(bool changed,bool forceRead=false)
    {
        if(!changed){ClaudeChanged?.Invoke();if(forceRead)await RefreshClaudeAsync(true);return;}
        claudeGeneration++;claudeCancellation?.Cancel();
        ClaudeQuota=ClaudeQuotaSnapshot.Empty(Config.ClaudeEnabled?"waiting":"disabled");ClaudeHistoryError=null;ClaudeChanged?.Invoke();Changed?.Invoke();
        if(claudeTask is {} pending)await pending;
        claudeCodeReader.Clear();ClaudeCode=ClaudeCodeSnapshot.Empty(Config.ClaudeEnabled&&Config.ClaudeCodeEnabled?"waiting":"disabled");ClaudeChanged?.Invoke();
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
        Config.ClaudeCodeEnabled=true;
        ClaudeCode=new("ready",Enumerable.Range(0,20).Select(i=>new ClaudeCodeUsage("preview-message-"+i,"preview-request-"+i,"preview-session-0001","preview-project","claude-preview",DateTimeOffset.Now.AddHours(-i*3),100+i*20,200+i*30,1000,300)).ToArray(),1);
        ClaudeQuota=new("snapshot",[new("five_hour",65,DateTimeOffset.Now.AddHours(1)),new("seven_day",42,DateTimeOffset.Now.AddDays(2))],DateTimeOffset.Now,"preview",["preview"]);
        var at=ClaudeQuota.ObservedAt!.Value;
        ClaudeQuota=ClaudeQuota with{History=ClaudeQuotaHistory.Normalize(Enumerable.Range(0,18).SelectMany(i=>new[]{
            new ClaudeQuotaObservation("preview",at.AddMinutes(-10*(17-i)),"five_hour",i*3,at.AddHours(1),"cache"),
            new ClaudeQuotaObservation("preview",at.AddMinutes(-10*(17-i)),"seven_day",20+i,at.AddDays(2),"cache")}))};
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
