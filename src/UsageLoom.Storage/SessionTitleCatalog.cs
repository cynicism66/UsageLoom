using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageLoom.Core;

namespace UsageLoom.Storage;

// Display-only metadata. Never open rollout bodies, credentials, or writable databases.
public sealed class SessionTitleCatalog
{
    private string? currentHome;
    private Dictionary<string,string> indexNames=new(StringComparer.Ordinal);
    private Dictionary<string,ThreadName> databaseNames=new(StringComparer.Ordinal);
    private sealed record ThreadName(bool HasNameColumn,string? Name,bool Background);
    public int UnavailableSources { get; private set; }

    public IReadOnlyDictionary<string,string> Read(string home,CancellationToken ct=default)
    {
        var canonical=Path.GetFullPath(home);
        if(!string.Equals(currentHome,canonical,StringComparison.OrdinalIgnoreCase))
        {currentHome=canonical;indexNames=new(StringComparer.Ordinal);databaseNames=new(StringComparer.Ordinal);}
        UnavailableSources=0;
        ct.ThrowIfCancellationRequested();
        try{indexNames=ReadIndex(Path.Combine(canonical,"session_index.jsonl"),ct);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){UnavailableSources++;}
        try{databaseNames=ReadDatabase(canonical,ct);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or SqliteException or JsonException){UnavailableSources++;}
        ct.ThrowIfCancellationRequested();
        var names=new Dictionary<string,string>(indexNames,StringComparer.Ordinal);
        foreach(var (id,thread) in databaseNames)
        {
            // The live name column is authoritative, including an explicit cleared name.
            // Older schemas without that column continue to use the title index.
            if(thread.HasNameColumn){names.Remove(id);if(thread.Name is {} name)names[id]=name;}
            if(!names.ContainsKey(id)&&thread.Background)
                names[id]=L10n.T("session.background")+" · "+ShortId(id);
        }
        return names;
    }
    private static string ShortId(string id)=>id.Length<=12?id:id[..8]+"…";
    private static string? Text(JsonElement item,string key)=>item.ValueKind==JsonValueKind.Object&&item.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static string? Clean(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var text=new string(value.Take(301).Select(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format?' ':c).ToArray()).Trim();
        if(text.Length>300){text=text[..300];if(char.IsHighSurrogate(text[^1]))text=text[..^1];text+="…";}
        return string.IsNullOrWhiteSpace(text)?null:text;
    }
    private static Dictionary<string,string> ReadIndex(string path,CancellationToken ct)
    {
        var names=new Dictionary<string,(string? Name,DateTimeOffset? At)>(StringComparer.Ordinal);
        if(!File.Exists(path))return new(StringComparer.Ordinal);
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>16*1024*1024)throw new IOException("Title index exceeds metadata budget");
        using var reader=new StreamReader(file);
        long budget=16*1024*1024;
        while(reader.ReadLine() is {} line)
        {
            ct.ThrowIfCancellationRequested();budget-=line.Length;if(budget<0)throw new IOException("Title index exceeds metadata budget");
            try
            {
                using var document=JsonDocument.Parse(line);var row=document.RootElement;
                var id=Text(row,"id");if(string.IsNullOrWhiteSpace(id)||id.Length>128)continue;
                if(!row.TryGetProperty("thread_name",out var title)||title.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))continue;
                DateTimeOffset? at=DateTimeOffset.TryParse(Text(row,"updated_at"),out var parsed)?parsed:null;
                if(names.TryGetValue(id,out var previous)&&previous.At is {} old&&at is {} next&&next<old)continue;
                names[id]=(Clean(Text(row,"thread_name")),at);
            }
            catch(JsonException){} // A malformed/partly written line must not hide other names.
        }
        return names.Where(row=>row.Value.Name is not null).ToDictionary(row=>row.Key,row=>row.Value.Name!,StringComparer.Ordinal);
    }
    private static Dictionary<string,ThreadName> ReadDatabase(string home,CancellationToken ct)
    {
        var result=new Dictionary<string,ThreadName>(StringComparer.Ordinal);
        if(!Directory.Exists(home))return result;
        var database=Directory.EnumerateFiles(home,"state_*.sqlite",new EnumerationOptions{AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false})
            .Select(path=>(Path:path,Version:int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(6),out var version)?version:-1))
            .Where(item=>item.Version>=0).OrderByDescending(item=>item.Version).FirstOrDefault();
        if(database.Path is null)return result;
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database.Path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());
        connection.Open();
        var columns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using(var schema=connection.CreateCommand())
        {
            schema.CommandText="PRAGMA table_info(threads)";
            using var rows=schema.ExecuteReader();while(rows.Read())columns.Add(rows.GetString(1));
        }
        if(!columns.Contains("id"))throw new IOException("Thread metadata schema unavailable");
        var hasName=columns.Contains("name");var hasSource=columns.Contains("source");
        using var command=connection.CreateCommand();
        // `title`, `preview`, and `first_user_message` may contain full prompt bodies.
        // Select only the explicit UI name and bounded source metadata, including archived/subagents.
        command.CommandText="SELECT id,"+(hasName?"substr(name,1,301)":"NULL")+","+(hasSource?"substr(source,1,512)":"NULL")+" FROM threads LIMIT 50001";
        using var records=command.ExecuteReader();
        while(records.Read())
        {
            ct.ThrowIfCancellationRequested();if(result.Count>=50000)throw new IOException("Thread metadata exceeds row budget");
            if(records.IsDBNull(0))continue;var id=records.GetString(0);if(string.IsNullOrWhiteSpace(id)||id.Length>128)continue;
            var source=records.IsDBNull(2)?null:records.GetString(2);var background=source=="subagent";
            if(source?.StartsWith('{')==true)
            {try{using var parsed=JsonDocument.Parse(source);background=parsed.RootElement.ValueKind==JsonValueKind.Object&&parsed.RootElement.TryGetProperty("subagent",out _);}catch(JsonException){}}
            result[id]=new(hasName,Clean(records.IsDBNull(1)?null:records.GetString(1)),background);
        }
        return result;
    }
}
