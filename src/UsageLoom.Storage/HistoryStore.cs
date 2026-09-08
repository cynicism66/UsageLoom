using Microsoft.Data.Sqlite;
using System.Text.Json;
using UsageLoom.Core;

namespace UsageLoom.Storage;

public sealed partial class HistoryStore(string dataDirectory)
{
    private readonly object writerGate = new();
    public CapacityCache? ReadCapacity()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT value FROM metadata WHERE key='weekly_capacity_v1'";
        var json=command.ExecuteScalar() as string;
        var cache=json is null?null:JsonSerializer.Deserialize<CapacityCache>(json);
        if(cache is not null)ArchiveCapacity(connection,null,cache);
        return cache;
    }
    public List<CapacityCache> ReadCapacityHistory(int page=0,int pageSize=20)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT payload FROM (SELECT id,saved_at,payload FROM capacity_history WHERE json_extract(payload,'$.Version')<>3 UNION ALL SELECT id,saved_at,payload FROM temporal_capacity_history) ORDER BY saved_at DESC,id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit",Math.Clamp(pageSize,1,100));
        command.Parameters.AddWithValue("$offset",checked(Math.Max(0,page)*Math.Clamp(pageSize,1,100)));
        using var rows=command.ExecuteReader();var result=new List<CapacityCache>();
        while(rows.Read())if(JsonSerializer.Deserialize<CapacityCache>(rows.GetString(0)) is {} value)result.Add(value);
        return result;
    }
    public List<CapacityCache> ReadValidCapacityHistory()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT payload FROM (SELECT id,saved_at,payload FROM capacity_history WHERE json_extract(payload,'$.Version')<>3 UNION ALL SELECT id,saved_at,payload FROM temporal_capacity_history) WHERE json_extract(payload,'$.Windows[0].Percent')>=5 AND json_extract(payload,'$.Windows[0].Samples')>=2 ORDER BY saved_at DESC,id DESC";
        using var rows=command.ExecuteReader();var result=new List<CapacityCache>();var seen=new HashSet<(string,string,string,int,string)>();
        while(rows.Read())
            if(JsonSerializer.Deserialize<CapacityCache>(rows.GetString(0)) is {} cache&&cache.Windows.FirstOrDefault() is {} w&&seen.Add((cache.Account,cache.Plan,cache.PricingVersion,cache.Version,w.Key)))result.Add(cache);
        return result;
    }
    private static void ArchiveCapacity(SqliteConnection connection,SqliteTransaction? transaction,CapacityCache cache)
    {
        if(string.IsNullOrWhiteSpace(cache.Account)||cache.Windows is null)return;
        foreach(var sample in cache.Windows.Where(w=>w.Samples>0&&w.Tokens>0&&double.IsFinite(w.Percent)&&w.Percent>0&&w.Percent<=100&&w.Priced>=0&&w.Priced<=w.Tokens&&w.Cost>=0))
        {
            var snapshot=cache with{Windows=[sample]};
            var identity=JsonSerializer.Serialize(snapshot with{SavedAt=default});
            var id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
            using var insert=connection.CreateCommand();insert.Transaction=transaction;
            insert.CommandText="INSERT OR IGNORE INTO capacity_history(id,saved_at,payload) VALUES($id,$at,$payload)";
            insert.Parameters.AddWithValue("$id",id);insert.Parameters.AddWithValue("$at",cache.SavedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(snapshot));insert.ExecuteNonQuery();
        }
    }
    public void SaveCapacity(CapacityCache? cache)
    {
        lock(writerGate)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            using var transaction=connection.BeginTransaction();command.Transaction=transaction;
            using(var previous=connection.CreateCommand())
            {
                previous.Transaction=transaction;previous.CommandText="SELECT value FROM metadata WHERE key='weekly_capacity_v1'";
                if(previous.ExecuteScalar() is string json&&JsonSerializer.Deserialize<CapacityCache>(json) is {} old)ArchiveCapacity(connection,transaction,old);
            }
            if(cache is not null)ArchiveCapacity(connection,transaction,cache);
            command.CommandText=cache is null?"DELETE FROM metadata WHERE key='weekly_capacity_v1'":"INSERT INTO metadata(key,value) VALUES('weekly_capacity_v1',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            if(cache is not null)command.Parameters.AddWithValue("$value",JsonSerializer.Serialize(cache));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
    private string DatabasePath => Path.Combine(dataDirectory, "usage-v2.sqlite");
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(dataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS capacity_history (id TEXT PRIMARY KEY,saved_at TEXT NOT NULL,payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS temporal_capacity_history (id TEXT PRIMARY KEY,saved_at TEXT NOT NULL,payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events (id TEXT PRIMARY KEY, source TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS scan_indexes (source TEXT PRIMARY KEY, payload TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public void Save(ScanReport report, CancellationToken cancellationToken = default, Action<int>? checkpoint = null, IReadOnlyList<IndexedFile>? indexes = null, IReadOnlySet<string>? replaceSessions = null)
    {
        lock(writerGate)SaveCore(report,cancellationToken,checkpoint,indexes,false,replaceSessions);
    }

    // Called only after explicit rebuild confirmation; SQLite backup includes committed WAL pages.
    public string ReplaceWithBackup(ScanReport report,IReadOnlyList<IndexedFile> indexes,CancellationToken ct=default,Action<int>? checkpoint=null)
    {
        lock(writerGate)
        {
            ct.ThrowIfCancellationRequested();
            if(report.Files==0)throw new InvalidDataException("重建来源为空，旧统计已保留；请先核对日志目录");
            // Indexed-file warnings mean a physical JSONL line was malformed or too
            // large. Semantic scanner warnings (fork/reset recovery) are still useful
            // partial evidence and must not permanently prevent a version migration.
            if(indexes.Any(index=>index.IntegrityWarnings>0))throw new InvalidDataException("原始日志含结构损坏记录，旧统计已保留；请先核对日志");
            var directory=Path.Combine(dataDirectory,"backups");Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,$"usage-before-rebuild-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            using(var source=Open())
            {
                if(ReadEventCount(source)>0&&report.Events.Count==0)
                    throw new InvalidDataException("重建结果意外为空，旧统计已保留；请先核对日志");
                using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
                backup.Open();source.BackupDatabase(backup);
            }
            // A failed or canceled replacement leaves both the current database and its backup intact.
            SaveCore(report,ct,checkpoint,indexes,true,null);
            return path;
        }
    }

    private static long ReadEventCount(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM events";
        return (long)(command.ExecuteScalar()??0L);
    }

    public bool HasEvents()
    {
        if(!File.Exists(DatabasePath))return false;
        using var connection=Open();
        return ReadEventCount(connection)>0;
    }

    private void SaveCore(ScanReport report,CancellationToken cancellationToken,Action<int>? checkpoint,IReadOnlyList<IndexedFile>? indexes,bool replace,IReadOnlySet<string>? replaceSessions)
    {
        foreach (var item in report.Events)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Session) || !item.Tokens.Valid)
                throw new InvalidDataException("拒绝保存身份或 Token 分类无效的记录");
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = new Dictionary<string, UsageEvent>(StringComparer.Ordinal);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT payload FROM events";
            using var rows = read.ExecuteReader();
            while (rows.Read())
            {
                var item = JsonSerializer.Deserialize<UsageEvent>(rows.GetString(0)) ?? throw new InvalidDataException("统计数据库含无效记录");
                existing.Add(item.Id, item);
            }
        }
        if(replace)
        {
            using var clear=connection.CreateCommand();clear.Transaction=transaction;
            clear.CommandText="DELETE FROM events; DELETE FROM scan_indexes; DELETE FROM metadata WHERE key!='weekly_capacity_v1';";
            clear.ExecuteNonQuery();
        }
        else if(replaceSessions is {Count:>0})
        {
            if(report.Events.Any(item=>!replaceSessions.Contains(item.Session)))
                throw new InvalidDataException("增量重算包含范围外 Session，旧统计已保留");
            foreach(var session in replaceSessions)
            {
                var previousTotal=existing.Values.Where(item=>item.Session==session).Sum(item=>item.Tokens.Total);
                var incomingTotal=report.Events.Where(item=>item.Session==session).Sum(item=>item.Tokens.Total);
                if(incomingTotal<previousTotal)
                    throw new InvalidDataException("受影响 Session 的累计总量回退，旧统计已保留，需要确认重建");
            }
            // The caller has replayed every indexed file belonging to these sessions.
            // Replace only that derived slice so a high-water event may safely move
            // between rollout files without blocking unrelated or newly appended data.
            foreach(var previous in existing.Values.Where(item=>replaceSessions.Contains(item.Session)))
            {
                using var remove=connection.CreateCommand();remove.Transaction=transaction;
                remove.CommandText="DELETE FROM events WHERE id=$id";remove.Parameters.AddWithValue("$id",previous.Id);remove.ExecuteNonQuery();
            }
        }
        var incoming = report.Events.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var sources = report.Sources.ToHashSet(StringComparer.Ordinal);
        foreach (var previous in existing.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!replace && replaceSessions is not {Count:>0} && sources.Contains(previous.Source) && !incoming.ContainsKey(previous.Id))
                throw new InvalidDataException("检测到已有来源的历史变化；已保留旧统计，需核对后重建，未自动覆盖。");
        }
        var written = 0;
        foreach (var item in report.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Tokens.Valid) throw new InvalidDataException("拒绝保存无效 Token 分类");
            var stored = item;
            if (existing.TryGetValue(item.Id, out var previous))
            {
                if (!replace && (previous.Session != item.Session || previous.Tokens.Total != item.Tokens.Total))
                    throw new InvalidDataException("已有事件的身份或总量变化，需核对后重建。");
                if(previous.Session==item.Session&&previous.Tokens.Total==item.Tokens.Total)
                    stored = item with { LocalDate = previous.LocalDate, AccountScope = previous.AccountScope, AccountAttribution = previous.AccountAttribution };
            }
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO events(id,source,payload) VALUES($id,$source,$payload) ON CONFLICT(id) DO UPDATE SET source=excluded.source,payload=excluded.payload";
            insert.Parameters.AddWithValue("$id", stored.Id); insert.Parameters.AddWithValue("$source", stored.Source);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(stored)); insert.ExecuteNonQuery();
            checkpoint?.Invoke(++written);
        }
        using var metadata = connection.CreateCommand(); metadata.Transaction = transaction;
        metadata.CommandText = "INSERT INTO metadata(key,value) VALUES('last_scan',$time) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        metadata.Parameters.AddWithValue("$time", report.ScannedAt.ToString("O")); metadata.ExecuteNonQuery();
        foreach(var index in indexes??[])
        {
            using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="INSERT INTO scan_indexes(source,payload) VALUES($source,$payload) ON CONFLICT(source) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$source",index.Path);command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(index));command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public Dictionary<string,IndexedFile> ReadIndexes()
    {
        if(!File.Exists(DatabasePath))return new(StringComparer.OrdinalIgnoreCase);
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT payload FROM scan_indexes";
        using var reader=command.ExecuteReader();var result=new Dictionary<string,IndexedFile>(StringComparer.OrdinalIgnoreCase);
        while(reader.Read()){var index=JsonSerializer.Deserialize<IndexedFile>(reader.GetString(0))??throw new InvalidDataException("索引损坏，需要重建");result.Add(index.Path,index);}
        return result;
    }

    public List<UsageEvent> Read(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath)) return [];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 5 }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM events ORDER BY id";
        using var rows = command.ExecuteReader(); var result = new List<UsageEvent>();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(JsonSerializer.Deserialize<UsageEvent>(rows.GetString(0)) ?? throw new InvalidDataException("统计数据库含无效记录"));
        }
        return result;
    }
}
