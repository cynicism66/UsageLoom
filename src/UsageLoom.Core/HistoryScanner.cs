using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageLoom.Core;

public sealed record ScanFileState(string Path,long Size,long Modified,long Offset,string Session,string Project,string Model,string Agent,TokenUsage Cumulative,bool ForkPending,string? Parent,string PrefixHash);

public sealed class HistoryScanner
{
    private string? previousHome;
    private Dictionary<string, (long Length, long Written, long Created)>? previousFiles;
    private ScanReport? previousReport;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    public void InvalidateCache() { previousReport = null; previousFiles = null; }
    public async Task<ScanReport> ScanIfChangedAsync(string home, CancellationToken cancellationToken, IProgress<string>? progress = null, bool force = false)
    {
        await scanGate.WaitAsync(cancellationToken);
        try
        {
            var canonical = Path.GetFullPath(home);
            if(!Directory.Exists(canonical))throw new DirectoryNotFoundException("指定的本地日志根目录不存在");
            var manifest = FileManifest(canonical, cancellationToken);
            if (!force && string.Equals(previousHome, canonical, StringComparison.OrdinalIgnoreCase) && previousReport is not null && previousFiles is not null &&
                manifest.Count == previousFiles.Count && manifest.All(p => previousFiles.TryGetValue(p.Key, out var state) && state == p.Value))
                return previousReport with { UsedCache = true };
            var report = await ScanAsync(canonical, cancellationToken, progress);
            var after = FileManifest(canonical, cancellationToken);
            // 扫描期间仍在写入的来源不进入缓存；下轮重新扫描，避免冻结半成品。
            if (manifest.Count == after.Count && manifest.All(p => after.TryGetValue(p.Key, out var state) && state == p.Value) && report.Warnings == 0)
            { previousHome = canonical; previousFiles = after; previousReport = report; }
            else { previousReport = null; previousFiles = null; }
            return report;
        }
        finally { scanGate.Release(); }
    }
    private static Dictionary<string, (long Length, long Written, long Created)> FileManifest(string home, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, (long, long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                files[file] = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
            }
        }
        return files;
    }
    public async Task<ScanReport> ScanAsync(string home,CancellationToken cancellationToken,IProgress<string>? progress=null,
        IReadOnlyDictionary<string,IReadOnlyList<string>>? indexedRecords=null)
    {
        var events=new List<UsageEvent>();var highWater=new Dictionary<string,TokenUsage>();var seen=new HashSet<string>();
        var segments=new Dictionary<string,int>();var snapshots=new HashSet<string>();
        var warnings=0;var files=0;var sources=new List<string>();
        foreach(var directory in new[]{Path.Combine(home,"sessions"),Path.Combine(home,"archived_sessions")})
        {
            if(indexedRecords is null&&!Directory.Exists(directory))continue;
            var sourceFiles=indexedRecords is null?Directory.EnumerateFiles(directory,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint})
                :indexedRecords.Keys.Where(path=>Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            foreach(var file in sourceFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();files++;
                if(indexedRecords is not null&&!indexedRecords.ContainsKey(file)){files--;continue;}
                var sourceKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant())));
                if(files%20==0)progress?.Report($"已读取 {files} 个日志文件");
                var session=Path.GetFileNameWithoutExtension(file);var model="unknown";var project="(未知项目)";var agent="主任务";
                var cumulative=new TokenUsage();string? parent=null;var ownerSeen=false;var forkPending=false;var replay=new TokenUsage();
                var segment=0;
                try
                {
                    await foreach(var line in indexedRecords is null?ReadRecords(file,cancellationToken):Replay(indexedRecords.GetValueOrDefault(file)??[],cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if(line is null){warnings++;continue;}
                        try
                        {
                            using var document=JsonDocument.Parse(line);var row=document.RootElement;
                            if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)continue;
                            var type=row.Text("type");
                            if(type=="session_meta")
                            {
                                var ownerId=payload.Text("id")??payload.Text("session_id")??session;
                                if(!ownerSeen)
                                {
                                    session=ownerId;ownerSeen=true;project=ProjectName(payload.Text("cwd"));parent=payload.Text("forked_from_id")??payload.Text("parent_thread_id");forkPending=parent is not null;
                                    if(payload.TryGetProperty("source",out var source)){var marker=source.ToString();agent=marker.Contains("guardian",StringComparison.OrdinalIgnoreCase)?"Guardian":marker.Contains("memory",StringComparison.OrdinalIgnoreCase)?"Memory":marker.Contains("agent",StringComparison.OrdinalIgnoreCase)?"子 Agent":"主任务";}
                                    cumulative=highWater.GetValueOrDefault(session)??new();
                                    segment=segments.GetValueOrDefault(session);
                                }
                                else if(ownerId==parent && replay.Total>0){cumulative=replay;forkPending=false;}
                                continue;
                            }
                            if(type=="turn_context"){model=payload.Text("model")??model;continue;}
                            if(type!="event_msg")continue;
                            if(forkPending&&payload.Text("type")=="task_started")
                            {
                                var turn=payload.Text("turn_id");
                                if(IsChildTurn(turn,session)){cumulative=replay;forkPending=false;}
                                continue;
                            }
                            if(payload.Text("type")!="token_count"||!payload.TryGetProperty("info",out var info)||info.ValueKind!=JsonValueKind.Object)continue;
                            var total=info.TryGetProperty("total_token_usage",out var totalJson)&&totalJson.ValueKind==JsonValueKind.Object?TokenUsage.Parse(totalJson,cumulative):null;
                            var lastUsage=info.TryGetProperty("last_token_usage",out var lastJson)&&lastJson.ValueKind==JsonValueKind.Object?TokenUsage.Parse(lastJson):null;
                            if(forkPending){if(total is not null)replay=total;continue;}
                            var snapshotKey=$"{session}|{row.Text("timestamp")}|{model}|{info.GetRawText()}";
                            if(!snapshots.Add(snapshotKey))continue;
                            string? quality=null;
                            TokenUsage delta;
                            if(total is not null)
                            {
                                if(!total.Valid){warnings++;continue;}
                                if(total==cumulative)continue;
                                if(total.Total<cumulative.Total)
                                {
                                    if(lastUsage is null){warnings++;continue;}
                                    delta=lastUsage;
                                    if(delta.Total==0){warnings++;continue;}
                                    segment++;segments[session]=segment;quality="累计回退：按 last_token_usage 补位，存在统计缺口";warnings++;
                                    cumulative=total;highWater[session]=total;
                                    goto AddEvent;
                                }
                                if(total.Total==cumulative.Total)
                                {
                                    var difference=total-cumulative;
                                    var candidates=events.Where(e=>e.Session==session&&e.Segment==segment&&(e.Tokens+difference).Valid).ToList();
                                    if(candidates.Count==1)
                                    {
                                        var previous=candidates[0];
                                        var index=events.LastIndexOf(previous);events[index]=previous with{Tokens=previous.Tokens+difference};
                                        cumulative=total;highWater[session]=total;
                                    }
                                    else warnings++;
                                    continue;
                                }
                                delta=total-cumulative;
                                if(!delta.Valid){warnings++;continue;}
                                cumulative=total;highWater[session]=total;
                            }
                            else if(lastUsage is not null){delta=lastUsage;warnings++;}
                            else continue;
                            AddEvent:
                            if(!delta.Valid||delta.Total==0)continue;
                            var timestamp=DateTimeOffset.TryParse(row.Text("timestamp"),out var parsed)?parsed:(DateTimeOffset?)null;
                            var idMaterial=total is not null?$"{session}|{total.Total}":$"{session}|{timestamp:O}|{model}|{delta}";
                            if(segment>0)idMaterial+=$"|segment:{segment}";
                            var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idMaterial)));
                            if(!seen.Add(id))continue;
                            var pricing=lastUsage is not null&&lastUsage==delta
                                ?new PricingContext(RequestInputTokens:lastUsage.Input,ValuationDate:timestamp is null?null:DateOnly.FromDateTime(timestamp.Value.LocalDateTime))
                                :null;
                            events.Add(new(id,session,project,model,agent,timestamp,timestamp?.ToLocalTime().ToString("yyyy-MM-dd")??"日期未知",delta,timestamp is null,sourceKey,pricing){Segment=segment,QualityNote=quality,AccountScope=row.Text("account_scope"),AccountAttribution=row.Text("account_attribution")});
                        }
                        catch(Exception ex) when(ex is JsonException or OverflowException or InvalidOperationException){warnings++;}
                    }
                    if(forkPending)warnings++;
                    sources.Add(sourceKey);
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){warnings++;}
            }
        }
        return new(events,files,warnings,DateTimeOffset.Now){Sources=sources};
    }
    private static bool IsChildTurn(string? turn,string owner)=>Guid.TryParse(turn,out _)&&Guid.TryParse(owner,out _)&&turn![14]=='7'&&owner[14]=='7'&&string.CompareOrdinal(turn,owner)>=0;
    private static async IAsyncEnumerable<string?> Replay(IReadOnlyList<string> records,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken)
    {
        foreach(var record in records){cancellationToken.ThrowIfCancellationRequested();yield return record;}
        await Task.CompletedTask;
    }
    private static string ProjectName(string? value)=>string.IsNullOrWhiteSpace(value)?"(未知项目)":value.Replace('\\','/').TrimEnd('/').Split('/').LastOrDefault()??"(未知项目)";

    // 限制单行内存，不解析无关的对话正文；只持久化统计字段。
    public static async IAsyncEnumerable<string?> ReadRecords(string file,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        using var reader=new StreamReader(stream,Encoding.UTF8,true,65536);
        var buffer=new char[32768];var line=new StringBuilder();var oversize=false;
        int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),ct))>0)
        {
            ct.ThrowIfCancellationRequested();
            for(var i=0;i<count;i++)
            {
                var c=buffer[i];
                if(c=='\n')
                {
                    if(oversize)yield return null;
                    else {var text=line.ToString();if(Relevant(text))yield return text;}
                    line.Clear();oversize=false;
                }
                else if(!oversize){if(line.Length>=1_048_576){oversize=true;line.Clear();}else line.Append(c);}
            }
        }
        if(!oversize&&line.Length>0)
        {
            var text=line.ToString();
            if(Relevant(text))
            {
                var complete=false;try{using var doc=JsonDocument.Parse(text);complete=true;}catch(JsonException){}
                if(complete)yield return text;
            }
        }
    }
    private static bool Relevant(string text)=>text.Contains("\"session_meta\"")||text.Contains("\"turn_context\"")||text.Contains("\"token_count\"")||text.Contains("\"task_started\"");
}
