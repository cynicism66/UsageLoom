using UsageLoom.Core;

namespace UsageLoom.App;

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
    internal async Task ConfigureClaudeAsync(bool enabled,string? directory,string? scope)
    {
        if(IsDemo)return;
        directory=directory?.Trim();
        if(!string.IsNullOrEmpty(directory)&&(!Path.IsPathFullyQualified(directory)||directory.StartsWith(@"\\")))throw new ArgumentException(L10n.T("claude.invalidPath"));
        claudeGeneration++;claudeCancellation?.Cancel();
        Config.ClaudeEnabled=enabled;Config.ClaudeDataDirectory=directory;Config.ClaudeScope=scope;Config.Save();
        ClaudeQuota=ClaudeQuotaSnapshot.Empty(enabled?"waiting":"disabled");ClaudeChanged?.Invoke();Changed?.Invoke();
        if(claudeTask is {} pending)await pending;
        await RefreshClaudeAsync(true);
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
        ClaudeQuota=(step%4) switch
        {
            0=>ClaudeQuotaSnapshot.Empty("readFailed"),
            1=>new("snapshot",[new("five_hour",100,now.AddMinutes(-1)),new("seven_day",70,now.AddHours(2))],now.AddHours(-1),"preview",["preview"]),
            2=>new("snapshot",[new("five_hour",32,null)],now,"preview",["preview"],true),
            _=>new("snapshot",[new("five_hour",32,now.AddHours(3)),new("seven_day",70,now.AddDays(2))],now,"preview",["preview"])
        };
        ClaudeChanged?.Invoke();
    }
}
