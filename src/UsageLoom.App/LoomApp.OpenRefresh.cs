using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    internal bool CodexOpenRefreshing {get;private set;}
    internal bool ClaudeOpenRefreshing {get;private set;}

    internal void RefreshOnOpenIfNeeded()
    {
        if(IsDemo||quitting||!Config.RefreshOnOpen||CodexOpenRefreshing||ClaudeOpenRefreshing)return;
        var now=DateTimeOffset.UtcNow;
        var pending=new List<Task>();
        if(CodexActive)
        {
            var period=SamplingSchedule.QuotaPeriod(true,now,lastUsageActivity,Config.ForegroundSeconds,Config.BackgroundSeconds);
            var backoff=TimeSpan.FromSeconds(Math.Min(1800,period*Math.Pow(2,Math.Min(failures,5))));
            if(OpenRefreshPolicy.ShouldRefresh(true,Quota.Fresh,Quota.FetchedAt,lastAttempt,backoff,now))
            {
                var task=refreshing?quotaTask:RefreshQuotaAsync(true,false);
                if(task is {IsCompleted:false}){CodexOpenRefreshing=true;pending.Add(task);}
            }
        }
        if(Config.ClaudeEnabled)
        {
            var fresh=ClaudeQuota.Status=="snapshot"&&!ClaudeQuota.HistoryOnly&&ClaudeQuota.Scope is not null;
            if(OpenRefreshPolicy.ShouldRefresh(true,fresh,ClaudeQuota.ObservedAt,claudeLastRead,TimeSpan.FromSeconds(30),now))
            {
                var task=claudeTask is {IsCompleted:false}?claudeTask:RefreshClaudeAsync(onOpen:true);
                if(task is {IsCompleted:false}){ClaudeOpenRefreshing=true;pending.Add(task);}
            }
        }
        if(pending.Count==0)return;
        Changed?.Invoke();ClaudeChanged?.Invoke();
        _=CompleteOpenRefreshAsync(pending);
    }
    private async Task CompleteOpenRefreshAsync(IReadOnlyList<Task> pending)
    {
        try{await Task.WhenAll(pending);}
        catch(Exception ex){Program.Log.Write("WARN","OpenRefresh",ex.Message);}
        finally
        {
            CodexOpenRefreshing=false;ClaudeOpenRefreshing=false;
            if(!quitting){Changed?.Invoke();ClaudeChanged?.Invoke();}
        }
    }
}
