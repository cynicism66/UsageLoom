using UsageLoom.Core;

static class QuotaPaceTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Quota pace assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var now=new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero);
        test("额度节奏：20 个百分点为正常，超过 20 才偏快",() =>
        {
            Check(QuotaPace.Evaluate(70,300,now.AddMinutes(150),true,now)==QuotaPaceKind.Normal);
            Check(QuotaPace.Evaluate(70.01,300,now.AddMinutes(150),true,now)==QuotaPaceKind.Fast);
            Check(QuotaPace.Evaluate(100,300,now.AddMinutes(150),true,now)==QuotaPaceKind.Exhausted);
        });
        test("额度节奏：缺少或过期重置、窗外进度、无效时长均不可用",() =>
        {
            foreach(var reset in new DateTimeOffset?[]{null,now,now.AddMinutes(-1),now.AddMinutes(301)})
                Check(QuotaPace.Evaluate(40,300,reset,true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.Evaluate(40,0,now.AddMinutes(20),true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.Evaluate(0,300,now.AddMinutes(300),true,now)==QuotaPaceKind.Normal);
            Check(QuotaPace.Evaluate(100,300,now.AddTicks(1),true,now)==QuotaPaceKind.Exhausted);
        });
        test("额度节奏：Codex 必须新鲜且可确认账号、数据源开启",() =>
        {
            var window=new QuotaWindow("codex:primary","5h",70,300,now.AddMinutes(150));
            var quota=new QuotaState([window],null,now,"ok",true,"account");
            Check(QuotaPace.ForCodex(quota,window,true,now)==QuotaPaceKind.Normal);
            Check(QuotaPace.ForCodex(quota with{Fresh=false},window,true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForCodex(quota with{AccountKey=null},window,true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForCodex(quota,window,false,now)==QuotaPaceKind.Unavailable);
        });
        test("额度节奏：Claude 仅新鲜同源已知窗口",() =>
        {
            var window=new ClaudeQuotaWindow("five_hour",70,now.AddMinutes(150));
            var snapshot=new ClaudeQuotaSnapshot("snapshot",[window],now,"scope");
            Check(QuotaPace.ForClaude(snapshot,window,true,now)==QuotaPaceKind.Normal);
            Check(QuotaPace.ForClaude(snapshot with{ObservedAt=now.AddMinutes(-16)},window,true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForClaude(snapshot with{Scope=null},window,true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForClaude(snapshot,window,false,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForClaude(snapshot,window with{Key="unknown"},true,now)==QuotaPaceKind.Unavailable);
            Check(QuotaPace.ForClaude(snapshot,new("seven_day",70,now.AddMinutes(5040)),true,now)==QuotaPaceKind.Normal);
        });
    }
}
