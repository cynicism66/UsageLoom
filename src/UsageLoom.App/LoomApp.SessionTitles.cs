using UsageLoom.Storage;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private readonly SessionTitleCatalog sessionTitles=new();
    private Task? sessionTitleTask;
    private DateTimeOffset lastSessionTitleCheck=DateTimeOffset.MinValue;
    private string? sessionTitleHome;
    private Task RefreshSessionTitlesAsync(bool force=false)
    {
        if(IsDemo||quitting)return Task.CompletedTask;
        string home;
        try{home=Path.GetFullPath(Config.CodexHome);}catch(ArgumentException){return Task.CompletedTask;}
        if(!string.Equals(home,sessionTitleHome,StringComparison.OrdinalIgnoreCase))
        {sessionTitleHome=home;SessionNames=new Dictionary<string,string>();lastSessionTitleCheck=DateTimeOffset.MinValue;}
        if(sessionTitleTask is {IsCompleted:false})return sessionTitleTask;
        var now=DateTimeOffset.UtcNow;
        if(!force&&now>=lastSessionTitleCheck&&now-lastSessionTitleCheck<TimeSpan.FromSeconds(30))return Task.CompletedTask;
        lastSessionTitleCheck=now;
        sessionTitleTask=ReadSessionTitlesAsync(home,configurationGeneration);
        return sessionTitleTask;
    }
    internal void ConfigureSessionTitlePreview(string title)
    {
        if(!IsDemo||!NavigationCheck||Events.Count==0)throw new InvalidOperationException("Title preview requires isolated navigation fixture");
        SessionNames=Events.Select(item=>item.Session).Distinct(StringComparer.Ordinal).ToDictionary(id=>id,_=>title,StringComparer.Ordinal);
        Changed?.Invoke();
    }
    private async Task ReadSessionTitlesAsync(string home,int epoch)
    {
        try
        {
            var names=await Task.Run(()=>sessionTitles.Read(home,lifetime.Token),lifetime.Token);
            if(quitting||epoch!=configurationGeneration||!string.Equals(home,Path.GetFullPath(Config.CodexHome),StringComparison.OrdinalIgnoreCase))return;
            if(names.Count!=SessionNames.Count||names.Any(pair=>!SessionNames.TryGetValue(pair.Key,out var value)||value!=pair.Value))
            {SessionNames=names;Changed?.Invoke();}
            if(sessionTitles.UnavailableSources>0)Program.Log.Write("WARN","SessionTitles","Local title metadata temporarily unavailable; retaining readable same-source names");
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {Program.Log.Write("WARN","SessionTitles","Local title metadata unavailable");}
    }
}
