using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageLoom.Core;

// Only this allowlisted metadata survives parsing. Conversation content is never retained.
public sealed record ClaudeCodeUsage(string Message,string Request,string Session,string Project,string Model,
    DateTimeOffset At,long Input,long Output,long CacheRead,long CacheWrite,bool Sidechain=false)
{
    public long Total=>Input+Output+CacheRead+CacheWrite;
    // Null means the transcript did not expose a trustworthy write-TTL split.
    public long? CacheWrite1h { get; init; }
}
public sealed record ClaudeCodeSnapshot(string Status,IReadOnlyList<ClaudeCodeUsage> Rows,int Files=0,int Skipped=0,int Duplicates=0)
{
    // Latest time any skipped usage could belong to: the record's own timestamp, else its file's
    // last write. MaxValue when unknown. Lets estimates ignore gaps that end before their cycle.
    public DateTimeOffset? SkippedThrough {get;init;}
    public static ClaudeCodeSnapshot Empty(string status)=>new(status,[]);
}

public static class ClaudeCodeParser
{
    private static string Text(ref Utf8JsonReader reader)=>reader.TokenType==JsonTokenType.String&&reader.ValueSpan.Length<=1024?reader.GetString()??"":"";
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static ClaudeCodeUsage? Parse(ReadOnlySpan<byte> line,string project,string fallbackSession)=>
        Parse(line,project,fallbackSession,out _,out _);
    // usageRecord: an assistant reply that carries usage, so a null result means lost usage.
    // Claude Code's own "<synthetic>" notices and other record types (attachments, user turns)
    // are not usage even when their content mentions it. recordedAt is the record's timestamp.
    public static ClaudeCodeUsage? Parse(ReadOnlySpan<byte> line,string project,string fallbackSession,out bool usageRecord,out DateTimeOffset? recordedAt)
    {
        usageRecord=false;recordedAt=null;
        var reader=new Utf8JsonReader(line,new JsonReaderOptions{MaxDepth=64});
        string type="",id="",request="",session="",model="",timestamp="";bool sidechain=false,hasUsage=false,invalid=false;
        long? input=null,output=null;long read=0,write=0;long? write1h=null,write5m=null;
        try
        {
            if(!reader.Read()||reader.TokenType!=JsonTokenType.StartObject)return null;
            while(reader.Read()&&reader.TokenType!=JsonTokenType.EndObject)
            {
                if(reader.TokenType!=JsonTokenType.PropertyName)return null;
                var key=reader.GetString();if(!reader.Read())return null;
                switch(key)
                {
                    case "type":type=Text(ref reader);break;
                    case "timestamp":timestamp=Text(ref reader);break;
                    case "requestId":request=Text(ref reader);break;
                    case "sessionId":session=Text(ref reader);break;
                    case "isSidechain":sidechain=reader.TokenType==JsonTokenType.True;break;
                    case "message" when reader.TokenType==JsonTokenType.StartObject:
                        while(reader.Read()&&reader.TokenType!=JsonTokenType.EndObject)
                        {
                            if(reader.TokenType!=JsonTokenType.PropertyName)return null;
                            var field=reader.GetString();if(!reader.Read())return null;
                            if(field=="id")id=Text(ref reader);
                            else if(field=="model")model=Text(ref reader);
                            else if(field=="usage"&&reader.TokenType==JsonTokenType.StartObject)
                            {
                                hasUsage=true;
                                while(reader.Read()&&reader.TokenType!=JsonTokenType.EndObject)
                                {
                                    if(reader.TokenType!=JsonTokenType.PropertyName)return null;
                                    var counter=reader.GetString();if(!reader.Read())return null;
                                    if(counter is "input_tokens" or "output_tokens" or "cache_read_input_tokens" or "cache_creation_input_tokens")
                                    {
                                        if(reader.TokenType!=JsonTokenType.Number||!reader.TryGetInt64(out var value)||value<0||value>1_000_000_000_000L){invalid=true;reader.Skip();continue;}
                                        switch(counter){case "input_tokens":input=value;break;case "output_tokens":output=value;break;case "cache_read_input_tokens":read=value;break;case "cache_creation_input_tokens":write=value;break;}
                                    }
                                    else if(counter=="cache_creation"&&reader.TokenType==JsonTokenType.StartObject)
                                    {
                                        while(reader.Read()&&reader.TokenType!=JsonTokenType.EndObject)
                                        {
                                            if(reader.TokenType!=JsonTokenType.PropertyName)return null;
                                            var part=reader.GetString();if(!reader.Read())return null;
                                            if(part is "ephemeral_1h_input_tokens" or "ephemeral_5m_input_tokens")
                                            {
                                                if(reader.TokenType!=JsonTokenType.Number||!reader.TryGetInt64(out var value)||value<0||value>1_000_000_000_000L){invalid=true;reader.Skip();continue;}
                                                if(part=="ephemeral_1h_input_tokens")write1h=value;else write5m=value;
                                            }
                                            else reader.Skip();
                                        }
                                    }
                                    else reader.Skip(); // Includes nested cache TTL breakdown: never count it twice.
                                }
                            }
                            else reader.Skip(); // content, tool inputs, reasoning and all unknown fields
                        }
                        break;
                    default:reader.Skip();break;
                }
                if(reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)reader.Skip();
            }
            usageRecord=type=="assistant"&&hasUsage&&!model.StartsWith('<');
            if(DateTimeOffset.TryParse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var stamp))recordedAt=stamp;
            if(reader.TokenType!=JsonTokenType.EndObject||reader.Read()||type!="assistant"||!hasUsage||invalid||input is null||output is null||
                !DateTimeOffset.TryParse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at)||at.Year<2020||
                string.IsNullOrWhiteSpace(model)||model.Length>128||model.StartsWith('<'))return null;
            // message.id is shared by streaming content blocks; record uuid is NOT that identity.
            if(string.IsNullOrWhiteSpace(id))return null; // No reliable identity: do not risk double counting.
            if(write1h>write||write5m>write||write1h is not null&&write5m is not null&&write1h+write5m!=write)return null;
            if(write1h is null&&write5m==write)write1h=0;
            if(write1h==write)write5m=0;
            return new(Digest(id),string.IsNullOrEmpty(request)?"":Digest(request),Digest(string.IsNullOrEmpty(session)?fallbackSession:session),
                project,model,at,input.Value,output.Value,read,write,sidechain){CacheWrite1h=write1h};
        }
        catch(JsonException){return null;}
    }

    public static IReadOnlyList<ClaudeCodeUsage> Normalize(IEnumerable<ClaudeCodeUsage> rows)
    {
        var result=new List<ClaudeCodeUsage>();
        foreach(var messages in rows.GroupBy(r=>r.Message))
        {
            var parents=messages.Where(r=>!r.Sidechain).ToArray();
            // Sidechain transcripts can replay parent messages with a new request ID.
            var candidates=parents.Length>0?parents:messages.ToArray();
            foreach(var group in candidates.GroupBy(r=>r.Request.Length>0?r.Request:r.Session))
                result.Add(group.OrderBy(r=>r.At).ThenBy(r=>r.Total).ThenBy(r=>r.Session,StringComparer.Ordinal).ThenBy(r=>r.Project,StringComparer.Ordinal).Last());
        }
        return result.OrderBy(r=>r.At).ThenBy(r=>r.Message,StringComparer.Ordinal).ThenBy(r=>r.Request,StringComparer.Ordinal).ToArray();
    }
}

// Per-file in-memory metadata cache. No transcript copies, credentials, database or network access.
// Changed files are reread so truncation/rewrite and incomplete trailing records recover safely.
public sealed class ClaudeCodeReader
{
    private sealed record Cached(long Length,DateTime Modified,IReadOnlyList<ClaudeCodeUsage> Rows,int Skipped,DateTimeOffset? SkippedThrough);
    private Dictionary<string,Cached> cache=new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultHome=>Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is {Length:>0} path?path:
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
    public void Clear()=>cache=new(StringComparer.OrdinalIgnoreCase);
    public ClaudeCodeSnapshot Read(string? home,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var next=new Dictionary<string,Cached>(StringComparer.OrdinalIgnoreCase);var all=new List<ClaudeCodeUsage>();int files=0,skipped=0;long readBytes=0;
        DateTimeOffset? through=null;
        void Skip(DateTimeOffset? at,int count=1){skipped+=count;var value=at??DateTimeOffset.MaxValue;if(through is null||value>through)through=value;}
        try
        {
            home=string.IsNullOrWhiteSpace(home)?DefaultHome:home.Trim();
            if(!Path.IsPathFullyQualified(home)||home.StartsWith(@"\\"))return ClaudeCodeSnapshot.Empty("invalidPath");
            var root=Path.Combine(home,"projects");
            if(!Directory.Exists(root)){Clear();return ClaudeCodeSnapshot.Empty("missing");}
            // Never follow junctions/symlinks, including explicitly selected ancestors.
            for(var parent=new DirectoryInfo(root);parent is not null;parent=parent.Parent)
                if((parent.Attributes&FileAttributes.ReparsePoint)!=0)return ClaudeCodeSnapshot.Empty("invalidPath");
            var options=new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=6};
            foreach(var file in Directory.EnumerateFiles(root,"*.jsonl",options))
            {
                token.ThrowIfCancellationRequested();
                if(++files>20000){Skip(null);break;}
                // Only transcript files; tool-results and backup/orphan files are not usage sources.
                var relative=Path.GetRelativePath(root,file);var parts=relative.Split(Path.DirectorySeparatorChar);
                if(!(parts.Length==2||parts.Length==3&&parts[2]=="chat.jsonl"||parts.Length==4&&parts[2]=="subagents")||Path.GetFileName(file).Contains(".orphaned-",StringComparison.Ordinal))continue;
                try
                {
                    var info=new FileInfo(file);Cached entry;var written=new DateTimeOffset(info.LastWriteTimeUtc);
                    if(cache.TryGetValue(file,out var previous)&&previous.Length==info.Length&&previous.Modified==info.LastWriteTimeUtc)entry=previous;
                    else
                    {
                        if(info.Length>128L*1024*1024||(readBytes+=info.Length)>512L*1024*1024){Skip(written);continue;}
                        var length=info.Length;var modified=info.LastWriteTimeUtc;
                        var rows=new List<ClaudeCodeUsage>();var rejected=0;DateTimeOffset? rejectedThrough=null;
                        // A rejected record is dated by its own timestamp when readable, else by the file write time.
                        void Reject(DateTimeOffset? at){rejected++;var value=at??written;if(rejectedThrough is null||value>rejectedThrough)rejectedThrough=value;}
                        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan);
                        var buffer=new byte[65536];using var line=new MemoryStream();bool oversized=false;long remaining=length;
                        while(remaining>0)
                        {
                            token.ThrowIfCancellationRequested();var count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(count==0)break;remaining-=count;
                            for(var i=0;i<count;i++)
                            {
                                if(buffer[i]==10)
                                {
                                    if(oversized)Reject(null);
                                    else if(line.Length>0)
                                    {
                                        var bytes=line.GetBuffer().AsSpan(0,(int)line.Length);
                                        if(bytes.StartsWith("\uFEFF"u8))bytes=bytes[3..];
                                        var row=ClaudeCodeParser.Parse(bytes,parts[0],Path.GetFileNameWithoutExtension(file),out var usageRecord,out var recordedAt);
                                        if(row is not null)rows.Add(row);
                                        else if(usageRecord)Reject(recordedAt);
                                        else if(!IsValidJson(bytes))Reject(null);
                                    }
                                    line.SetLength(0);oversized=false;
                                }
                                else if(!oversized){if(line.Length>=4*1024*1024){oversized=true;line.SetLength(0);}else line.WriteByte(buffer[i]);}
                            }
                            if(rows.Count>250000){Reject(null);break;}
                        }
                        // No newline means a writer may still be appending, even if the JSON currently parses.
                        if(line.Length>0||oversized)Reject(null);
                        entry=new(length,modified,ClaudeCodeParser.Normalize(rows),rejected,rejectedThrough);
                        info.Refresh();
                        // Do not publish a mixture of bytes from a concurrent rewrite or truncated file.
                        if(!info.Exists||info.Length!=length||info.LastWriteTimeUtc!=modified){Skip(info.Exists?new DateTimeOffset(info.LastWriteTimeUtc):null);continue;}
                        next[file]=entry;
                    }
                    if(ReferenceEquals(entry,previous))next[file]=entry;
                    all.AddRange(entry.Rows);if(entry.Skipped>0)Skip(entry.SkippedThrough,entry.Skipped);
                    if(all.Count>1_000_000){Skip(null);break;}
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Skip(null);}
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {return new("readFailed",[],files,skipped+1){SkippedThrough=DateTimeOffset.MaxValue};}
        token.ThrowIfCancellationRequested();cache=next;
        var normalized=ClaudeCodeParser.Normalize(all);
        return new(skipped>0?"partial":files==0?"missing":normalized.Count==0?"empty":"ready",normalized,files,skipped,all.Count-normalized.Count){SkippedThrough=through};
    }
    private static bool IsValidJson(ReadOnlySpan<byte> bytes)
    {
        try{var reader=new Utf8JsonReader(bytes);while(reader.Read())reader.Skip();return true;}catch(JsonException){return false;}
    }
}
