using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageLoom.Core;

namespace UsageLoom.Storage;

public sealed record IndexedFile(string Path,int Version,long Offset,long Length,long Written,long Created,
    string PrefixHash,[property: System.Text.Json.Serialization.JsonConverter(typeof(IndexRecordsConverter))] List<string> Records,int Warnings,string? AccountScope=null,int IntegrityWarnings=0)
{
    public bool RetainedAhead { get; init; }
    // Mode evidence is additive and independently versioned. Do not rewrite the
    // counter ledger: restart checkpoints authenticate its original prefix.
    public int ModeMetadataVersion { get; init; }
    public List<string> ModeRecords { get; init; }=[];
}
public sealed record IncrementalResult(ScanReport Report,long BytesParsed,int FilesUpdated)
{
    public string? BackupPath { get; init; }
    public long RecordsReplayed { get; init; }
    public bool Migrated { get; init; }
    public IReadOnlyList<int> PreviousParserVersions { get; init; }=[];
    public int DeferredFiles { get; init; }
    public int PreservedFiles { get; init; }
    public bool ModeMetadataMigrated { get; init; }
    public IReadOnlyList<UsageUncertainty> UncertainRanges { get; init; }=[];
}

/// <summary>仅索引必要元数据与计数；保留半行起点，索引与派生事件同事务保存。</summary>
public sealed class IncrementalHistory(HistoryStore store)
{
    public const int ParserVersion=7;
    public const int ModeMetadataVersion=1;
    private readonly SemaphoreSlim gate=new(1,1);
    private string? observedAccount,observedHome;
    private DateTimeOffset observedAt;
    public void SaveExitCheckpoint(string? account,string home,DateTimeOffset closedAt)
    {
        if(string.IsNullOrWhiteSpace(account)||account!=observedAccount||
           !string.Equals(Path.GetFullPath(home),observedHome,StringComparison.OrdinalIgnoreCase)||
           closedAt<observedAt||closedAt-observedAt>TimeSpan.FromMinutes(5))return;
        store.SaveRestartCheckpoint(account,home,observedAt,closedAt);
    }
    public Task<IncrementalResult> ScanAsync(string home,CancellationToken ct,IProgress<string>? progress=null,bool verifyIntegrity=false,string? accountScope=null)
        =>ScanCoreAsync(home,ct,progress,verifyIntegrity,false,accountScope);
    public Task<IncrementalResult> RebuildAsync(string home,CancellationToken ct,IProgress<string>? progress=null)
        =>ScanCoreAsync(home,ct,progress,true,true,null);
    private async Task<IncrementalResult> ScanCoreAsync(string home,CancellationToken ct,IProgress<string>? progress,bool verifyIntegrity,bool rebuild,string? accountScope)
    {
        await gate.WaitAsync(ct);
        var scanStarted=DateTimeOffset.UtcNow;var completed=false;
        var previousAccount=observedAccount;var previousHome=observedHome;var previousAt=observedAt;
        observedAccount=null; // Failed scans and restarts cannot establish continuity.
        try
        {
            home=System.IO.Path.GetFullPath(home);
            if(!Directory.Exists(home))throw new DirectoryNotFoundException("日志根目录不存在");
            var saved=rebuild?new Dictionary<string,IndexedFile>(StringComparer.OrdinalIgnoreCase):store.ReadIndexes();var updated=new List<IndexedFile>();
            var unversionedHistory=!rebuild&&saved.Count==0&&store.HasEvents();
            var previousParserVersions=unversionedHistory?[0]:saved.Values.Select(index=>index.Version).Where(version=>version!=ParserVersion).Distinct().Order().ToArray();
            var migrating=previousParserVersions.Length>0;
            var modeMigrating=!rebuild&&!migrating&&saved.Values.Any(index=>index.ModeMetadataVersion<ModeMetadataVersion);
            if(migrating)
            {
                // Parser output is derived data. Reusing offsets or event identities from
                // another schema can freeze all future increments, so rebuild it from the
                // immutable source logs and retain the old database as a rollback backup.
                var source=unversionedHistory?"未版本化历史":"旧索引 v"+string.Join(',',previousParserVersions);
                progress?.Report($"检测到{source}，正在验证原始日志并安全迁移");
                rebuild=true;saved=new(StringComparer.OrdinalIgnoreCase);
            }
            var records=new Dictionary<string,IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var preserved=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uncertain=new Dictionary<string,UsageUncertainty>(StringComparer.OrdinalIgnoreCase);
            void Preserve(string path,IndexedFile index,IReadOnlyList<string>? physical=null)
            {
                records[path]=index.Records;preserved.Add(path);
                uncertain[path]=physical is not null&&Owner(path,index.Records)==Owner(path,physical)
                    ?UsageUncertainty.FromRecords(index.Records.Concat(physical)):new(null,null);
            }
            long bytesParsed=0;var warnings=0;
            foreach(var leaf in new[]{"sessions","archived_sessions"})
            {
                var directory=System.IO.Path.Combine(home,leaf);if(!Directory.Exists(directory))continue;
                foreach(var path in Directory.EnumerateFiles(directory,"*.jsonl",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint}).Order(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();var info=new FileInfo(path);saved.TryGetValue(path,out var old);
                    var refreshModes=old is not null&&(old.ModeMetadataVersion<ModeMetadataVersion||verifyIntegrity);
                    if(old is not null&&!refreshModes&&old.Length==info.Length&&old.Written==info.LastWriteTimeUtc.Ticks&&old.Created==info.CreationTimeUtc.Ticks)
                    {
                        if(verifyIntegrity)
                        {
                            await using var verified=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                            if(await HashPrefix(verified,old.Offset,ct)!=old.PrefixHash)
                            {Preserve(path,old);continue;}
                            info.Refresh();
                            if(info.Length!=old.Length||info.LastWriteTimeUtc.Ticks!=old.Written||info.CreationTimeUtc.Ticks!=old.Created)
                                throw new IOException("完整校验期间日志变化，本次检查暂缓");
                        }
                        records[path]=old.Records;warnings+=old.Warnings;continue;
                    }
                    progress?.Report($"正在增量索引：已处理 {records.Count} 个文件");
                    await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                    var capturedLength=file.Length;var written=info.LastWriteTimeUtc.Ticks;var created=info.CreationTimeUtc.Ticks;
                    // Bind timestamps to this file's captured byte prefix, not the
                    // beginning of the whole scan (other files may take time).
                    var capturedAt=DateTimeOffset.UtcNow;
                    var paginationReplay=false;
                    if(old is not null)
                    {
                        if(old.RetainedAhead||capturedLength<old.Length||created!=old.Created||capturedLength==old.Length&&written!=old.Written)
                            paginationReplay=true;
                        else paginationReplay=await HashPrefix(file,old.Offset,ct)!=old.PrefixHash;
                    }
                    var modeRecords=new List<string>(old?.ModeRecords??[]);
                    if(refreshModes&&!paginationReplay)
                    {
                        modeRecords=MergeModes(modeRecords,await ReadModeRecords(file,capturedLength,ct));
                        bytesParsed=checked(bytesParsed+capturedLength);
                    }
                    file.Position=paginationReplay?0:old?.Offset??0;var offset=file.Position;var buffer=new byte[65536];var line=new MemoryStream();var oversized=false;
                    var compact=old is null||paginationReplay?new List<string>():new List<string>(old.Records);var fileWarnings=paginationReplay?0:old?.Warnings??0;var integrityWarnings=paginationReplay?0:old?.IntegrityWarnings??0;
                    var recordAccountScope=old is not null&&!string.IsNullOrWhiteSpace(accountScope)&&previousAccount==accountScope&&string.Equals(previousHome,home,StringComparison.OrdinalIgnoreCase)&&string.Equals(old.AccountScope,accountScope,StringComparison.Ordinal)?accountScope:null;
                    DateTimeOffset? newFileSince=null;
                    if(old is null&&!rebuild&&!string.IsNullOrWhiteSpace(accountScope)&&accountScope==previousAccount&&
                        string.Equals(home,previousHome,StringComparison.OrdinalIgnoreCase)&&scanStarted>=previousAt&&capturedAt>=scanStarted&&
                        info.CreationTimeUtc>previousAt.UtcDateTime&&info.CreationTimeUtc<=capturedAt.UtcDateTime)
                    {recordAccountScope=accountScope;newFileSince=previousAt;}
                    while(file.Position<capturedLength)
                    {
                        var read=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,capturedLength-file.Position)),ct);
                        if(read==0)throw new IOException("扫描期间日志被截断");bytesParsed+=read;
                        var blockStart=file.Position-read;
                        for(var i=0;i<read;i++)
                        {
                            if(buffer[i]==(byte)'\n')
                            {
                                if(oversized)fileWarnings++;
                                else {var data=line.ToArray();CollectMode(data,modeRecords);Compact(data,compact,ref fileWarnings,ref integrityWarnings,recordAccountScope,newFileSince,capturedAt);}
                                line.SetLength(0);oversized=false;offset=blockStart+i+1;
                            }
                            else if(!oversized)
                            {
                                if(line.Length>=1_048_576){oversized=true;line.SetLength(0);}else line.WriteByte(buffer[i]);
                            }
                        }
                    }
                    if(!oversized&&line.Length>0)
                    {
                        var complete=false;
                        try{using var tail=JsonDocument.Parse(Encoding.UTF8.GetString(line.ToArray()).TrimStart('\uFEFF'));complete=true;}catch(JsonException){}
                        if(complete){var data=line.ToArray();CollectMode(data,modeRecords);Compact(data,compact,ref fileWarnings,ref integrityWarnings,recordAccountScope,newFileSince,capturedAt);offset=capturedLength;}
                    }
                    // 只有不完整末行回退到行首；下次追加从该字节位置重读。
                    line.Dispose();
                    var hash=await HashPrefix(file,offset,ct);
                    info.Refresh();
                    if(info.Length<capturedLength||info.CreationTimeUtc.Ticks!=created)throw new IOException("扫描期间来源发生替换，取消本次保存");
                    // Append-only growth after the captured boundary is safe: the
                    // current prefix is committed and the tail remains for the next
                    // incremental pass. Rewrites inside that prefix are still rejected.
                    if(info.Length!=capturedLength||info.LastWriteTimeUtc.Ticks!=written)
                    {
                        await using var current=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                        if(await HashPrefix(current,offset,ct)!=hash)throw new IOException("扫描期间已读取的日志前缀发生变化，取消本次保存");
                    }
                    var retainedAhead=false;
                    if(paginationReplay)
                    {
                        if(integrityWarnings>0||!PaginatedContinuation.TryJoin(old!.Records,compact,out var joined,out retainedAhead)){Preserve(path,old!,integrityWarnings==0?compact:null);continue;}
                        compact=joined;
                    }
                    var index=new IndexedFile(path,ParserVersion,offset,capturedLength,written,created,hash,compact,fileWarnings,string.IsNullOrWhiteSpace(accountScope)?null:accountScope,integrityWarnings)
                        {RetainedAhead=retainedAhead,ModeMetadataVersion=ModeMetadataVersion,ModeRecords=modeRecords.Distinct(StringComparer.Ordinal).ToList()};
                    updated.Add(index);records[path]=compact;warnings+=fileWarnings;
                }
            }
            if(rebuild&&records.Count==0)throw new InvalidDataException("没有可重建的日志文件，旧统计已保留");
            // Do not replay another physical file belonging to a quarantined session:
            // its new baseline could otherwise replace previously recorded events.
            var blockedOwners=preserved.Select(path=>Owner(path,saved[path].Records)).ToHashSet(StringComparer.Ordinal);
            foreach(var index in updated.Where(i=>blockedOwners.Contains(Owner(i.Path,i.Records))).ToArray())
            {updated.Remove(index);preserved.Add(index.Path);uncertain[index.Path]=new(null,null);}
            if(updated.Count==0)
            {var cached=store.Read(ct);completed=true;return new(new(cached,records.Count,warnings+preserved.Count,DateTimeOffset.Now){UsedCache=true},0,0){PreservedFiles=preserved.Count,UncertainRanges=uncertain.Values.ToArray()};}
            HashSet<string>? affectedSessions=null;
            var ancestryRecords=new Dictionary<string,IReadOnlyList<string>>(records,StringComparer.OrdinalIgnoreCase);
            foreach(var index in saved.Values)
                if(index.Version==ParserVersion)ancestryRecords.TryAdd(index.Path,index.Records);
            if(!rebuild)
            {
                // Recompute only affected sessions, retaining their cross-file and removed-file prefix.
                affectedSessions=updated.Select(index=>Owner(index.Path,index.Records)).ToHashSet(StringComparer.Ordinal);
                records=records.Where(pair=>affectedSessions.Contains(Owner(pair.Key,pair.Value))).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var index in saved.Values)
                    if(!records.ContainsKey(index.Path)&&index.Version==ParserVersion&&affectedSessions.Contains(Owner(index.Path,index.Records)))records[index.Path]=index.Records;
            }
            var replayed=records.Values.Sum(value=>(long)value.Count);
            // Settings are collected separately and matched by effective time by
            // the scanner. Appending them must not change counter replay order.
            var modeIndexes=new Dictionary<string,IndexedFile>(saved,StringComparer.OrdinalIgnoreCase);
            foreach(var index in updated)modeIndexes[index.Path]=index;
            IReadOnlyDictionary<string,IReadOnlyList<string>> WithModes(Dictionary<string,IReadOnlyList<string>> source)=>
                source.ToDictionary(pair=>pair.Key,pair=>(IReadOnlyList<string>)pair.Value.Concat(modeIndexes.GetValueOrDefault(pair.Key)?.ModeRecords??[]).ToArray(),StringComparer.OrdinalIgnoreCase);
            var report=await new HistoryScanner().ScanAsync(home,ct,progress,WithModes(records),WithModes(ancestryRecords));
            report=report with{Warnings=report.Warnings+warnings+preserved.Count};
            if(rebuild)
            {
                // Validate all captured prefixes again before replacement. A live file
                // may have appended bytes or an unfinished last line; neither invalidates
                // the immutable prefix and both are resumed by the next incremental scan.
                var deferredFiles=0;
                foreach(var index in updated)
                {
                    ct.ThrowIfCancellationRequested();var current=new FileInfo(index.Path);
                    if(!current.Exists||current.Length<index.Length||current.CreationTimeUtc.Ticks!=index.Created)
                        throw new IOException("重建期间日志被截断或替换，旧统计已保留，请稍后重试");
                    await using var verified=new FileStream(index.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
                    if(await HashPrefix(verified,index.Offset,ct)!=index.PrefixHash)
                        throw new IOException("重建期间已读取的日志前缀发生变化，旧统计已保留，请稍后重试");
                    if(index.Offset<index.Length||current.Length>index.Length)deferredFiles++;
                }
                if(report.Files!=records.Count)throw new IOException("重建期间文件清单变化，旧统计已保留");
                var backup=store.ReplaceWithBackup(report,updated,ct);
                return new(report,bytesParsed,updated.Count){BackupPath=backup,RecordsReplayed=replayed,Migrated=migrating,PreviousParserVersions=previousParserVersions,DeferredFiles=deferredFiles};
            }
            // Preserve the complete database, including committed WAL pages,
            // before the first metadata upgrade. No floor/proof/cache is deleted.
            modeMigrating=modeMigrating&&updated.Any(index=>saved.TryGetValue(index.Path,out var previous)&&previous.ModeMetadataVersion<ModeMetadataVersion);
            var modeBackup=modeMigrating?store.BackupCapacityData():null;
            if(modeMigrating)
            {
                // Compare the same captured counter ledger with and without the
                // sidecar. New log appends may legitimately correct categories or
                // move a high-water event; comparing to the old database instead
                // would permanently block a safe, ordinary incremental update.
                var counterBaseline=await new HistoryScanner().ScanAsync(home,ct,indexedRecords:records,ancestryRecords:ancestryRecords);
                ValidateModeUpgrade(counterBaseline.Events,report.Events);
            }
            store.Save(report,ct,indexes:updated,replaceSessions:affectedSessions);
            completed=true;
            return new(report,bytesParsed,updated.Count){BackupPath=modeBackup,ModeMetadataMigrated=modeMigrating,RecordsReplayed=replayed,PreservedFiles=preserved.Count,UncertainRanges=uncertain.Values.ToArray()};
        }
        finally{if(completed&&!rebuild){observedAccount=accountScope;observedHome=home;observedAt=scanStarted;}gate.Release();}
    }
    private static string Owner(string path,IReadOnlyList<string> records)
    {
        foreach(var record in records)
        {
            using var doc=JsonDocument.Parse(record);var root=doc.RootElement;
            if(root.Text("type")=="session_meta"&&root.TryGetProperty("payload",out var payload))return payload.Text("id")??payload.Text("session_id")??Path.GetFileNameWithoutExtension(path);
        }
        return Path.GetFileNameWithoutExtension(path);
    }
    private static async Task<string> HashPrefix(FileStream file,long length,CancellationToken ct)
    {
        file.Position=0;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];
        long consumed=0;
        while(consumed<length){var read=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,length-consumed)),ct);if(read==0)throw new IOException("索引前缀缺失");hash.AppendData(buffer,0,read);consumed+=read;}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void ValidateModeUpgrade(IReadOnlyList<UsageEvent> previous,IReadOnlyList<UsageEvent> incoming)
    {
        var next=incoming.ToDictionary(item=>item.Id,StringComparer.Ordinal);
        if(previous.Count!=incoming.Count)throw new InvalidDataException("模式证据升级改变了计数记录数量，旧数据已保留");
        foreach(var old in previous)
            if(!next.TryGetValue(old.Id,out var item)||old.Session!=item.Session||old.Tokens!=item.Tokens||
                old.Timestamp!=item.Timestamp||old.Model!=item.Model||old.Segment!=item.Segment||
                old.Source!=item.Source||old.AccountScope!=item.AccountScope||old.AccountAttribution!=item.AccountAttribution)
                throw new InvalidDataException("模式证据升级改变了既有计数或时间边界，旧数据已保留");
    }
    private static List<string> MergeModes(IEnumerable<string> previous,IEnumerable<string> current)=>
        previous.Concat(current).Distinct(StringComparer.Ordinal).ToList();

    private static async Task<List<string>> ReadModeRecords(FileStream file,long length,CancellationToken ct)
    {
        file.Position=0;var result=new List<string>();var buffer=new byte[65536];using var line=new MemoryStream();var oversized=false;
        while(file.Position<length)
        {
            var count=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,length-file.Position)),ct);
            if(count==0)throw new IOException("模式证据读取期间来源被截断");
            for(var i=0;i<count;i++)
            {
                if(buffer[i]==(byte)'\n')
                {if(!oversized)CollectMode(line.ToArray(),result);line.SetLength(0);oversized=false;}
                else if(!oversized)
                {if(line.Length>=1_048_576){oversized=true;line.SetLength(0);}else line.WriteByte(buffer[i]);}
            }
        }
        if(!oversized&&line.Length>0)CollectMode(line.ToArray(),result);
        return result;
    }
    private static void CollectMode(byte[] bytes,List<string> records)
    {
        if(bytes.Length==0)return;
        try
        {
            using var document=JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)return;
            var type=root.Text("type");var minimal=new Dictionary<string,object?>();
            static void Copy(JsonElement source,Dictionary<string,object?> target,params string[] names)
            {
                // An explicit null/invalid value clears evidence; dropping its
                // key would incorrectly inherit an earlier Fast configuration.
                foreach(var name in names)
                    if(source.TryGetProperty(name,out var field))
                        target[name]=field.ValueKind==JsonValueKind.String&&field.GetString() is {Length:>0 and <=256} value?value:null;
            }
            if(type=="turn_context")
            {
                Copy(payload,minimal,"model","effort","reasoning_effort","service_tier","turn_id","thread_id");
                // Even a model-only or invalid context is a request boundary.
                // Keep its original thread/turn constraints outside Records.
            }
            else if(type=="event_msg"&&payload.Text("type")=="thread_settings_applied"&&
                    payload.TryGetProperty("thread_settings",out var settings)&&settings.ValueKind==JsonValueKind.Object)
            {
                var values=new Dictionary<string,object?>();Copy(settings,values,"model","service_tier","reasoning_effort");
                minimal["type"]="thread_settings_applied";Copy(payload,minimal,"thread_id");minimal["thread_settings"]=values;
            }
            else return;
            long? ordinal=root.TryGetProperty("ordinal",out var sequence)&&sequence.ValueKind==JsonValueKind.Number&&sequence.TryGetInt64(out var position)?position:null;
            records.Add(JsonSerializer.Serialize(new{type,timestamp=root.Text("timestamp"),ordinal,payload=minimal}));
        }
        catch(JsonException){} // Counter parsing owns integrity errors; never store the raw line.
    }
    private static void Compact(byte[] bytes,List<string> records,ref int warnings,ref int integrityWarnings,string? accountScope,DateTimeOffset? newFileSince=null,DateTimeOffset capturedAt=default)
    {
        if(bytes.Length==0)return;
        try
        {
            // UTF-8 BOM 仅在首行出现时剥离；只保存白名单字段，不保存原始响应。
            var text=Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            using var doc=JsonDocument.Parse(text);var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)return;
            var type=root.Text("type");var minimal=new Dictionary<string,object?>();
            if(type=="session_meta")
            {
                foreach(var key in new[]{"id","session_id","forked_from_id","parent_thread_id","history_mode"})if(payload.Text(key)is{} value)minimal[key]=value;
                if(payload.Text("cwd")is{} cwd)minimal["cwd"]=cwd.Replace('\\','/').TrimEnd('/').Split('/').LastOrDefault();
                if(payload.TryGetProperty("source",out var source))
                {var marker=source.ToString();minimal["source"]=marker.Contains("guardian",StringComparison.OrdinalIgnoreCase)?"guardian":marker.Contains("memory",StringComparison.OrdinalIgnoreCase)?"memory":marker.Contains("agent",StringComparison.OrdinalIgnoreCase)?"subagent":"main";}
            }
            else if(type=="turn_context")
            {minimal["model"]=payload.Text("model");}
            else if(type=="event_msg"&&payload.Text("type")=="task_started")
            {minimal["type"]="task_started";minimal["turn_id"]=payload.Text("turn_id");}
            else if(type=="event_msg"&&payload.Text("type")=="token_count"&&payload.TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object)
            {
                var counters=new Dictionary<string,object?>();
                foreach(var field in new[]{"total_token_usage","last_token_usage"})
                {
                    if(!info.TryGetProperty(field,out var usage)||usage.ValueKind!=JsonValueKind.Object)continue;
                    var categories=new Dictionary<string,object?>();
                    foreach(var key in new[]{"input_tokens","cached_input_tokens","cache_write_input_tokens","output_tokens","reasoning_output_tokens","total_tokens"})
                        if(usage.TryGetProperty(key,out var value)){if(value.ValueKind==JsonValueKind.Null)categories[key]=null;else if(value.TryLong(out var n)&&n>=0)categories[key]=n;else throw new JsonException("无效计数");}
                    counters[field]=categories;
                }
                minimal["type"]="token_count";minimal["info"]=counters;
            }
            else return;
            if(newFileSince is {} since&&(!DateTimeOffset.TryParse(root.Text("timestamp"),out var timestamp)||timestamp<=since||timestamp>capturedAt))accountScope=null;
            long? ordinal=root.TryGetProperty("ordinal",out var sequence)&&sequence.ValueKind==JsonValueKind.Number&&sequence.TryGetInt64(out var position)?position:null;
            records.Add(JsonSerializer.Serialize(new{type,timestamp=root.Text("timestamp"),ordinal,account_scope=accountScope,payload=minimal}));
        }
        catch(JsonException){warnings++;integrityWarnings++;}
    }
}
