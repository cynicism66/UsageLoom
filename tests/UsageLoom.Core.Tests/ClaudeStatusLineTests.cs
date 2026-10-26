using System.Text.Json;
using UsageLoom.Core;

static class ClaudeStatusLineTests
{
    private static void Check(bool condition,string message="Claude status line assertion"){if(!condition)throw new Exception(message);}
    internal static void Run(Action<string,Action> test,string fixtureRoot)
    {
        var now=new DateTimeOffset(2026,9,21,13,0,0,TimeSpan.Zero);
        byte[] Body(object? five=null,object? week=null,long? at=null,int version=1)=>JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string,object?>
        {
            ["version"]=version,["captured_at"]=at??now.ToUnixTimeMilliseconds(),
            ["five_hour"]=five??new{used_percentage=40.5,resets_at=now.AddHours(2).ToUnixTimeSeconds()},
            ["seven_day"]=week??new{used_percentage=12,resets_at=now.AddDays(3).ToUnixTimeSeconds()}
        });
        test("Claude 状态栏快照：百分比、秒级重置时间与来源说明",()=>
        {
            var snapshot=ClaudeStatusLineReader.Parse(Body(),"s",now);
            Check(snapshot.Status=="snapshot"&&snapshot.Source=="statusline"&&snapshot.ObservedAt==now&&snapshot.Scope=="s");
            Check(snapshot.Windows.Count==2&&snapshot.Windows[0].Key=="five_hour"&&snapshot.Windows[0].RemainingText(now)=="59.5%");
            Check(snapshot.Windows[0].ResetsAt==now.AddHours(2)&&snapshot.Windows[1].ResetsAt==now.AddDays(3));
            Check(snapshot.Describe(now)==L10n.T("claude.statusLine.snapshot")&&snapshot.Describe(now.AddMinutes(16))==L10n.T("claude.statusLine.stale"));
            // Observations feed the same quota history and weekly comparison as cache snapshots.
            Check(ClaudeQuotaHistory.Observations(snapshot).Count(p=>p.Origin=="cache"&&p.Scope=="s")==2);
            var noReset=ClaudeStatusLineReader.Parse(Body(new{used_percentage=5,resets_at=(long?)null}),"s",now);
            Check(noReset.Windows[0].ResetsAt is null&&noReset.Windows[0].Countdown(now)==L10n.T("claude.noReset"));
        });
        test("Claude 状态栏快照拒绝越界、未来、无窗口与未知版本",()=>
        {
            foreach(var bad in new object[]{new{used_percentage=-1},new{used_percentage=101},new{used_percentage="50"},
                new{used_percentage=10,resets_at=now.AddHours(6).ToUnixTimeSeconds()},new{used_percentage=10,resets_at=now.AddDays(-9).ToUnixTimeSeconds()},
                new{used_percentage=10,resets_at="soon"}})
            {try{ClaudeStatusLineReader.Parse(Body(bad),"s",now);throw new Exception("accepted invalid window");}catch(InvalidDataException){}}
            try{ClaudeStatusLineReader.Parse(Body(at:now.AddMinutes(5).ToUnixTimeMilliseconds()),"s",now);throw new Exception("future snapshot accepted");}catch(InvalidDataException){}
            try{ClaudeStatusLineReader.Parse("{\"version\":1,\"captured_at\":1}"u8.ToArray(),"s",DateTimeOffset.FromUnixTimeMilliseconds(1));throw new Exception("empty snapshot accepted");}catch(InvalidDataException){}
            Check(ClaudeStatusLineReader.Parse(Body(version:2),"s",now).Status=="statusLine.invalid");
            Check(ClaudeStatusLineReader.Parse(Body(at:now.AddDays(-32).ToUnixTimeMilliseconds()),"s",now).Status=="statusLine.missing");
        });
        test("Claude 桌面缓存与状态栏整份择新，不混合窗口",()=>
        {
            var line=ClaudeStatusLineReader.Parse(Body(),"line",now);
            var desktop=new ClaudeQuotaSnapshot("snapshot",[new("five_hour",80,now.AddHours(1))],now.AddMinutes(-30),"desk",["desk"]);
            var chosen=ClaudeStatusLineReader.Choose(desktop,line);
            Check(chosen.Source=="statusline"&&chosen.Scope=="line"&&chosen.Windows.Count==2&&chosen.Windows[0].Used==40.5);
            Check(chosen.Scopes is ["desk"],"desktop scope list must stay available for settings");
            var newerDesktop=desktop with{ObservedAt=now.AddMinutes(1)};
            Check(ReferenceEquals(ClaudeStatusLineReader.Choose(newerDesktop,line),newerDesktop));
            Check(ClaudeStatusLineReader.Choose(desktop with{ObservedAt=now},line).Source=="desktop","equal time keeps the desktop cache");
            var missingLine=ClaudeQuotaSnapshot.Empty("statusLine.missing");
            Check(ReferenceEquals(ClaudeStatusLineReader.Choose(desktop,missingLine),desktop));
            var notFound=ClaudeQuotaSnapshot.Empty("notFound");
            Check(ClaudeStatusLineReader.Choose(notFound,line).Source=="statusline");
            Check(ReferenceEquals(ClaudeStatusLineReader.Choose(notFound,ClaudeQuotaSnapshot.Empty("statusLine.invalid")),notFound));
        });
        test("Claude 状态栏快照文件读取：缺失、损坏、超限与目录作用域",()=>
        {
            var home=Path.Combine(fixtureRoot,"claude-statusline");Directory.CreateDirectory(home);
            var file=Path.Combine(home,ClaudeStatusLineReader.FileName);
            var current=DateTimeOffset.UtcNow;
            Check(ClaudeStatusLineReader.Read(home,current).Status=="statusLine.missing");
            Check(ClaudeStatusLineReader.Read("relative",current).Status=="statusLine.missing");
            File.WriteAllBytes(file,JsonSerializer.SerializeToUtf8Bytes(new{version=1,captured_at=current.ToUnixTimeMilliseconds(),
                five_hour=new{used_percentage=30,resets_at=current.AddHours(1).ToUnixTimeSeconds()}}));
            var read=ClaudeStatusLineReader.Read(home,current);
            Check(read.Status=="snapshot"&&read.Scope==ClaudeQuotaParser.ScopeKey(home,"claude-code-statusline")&&read.Windows.Count==1);
            // The relay may replace the file while UsageLoom holds it open.
            using(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                Check(ClaudeStatusLineReader.Read(home,current).Status=="snapshot");
            File.WriteAllText(file,"{\"version\":1,");
            Check(ClaudeStatusLineReader.Read(home,current).Status=="statusLine.invalid");
            File.WriteAllText(file,new string(' ',20000));
            Check(ClaudeStatusLineReader.Read(home,current).Status=="statusLine.invalid");
            File.Delete(file);
        });
    }
}
