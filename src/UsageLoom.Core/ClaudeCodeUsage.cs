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
    // Session digest -> the name Claude Code shows for it: the /rename name, else its generated title.
    public IReadOnlyDictionary<string,string> Titles {get;init;}=new Dictionary<string,string>(StringComparer.Ordinal);
    public string SessionName(string session)=>Titles.TryGetValue(session,out var title)?title:ClaudeCodeParser.SessionLabel(session);
    public static ClaudeCodeSnapshot Empty(string status)=>new(status,[]);
}
// A session title record. Text is null when the record clears the name or holds no usable text.
public readonly record struct ClaudeCodeTitle(string Session,string? Text,bool Custom);

public static class ClaudeCodeParser
{
    private static string Text(ref Utf8JsonReader reader)=>reader.TokenType==JsonTokenType.String&&reader.ValueSpan.Length<=1024?reader.GetString()??"":"";
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string SessionLabel(string session)=>"Session · "+session[..Math.Min(12,session.Length)];
    // Titles are short one-line labels; control and format characters cannot reshape the UI.
    private static string? Title(string value)
    {
        var text=new string(value.Take(81).Select(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==UnicodeCategory.Format?' ':c).ToArray()).Trim();
        if(text.Length>80){text=text[..80];if(char.IsHighSurrogate(text[^1]))text=text[..^1];text=text.TrimEnd()+"…";}
        return text.Length==0?null:text;
    }
    public static ClaudeCodeUsage? Parse(ReadOnlySpan<byte> line,string project,string fallbackSession)=>
        Parse(line,project,fallbackSession,out _,out _);
    public static ClaudeCodeUsage? Parse(ReadOnlySpan<byte> line,string project,string fallbackSession,out bool usageRecord,out DateTimeOffset? recordedAt)=>
        Parse(line,project,fallbackSession,out usageRecord,out recordedAt,out _);
    // usageRecord: an assistant reply that carries usage, so a null result means lost usage.
    // Claude Code's own "<synthetic>" notices and other record types (attachments, user turns)
    // are not usage even when their content mentions it. recordedAt is the record's timestamp.
    // title: Claude Code's own session name record ("custom-title" from /rename, "ai-title" generated).
    public static ClaudeCodeUsage? Parse(ReadOnlySpan<byte> line,string project,string fallbackSession,out bool usageRecord,out DateTimeOffset? recordedAt,out ClaudeCodeTitle? title)
    {
        usageRecord=false;recordedAt=null;title=null;
        var reader=new Utf8JsonReader(line,new JsonReaderOptions{MaxDepth=64});
        string type="",id="",request="",session="",model="",timestamp="";string? aiTitle=null,customTitle=null;bool sidechain=false,hasUsage=false,invalid=false;
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
                    case "aiTitle":aiTitle=Text(ref reader);break;
                    case "customTitle":customTitle=Text(ref reader);break;
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
            if(reader.TokenType==JsonTokenType.EndObject&&(type=="custom-title"&&customTitle is not null||type=="ai-title"&&aiTitle is not null))
            {
                var name=string.IsNullOrEmpty(session)?fallbackSession:session;
                if(name.Length>0)title=new(Digest(name),Title(type=="custom-title"?customTitle!:aiTitle!),type=="custom-title");
                return null;
            }
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
// A file that only grew is read from its last complete line; truncation/rewrite is reread in full,
// and incomplete trailing records recover on the next pass.
public sealed class ClaudeCodeReader
{
    // Parsed: offset just after the last complete line; Anchor: hash of the bytes right before it.
    // Rejected/RejectedThrough cover complete lines only, so a resumed pass can extend them;
    // Skipped/SkippedThrough add this pass's trailing partial line or row-limit stop.
    private sealed record Cached(long Length,DateTime Modified,long Parsed,byte[] Anchor,IReadOnlyList<ClaudeCodeUsage> Rows,
        int Rejected,DateTimeOffset? RejectedThrough,int Skipped,DateTimeOffset? SkippedThrough,IReadOnlyDictionary<string,SessionTitle> Titles);
    // The latest of each title kind in one transcript; a /rename name wins over the generated title.
    private readonly record struct SessionTitle(string? Generated,string? Custom);
    private const long MaxFileBytes=1L<<30,MaxPassBytes=2L<<30;
    private const int AnchorBytes=4096;
    // Bytes of transcript read by the last pass, for tests of append-only resumption.
    internal long LastReadBytes {get;private set;}
    private Dictionary<string,Cached> cache=new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultHome=>Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is {Length:>0} path?path:
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");
    public void Clear()=>cache=new(StringComparer.OrdinalIgnoreCase);
    public ClaudeCodeSnapshot Read(string? home,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var next=new Dictionary<string,Cached>(StringComparer.OrdinalIgnoreCase);var all=new List<ClaudeCodeUsage>();int files=0,skipped=0;long readBytes=0;
        var titles=new Dictionary<string,string>(StringComparer.Ordinal);
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
                        var length=info.Length;var modified=info.LastWriteTimeUtc;
                        // Claude Code only appends. Resume after the last complete line when the bytes just
                        // before it are unchanged, so a long, growing session is not reread in full.
                        var resume=previous is not null&&previous.Parsed>0&&length>=previous.Parsed&&AnchorMatches(file,previous.Parsed,previous.Anchor);
                        var start=resume?previous!.Parsed:0;
                        if(length-start>MaxFileBytes||(readBytes+=length-start)>MaxPassBytes){Skip(written);continue;}
                        var rows=resume?new List<ClaudeCodeUsage>(previous!.Rows):new List<ClaudeCodeUsage>();var added=0;
                        var rejected=resume?previous!.Rejected:0;DateTimeOffset? rejectedThrough=resume?previous!.RejectedThrough:null;
                        var named=resume?new Dictionary<string,SessionTitle>(previous!.Titles,StringComparer.Ordinal):new Dictionary<string,SessionTitle>(StringComparer.Ordinal);
                        // A rejected record is dated by its own timestamp when readable, else by the file write time.
                        void Reject(DateTimeOffset? at){rejected++;var value=at??written;if(rejectedThrough is null||value>rejectedThrough)rejectedThrough=value;}
                        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan);
                        stream.Seek(start,SeekOrigin.Begin);
                        var buffer=new byte[65536];using var line=new MemoryStream();bool oversized=false,limited=false;
                        long remaining=length-start,position=start,parsed=start;
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
                                        var row=ClaudeCodeParser.Parse(bytes,parts[0],Path.GetFileNameWithoutExtension(file),out var usageRecord,out var recordedAt,out var title);
                                        if(row is not null){rows.Add(row);added++;}
                                        else if(title is {} found)
                                        {
                                            var current=named.GetValueOrDefault(found.Session);
                                            named[found.Session]=found.Custom?current with{Custom=found.Text}:current with{Generated=found.Text};
                                        }
                                        else if(usageRecord)Reject(recordedAt);
                                        else if(!IsValidJson(bytes))Reject(null);
                                    }
                                    line.SetLength(0);oversized=false;parsed=position+i+1;
                                }
                                else if(!oversized){if(line.Length>=4*1024*1024){oversized=true;line.SetLength(0);}else line.WriteByte(buffer[i]);}
                            }
                            position+=count;
                            // The rest stays unparsed; the next pass resumes from the last complete line.
                            if(added>250000){limited=true;break;}
                        }
                        // No newline means a writer may still be appending, even if the JSON currently parses.
                        var pending=(line.Length>0||oversized||limited)?1:0;
                        var anchor=Anchor(stream,parsed);
                        info.Refresh();
                        // A shrink or same-length rewrite during the read would mix bytes. Growth is ordinary
                        // appending: this pass covers exactly the first `length` bytes.
                        if(!info.Exists||info.Length<length||info.Length==length&&info.LastWriteTimeUtc!=modified){Skip(info.Exists?new DateTimeOffset(info.LastWriteTimeUtc):null);continue;}
                        var skippedThrough=pending>0&&(rejectedThrough is null||written>rejectedThrough)?written:rejectedThrough;
                        // A pass stopped by the row limit must not look complete to the unchanged-file shortcut.
                        entry=new(limited?-1:length,modified,parsed,anchor,ClaudeCodeParser.Normalize(rows),rejected,rejectedThrough,rejected+pending,skippedThrough,named);
                        next[file]=entry;
                    }
                    if(ReferenceEquals(entry,previous))next[file]=entry;
                    all.AddRange(entry.Rows);if(entry.Skipped>0)Skip(entry.SkippedThrough,entry.Skipped);
                    // A session is named by its own transcript; subagent files never rename their parent.
                    if(parts.Length!=4)foreach(var (session,title) in entry.Titles)if((title.Custom??title.Generated) is {} name)titles[session]=name;
                    if(all.Count>1_000_000){Skip(null);break;}
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Skip(null);}
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {return new("readFailed",[],files,skipped+1){SkippedThrough=DateTimeOffset.MaxValue};}
        token.ThrowIfCancellationRequested();cache=next;LastReadBytes=readBytes;
        var normalized=ClaudeCodeParser.Normalize(all);
        return new(skipped>0?"partial":files==0?"missing":normalized.Count==0?"empty":"ready",normalized,files,skipped,all.Count-normalized.Count){SkippedThrough=through,Titles=titles};
    }
    private static byte[] Anchor(FileStream stream,long parsed)
    {
        var size=(int)Math.Min(AnchorBytes,parsed);var bytes=new byte[size];
        stream.Seek(parsed-size,SeekOrigin.Begin);stream.ReadExactly(bytes);
        return SHA256.HashData(bytes);
    }
    private static bool AnchorMatches(string file,long parsed,byte[] anchor)
    {
        try
        {
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            return stream.Length>=parsed&&Anchor(stream,parsed).AsSpan().SequenceEqual(anchor);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return false;}
    }
    private static bool IsValidJson(ReadOnlySpan<byte> bytes)
    {
        try{var reader=new Utf8JsonReader(bytes);while(reader.Read())reader.Skip();return true;}catch(JsonException){return false;}
    }
}
