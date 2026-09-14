using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageLoom.Core;
using UsageLoom.Storage;

internal static class SessionTitleTests
{
    public static void Register(Action<string,Action> test)
    {
        void Case(string name,Action<string> action)=>test(name,()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"UsageLoom-title-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{action(root);}finally{Directory.Delete(root,true);}
        });
        Case("标题索引离线读取、重复改名按时间取最新、坏行不阻断",root=>
        {
            Index(root,Row("one","旧名称","2026-09-01"),"{broken",Row("one","新名称","2026-09-03"),Row("one","乱序旧名称","2026-09-02"),Row("two","另一个"),"null");
            var names=new SessionTitleCatalog().Read(root);
            Check(names.Count==2&&names["one"]=="新名称"&&names["two"]=="另一个");
        });
        Case("当前数据库只使用显式 name，不读取正文型 title 和 preview",root=>
        {
            Index(root,Row("one","过期名称"));
            Database(root,"state_5.sqlite",true);
            Insert(root,"state_5.sqlite","one","当前名称","vscode");
            Insert(root,"state_5.sqlite","two",null,"cli");
            var names=new SessionTitleCatalog().Read(root);
            Check(names.Count==1&&names["one"]=="当前名称"&&!names.Values.Contains("PRIVATE PROMPT"));
        });
        Case("后台子会话明确标识，命名子会话仍显示实际名称",root=>
        {
            Database(root,"state_5.sqlite",true);
            Insert(root,"state_5.sqlite","helper-a",null,"{\"subagent\":{\"other\":\"guardian\"}}");
            Insert(root,"state_5.sqlite","helper-b","已命名辅助任务","subagent");
            var names=new SessionTitleCatalog().Read(root);
            Check(names["helper-a"].Contains("helper-a")&&names["helper-a"].StartsWith(L10n.T("session.background"))&&names["helper-b"]=="已命名辅助任务");
        });
        Case("标题选择最新数据库版本，不把旧数据库名称合并回来",root=>
        {
            Database(root,"state_5.sqlite",true);Database(root,"state_10.sqlite",true);
            Insert(root,"state_5.sqlite","old","旧库名称","cli");Insert(root,"state_10.sqlite","new","新库名称","cli");
            var names=new SessionTitleCatalog().Read(root);Check(names.Count==1&&names.ContainsKey("new"));
        });
        Case("旧数据库缺少 name 字段时使用标题索引，禁止 title 正文回退",root=>
        {
            Index(root,Row("one","索引名称"));Database(root,"state_5.sqlite",false);
            var names=new SessionTitleCatalog().Read(root);Check(names["one"]=="索引名称");
        });
        Case("改名和清空名称无需 Token 变化或重建用量库",root=>
        {
            Database(root,"state_5.sqlite",true);Index(root,Row("one","旧索引名"));Insert(root,"state_5.sqlite","one","第一次","cli");
            var catalog=new SessionTitleCatalog();Check(catalog.Read(root)["one"]=="第一次");
            Insert(root,"state_5.sqlite","one","重命名","cli");Check(catalog.Read(root)["one"]=="重命名");
            Insert(root,"state_5.sqlite","one",null,"cli");Check(!catalog.Read(root).ContainsKey("one"));
        });
        Case("损坏数据库保留同源标题缓存，切换日志目录不泄露旧标题",root=>
        {
            Database(root,"state_5.sqlite",true);Insert(root,"state_5.sqlite","one","缓存名称","cli");
            var catalog=new SessionTitleCatalog();Check(catalog.Read(root)["one"]=="缓存名称");
            File.WriteAllText(Path.Combine(root,"state_5.sqlite"),"not sqlite");
            Check(catalog.Read(root)["one"]=="缓存名称"&&catalog.UnavailableSources==1);
            var other=Path.Combine(root,"other");Directory.CreateDirectory(other);Index(other,Row("two","另一个目录"));
            var names=catalog.Read(other);Check(names.Count==1&&names.ContainsKey("two"));
        });
        Case("只读标题读取不创建数据库，不修改源文件，包含归档和超出旧分页范围记录",root=>
        {
            var missing=Path.Combine(root,"missing");Check(new SessionTitleCatalog().Read(missing).Count==0&&!Directory.Exists(missing));
            Database(root,"state_5.sqlite",true);
            using(var connection=Open(root,"state_5.sqlite"))
            {using var command=connection.CreateCommand();command.CommandText="WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<601) INSERT INTO threads(id,name,source,archived) SELECT 'id-'||x,'名称-'||x,'vscode',1 FROM n";command.ExecuteNonQuery();}
            var path=Path.Combine(root,"state_5.sqlite");var before=File.ReadAllBytes(path);
            Check(new SessionTitleCatalog().Read(root).Count==601&&before.SequenceEqual(File.ReadAllBytes(path)));
        });
        Case("标题限长与控制字符清理，未知会话保留可区分短 ID",root=>
        {
            Index(root,Row("long",new string('字',400)),Row("controls","前\n后\u202e"));var names=new SessionTitleCatalog().Read(root);
            Check(names["long"].Length==301&&!names["controls"].Contains('\n')&&!names["controls"].Contains('\u202e'));
            var id="01234567-1234-1234-1234-123456789abc";Check(HistoryQuery.SessionName(id,names).Contains("01234567"));
        });
        Case("只读标题包含活动 WAL 中的最新名称",root=>
        {
            Database(root,"state_5.sqlite",true);
            using var writer=Open(root,"state_5.sqlite");using var command=writer.CreateCommand();
            command.CommandText="PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO threads(id,name,source) VALUES('wal','活动名称','vscode')";command.ExecuteNonQuery();
            Check(File.Exists(Path.Combine(root,"state_5.sqlite-wal"))&&new SessionTitleCatalog().Read(root)["wal"]=="活动名称");
        });
        Case("标题读取响应取消，不把部分结果当完整快照",root=>
        {
            using var cancel=new CancellationTokenSource();cancel.Cancel();var canceled=false;
            try{new SessionTitleCatalog().Read(root,cancel.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled);
        });
    }
    private static void Check(bool condition){if(!condition)throw new InvalidOperationException("Title metadata assertion failed");}
    private static string Row(string id,string? name,string at="2026-09-01")=>JsonSerializer.Serialize(new{id,thread_name=name,updated_at=at});
    private static void Index(string root,params string[] rows)=>File.WriteAllLines(Path.Combine(root,"session_index.jsonl"),rows);
    private static SqliteConnection Open(string root,string filename)
    {var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,filename),Pooling=false}.ToString());connection.Open();return connection;}
    private static void Database(string root,string filename,bool name)
    {
        using var connection=Open(root,filename);using var command=connection.CreateCommand();
        command.CommandText="CREATE TABLE threads(id TEXT PRIMARY KEY,"+(name?"name TEXT,":"")+"source TEXT,title TEXT DEFAULT 'PRIVATE PROMPT',preview TEXT DEFAULT 'PRIVATE PREVIEW',archived INTEGER DEFAULT 0)";command.ExecuteNonQuery();
    }
    private static void Insert(string root,string file,string id,string? name,string source)
    {
        using var connection=Open(root,file);using var command=connection.CreateCommand();
        command.CommandText="INSERT OR REPLACE INTO threads(id,name,source) VALUES($id,$name,$source)";
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$name",(object?)name??DBNull.Value);command.Parameters.AddWithValue("$source",source);command.ExecuteNonQuery();
    }
}
