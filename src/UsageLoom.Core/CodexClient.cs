using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace UsageLoom.Core;

public sealed class CodexClient(DiagnosticLog log, LocalAccountFingerprint? fingerprints = null) : IAsyncDisposable
{
    private Process? process;
    private readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim requests = new(1,1);
    private readonly SemaphoreSlim writes = new(1,1);
    private CancellationTokenSource? lifetime;
    private Task? outputPump, errorPump;
    private int nextId, generation, accountRevision;
    private DateTimeOffset lastThreadList=DateTimeOffset.MinValue;
    private volatile bool connectionFaulted;
    private bool proxyConnection;
    private bool managedConnection;
    private string? connectedHome;
    private string? threadNamesHome;
    private readonly Channel<JsonElement> loginEvents=Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(8){FullMode=BoundedChannelFullMode.DropOldest});
    public string LastStage { get; private set; } = L10n.T("s84D11BAB76DC");
    public string AccountObservation { get; private set; } = L10n.T("sCDBC0434F568");
    internal Func<string?, string, ProcessStartInfo>? TestProcessFactory { get; init; }
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal Func<string?,string?> ExecutableResolver { get; init; } = FindExecutable;
    private string? accountKey;
    public event Action? AccountInvalidated;
    public event Action<JsonElement>? QuotaUpdated;
    public IReadOnlyDictionary<string,string> ThreadNames { get; private set; }=new Dictionary<string,string>();
    public bool IsConnected => !connectionFaulted && process is {HasExited:false};

    public static string? FindExecutable(string? configured)
        => FindExecutable(configured,Environment.GetEnvironmentVariable("PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin"));

    internal static string? FindExecutable(string? configured,string? searchPath,string desktopBin)
    {
        if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return Path.GetFullPath(configured);
        // Desktop updates replace the version directory. Discover it on each new
        // connection rather than persisting a hash-directory path as configuration.
        try
        {
            if(Directory.Exists(desktopBin))
            {
                var candidates=Directory.EnumerateDirectories(desktopBin,"*",new EnumerationOptions{RecurseSubdirectories=false,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint})
                    .Select(folder=>new FileInfo(Path.Combine(folder,"codex.exe")))
                    .Where(file=>file.Exists&&(file.Attributes&FileAttributes.ReparsePoint)==0)
                    .OrderByDescending(file=>file.LastWriteTimeUtc).ThenBy(file=>file.FullName,StringComparer.OrdinalIgnoreCase);
                var newest=candidates.FirstOrDefault();
                if(newest is not null)return newest.FullName;
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){}
        foreach(var folder in (searchPath??"").Split(Path.PathSeparator))
        {
            if(string.IsNullOrWhiteSpace(folder))continue;
            try{var p=Path.Combine(folder.Trim('"'),OperatingSystem.IsWindows()?"codex.exe":"codex");if(File.Exists(p))return Path.GetFullPath(p);}catch(ArgumentException){}
        }
        return null;
    }
    public async Task<QuotaState> ReadAsync(string? executable,string home,CancellationToken ct,bool reuseBackend=false,bool managedAccount=false)
    {
        await requests.WaitAsync(ct);
        try
        {
            LastStage=L10n.T("sE70AD0AA5350");
            AccountObservation=L10n.T("s558874DEE4FA");
            if(managedAccount&&reuseBackend)throw new InvalidOperationException(L10n.T("sFBEA9728D07D"));
            if(IsConnected&&(proxyConnection!=reuseBackend||managedConnection!=managedAccount||connectedHome!=Path.GetFullPath(home)))await StopAsync();
            if(!IsConnected)
            {
                if(TestProcessFactory is null&&ExecutableResolver(executable) is null)
                {
                    LastStage=L10n.T("s5BEA9B86A80C");
                    AccountObservation=L10n.T("s3FEF6CF82908");
                    return QuotaState.LocalAccount with{Status=L10n.T("s093685B5C19C")};
                }
                await StartAsync(executable,home,ct,reuseBackend,managedAccount);
            }
            await TryReadThreadNamesAsync(executable,home,ct,reuseBackend,managedAccount);
            var before=await RequestAsync("account/read",new{refreshToken=false},ct);
            if(!before.TryGetProperty("account",out var account)||account.ValueKind==JsonValueKind.Null)
            {AccountObservation=L10n.T("s1C0E27F0D7E0");accountKey=null;AccountInvalidated?.Invoke();return QuotaState.LocalAccount;}
            var type=account.Text("type");
            if(type!="chatgpt")
            {accountKey=null;AccountInvalidated?.Invoke();return new([],null,null,L10n.T("s33E324A070D1")+(type??L10n.T("s4D8C1C5B4283"))+"）",false);}
            var identity=ReadAccountKey(account);
            var key=identity.Key;
            AccountObservation=L10n.T("s5EE718BBC5FA")+identity.Description;
            if(accountKey is not null&&key!=accountKey)AccountInvalidated?.Invoke();
            accountKey=key;
            var revision=Volatile.Read(ref accountRevision);
            var result=await ReadRateLimitsAsync(ct);
            var after=await RequestAsync("account/read",new{refreshToken=false},ct);
            if(revision!=Volatile.Read(ref accountRevision)||!after.TryGetProperty("account",out var second)||second.ValueKind!=JsonValueKind.Object||ReadAccountKey(second).Key!=key||second.Text("type")!=type)
            {accountKey=null;AccountInvalidated?.Invoke();return new([],null,null,L10n.T("s62896AA1C5E5"),false);}
            if(key is null)
            {
                if(reuseBackend)return new([],null,null,L10n.T("s5228C3F2BA5D"),false);
                if(account.GetRawText()!=second.GetRawText())return new([],null,null,L10n.T("sE2AC7761D8B2"),false);
                LastStage=L10n.T("s024636D6D83D");
                var snapshot=QuotaParser.Parse(result,DateTimeOffset.Now,null,account.Text("planType"));
                return snapshot with{IsAuthorizedAccount=managedAccount,IsCachedAccount=!managedAccount,
                    Status=(managedAccount?L10n.T("sAE8E2779555D"):L10n.T("s4D9071E7F3DD"))+L10n.T("s0DA671152837")+(snapshot.Fresh?"":L10n.T("sDD9DAEEF70AF"))};
            }
            LastStage=L10n.T("sADB8BBDC1E8E");
            var parsed=QuotaParser.Parse(result,DateTimeOffset.Now,key,account.Text("planType"));
            // Allowlisted diagnostics only: no response body, credentials, email or account ID.
            var source=result.TryGetProperty("rateLimitsByLimitId",out var buckets)&&buckets.ValueKind==JsonValueKind.Object&&
                QuotaParser.Parse(JsonSerializer.SerializeToElement(new{rateLimitsByLimitId=buckets}),DateTimeOffset.Now,null,null).Windows.Count>0
                ?"rateLimitsByLimitId":"rateLimits";
            var identitySource=ReadIdentity(account) is not null?"official-id":"email-fingerprint";
            log.Write("INFO","QuotaSource",$"transport={(reuseBackend?"proxy":"stdio")}; managed={managedAccount}; identity={identitySource}; branch={source}; weekly="+
                string.Join(";",parsed.PrimaryWindows.Where(w=>w.Minutes==10080).Select(w=>$"used={w.Used.ToString(System.Globalization.CultureInfo.InvariantCulture)},reset={w.ResetsAt:O}")));
            var unavailable=parsed.Fresh?"":L10n.T("sDD9DAEEF70AF");
            return managedAccount?parsed with{IsAuthorizedAccount=true,Status=L10n.T("s9903587501AE")+unavailable}:
                reuseBackend?parsed with{IsCachedAccount=true,Status=L10n.T("sFD4917818970")+unavailable}:
                parsed with{IsCachedAccount=true,Status=L10n.T("s3D5FA3C5D5F7")+unavailable};
        }
        catch { await StopAsync(); throw; }
        finally { requests.Release(); }
    }
    public static string? ReadIdentity(JsonElement account)
    {
        var identity=account.Text("accountId")??account.Text("chatgptAccountId")??account.Text("id");
        return string.IsNullOrWhiteSpace(identity)?null:identity;
    }

    private (string? Key,string Description) ReadAccountKey(JsonElement account)
    {
        var official=ReadIdentity(account);
        if(official is not null)
            return (fingerprints?.Create("official-id",official)??official,
                fingerprints is null?L10n.T("s309E2C202FE6"):L10n.T("s437F17E930DD"));
        var email=account.Text("email");
        if(email is null||fingerprints is null)return (null,L10n.T("s4C6AC9619552"));
        return (fingerprints.Create("email",email),L10n.T("s0C9B1352A781"));
    }

    private async Task StartAsync(string? executable,string home,CancellationToken ct,bool reuseBackend,bool managedAccount=false)
    {
        await StopAsync();
        proxyConnection=reuseBackend;
        managedConnection=managedAccount;connectedHome=Path.GetFullPath(home);
        ProcessStartInfo start;
        if(TestProcessFactory is not null) start=TestProcessFactory(executable,home);
        else
        {
        var path=ExecutableResolver(executable)??throw new FileNotFoundException(L10n.T("s542A459FCFDB"));
        if(!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&OperatingSystem.IsWindows())
            throw new InvalidOperationException(L10n.T("sB5F3CBD3278D"));
        start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in ConnectionArguments(reuseBackend))start.ArgumentList.Add(argument);
        if(managedAccount)
        {
            start.ArgumentList.Add("-c");start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            foreach(var name in new[]{"OPENAI_API_KEY","CODEX_ACCESS_TOKEN","CODEX_API_KEY"})start.Environment.Remove(name);
        }
        start.Environment["CODEX_HOME"]=home;
        }
        LastStage=reuseBackend?L10n.T("s47BAB47013A5"):L10n.T("s47F134A3A574");
        process=Process.Start(start)??throw new IOException(L10n.T("sCEC742CFAD38"));
        connectionFaulted=false;
        lifetime=new();var epoch=++generation;
        outputPump=PumpAsync(process,epoch,lifetime.Token);
        errorPump=DrainErrorsAsync(process,lifetime.Token);
        await RequestAsync("initialize",new{clientInfo=new{name="usage_loom",title="UsageLoom",version="0.2.0"}},ct);
        await SendAsync(new{method="initialized",@params=new{}},ct);
        log.Write("INFO","RPC",reuseBackend?L10n.T("sF32C19A4BAED"):L10n.T("sA9B05E914BA6"));
    }
    internal static string[] ConnectionArguments(bool reuseBackend)=>reuseBackend
        ?["app-server","proxy"]
        :["-s","read-only","-a","on-request","app-server","--listen","stdio://"];
    public static Uri ValidateLoginUrl(string? value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length>0||uri.Host is not ("auth.openai.com" or "chatgpt.com"))
            throw new InvalidDataException(L10n.T("s8445D783EA4B"));
        return uri;
    }
    public async Task LoginAsync(string? executable,string isolatedHome,Func<Uri,Task<bool>> openBrowser,CancellationToken ct)
    {
        await requests.WaitAsync(ct);string? loginId=null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            Directory.CreateDirectory(isolatedHome);
            await StartAsync(executable,isolatedHome,timeout.Token,false,true);
            while(loginEvents.Reader.TryRead(out _)){}
            var result=await RequestAsync("account/login/start",new{type="chatgpt"},timeout.Token);
            loginId=result.Text("loginId")??throw new InvalidDataException(L10n.T("sF19CB2FA0241"));
            var uri=ValidateLoginUrl(result.Text("authUrl"));
            if(!await openBrowser(uri))throw new IOException(L10n.T("s7E62984E50B0"));
            LastStage=L10n.T("s70E3D5A8E6F1");
            while(true)
            {
                var update=await loginEvents.Reader.ReadAsync(timeout.Token);
                if(update.Text("loginId") is null)throw new IOException(L10n.T("sAFABCAD4AD87"));
                if(update.Text("loginId")!=loginId)continue;
                if(!update.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True)throw new IOException(L10n.T("sD9DF941A9B55"));
                LastStage=L10n.T("s19FCD5031AB6");return;
            }
        }
        catch
        {
            var failedStage=LastStage;
            if(loginId is not null&&IsConnected)
            {
                using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try{await RequestAsync("account/login/cancel",new{loginId},cancel.Token);}catch{}
            }
            LastStage=failedStage;
            throw;
        }
        finally{await StopAsync();requests.Release();}
    }
    public async Task LogoutAuthorizedAsync(string? executable,string isolatedHome,CancellationToken ct)
    {
        await requests.WaitAsync(ct);
        try{await StartAsync(executable,isolatedHome,ct,false,true);await RequestAsync("account/logout",new{},ct);AccountInvalidated?.Invoke();}
        finally{await StopAsync();requests.Release();}
    }
    private async Task<JsonElement> RequestAsync(string method,object? parameters,CancellationToken ct)
    {
        if(!IsConnected)throw new IOException(L10n.T("sE65ED89970CA"));
        LastStage=L10n.T("sEED5D32D162E")+method;
        var id=Interlocked.Increment(ref nextId);
        var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id]=completion;
        try
        {
            await SendAsync(new{id,method,@params=parameters??new{}},ct);
            try{return await completion.Task.WaitAsync(RequestTimeout,ct);}
            catch(TimeoutException){throw new CodexRpcException("request timed out");}
        }
        finally {pending.TryRemove(id,out _);}
    }
    private async Task<JsonElement> ReadRateLimitsAsync(CancellationToken ct)
    {
        try{return await RequestAsync("account/rateLimits/read",null,ct);}
        catch(CodexRpcException ex) when(ex.Kind==RpcFailureKind.Authentication)
        {
            LastStage=L10n.T("sE215838AEFE3");
            log.Write("WARN","RPC",L10n.T("sB38761D8644A"));
            var refreshed=await RequestAsync("account/read",new{refreshToken=true},ct);
            if(!refreshed.TryGetProperty("account",out var account)||account.ValueKind!=JsonValueKind.Object||account.Text("type")!="chatgpt")throw;
            return await RequestAsync("account/rateLimits/read",null,ct);
        }
        catch(CodexRpcException ex) when(ex.Retryable)
        {
            LastStage=L10n.T("sB3465B0D0EF6");
            log.Write("WARN","RPC",$"{ex.Message}；method=account/rateLimits/read retry=1");
            await Task.Delay(TimeSpan.FromMilliseconds(650),ct);
            return await RequestAsync("account/rateLimits/read",null,ct);
        }
    }
    private async Task TryReadThreadNamesAsync(string? executable,string home,CancellationToken ct,bool reuseBackend,bool managedAccount)
    {
        var canonical=Path.GetFullPath(home);
        if(!string.Equals(threadNamesHome,canonical,StringComparison.OrdinalIgnoreCase)){ThreadNames=new Dictionary<string,string>();threadNamesHome=canonical;lastThreadList=DateTimeOffset.MinValue;}
        if(DateTimeOffset.Now-lastThreadList<TimeSpan.FromMinutes(10))return;
        lastThreadList=DateTimeOffset.Now;
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        try
        {
            // Optional metadata must not break the account/limits transport when
            // the desktop backend emits an invalid thread/list response.
            await using var metadata=new CodexClient(log){TestProcessFactory=TestProcessFactory,ExecutableResolver=ExecutableResolver,RequestTimeout=RequestTimeout};
            await metadata.StartAsync(executable,home,ct,reuseBackend,managedAccount);
            foreach(var archived in new[]{false,true})
            {
                string? cursor=null;
                for(var page=0;page<5;page++)
                {
                    var result=await metadata.RequestAsync("thread/list",new{cursor,limit=100,sortKey="updated_at",sortDirection="desc",archived,useStateDbOnly=true,sourceKinds=new[]{"cli","vscode","appServer"}},ct);
                    if(!result.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Array)break;
                    foreach(var thread in data.EnumerateArray())
                    {
                        var name=thread.Text("name");
                        if(string.IsNullOrWhiteSpace(name))continue;
                        name=name.Trim();if(name.Length>300)name=name[..300]+"…";
                        foreach(var key in new[]{thread.Text("id"),thread.Text("sessionId")})if(!string.IsNullOrWhiteSpace(key))names[key]=name;
                    }
                    cursor=result.Text("nextCursor");if(string.IsNullOrWhiteSpace(cursor))break;
                }
            }
            ThreadNames=names;
        }
        catch(Exception ex)when(ex is CodexRpcException or TimeoutException or JsonException or IOException)
        {
            log.Write("WARN","Threads",L10n.T("sF9701479FEB3")+ex.Message);
        }
    }
    private async Task SendAsync(object message,CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try{var child=process??throw new IOException(L10n.T("s30F12A33E524"));await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(),ct);await child.StandardInput.FlushAsync(ct);}
        finally{writes.Release();}
    }
    private async Task PumpAsync(Process child,int epoch,CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var line=await ReadBoundedLine(child.StandardOutput,ct);if(line is null)break;
                if(epoch!=Volatile.Read(ref generation))break;
                using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
                if(root.TryGetProperty("id",out var id)&&id.TryLong(out var n)&&pending.TryRemove((int)n,out var response))
                {
                    if(root.TryGetProperty("error",out var error))
                    {
                        int? code=error.TryGetProperty("code",out var codeValue)&&codeValue.TryGetInt32(out var parsedCode)?parsedCode:null;
                        response.TrySetException(new CodexRpcException(error.Text("message")??L10n.T("s0AC92739BFBB"),code));
                    }
                    else if(root.TryGetProperty("result",out var result))response.TrySetResult(result.Clone());
                    else response.TrySetException(new IOException(L10n.T("sF42DC10D5823")));
                }
                else if(epoch==generation)
                {
                    if(root.Text("method")=="account/login/completed"&&root.TryGetProperty("params",out var login))loginEvents.Writer.TryWrite(login.Clone());
                    if(root.Text("method")=="account/updated"){Interlocked.Increment(ref accountRevision);accountKey=null;AccountInvalidated?.Invoke();}
                    if(root.Text("method")=="account/rateLimits/updated"&&root.TryGetProperty("params",out var value))QuotaUpdated?.Invoke(value.Clone());
                }
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or OperationCanceledException or InvalidOperationException){if(!ct.IsCancellationRequested)log.Write("WARN","RPC",ex.Message);}
        finally{if(epoch==generation){connectionFaulted=true;loginEvents.Writer.TryWrite(JsonSerializer.SerializeToElement(new{disconnected=true}));foreach(var item in pending.Values)item.TrySetException(new CodexConnectionClosedException());}}
    }
    private static async Task<string?> ReadBoundedLine(StreamReader reader,CancellationToken ct)
    {
        var text=new StringBuilder();var one=new char[1];
        while(await reader.ReadAsync(one.AsMemory(),ct)>0){if(one[0]=='\n')return text.ToString();if(text.Length>=1_048_576)throw new IOException(L10n.T("s7A80C0FB7AA9"));text.Append(one[0]);}
        return text.Length>0?text.ToString():null;
    }
    private static async Task DrainErrorsAsync(Process child,CancellationToken ct)
    {
        var buffer=new char[4096];try{while(await child.StandardError.ReadAsync(buffer.AsMemory(),ct)>0){}}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
    }
    public async Task StopAsync()
    {
        Interlocked.Increment(ref generation);connectionFaulted=true;
        lastThreadList=DateTimeOffset.MinValue;
        var stopping=lifetime;lifetime=null;stopping?.Cancel();accountKey=null;
        foreach(var item in pending.Values)item.TrySetCanceled();pending.Clear();
        var child=process;process=null;
        if(child is not null)
        {
            try{if(!child.HasExited)child.Kill(!proxyConnection);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
            catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException){log.Write("WARN","RPC",ex.Message);}
        }
        try{await Task.WhenAll(outputPump??Task.CompletedTask,errorPump??Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));}
        catch(Exception ex)when(ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException){log.Write("WARN","RPC",ex.Message);}
        finally{child?.Dispose();stopping?.Dispose();outputPump=null;errorPump=null;}
    }
    public async ValueTask DisposeAsync(){await StopAsync();requests.Dispose();writes.Dispose();}
}
