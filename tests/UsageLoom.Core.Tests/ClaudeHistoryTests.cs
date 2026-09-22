using System.Text.Json;
using UsageLoom.Core;
using UsageLoom.Storage;

static class ClaudeHistoryTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Claude history assertion");}
    internal static void Run(Action<string,Action> test,string root)
    {
        var now=new DateTimeOffset(2026,9,22,12,0,0,TimeSpan.Zero);
        ClaudeQuotaObservation P(int minute,double? used=10,string scope="a",string window="five_hour",bool reset=true)=>new(scope,now.AddMinutes(minute),window,used,reset?now.AddHours(1):null,used is null?"conflict":reset?"cache":"history");
        IReadOnlyList<ClaudeQuotaStep> S(params ClaudeQuotaObservation[] points)=>ClaudeQuotaHistory.Series(points,"a","five_hour",now.AddDays(-1),now.AddDays(1));
        test("Claude 历史：去重、来源和窗口隔离、冲突保守处理",()=>
        {
            Check(ClaudeQuotaHistory.Normalize([P(0),P(0)]).Count==1);
            Check(ClaudeQuotaHistory.Normalize([P(0),P(0,11)])[0].Used is null);
            Check(ClaudeQuotaHistory.Normalize([P(0),P(0) with{ResetsAt=now.AddHours(2)}])[0].Used is null);
            var merged=ClaudeQuotaHistory.Normalize([P(0,reset:false),P(0)]).Single();Check(merged.ResetsAt is not null&&merged.Origin=="cache");
            Check(S(P(0),P(1,20,scope:"b"),P(1,20,window:"seven_day"),P(2,11)).Count==2);
            Check(ClaudeQuotaHistory.Normalize([P(0,-1),P(0,double.NaN),P(0,101),P(0) with{Window="other"}]).Count==0);
            Check(ClaudeQuotaHistory.Normalize([P(0,null),P(0)]).Single().Used is null);
        });
        test("Claude 历史：乱序与同周期增长，缺口重置回退不连线",()=>
        {
            var steps=S(P(10,15),P(0,10),P(5,12));Check(steps.Count(s=>s.Connected)==2&&steps.Sum(s=>s.Increase??0)==5);
            Check(!S(P(0),P(16,12))[1].Connected);
            Check(S(P(0),P(15,12))[1].Connected);
            Check(!S(P(0),P(5,8))[1].Connected);
            Check(!S(P(0),P(5,12) with{ResetsAt=now.AddHours(2)})[1].Connected);
            Check(!S(P(60),P(65,12))[1].Connected);
            Check(!S(P(0,reset:false),P(5,12,reset:false))[1].Connected);
            Check(!S(P(0),P(5,null),P(10,12))[2].Connected);
            Check(ClaudeQuotaHistory.Series([P(0),P(5,12)],"a","five_hour",now.AddMinutes(1),now.AddMinutes(10)).Single().Increase is null);
        });
        test("Claude 历史：历史文件保留所有时间点且不借用重置时间",()=>
        {
            var org="11111111-1111-1111-1111-111111111111";
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=2,samples=new[]{new{org,t=now.AddMinutes(-5).ToUnixTimeMilliseconds(),u=new{fh=10,sd=20}},new{org,t=now.ToUnixTimeMilliseconds(),u=new{fh=15,sd=22}}}});
            var snapshot=ClaudeQuotaParser.History(bytes,root,null,now);
            Check(snapshot.History.Count==4&&snapshot.History.All(p=>p.ResetsAt is null));
            Check(snapshot.Windows[0].Used==15);
        });
        test("Claude 历史：持久化、重启去重、来源隔离、保留期与取消",()=>
        {
            var path=Path.Combine(root,"claude-history-store");var store=new ClaudeHistoryStore(path);
            var snapshot=new ClaudeQuotaSnapshot("snapshot",[new("five_hour",12,now.AddHours(1))],now,"a"){History=[P(-5),P(-50000)]};
            var initial=store.Merge(snapshot,now);Check(initial.Count==2);
            var reopened=new ClaudeHistoryStore(path);Check(reopened.Merge(snapshot,now).Count==2);
            var conflict=snapshot with{Windows=[new("five_hour",13,now.AddHours(1))]};Check(reopened.Merge(conflict,now).Last().Used is null);
            Check(reopened.Merge(snapshot with{Scope="b",History=[]},now).Count==1);
            Check(reopened.Merge(snapshot,now).Count==2);
            Check(reopened.Merge(ClaudeQuotaSnapshot.Empty("readFailed"),now).Count==0);
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{reopened.Merge(snapshot,now,cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
            Check(reopened.Merge(snapshot,now.AddDays(32)).Count==0);
        });
        test("Claude 历史：损坏存储不覆盖，不创建 Codex 数据库",()=>
        {
            var path=Path.Combine(root,"claude-history-broken");Directory.CreateDirectory(path);
            var file=Path.Combine(path,"claude-quota-history.db");var bytes="broken history fixture"u8.ToArray();File.WriteAllBytes(file,bytes);
            var snapshot=new ClaudeQuotaSnapshot("snapshot",[new("five_hour",12,null)],now,"a");
            try{new ClaudeHistoryStore(path).Merge(snapshot,now);throw new Exception("Corruption ignored");}catch(Microsoft.Data.Sqlite.SqliteException){}
            Check(File.ReadAllBytes(file).SequenceEqual(bytes));Check(Directory.GetFiles(path).Length==1);
        });
        test("Claude 历史：四万条上限保留最新记录，重复导入不膨胀",()=>
        {
            var path=Path.Combine(root,"claude-history-limit");var store=new ClaudeHistoryStore(path);
            var snapshot=new ClaudeQuotaSnapshot("snapshot",[],now,"a"){
                History=Enumerable.Range(0,40005).Select(i=>new ClaudeQuotaObservation("a",now.AddMinutes(-i),"five_hour",i%100,null,"history")).ToArray()};
            var rows=store.Merge(snapshot,now);Check(rows.Count==40000&&rows[0].At==now.AddMinutes(-39999)&&rows[^1].At==now);
            Check(new ClaudeHistoryStore(path).Merge(snapshot,now).SequenceEqual(rows));
        });
    }
}
