using System.Text.Json;
using UsageLoom.Core;
using UsageLoom.Storage;
using System.Diagnostics;

if(args.Length>=2&&args[0]=="--fake-rpc")
{
    var scenario=args[1];var reads=0;var rateReads=0;
    string? line;
    while((line=await Console.In.ReadLineAsync())is not null)
    {
        using var document=JsonDocument.Parse(line);var message=document.RootElement;
        if(!message.TryGetProperty("id",out var identifier))continue;
        var id=identifier.GetInt32();var method=message.GetProperty("method").GetString();
        if(method=="initialize")Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{}}));
        else if(method=="thread/list"&&scenario=="bad-thread-json")Console.WriteLine("{invalid}");
        else if(method=="thread/list")Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{data=new[]{new{id="account-a",sessionId="session-root",name="可读会话标题",preview="不应作为标题"}},nextCursor=(string?)null}}));
        else if(method=="account/login/start"&&scenario.StartsWith("login-"))
        {
            if(message.GetProperty("params").GetProperty("type").GetString()!="chatgpt")throw new Exception("仅允许官方浏览器登录");
            if(scenario=="login-success")Console.WriteLine("{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"synthetic-login\",\"success\":true}}");
            Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{type="chatgpt",loginId="synthetic-login",authUrl=scenario=="login-url"?"https://example.invalid/auth":"https://auth.openai.com/authorize?state=synthetic"}}));
        }
        else if(method=="account/login/cancel"&&scenario.StartsWith("login-"))Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{status="canceled"}}));
        else if(method=="account/logout"&&scenario=="login-logout")Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{}}));
        else if(method=="account/read")
        {
            reads++;
            var refresh=message.GetProperty("params").GetProperty("refreshToken").GetBoolean();
            if(refresh&&scenario!="auth-once")throw new Exception("不允许强制刷新凭据");
            object? account=scenario=="signed-out"?null:scenario.StartsWith("no-identity")?new{type="chatgpt",email=scenario=="no-identity-switch"&&reads>1?"other@example.com":"synthetic@example.com"}:
                new{type="chatgpt",accountId=scenario=="switch"&&reads>1?"account-b":"account-a",planType="test"};
            Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{account}}));
        }
        else if(method=="account/rateLimits/read")
        {
            rateReads++;
            if(scenario=="signed-out")throw new Exception("未登录时不应请求额度");
            if(scenario=="timeout"){await Task.Delay(10000);continue;}
            if(scenario=="malformed"){Console.WriteLine("not json");continue;}
            if(scenario=="network-always"||(scenario=="network-once"&&rateReads==1))
            {Console.WriteLine(JsonSerializer.Serialize(new{id,error=new{code=-32000,message="failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/private/path): dns error: no such host"}}));await Console.Out.FlushAsync();continue;}
            if(scenario=="auth-once"&&rateReads==1)
            {Console.WriteLine(JsonSerializer.Serialize(new{id,error=new{code=401,message="unauthorized: token expired"}}));await Console.Out.FlushAsync();continue;}
            if(scenario=="stderr")await Console.Error.WriteAsync(new string('x',200000));
            if(scenario=="event")Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
            if(scenario is "invalidate" or "no-identity-event")Console.WriteLine("{\"method\":\"account/updated\",\"params\":{\"authMode\":\"chatgpt\"}}");
            if(scenario=="no-identity-empty"){Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{}}));await Console.Out.FlushAsync();continue;}
            Console.WriteLine(JsonSerializer.Serialize(new{id,result=new{rateLimits=new{primary=new{usedPercent=25,windowDurationMins=300,resetsAt=2000000000}},rateLimitResetCredits=new{availableCount=2}}}));
        }
        else throw new Exception("模拟服务器拒绝不在只读白名单中的请求");
        await Console.Out.FlushAsync();
        if(method=="account/login/start"&&scenario=="login-disconnect")return;
    }
    return;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool result, string message = "断言失败") { if (!result) throw new InvalidOperationException(message); }
JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
Test("更新检查频率：从不、首次、间隔边界与旧配置默认值", () =>
{
    var now=DateTimeOffset.UtcNow;
    Check(!UpdateSchedule.IsDue(false,1,null,now));
    Check(!UpdateSchedule.IsDue(false,1,now.AddDays(-10),now));
    foreach(var hours in new[]{1,6,12,24,72,168})
    {
        Check(UpdateSchedule.IsDue(true,hours,null,now));
        Check(!UpdateSchedule.IsDue(true,hours,now.AddHours(-hours).AddSeconds(1),now));
        Check(UpdateSchedule.IsDue(true,hours,now.AddHours(-hours),now));
        Check(!UpdateSchedule.IsDue(true,hours,now.AddHours(1),now));
    }
    Check(UpdateSchedule.NormalizeHours(0)==24 && UpdateSchedule.NormalizeHours(-1)==24 && UpdateSchedule.NormalizeHours(int.MaxValue)==24);
    Check(!UpdateSchedule.IsDue(true,0,now.AddHours(-23),now));
    Check(UpdateSchedule.IsDue(true,0,now.AddHours(-24),now));
});
AsyncTest("更新清单优先、API 备用与双源限流不伪报最新版",async()=>
{
    var json=JsonSerializer.Serialize(new{tag_name="v0.7.0",draft=false,prerelease=false,body="notes",assets=new[]{new{name="UsageLoom-0.7.0-win-x64.zip",size=100,digest="sha256:"+new string('a',64),browser_download_url="https://github.com/cynicism66/UsageLoom/releases/download/v0.7.0/UsageLoom-0.7.0-win-x64.zip"}}});
    foreach(var first in new[]{"ok","missing","invalid","oversized"})
    {
        using var handler=new UpdateFeedHandler((n,request)=>
        {
            Check(request.RequestUri!.AbsoluteUri==(n==1?UpdateFeed.ManifestUrl:UpdateFeed.ApiUrl));
            if(n==1&&first=="missing")return UpdateFeedHandler.Reply(System.Net.HttpStatusCode.NotFound);
            return UpdateFeedHandler.Reply(System.Net.HttpStatusCode.OK,n==1&&first=="invalid"?"{}":n==1&&first=="oversized"?new string('x',1024*1024+1):json);
        });
        using var client=new System.Net.Http.HttpClient(handler);
        var value=await UpdateFeed.ReadAsync(client,"0.6.8",false);
        Check(UpdateRelease.Parse(value,"0.6.8",false)?.Version=="0.7.0"&&handler.Calls==(first=="ok"?1:2));
    }
    using var limited=new UpdateFeedHandler((n,r)=>UpdateFeedHandler.Reply(System.Net.HttpStatusCode.Forbidden));
    using var limitedClient=new System.Net.Http.HttpClient(limited);var rejected=false;
    try{await UpdateFeed.ReadAsync(limitedClient,"0.6.8",false);}catch(System.Net.Http.HttpRequestException){rejected=true;}
    Check(rejected&&limited.Calls==2);
});
Test("更新只接受正式新版本、匹配架构与 GitHub 摘要", () =>
{
    string Release(string tag="v0.7.0", string extension="zip", string? url=null, string? digest=null, bool preview=false, long size=123) => JsonSerializer.Serialize(new {
        tag_name=tag, draft=false, prerelease=preview, body="Release notes",
        assets=new[]{new{name=$"UsageLoom-{tag[1..]}-win-x64.{extension}", browser_download_url=url??$"https://github.com/cynicism66/UsageLoom/releases/download/{tag}/UsageLoom-{tag[1..]}-win-x64.{extension}", digest=digest??("sha256:"+new string('a',64)), size}}
    });
    Check(UpdateRelease.Parse(Release(),"0.6.6",false)?.Version=="0.7.0");
    Check(UpdateRelease.Parse(Release(extension:"msi"),"0.6.6",true)?.Url.EndsWith(".msi")==true);
    Check(UpdateRelease.Parse(Release(),"0.7.0",false) is null);
    Check(UpdateRelease.Parse(Release(),"0.8.0",false) is null);
    Check(UpdateRelease.Parse(Release(preview:true),"0.6.6",false) is null);
    foreach(var bad in new[]{Release(url:"https://example.com/fake.zip"),Release(digest:""),Release(digest:"sha256:abc"),Release(size:0),Release(size:long.MaxValue),Release(tag:"v0.7.0-beta")})
    {
        var rejected=false;
        try{UpdateRelease.Parse(bad,"0.6.6",false);}catch(InvalidDataException){rejected=true;}
        Check(rejected);
    }
});
Test("主额度分组与百分比边界倒计时",()=>
{
    var now=DateTimeOffset.Now;
    var primary=new QuotaWindow("codex:secondary","每周额度",.001,10080,now.AddDays(7));
    var other=primary with{Key="codex_bengalfox:primary"};
    var state=new QuotaState([primary,other],null,now,"ok",true);
    Check(state.PrimaryWindows.Single()==primary&&state.OtherWindows.Single()==other);
    Check(primary.RemainingText==">99.9%"&&primary.ResetCountdown(now).Contains("7 天"));
    Check((primary with{Used=99.999}).RemainingText=="<0.1%");
    Check((primary with{ResetsAt=now}).ResetCountdown(now).Contains("等待刷新"));
});

Test("日志脱敏不保留 Token/邮箱/路径", () =>
{
    var sample = Privacy.Redact("Bearer test-value alice@example.com C:\\Users\\sample\\file.txt");
    Check(!sample.Contains("test-value"), "Token 未脱敏");
    Check(!sample.Contains("alice"), "邮箱未脱敏");
    Check(!sample.Contains("sample"), "路径未脱敏");
    foreach (var path in new[] { @"D:\private project\sample.txt", @"\\server\private\sample.txt", "/home/sample/private", "C:/Users/sample/private" })
        Check(!Privacy.Redact(path).Contains("sample"), "路径变体未脱敏");
    Check(Privacy.Redact(new string('x', 5000)).Length <= 1201);
});
Test("网址与路径分开脱敏且保留安全的网络原因", () =>
{
    var value=Privacy.Redact("request https://chatgpt.com/private/path failed: dns error C:\\Users\\sample\\secret.txt");
    Check(value.Contains("[网络地址已隐藏]")&&value.Contains("dns error")&&value.Contains("[路径已隐藏]")&&!value.Contains("chatgpt.com")&&!value.Contains("sample"));
    Check(!Privacy.Redact("https://example.com/a").Contains("[路径已隐藏]"));
});
Test("RPC 错误安全分类不回显服务地址",() =>
{
    var error=new CodexRpcException("failed to fetch https://chatgpt.com/private: dns error: no such host");
    Check(error.Kind==RpcFailureKind.Dns&&error.Retryable&&error.Message.Contains("DNS")&&!error.Message.Contains("chatgpt.com"));
    Check(RpcFailure.Classify("proxy tunnel connection failed")==RpcFailureKind.Proxy);
    Check(RpcFailure.Classify("certificate verify failed")==RpcFailureKind.Tls);
    Check(RpcFailure.Classify("unauthorized",401)==RpcFailureKind.Authentication);
});
Test("Token 子分类不重复累计", () => Check(new TokenUsage(100, 30, 10, 20, 5).Total == 120));
Test("未知模型不会通过子字符串套价", () => Check(Pricing.Calculate("unknown-gpt-5.4-extra", new(100, 0, 0, 10)).Unpriced == 110));
Test("缺失子分类价格降低覆盖率", () =>
{
    var value = Pricing.Calculate("synthetic", new(100, 0, 20, 10), new(2, .2m, null, 8));
    Check(value.Priced == 90 && value.Unpriced == 20 && value.Cost == .00024m);
});
Test("显式零价格不当作未知", () => Check(Pricing.Calculate("synthetic", new(100, 0, 20, 10), new(2, .2m, 0, 8)).Priced == 110));
Test("非法分类不参与计价", () => Check(Pricing.Calculate("synthetic", new(100, 90, 20, 10), new(2, .2m, 1, 8)).Priced == 0));
Test("重置次数缺失与零不同", () =>
{
    var missing = QuotaParser.Parse(Json("{}"), DateTimeOffset.Now, "test", null);
    var zero = QuotaParser.Parse(Json("{\"rateLimitResetCredits\":{\"availableCount\":0}}"), DateTimeOffset.Now, "test", null);
    Check(missing.ResetCount is null && zero.ResetCount == 0);
});
Test("次数依据 availableCount 而非明细长度", () =>
{
    var value = QuotaParser.Parse(Json("{\"rateLimitResetCredits\":{\"availableCount\":5,\"credits\":[]}}"), DateTimeOffset.Now, "test", null);
    Check(value.ResetCount == 5);
});
Test("非法额度字段安全降级", () =>
{
    foreach (var text in new[] { "null", "[]", "{\"rateLimits\":{\"primary\":{\"usedPercent\":\"invalid\",\"windowDurationMins\":300}}}", "{\"rateLimitResetCredits\":{\"availableCount\":-1}}" })
        Check(QuotaParser.Parse(Json(text), DateTimeOffset.Now, null, null).ResetCount is null);
});
Test("邮箱和套餐不能代替账号身份", () => Check(CodexClient.ReadIdentity(Json("{\"type\":\"chatgpt\",\"email\":\"test@example.com\",\"planType\":\"pro\"}")) is null));
Test("有效额度可计算剩余比例", () =>
{
    var value = QuotaParser.Parse(Json("{\"rateLimits\":{\"primary\":{\"usedPercent\":35,\"windowDurationMins\":300,\"resetsAt\":2000000000}}}"), DateTimeOffset.Now, "test", null);
    Check(value.Fresh && value.Windows.Single().Remaining == 65);
});
Test("周容量只用同窗口成对增量并公开可信度",() =>
{
    var at=DateTimeOffset.Parse("2026-09-14T00:00:00Z");var tracker=new WeeklyCapacityEstimator();
    QuotaState State(double used,DateTimeOffset reset)=>new([new("codex:secondary","每周额度",used,10080,reset)],null,DateTimeOffset.Now,"ok",true,"account");
    Check(tracker.Observe(State(10,at),1_000_000).Count==0);
    var first=tracker.Observe(State(12,at.AddSeconds(30)),1_200_000).Single();
    Check(Math.Abs(first.EstimatedTokens-10_000_000)<1&&first.Samples==1&&first.Confidence=="低");
    Check(tracker.Observe(State(12,at),1_300_000).Single().ObservedTokens==200_000,"额度未变化不应移动基线");
    var second=tracker.Observe(State(16,at),1_600_000).Single();
    Check(Math.Abs(second.EstimatedTokens-10_000_000)<1&&second.Samples==2&&second.Confidence=="中");
    Check(tracker.Observe(State(1,at.AddDays(7)),1_700_000).Count==0,"新周窗口必须重新采样");
});
Test("重置后展示上次有效估算但新样本独立，跨重启套餐账号隔离",() =>
{
    var reset=DateTimeOffset.Now.AddDays(5);var tracker=new WeeklyCapacityEstimator();
    QuotaState State(double used,DateTimeOffset at)=>new QuotaState([new("codex:weekly","每周额度",used,10080,at)],null,DateTimeOffset.Now,"ok",true,"account"){Plan="prolite"};
    tracker.Observe(State(10,reset),1000);tracker.Observe(State(12,reset),1200);tracker.Observe(State(16,reset),1600);
    var saved=tracker.Export()!;Check(saved.Windows.Single().Percent==6);
    tracker.Observe(State(1,reset.AddDays(7)),9000);
    Check(tracker.Current.Count==0&&tracker.Export()!.Windows.Count==0);
    Check(tracker.DisplayCurrent.Single().HistoricalAt is not null&&tracker.DisplayCurrent.Single().EstimatedTokens==10000);
    tracker.Observe(State(3,reset.AddDays(7)),9400);Check(tracker.DisplayCurrent.Single().HistoricalAt is not null);
    tracker.Observe(State(6,reset.AddDays(7)),10000);
    Check(tracker.DisplayCurrent.Single().HistoricalAt is null&&tracker.DisplayCurrent.Single().EstimatedTokens==20000);
    var restarted=new WeeklyCapacityEstimator();restarted.Restore(new(2,"account","prolite",Pricing.CatalogVersion,DateTimeOffset.Now,[]),[saved]);
    restarted.Observe(State(0,reset.AddDays(7)),50000);Check(restarted.DisplayCurrent.Single().HistoricalAt is not null&&restarted.Export()!.Windows.Count==0);
    restarted.Observe(State(0,reset.AddDays(7)) with{Plan="pro"},50000);Check(restarted.DisplayCurrent.Count==0);
    restarted.Observe(State(0,reset.AddDays(7)) with{AccountKey="other"},50000);Check(restarted.DisplayCurrent.Count==0);
    restarted.Restore(saved);restarted.Observe(State(1,reset),60000);Check(restarted.DisplayCurrent.Single().HistoricalAt is not null&&restarted.Current.Count==0);
});
Test("时间配对保留小数平台与端点，确认补归属和迟到日志替换样本",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int minute,double used)=>new(at.AddMinutes(minute),"a","prolite",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,reset)]);
    UsageEvent E(string id,int minute,long tokens,string? scope="a")=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(minute),"2026-09-08",new(tokens),Source:"source"){AccountScope=scope};
    var observations=new[]{O(0,1.25),O(1,1.25),O(3,3.75),O(5,6.25)};
    var events=new[]{E("lower",0,999),E("a",1,100),E("b",3,200,null),E("c",5,300),E("future",6,999)};
    var first=TemporalCapacity.Calculate(observations,events);
    Check(first.Cache!.Windows.Single().Tokens==300&&first.Intervals.Count==2&&first.Intervals[0].Exclusion=="unverified-ownership");
    events[2]=events[2] with{AccountScope="a",AccountAttribution="user-confirmed"};
    var corrected=TemporalCapacity.Calculate(observations,events);
    Check(corrected.Cache!.Version==3&&corrected.Cache.Windows.Single().Tokens==600&&corrected.Cache.Windows.Single().Percent==5);
    Check(corrected.Intervals[0].EventIds.SequenceEqual(new[]{"a","b"}));
    var late=TemporalCapacity.Calculate(observations,events.Append(E("late",2,50)));
    Check(late.Cache!.Windows.Single().Tokens==650&&late.Intervals.Count==2);
    Check(TemporalCapacity.Calculate(observations,events).Cache!.Windows.Single().Tokens==600);
    events[2]=events[2] with{AccountAttribution="restart-inferred"};
    Check(TemporalCapacity.Calculate(observations,events).Cache!.Windows.Single().Tokens==300);
});
Test("时间配对断开账号套餐重置缺价口径和关闭边界",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(1);
    QuotaObservation O(int min,double used)=>new(at.AddMinutes(min),"a","prolite",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,reset)]);
    var row=new UsageEvent("e","s","p","unknown","main",at.AddSeconds(30),"2026-09-08",new(100)){AccountScope="a",AccountAttribution="user-confirmed"};
    foreach(var end in new[]{O(1,20) with{Account="b"},O(1,20) with{Plan="pro"},O(1,20) with{PricingVersion="different"},O(1,0),O(1,20) with{Windows=[new("codex:weekly","weekly",20,10080,reset.AddDays(7))]}})
        Check(TemporalCapacity.Calculate([O(0,10),end],[row]).Intervals.Count==0);
    Check(TemporalCapacity.Calculate([O(0,10),new(at.AddSeconds(10),null,null,Pricing.CatalogVersion,[],true),O(1,20)],[row]).Intervals.Count==0);
    var valid=TemporalCapacity.Calculate([O(0,10),O(1,20)],[row]);
    Check(valid.Cache!.Windows.Single().Tokens==100&&valid.Cache.Windows.Single().Priced==0);
    Check(TemporalCapacity.Calculate([O(0,10),O(1,20)],[row with{Timestamp=null}]).Cache!.Windows.Count==0);
});
Test("稳定估算合并区间、延迟确认、累计加权和迟到重算",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","prolite",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,reset)]);
    UsageEvent E(string id,int m,long tokens)=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(m),"2026-09-08",new(tokens)){AccountScope="a"};
    var observations=new[]{O(0,0),O(1,1),O(3,3),O(6,8),O(9,11),O(12,11)};
    var events=new[]{E("a",1,300),E("b",5,1000),E("c",8,900)};
    foreach(var used in new[]{1d,2d,3d})
    {
        var pending=TemporalCapacity.CalculateStable([O(0,0),O(1,used)],events,at.AddMinutes(12));
        Check(pending.PendingBaselineUsed["codex:weekly"]==0&&pending.Intervals.Count==0&&pending.Cache!.Windows.Count==0);
    }
    Check(TemporalCapacity.CalculateStable(observations[..3],events,at.AddMinutes(12)).Intervals.Count==0);
    Check(TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(4)).Intervals.Count==0);
    var result=TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(12));var sample=result.Cache!.Windows.Single();
    Check(result.Cache.Version==4&&sample.Percent==11&&sample.Tokens==2200&&sample.Samples==3&&sample.RangeSamples==3);
    Check(result.PendingBaselineUsed["codex:weekly"]==11); // Confirmed points are not counted twice.
    Check(sample.DollarLow<sample.Cost*100m/11&&sample.DollarHigh>sample.Cost*100m/11);
    var updated=TemporalCapacity.CalculateStable(observations,events.Append(E("late",2,100)),at.AddMinutes(12));
    Check(updated.Intervals.Count==3&&updated.Cache!.Windows.Single().Tokens==2300);
    var dip=TemporalCapacity.CalculateStable([O(0,0),O(1,2),O(2,1),O(5,4),O(8,4)],[E("old",1,999),E("new",4,100)],at.AddMinutes(8));
    Check(dip.Cache!.Windows.Count==0); // A transient rollback is quarantined, not a lower sampling baseline.
    Check(dip.PendingBaselineUsed["codex:weekly"]==4);
    foreach(var barrier in new[]{O(4,3) with{Account="b"},O(4,3) with{Plan="pro"},O(4,3) with{Barrier=true},O(4,3) with{Windows=[]}})
        Check(TemporalCapacity.CalculateStable([O(0,0),O(3,3),barrier],events,at.AddMinutes(12)).Intervals.Count==0);
});
Test("周期短暂切换恢复已确认样本，不跨异常区间累计",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used,bool other=false)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,other?reset.AddDays(-2):reset)]);
    UsageEvent E(string id,int m,long tokens)=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(m),"2026-09-10",new(tokens)){AccountScope="a"};
    var observations=new[]{O(0,0),O(3,3),O(6,4),O(9,11,true),O(12,11,true),O(15,4),O(18,6)};
    var events=new[]{E("first",2,300),E("gap",10,999),E("later",17,300)};
    var resumed=TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(25));
    var sample=resumed.Cache!.Windows.Single();
    Check(sample.Percent==3&&sample.Tokens==300&&sample.Samples==1&&sample.LastUsed==4);
    Check(resumed.PendingBaselineUsed["codex:primary"]==4);
    var complete=TemporalCapacity.CalculateStable(observations.Concat([O(21,7),O(24,7)]),events,at.AddMinutes(25));
    Check(complete.Cache!.Windows.Single().Percent==6&&complete.Cache.Windows.Single().Tokens==600);
    Check(!complete.Intervals.SelectMany(i=>i.EventIds).Contains("gap"));
    var safeEvents=new[]{E("first",2,300),E("before",5,100),E("later",17,200)};
    var safe=TemporalCapacity.CalculateStable(observations.Concat([O(21,6)]),safeEvents,at.AddMinutes(25));
    Check(safe.Cache!.Windows.Single().Percent==6&&safe.Cache.Windows.Single().Tokens==600);
    Check(safe.Intervals.SelectMany(i=>i.EventIds).Distinct().Count()==3);
    var inferred=TemporalCapacity.CalculateStable(observations.Concat([O(21,6)]),safeEvents.Select(e=>e.Id=="before"?e with{AccountAttribution="restart-inferred"}:e),at.AddMinutes(25));
    Check(inferred.Cache!.Windows.Single().Percent==3);
    foreach(var barrier in new[]{O(12,11,true) with{Account="b"},O(12,11,true) with{Plan="prolite"},O(12,11,true) with{Barrier=true}})
    {
        var changed=observations.ToArray();changed[4]=barrier;
        Check(TemporalCapacity.CalculateStable(changed,events,at.AddMinutes(25)).Cache!.Windows.Count==0);
    }
    var rollback=observations.ToArray();rollback[5]=O(15,2);rollback[6]=O(18,2);
    Check(TemporalCapacity.CalculateStable(rollback,events,at.AddMinutes(25)).Cache!.Windows.Count==0);
});
Test("查询超时恢复已确认样本，保留硬边界与历史证据",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,reset)]);
    UsageEvent E(string id,int m)=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(m),"2026-09-10",new(300)){AccountScope="a"};
    var failure=O(7,4) with{Barrier=true,Windows=[],BarrierReason="query-failure"};
    var observations=new[]{O(0,0),O(3,3),O(6,4),failure,failure with{At=at.AddMinutes(8)},O(9,4),O(12,7),O(15,7)};
    var events=new[]{E("first",2),E("gap",8),E("second",11)};
    var result=TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(20));
    Check(result.Cache!.Windows.Single().Percent==7&&result.Cache.Windows.Single().Samples==2&&result.Cache.Windows.Single().Tokens==900);
    Check(result.Intervals.SelectMany(i=>i.EventIds).Count(id=>id=="gap")==1);
    var unverified=TemporalCapacity.CalculateStable(observations,events.Select(e=>e.Id=="gap"?e with{AccountScope=null}:e),at.AddMinutes(20));
    Check(unverified.Cache!.Windows.Single().Percent==6&&unverified.Cache.Windows.Single().Tokens==600);
    Check(unverified.Interruptions.Any(i=>i.Reason=="unverified-ownership"));
    var missingIdentity=observations.Select(o=>o.Barrier?o with{Account=null,Plan=null}:o).ToArray();
    var recovered=TemporalCapacity.CalculateStable(missingIdentity,events,at.AddMinutes(20));
    Check(recovered.Cache!.Windows.Single().Percent==7&&recovered.Cache.Windows.Single().Tokens==900);
    Check(missingIdentity.Where(o=>o.Barrier).All(o=>o.Account is null&&o.Plan is null));
    var otherAccount=missingIdentity.Select(o=>o.At>=at.AddMinutes(9)?o with{Account="b"}:o);
    Check(TemporalCapacity.CalculateStable(otherAccount,events,at.AddMinutes(20)).Cache!.Windows.Count==0);
    var longOutage=new[]{O(0,0),O(3,3),O(6,4),failure with{Account=null,Plan=null},O(90,4),O(93,7),O(96,7)};
    var retained=TemporalCapacity.CalculateStable(longOutage,new[]{E("first",2),E("after",92)},at.AddMinutes(100));
    Check(retained.Cache!.Windows.Single().Percent==6&&retained.Cache.Windows.Single().Tokens==600);
    Check(retained.Interruptions.Any(i=>i.Reason=="continuity-not-proven"));
    var confirmedAgain=TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(20));
    Check(confirmedAgain.Interruptions.Count==0&&confirmedAgain.Cache!.Windows.Single().Percent==7);
    var uncertain=TemporalCapacity.CalculateStable(observations,events,at.AddMinutes(20),uncertainRanges:[new(at.AddMinutes(7),at.AddMinutes(8))]);
    Check(uncertain.Cache!.Windows.Single().Percent==6);
    var idleObservations=new[]{O(0,0),O(3,3),O(6,4),failure,O(15,4),O(18,7),O(21,7)};
    var idleEvents=new[]{E("first",2),E("before-failure",6),E("second",17)};
    var idle=TemporalCapacity.CalculateStable(idleObservations,idleEvents,at.AddMinutes(25));
    Check(idle.Cache!.Windows.Single().Percent==7&&idle.Cache.Windows.Single().Tokens==900);
    var active=TemporalCapacity.CalculateStable(idleObservations,idleEvents.Append(E("offline",10)),at.AddMinutes(25));
    Check(active.Cache!.Windows.Single().Percent==6&&active.Cache.Windows.Single().Tokens==600);
    foreach(var hard in new[]{failure with{BarrierReason=null},failure with{BarrierReason="explicit-boundary"},failure with{Account="b"},failure with{Plan="prolite"}})
    {
        var changed=observations.ToArray();changed[3]=hard;
        Check(TemporalCapacity.CalculateStable(changed,events,at.AddMinutes(20)).Cache!.Windows.Single().Percent==3);
    }
    var legacy=failure with{Account=null,Plan=null,BarrierReason=null};
    var lines=new[]{$"{legacy.At.AddMilliseconds(4):O} [WARN] Quota Codex 额度服务响应超时，请稍后重试"};
    Check(LegacyQuotaFailures.Recover([O(6,4),legacy],lines)[1].BarrierReason=="query-failure");
    Check(LegacyQuotaFailures.Recover([O(6,4),legacy],[])[1].BarrierReason is null);
    Check(LegacyQuotaFailures.Recover([O(6,4),legacy with{BarrierReason="explicit-boundary"}],lines)[1].BarrierReason=="explicit-boundary");
    var disconnect=new[]{$"{legacy.At.AddMilliseconds(4):O} [WARN] Quota app-server 连接已断开"};
    Check(LegacyQuotaFailures.Recover([O(6,4),legacy with{BarrierReason="explicit-boundary"}],disconnect)[1].BarrierReason=="query-failure");
    Check(LegacyQuotaFailures.Recover([O(6,4),legacy with{At=legacy.At.AddSeconds(2),BarrierReason="explicit-boundary"}],disconnect)[1].BarrierReason=="explicit-boundary");
});
Test("启动立即恢复部分有效采样，隔离套餐周期并保留批量门控",() =>
{
    var now=DateTimeOffset.Now;var reset=now.AddDays(6);
    var quota=new QuotaState([new("codex:weekly","weekly",3,10080,reset)],null,now,"ok",true,"a"){Plan="pro"};
    var saved=new CapacityCache(4,"a","pro",Pricing.CatalogVersion,now.AddMinutes(-3),[new("codex:weekly","weekly",reset,3,3,300,1m,300,1,0)]);
    var tracker=new WeeklyCapacityEstimator();tracker.Restore(saved);tracker.InitializeTemporal(quota);
    Check(tracker.Export()!.Windows.Single().Percent==3&&tracker.Export()!.Windows.Single().Samples==1&&tracker.RestoredAt==saved.SavedAt);
    Check(tracker.DescribeProgress(quota,0,true).Contains("3/6"));
    Check(tracker.DescribeProgress(quota with{Windows=[new("codex:weekly","weekly",4,10080,reset)]},0,true).Contains("4/6"));
    foreach(var changed in new[]{quota with{AccountKey="b"},quota with{Plan="prolite"},quota with{Windows=[new("codex:weekly","weekly",0,10080,reset)]},quota with{Windows=[new("codex:weekly","weekly",3,10080,reset.AddDays(7))]}})
    {
        tracker.Restore(saved);tracker.InitializeTemporal(changed);Check(tracker.Export()!.Windows.Count==0&&tracker.RestoredAt is null);
    }
    tracker.Restore(null);tracker.InitializeTemporal(quota);Check(tracker.DescribeProgress(quota,0,true).Contains("0/6"));
});
Test("周估算五分钟批量门控，空闲跳过且手动可立即计算",() =>
{
    var at=DateTimeOffset.UtcNow;var schedule=new CapacityBatchSchedule(at);
    Check(!schedule.TryBegin(at)&&!schedule.TryBegin(at.AddMinutes(4)));
    schedule.MarkDirty();Check(schedule.TryBegin(at.AddMinutes(5))&&!schedule.Dirty);
    Check(!schedule.TryBegin(at.AddMinutes(20)));schedule.MarkDirty();Check(schedule.TryBegin(at.AddMinutes(20)));
    schedule.MarkDirty();Check(!schedule.TryBegin(at.AddMinutes(21)));Check(schedule.TryBegin(at.AddMinutes(21),true));
    Check(!schedule.TryBegin(at.AddMinutes(25)));schedule.MarkDirty();Check(schedule.TryBegin(at.AddMinutes(26)));
});
Test("重启推定用量需审计和前后快照核验，不修改原始归属",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",12,10080,reset)]);
    var e=new UsageEvent("gap","s","p","gpt-5.4","main",at.AddMinutes(2),"2026-09-10",new(100)){AccountScope="a",AccountAttribution="restart-inferred"};
    var proof=new RestartCapacityEvidence("a",at,at.AddMinutes(1),at.AddMinutes(3),["gap"],"restart-inferred");
    var ledger=new[]{O(0),O(3),O(5)};
    Check(RestartCapacityVerification.Verify([e],ledger,[proof],at.AddMinutes(6))[0].AccountAttribution=="restart-verified");
    Check(e.AccountAttribution=="restart-inferred");
    Check(RestartCapacityVerification.Verify([e],ledger,[],at.AddMinutes(6))[0]==e);
    foreach(var bad in new[]{proof with{Account="b"},proof with{Events=["missing"]},proof with{OpenedAt=at.AddHours(1)}})
        Check(RestartCapacityVerification.Verify([e],ledger,[bad],at.AddHours(2))[0]==e);
    Check(RestartCapacityVerification.Verify([e],ledger,[proof],at.AddMinutes(4))[0]==e);
    Check(RestartCapacityVerification.Verify([e],ledger.Append(O(4) with{Barrier=true}),[proof],at.AddMinutes(6))[0]==e);
    Check(RestartCapacityVerification.Verify([e],new[]{O(0),O(3) with{Account="b"},O(5)},[proof],at.AddMinutes(6))[0]==e);
});
Test("重启确认可以迟到并跨软失败，但不能跨身份与周期",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",12,10080,reset)]);
    var e=new UsageEvent("gap","s","p","gpt-5.4","main",at.AddMinutes(2),"",new(100)){AccountScope="a",AccountAttribution="restart-inferred"};
    var proof=new RestartCapacityEvidence("a",at,at.AddMinutes(1),at.AddMinutes(3),["gap"],"restart-inferred");
    var ledger=new[]{O(0),O(3) with{Barrier=true,BarrierReason="query-failure",Windows=[]},O(10),O(11) with{Barrier=true,BarrierReason="query-failure",Account=null,Plan=null,Windows=[]},O(30)};
    Check(RestartCapacityVerification.Verify([e],ledger,[proof],at.AddMinutes(31))[0].AccountAttribution=="restart-verified");
    Check(RestartCapacityVerification.Verify([e],ledger,[proof],at.AddMinutes(20))[0]==e);
    Check(RestartCapacityVerification.Verify([e],ledger.Append(O(15) with{Account="b"}),[proof],at.AddMinutes(31))[0]==e);
});
Test("异常快照保留已确认样本但不跨异常计算",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,reset)]);
    UsageEvent E(string id,int m)=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(m),"",new(100)){AccountScope="a"};
    foreach(var bad in new[]{O(7,1),O(7,4) with{Windows=[]},O(7,double.NaN)})
    {
        var ledger=new[]{O(0,0),O(3,3),O(6,4),bad,O(9,4),O(12,7),O(15,7)};
        var result=TemporalCapacity.CalculateStable(ledger,[E("a",2),E("gap",8),E("b",11)],at.AddMinutes(20));
        Check(result.Cache!.Windows.Single().Percent==6);
        Check(!result.Intervals.SelectMany(i=>i.EventIds).Contains("gap"));
        Check(ledger[3]==bad);
    }
});
Test("短断网增长可核验续接且重算幂等，旧异账号坏记录不阻断",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,reset)]);
    UsageEvent E(string id,int m)=>new(id,"s","p","gpt-5.4","main",at.AddMinutes(m),"",new(100)){AccountScope="a"};
    var ledger=new[]{O(0,0),O(3,3),O(6,4),O(7,4) with{Barrier=true,BarrierReason="query-failure",Windows=[]},O(9,5),O(12,6),O(15,6)};
    var events=new[]{E("first",2),E("gap",8),E("after",11),E("old",-100) with{Timestamp=null,AccountScope="b"}};
    var result=TemporalCapacity.CalculateStable(ledger,events,at.AddMinutes(20));
    Check(result.Cache!.Windows.Single().Percent==6&&result.Cache.Windows.Single().Tokens==300);
    var again=TemporalCapacity.CalculateStable(ledger.Reverse().Concat([ledger[0]]),events.Concat([events[0]]),at.AddMinutes(20));
    Check(System.Text.Json.JsonSerializer.Serialize(result)==System.Text.Json.JsonSerializer.Serialize(again));
    var unknown=TemporalCapacity.CalculateStable(ledger,events.Select(e=>e.Id=="gap"?e with{AccountScope=null}:e),at.AddMinutes(20));
    Check(unknown.Interruptions.Any(i=>i.Reason=="unverified-ownership"));
});
Test("冲突快照与冲突事件不参与估算，UTC 偏移与重复输入一致",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,reset)]);
    var e=new UsageEvent("e","s","p","unknown","main",at.AddMinutes(1),"",new(100)){AccountScope="a"};
    var ledger=new[]{O(0,0),O(3,3),O(6,3)};
    var a=TemporalCapacity.CalculateStable(ledger,[e],at.AddMinutes(8));
    var b=TemporalCapacity.CalculateStable(ledger.Select(o=>o with{At=o.At.ToOffset(TimeSpan.FromHours(8))}),[e],at.AddMinutes(8));
    Check(a.Cache!.Windows.Single()==b.Cache!.Windows.Single());
    var conflict=TemporalCapacity.CalculateStable(ledger,[e,e with{Tokens=new(999)}],at.AddMinutes(8));
    Check(conflict.Cache!.Windows.Count==0&&conflict.Intervals.Single().Exclusion is not null);
    Check(TemporalCapacity.CalculateStable(ledger.Append(O(3,9)),[e],at.AddMinutes(8)).Cache!.Windows.Count==0);
    var schedule=new CapacityBatchSchedule(at);schedule.MarkDirty();Check(schedule.TryBegin(at.AddHours(-1)));
});
Test("重启待核验状态跨读取持久保留，终点不随再次启动扩张",() =>
{
    var home=Path.Combine(Path.GetTempPath(),"UsageLoom-pending-"+Guid.NewGuid().ToString("N"));var store=new HistoryStore(Path.Combine(home,"data"));var at=DateTimeOffset.UtcNow;
    store.SaveRestartCheckpoint("a",home,at,at.AddSeconds(1));
    var first=store.ReadPendingRestart(at.AddMinutes(1))!;
    var second=new HistoryStore(Path.Combine(home,"data")).ReadPendingRestart(at.AddMinutes(5))!;
    Check(first.Since==second.Since&&second.RecoveryOpenedAt==at.AddMinutes(1));
    store.SaveRestartCheckpoint("a",home,at.AddMinutes(10),at.AddMinutes(11));
    Check(store.ReadPendingRestart(at.AddMinutes(12))!.Since==at);
    Check(store.AttributeRestartGap(second,"a",home,second.RecoveryOpenedAt!.Value)==0);
    Check(store.ReadPendingRestart(at.AddMinutes(15)) is null);
});
Test("区间历史和当前缓存一次事务提交",() =>
{
    var directory=Path.Combine(Path.GetTempPath(),"UsageLoom-atomic-"+Guid.NewGuid().ToString("N"));
    var store=new HistoryStore(directory);var at=DateTimeOffset.UtcNow;
    var cache=new CapacityCache(4,"a","pro",Pricing.CatalogVersion,at,[new("weekly","weekly",at.AddDays(7),3,3,100,1,100,1,0)]);
    store.SaveTemporalIntervals([], [cache],4,cache,true);
    Check(store.ReadCapacity()!.Windows.Single().Tokens==100);
    using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(directory,"usage-v2.sqlite")))
    {
        connection.Open();using var command=connection.CreateCommand();
        command.CommandText="CREATE TRIGGER fail_capacity BEFORE INSERT ON metadata WHEN NEW.key='weekly_capacity_v1' BEGIN SELECT RAISE(ABORT,'test write failure'); END";command.ExecuteNonQuery();
        var failed=false;try{store.SaveTemporalIntervals([],[],4,cache with{Account="b"},true);}catch(Microsoft.Data.Sqlite.SqliteException){failed=true;}
        Check(failed&&store.ReadCapacity()!.Account=="a"&&store.ReadCapacityHistory().Any(c=>c.Account=="a"));
        command.CommandText="DROP TRIGGER fail_capacity";command.ExecuteNonQuery();
    }
    store.SaveTemporalIntervals([],[],4,null,true);
    Check(store.ReadCapacity() is null);
});
Test("反复查询失败不永久截断后续快照确认",() =>
{
    var at=DateTimeOffset.UtcNow;var reset=at.AddDays(7);
    QuotaObservation O(int m,double used)=>new(at.AddMinutes(m),"a","pro",Pricing.CatalogVersion,[new("codex:primary","weekly",used,10080,reset)]);
    var rows=new[]{O(0,0),O(1,0) with{Barrier=true,BarrierReason="query-failure",Windows=[]},O(2,0),O(3,0) with{Barrier=true,BarrierReason="query-failure",Windows=[]},O(4,0),O(5,3),O(8,3)};
    var e=new UsageEvent("e","s","p","gpt-5.4","main",at.AddMinutes(4.5),"2026-09-10",new(300)){AccountScope="a"};
    var result=TemporalCapacity.CalculateStable(rows,[e],at.AddMinutes(10));
    Check(result.Interruptions.Count==0&&result.Cache!.Windows.Single().Percent==3);
});
Test("设置采样边界只比较数据来源与登录模式",() =>
{
    var source=new CapacitySource(@"C:\Tools\codex.exe",@"C:\Data\Codex",false,false);
    Check(source.Matches(new(@"c:\tools\codex.exe",@"C:\Data\Codex\",false,false)));
    Check(!source.Matches(source with{Home=@"C:\Data\Other"}));
    Check(!source.Matches(source with{Executable=@"C:\Other\codex.exe"}));
    Check(!source.Matches(source with{Authorized=true}));Check(!source.Matches(source with{ReuseBackend=true}));
    Check(new CapacitySource(null,@"C:\Data\Codex",false,false).Matches(new(" ",@"C:\Data\Codex",false,false)));
    // Saving ordinary settings leaves this source snapshot and estimator intact.
    var now=DateTimeOffset.Now;var reset=now.AddDays(6);
    var quota=new QuotaState([new("codex:primary","weekly",13,10080,reset)],null,now,"ok",true,"a"){Plan="pro"};
    var cache=new CapacityCache(4,"a","pro",Pricing.CatalogVersion,now,[new("codex:primary","weekly",reset,12,12,1200,12m,1200,4,0)]);
    var estimator=new WeeklyCapacityEstimator();estimator.Restore(cache);estimator.InitializeTemporal(quota);
    for(var i=0;i<3;i++){Check(source.Matches(source with{}));Check(estimator.DescribeProgress(quota,0,true).Contains("13/6"));}
});
Test("首次索引就绪立即重算旧采样起点，重启保持 8/6",() =>
{
    var now=DateTimeOffset.Now;var reset=now.AddDays(6);
    var schedule=new CapacityBatchSchedule(now);
    Check(!schedule.TryBegin(now));Check(schedule.TryBegin(now,initialReady:true));
    Check(!schedule.TryBegin(now,initialReady:true));schedule.MarkDirty();Check(!schedule.TryBegin(now.AddMinutes(1)));
    var quota=new QuotaState([new("codex:primary","weekly",8,10080,reset)],null,now,"ok",true,"a"){Plan="pro"};
    var saved=new CapacityCache(4,"a","pro",Pricing.CatalogVersion,now,[new("codex:primary","weekly",reset,7,6,600,1m,600,2,0)]);
    var estimator=new WeeklyCapacityEstimator();estimator.Restore(saved);estimator.InitializeTemporal(quota);
    Check(estimator.DescribeProgress(quota,0,true).Contains("7/6"));
    estimator.ApplyTemporal(quota,saved,0,null,new Dictionary<string,double>{{"codex:primary",6}});
    Check(estimator.DescribeProgress(quota,0,true).Contains("8/6"));
    var roundtrip=System.Text.Json.JsonSerializer.Deserialize<CapacityCache>(System.Text.Json.JsonSerializer.Serialize(estimator.Export()));
    estimator.Restore(roundtrip);estimator.InitializeTemporal(quota);
    Check(estimator.DescribeProgress(quota,0,true).Contains("8/6")&&estimator.Export()!.Windows.Single().Percent==6);
});
Test("后台估算拒绝旧扫描、账号套餐快照和设置世代",() =>
{
    var events=new object();var at=DateTimeOffset.UtcNow;
    var quota=new QuotaState([],null,at,"ok",true,"a") with{Plan="pro"};
    var stamp=new CapacityInputStamp(events,1,2,"a","pro",at);
    var sameValue=stamp with{Windows=[]};
    Check(sameValue.Matches(events,1,2,quota with{FetchedAt=at.AddMinutes(1)},true,false));
    Check(!sameValue.Matches(events,1,2,quota with{FetchedAt=at.AddMinutes(1),Windows=[new("codex:weekly","weekly",4,10080,at.AddDays(7))]},true,false));
    Check(stamp.Matches(events,1,2,quota,true,false));
    Check(!stamp.Matches(new object(),1,2,quota,true,false));
    Check(!stamp.Matches(events,2,2,quota,true,false)&&!stamp.Matches(events,1,3,quota,true,false));
    foreach(var changed in new[]{quota with{AccountKey="b"},quota with{Plan="prolite"},quota with{Fresh=false},quota with{FetchedAt=at.AddSeconds(1)}})
        Check(!stamp.Matches(events,1,2,changed,true,false));
    Check(!stamp.Matches(events,1,2,quota,false,false)&&!stamp.Matches(events,1,2,quota,true,true));
});
Test("已取消批量计算不产出结果",() =>
{
    using var cancellation=new CancellationTokenSource();cancellation.Cancel();
    var canceled=false;try{TemporalCapacity.Calculate([],[],cancellation.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled);
});
Test("额度变化没有本机 Token 增量时不伪造周容量",() =>
{
    var tracker=new WeeklyCapacityEstimator();var reset=DateTimeOffset.Now.AddDays(3);
    tracker.Observe(new([new("weekly","每周额度",20,10080,reset)],null,DateTimeOffset.Now,"ok",true),500);
    Check(tracker.Observe(new([new("weekly","每周额度",21,10080,reset)],null,DateTimeOffset.Now,"ok",true),500).Count==0);
    var paired=tracker.Observe(new([new("weekly","每周额度",21,10080,reset)],null,DateTimeOffset.Now,"ok",true),600).Single();
    Check(Math.Abs(paired.EstimatedTokens-10_000)<1,"等待日志扫描后应配对同一次额度变化");
});
Test("周容量采样状态说明等待原因并排除不明额度组",() =>
{
    var tracker=new WeeklyCapacityEstimator();var now=DateTimeOffset.Now;
    var quota=new QuotaState([new("codex:weekly","每周额度",10,10080,now.AddDays(3)),new("codex_bengalfox:weekly","未知",20,10080,now.AddDays(3))],null,now,"ok",true,"account");
    Check(tracker.DescribeProgress(QuotaState.LocalAccount,0,true).Contains("本地模式"));
    Check(tracker.DescribeProgress(quota with{Fresh=false},0,true).Contains("暂停"));
    Check(tracker.DescribeProgress(quota,0,false).Contains("索引"));
    tracker.Observe(quota,1000);
    Check(tracker.DescribeProgress(quota,1000,true).Contains("采样起点已建立"));
    Check(tracker.DescribeProgress(quota,1200,true).Contains("等待周额度百分比变化"));
    var changed=quota with{Windows=[quota.Windows[0] with{Used=12},quota.Windows[1] with{Used=30}]};
    tracker.Observe(changed,1000);
    Check(tracker.DescribeProgress(changed,1000,true).Contains("等待本机 Token"));
    Check(tracker.Observe(changed,1200).Single().WindowKey=="codex:weekly");
    Check(tracker.DescribeProgress(changed,1200,true).Contains("配对进度"));
});
Test("日趋势补齐24小时并保留未知时间且总量守恒",() =>
{
    var day=new DateOnly(2026,9,7);
    var at=new DateTimeOffset(day.ToDateTime(new TimeOnly(13,25)));
    var rows=new[]{Event("h1",100,20) with{Timestamp=at.ToUniversalTime(),LocalDate="2026-09-07"},Event("h2",200,30) with{Timestamp=at.AddMinutes(10),LocalDate="2026-09-07"},Event("unknown",50,10) with{Timestamp=null,LocalDate="2026-09-07"},Event("outside",999,0) with{LocalDate="2026-09-06"}};
    var buckets=HistoryQuery.HourlyTrend(rows,day);
    Check(buckets.Count==25&&buckets[0].Label=="00:00"&&buckets[23].Label=="23:00");
    Check(buckets[13].Tokens==350&&buckets[13].Requests==2&&buckets[12].Tokens==0);
    Check(buckets[^1].Label=="时间未知"&&buckets[^1].Tokens==60);
    Check(buckets.Sum(item=>item.Tokens)==410&&buckets.Sum(item=>item.Requests)==3);
    Check(buckets.Sum(item=>item.EstimatedCost)==Pricing.Summarize(rows.Take(3)).Cost);
    Check(HistoryQuery.HourlyTrend([],day).Count==24);
});
Test("周美元只换算配对费用增量并明确缺价",() =>
{
    var tracker=new WeeklyCapacityEstimator();var now=DateTimeOffset.Now;
    QuotaState State(double used)=>new([new("codex:weekly","周",used,10080,now.AddDays(3))],null,now,"ok",true,"a");
    tracker.Observe(State(10),1000,new Estimate(100m,1000,0,null));
    var first=tracker.Observe(State(12),1200,new Estimate(104m,1200,0,null)).Single();
    Check(first.EstimatedDollars==200m&&first.PricingCoverage==100);
    var partial=tracker.Observe(State(14),1400,new Estimate(106m,1300,100,"missing")).Single();
    Check(partial.EstimatedDollars==150m&&partial.PricingCoverage==75&&partial.DollarDisplay.Contains("部分估算"));
    tracker.Reset();tracker.Observe(State(10),1000);
    Check(tracker.Observe(State(12),1200).Single().EstimatedDollars is null);
});
Test("Spark 明确名称与已验证ID映射但不覆盖冲突名称",() =>
{
    QuotaWindow Read(string? name)=>QuotaParser.Parse(Json(JsonSerializer.Serialize(new{rateLimitsByLimitId=new{codex_bengalfox=new{limitName=name,secondary=new{usedPercent=12,windowDurationMins=10080}}}})),DateTimeOffset.Now,"a",null).Windows.Single();
    Check(Read("GPT-5.3-Codex-Spark").IsSpark);
    Check(Read(null).IsSpark&&!Read(null).IsPrimary);
    Check(!Read("Different model").IsSpark);
    Check(Read(null).Label=="每周额度");
});
Test("套餐展示保留未知类型且区分本地与过期",() =>
{
    var quota=new QuotaState([],null,DateTimeOffset.Now,"ok",true,"a","prolite");
    Check(quota.PlanDisplay=="套餐：Pro 5X");
    Check((quota with{Plan="pro"}).PlanDisplay=="套餐：Pro 20X");
    Check((quota with{Plan="unknown"}).PlanDisplay=="套餐：unknown（后端标识）");
    Check((quota with{Plan="plus"}).PlanDisplay=="套餐：Plus");
    Check((quota with{Plan=null}).PlanDisplay=="套餐暂不可用");
    Check((quota with{Fresh=false}).PlanDisplay.Contains("待刷新确认"));
    Check((quota with{IsLocalAccount=true}).PlanDisplay=="本地模式 · 无在线套餐");
});
Test("账户指纹变化时重置周容量样本",() =>
{
    var tracker=new WeeklyCapacityEstimator();var reset=DateTimeOffset.Now.AddDays(3);
    QuotaState State(string account,double used)=>new([new("weekly","每周额度",used,10080,reset)],null,DateTimeOffset.Now,"ok",true,account);
    tracker.Observe(State("account-a",10),1_000);
    Check(tracker.Observe(State("account-a",20),2_000).Count==1);
    Check(tracker.Observe(State("account-b",20),2_000).Count==0);
});
Test("会话标题优先于项目目录和内部 ID",() =>
{
    var rows=new[]{Event("a",80,20) with{Session="session-root",Project="new-chat"}};
    var names=new Dictionary<string,string>{{"session-root","确认 ChatGPT 是否自动续费"}};
    var session=HistoryQuery.Sessions(rows,"recent",names).Single();
    Check(session.Name=="确认 ChatGPT 是否自动续费"&&session.Project=="new-chat"&&session.ShortId=="session-root");
    Check(HistoryQuery.Filter(rows,new(Search:"自动续费"),names).Count==1);
});
Test("历史范围区分滚动七天、本周、本月和自定义",() =>
{
    var today=new DateOnly(2026,9,9);
    Check(HistoryQuery.ResolveRange(HistoryRangeKind.Rolling7Days,today)==new HistoryDateRange(new(2026,9,3),today,"近 7 天"));
    Check(HistoryQuery.ResolveRange(HistoryRangeKind.Week,today).From==new DateOnly(2026,9,7));
    Check(HistoryQuery.ResolveRange(HistoryRangeKind.Month,today).From==new DateOnly(2026,9,1));
    Check(!HistoryQuery.ResolveRange(HistoryRangeKind.Custom,today,new(2026,9,9),new(2026,9,8)).IsBounded);
});
Test("后台活跃采样不依赖打开窗口，空闲恢复兜底周期",() =>
{
    var now=DateTimeOffset.Now;
    Check(SamplingSchedule.QuotaPeriod(false,now,now.AddMinutes(-1),30,300)==30);
    Check(SamplingSchedule.QuotaPeriod(false,now,now.AddMinutes(-5),30,300)==300);
    Check(SamplingSchedule.QuotaPeriod(true,now,DateTimeOffset.MinValue,30,300)==30);
    Check(SamplingSchedule.QuotaPeriod(false,now,now.AddMinutes(1),30,300)==300);
    Check(SamplingSchedule.QuotaPeriod(false,now,now,600,300)==300);
});
Test("自定义单日使用所选历史日期的小时趋势",() =>
{
    var day=new DateOnly(2026,9,4);
    var range=HistoryQuery.ResolveRange(HistoryRangeKind.Custom,new(2026,9,8),day,day);
    Check(range.IsSingleDay);
    var rows=new[]{Event("custom-hour",80,20) with{LocalDate="2026-09-04",Timestamp=new DateTimeOffset(day.ToDateTime(new TimeOnly(15,0)))}};
    var buckets=HistoryQuery.HourlyTrend(rows,range.From!.Value);
    Check(buckets.Count==24&&buckets[15].Tokens==100&&buckets.Sum(b=>b.Tokens)==100);
    Check(!new HistoryDateRange(day,day.AddDays(1),"").IsSingleDay);
    Check(!new HistoryDateRange(null,null,"").IsSingleDay);
});
Test("趋势补齐空日期并压缩长区间",() =>
{
    var rows=new[]{Event("a",80,20) with{Session="one",LocalDate="2026-09-01"},Event("b",40,10) with{Session="two",LocalDate="2026-09-03"}};
    var daily=HistoryQuery.Trend(rows,new(new(2026,9,1),new(2026,9,3),"test"));
    Check(daily.Count==3&&daily[0].Tokens==100&&daily[0].Requests==1&&daily[1].Tokens==0&&daily[1].Requests==0&&daily[2].Tokens==50&&daily[2].Requests==1);
    var compressed=HistoryQuery.Trend(rows,new(new(2026,1,1),new(2026,12,31),"test"),12);
    Check(compressed.Count<=12&&compressed.Sum(item=>item.Tokens)==150);
    Check(compressed.Sum(item=>item.Requests)==2);
});
Test("非法 Token 字段不当作零", () =>
{
    foreach(var value in new[]{"{\"input_tokens\":\"wrong\"}","{\"input_tokens\":-1}","{\"input_tokens\":80,\"output_tokens\":20,\"total_tokens\":150}","{\"input_tokens\":9223372036854775807,\"output_tokens\":1}"})
    {
        var rejected=false;try{TokenUsage.Parse(Json(value));}catch(JsonException){rejected=true;}Check(rejected);
    }
});
Test("通知默认关闭且阈值包括等于边界", () =>
{
    var now=DateTimeOffset.Parse("2026-09-06T01:00:00Z");
    var quota=new QuotaState([new("primary","5 小时额度",80,300,now.AddHours(1))],2,now,"test",true,"account-test");
    var policy=new NotificationPolicy();Check(policy.Evaluate(quota,new(),now,TimeSpan.FromMinutes(10)).Count==0);
    Check(policy.Evaluate(quota,new(true),now,TimeSpan.FromMinutes(10)).Count==1);
    Check(policy.Evaluate(quota,new(true,true,99),now,TimeSpan.FromMinutes(10)).Count==0);
});
Test("两类通知独立且重启时间微调不重复", () =>
{
    var now=DateTimeOffset.Parse("2026-09-06T01:00:00Z");
    var quota=new QuotaState([new("primary","5 小时额度",80,300,now.AddMinutes(10))],2,now,"test",true,"account-test");
    var policy=new NotificationPolicy();Check(policy.Evaluate(quota,new(true,true),now,TimeSpan.FromMinutes(10)).Count==2);
    var restored=new NotificationPolicy();restored.Restore(policy.Export(),now);
    var adjusted=quota with{Windows=[quota.Windows[0] with{ResetsAt=now.AddMinutes(10).AddSeconds(30)}]};
    Check(restored.Evaluate(adjusted,new(true,true,90,20),now,TimeSpan.FromMinutes(10)).Count==0);
    Check(!restored.Export().Single().Account.Contains("account-test"));
});
Test("过期身份不明和已过重置时间不提醒", () =>
{
    var now=DateTimeOffset.Parse("2026-09-06T01:00:00Z");
    var quota=new QuotaState([new("primary","5 小时额度",99,300,now.AddMinutes(5))],2,now,"test",true,"account-test");
    foreach(var invalid in new[]{quota with{Fresh=false},quota with{AccountKey=null},quota with{FetchedAt=now.AddHours(-1)},quota with{Windows=[quota.Windows[0] with{ResetsAt=now}]}})
        Check(new NotificationPolicy().Evaluate(invalid,new(true,true),now,TimeSpan.FromMinutes(10)).Count==0);
});
Test("新窗口周期重新允许提醒", () =>
{
    var now=DateTimeOffset.Parse("2026-09-06T01:00:00Z");
    var quota=new QuotaState([new("primary","5 小时额度",90,300,now.AddMinutes(5))],2,now,"test",true,"account-test");
    var policy=new NotificationPolicy();Check(policy.Evaluate(quota,new(true,true),now,TimeSpan.FromMinutes(10)).Count==2);
    var later=now.AddMinutes(6);var updated=quota with{FetchedAt=later,Windows=[quota.Windows[0] with{ResetsAt=later.AddMinutes(5)}]};
    Check(policy.Evaluate(updated,new(true,true),later,TimeSpan.FromMinutes(10)).Count==2);
});

Test("每个模型价格都有官方来源和核对日期",()=>
{
    Check(Pricing.Entries.Count==9);
    foreach(var entry in Pricing.Entries){Check(entry.Source.Host=="developers.openai.com"&&entry.CheckedOn==new DateOnly(2026,9,7));Check(entry.EffectiveFrom is null&&entry.EffectiveUntil is null);}
    Check(Pricing.Find("gpt-5.4-mini-2026-03-17")?.Model=="gpt-5.4-mini");
    Check(Pricing.Find("gpt-5.6")?.Model=="gpt-5.6-sol");
});
Test("Astra 长上下文与 Fast 明确条件倍率",()=>
{
    var usage=new TokenUsage(100,20,10,20);
    var normal=Pricing.Calculate("gpt-6-astra",usage,context:new(RequestInputTokens:272000,FastMode:false,ValuationDate:new(2026,9,6)));
    var longer=Pricing.Calculate("gpt-6-astra",usage,context:new(RequestInputTokens:272001,FastMode:true,ValuationDate:new(2026,9,6)));
    Check(normal.Cost==.001845m&&longer.Cost==.00638m);
    Check(longer.Notes.Any(n=>n.Contains("长上下文"))&&longer.Notes.Any(n=>n.Contains("Fast")));
});
Test("Astra Batch 价格为标准一半",()=>
{
    var context=new PricingContext(RequestInputTokens:100,ServiceTier:"batch",FastMode:false,ValuationDate:new(2026,9,6));
    Check(Pricing.Calculate("gpt-6-astra",new(100,0,0,0),context:context).Cost==.0005m);
});
Test("长上下文条件未知不以累计总量推断",()=>
{
    var value=Pricing.Calculate("gpt-6-astra",new(1000000,0,0,0),context:new(ValuationDate:new(2026,9,6)));
    Check(value.Cost==10m&&value.Notes.Any(n=>n.Contains("未核实长上下文")));
});
Test("未知长上下文缓存倍率明确缺价",()=>
{
    var value=Pricing.Calculate("gpt-5.6-sol",new(100,20,10,20),context:new(RequestInputTokens:300000,ValuationDate:new(2026,9,6)));
    Check(value.Unpriced==30&&value.Priced==90&&value.Status=="部分估算");
});
Test("区域倍率需有明确依据",()=>
{
    var value=Pricing.Calculate("gpt-5.4-mini",new(1000000,0,0,0),context:new(RegionalProcessing:true,ValuationDate:new(2026,9,6)));
    Check(value.Cost==.825m);
});
Test("未知价格和失效促销不显示零美元",()=>
{
    Check(Pricing.Calculate("unlisted",new(100,0,0,0)).DisplayAmount=="暂不可估算");
    var stale=Pricing.Calculate("gpt-5.6-sol",new(100,0,0,0),context:new(ValuationDate:new(2026,11,22)));
    Check(!stale.HasAmount&&stale.Reason!.Contains("促销"));
});

var fixtureRoot = Path.Combine(Path.GetTempPath(), "UsageLoom-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
string Fixture(string name) { var p = Path.Combine(fixtureRoot, name); Directory.CreateDirectory(Path.Combine(p, "sessions")); return p; }
string Meta(string id, string? parent = null) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, forked_from_id = parent, cwd = "sample-project" } });
string Model() => "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.4-mini\"}}";
string Count(long input, long output, long? cached = null, string timestamp = "2026-09-05T01:00:00Z") => JsonSerializer.Serialize(new
{
    type = "event_msg", timestamp,
    payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, output_tokens = output, cached_input_tokens = cached } } }
});
Test("归属确认备份、重复拒绝、重建保留且不覆盖其他账号",() =>
{
    var root=Fixture("confirmed-attribution");var store=new HistoryStore(root);var at=DateTimeOffset.UtcNow;
    var one=new UsageEvent("e1","s","p","unknown","main",at,"2026-09-08",new(100)){Source="source"};
    var other=one with{Id="e2",AccountScope="b"};
    store.Save(new([one,other],1,0,at));
    var backup=store.ConfirmAttribution([one],"a");Check(File.Exists(backup));
    Check(store.Read().Single(e=>e.Id==one.Id).AccountAttribution=="user-confirmed");
    var rejected=false;try{store.ConfirmAttribution([one],"b");}catch(InvalidOperationException){rejected=true;}Check(rejected);
    rejected=false;try{store.ConfirmAttribution([other],"a");}catch(InvalidOperationException){rejected=true;}Check(rejected);
    store.ReplaceWithBackup(new([one,other],1,0,at),[]);
    Check(store.Read().Single(e=>e.Id==one.Id).AccountScope=="a");
    Check(store.Read().Single(e=>e.Id==other.Id).AccountScope=="b");
});
Test("归属预览改变拒绝事务，缺时间不合并",() =>
{
    var store=new HistoryStore(Fixture("confirmation-conflict"));var at=DateTimeOffset.UtcNow;
    var one=new UsageEvent("e","s","p","unknown","main",at,"2026-09-08",new(100));
    store.Save(new([one],1,0,at));
    foreach(var wrong in new[]{one with{Tokens=new(101)},one with{Timestamp=null}})
    {
        var rejected=false;try{store.ConfirmAttribution([wrong],"a");}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&store.Read().Single().AccountScope is null);
    }
});
Test("本地账户指纹稳定隔离且不保存邮箱",() =>
{
    var path=Path.Combine(Fixture("fingerprint"),"account-fingerprint.key");
    var fingerprints=new LocalAccountFingerprint(path);
    var first=fingerprints.Create("email"," Test@Example.com ");
    var same=fingerprints.Create("email","test@example.com");
    var other=fingerprints.Create("email","other@example.com");
    Check(first==same&&first!=other&&first is not null&&!first.Contains("example",StringComparison.OrdinalIgnoreCase));
    Check(File.ReadAllBytes(path).Length==32&&!File.ReadAllText(path).Contains("test@example.com",StringComparison.OrdinalIgnoreCase));
    Check(new LocalAccountFingerprint(path).Create("email","test@example.com")==first);
});
Test("修复工具对象索引兼容保留归属和64位时间，其他损坏仍拒绝",() =>
{
    var json="""{"Path":"test","Version":5,"Offset":0,"Length":0,"Written":639000000000000001,"Created":639000000000000003,"PrefixHash":"","Records":[{"type":"event_msg","account_attribution":"user-confirmed","account_scope":"a","payload":{"type":"token_count"}}],"Warnings":0}""";
    var index=JsonSerializer.Deserialize<IndexedFile>(json)!;
    Check(index.Written==639000000000000001&&index.Created==639000000000000003);
    Check(Json(index.Records.Single()).GetProperty("account_scope").GetString()=="a");
    var normalized=JsonSerializer.Serialize(index);Check(Json(normalized).GetProperty("Records")[0].ValueKind==JsonValueKind.String);
    Check(JsonSerializer.Deserialize<IndexedFile>(normalized)!.Written==index.Written);
    var rejected=false;try{JsonSerializer.Deserialize<IndexedFile>(json.Replace("user-confirmed","unexpected"));}catch(JsonException){rejected=true;}Check(rejected);
});
Test("扫描未完成也可按已核实账号回显历史但不产生新样本",() =>
{
    var at=DateTimeOffset.Now;var tracker=new WeeklyCapacityEstimator();
    var cache=new CapacityCache(2,"a","prolite",Pricing.CatalogVersion,at.AddHours(-1),[new("codex:weekly","weekly",at.AddDays(7),15,13,10000,10m,8000,13,0)]);
    tracker.Restore(cache);
    var quota=new QuotaState([new("codex:weekly","weekly",18,10080,at.AddDays(7))],null,at,"ok",true,"a","prolite");
    tracker.ApplyTemporal(quota,null,0,null);
    Check(tracker.DisplayCurrent.Single().HistoricalAt is not null&&tracker.Current.Count==0);
    tracker.ApplyTemporal(quota with{AccountKey="b"},null,0,null);Check(tracker.DisplayCurrent.Count==0);
});
Test("每天清理过期额度明细并保留完整周期和估算归档",() =>
{
    var store=new HistoryStore(Fixture("retention"));var now=DateTimeOffset.UtcNow;var start=now.AddDays(-50);
    QuotaObservation O(int day,double used)=>new(start.AddDays(day),"a","prolite",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,start.AddDays(day<7?7:day<14?14:day<21?21:day<28?28:56))]);
    foreach(var o in new[]{O(0,0),O(1,3),O(2,6),O(7,0),O(8,3),O(9,6),O(14,0),O(15,3),O(21,0),O(28,0),O(49,4)})store.SaveQuotaObservation(o);
    var archived=new CapacityCache(3,"a","prolite",Pricing.CatalogVersion,start.AddDays(2),[new("codex:weekly","weekly",start.AddDays(7),6,6,100,1,100,2,0)]);
    store.SaveTemporalIntervals([], [archived]);store.SaveCapacity(archived);
    var beforeRecent=store.ReadQuotaObservations().Where(o=>o.At>=start.AddDays(14)).ToList();
    var result=store.CleanupCapacity(now);
    Check(result.Ran&&result.Cutoff==start.AddDays(14)&&result.Observations==6);
    Check(store.ReadQuotaObservations().Select(o=>o.At).SequenceEqual(beforeRecent.Select(o=>o.At)));
    Check(store.ReadCapacityHistory().Single().Windows.Single().Tokens==100);
    store.SaveTemporalIntervals([],[]);Check(store.ReadValidCapacityHistory().Single().Windows.Single().Tokens==100);
    Check(!store.CleanupCapacity(now.AddHours(1)).Ran&&store.CleanupCapacity(now.AddDays(1)).Ran);
    Check(store.ReadCapacity()!.Windows.Single().Tokens==100);
});
Test("时间快照跨重启持久化并保留原始小数与区间替换",() =>
{
    var folder=Fixture("temporal-store");var store=new HistoryStore(folder);var at=DateTimeOffset.UtcNow;
    var o=new QuotaObservation(at,"a","prolite",Pricing.CatalogVersion,[new("codex:weekly","weekly",12.3456789,10080,at.AddDays(7))]);
    store.SaveQuotaObservation(o);store.SaveQuotaObservation(o);
    var loaded=new HistoryStore(folder).ReadQuotaObservations();Check(loaded.Count==1&&loaded[0].Windows.Single().Used==12.3456789&&loaded[0].At==at);
    var end=o with{At=at.AddMinutes(1),Windows=[o.Windows[0] with{Used=15.3456789}]};store.SaveQuotaObservation(end);
    var e=new UsageEvent("e","s","p","unknown","main",at.AddSeconds(20),"2026-09-08",new(100)){AccountScope="a",AccountAttribution="user-confirmed"};
    var result=TemporalCapacity.Calculate(store.ReadQuotaObservations(),[e]);store.SaveTemporalIntervals(result.Intervals,result.History);store.SaveTemporalIntervals(result.Intervals,result.History);
    Check(store.ReadCapacityHistory().Single().Windows.Single().Tokens==100);
    var repaired=TemporalCapacity.Calculate(store.ReadQuotaObservations(),[e with{Tokens=new(200)}]);store.SaveTemporalIntervals(repaired.Intervals,repaired.History);
    Check(store.ReadCapacityHistory().Single().Windows.Single().Tokens==200);
    using var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(folder,"usage-v2.sqlite"));c.Open();using var command=c.CreateCommand();command.CommandText="SELECT count(*) FROM capacity_intervals";
    Check(Convert.ToInt32(command.ExecuteScalar())==1);
});
Test("新算法历史迁移与参考范围持久化不重复",() =>
{
    var store=new HistoryStore(Fixture("stable-capacity"));var at=DateTimeOffset.UtcNow;
    var sample=new CapacitySample("codex:weekly","weekly",at.AddDays(7),10,9,2200,22,2200,3,0){DollarLow=200,DollarHigh=400,RangeSamples=3};
    var legacy=new CapacityCache(3,"a","prolite",Pricing.CatalogVersion,at,[sample]);
    store.SaveTemporalIntervals([],[legacy]);store.SaveTemporalIntervals([],[],4);
    Check(store.ReadCapacityHistory().Single().Version==3);
    var current=legacy with{Version=4};store.SaveTemporalIntervals([],[current],4);store.SaveCapacity(current);
    Check(store.ReadCapacityHistory().Count(c=>c.Version==4)==1);
    Check(store.ReadCapacity()!.Windows.Single().DollarHigh==400);
});
AsyncTest("重复扫描、累计去重与分类修正", async () =>
{
    var home = Fixture("correction");
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "a.jsonl"), [Meta("s1"), Model(), Count(160, 40, 20), Count(160, 40, 30), Count(160, 40, 30)]);
    var scanner = new HistoryScanner();
    var first = await scanner.ScanAsync(home, default); var second = await scanner.ScanAsync(home, default);
    Check(first.Total == new TokenUsage(160, 30, 0, 40) && first.Events.Count == 1 && second.Total == first.Total);
});
AsyncTest("扫描保存可靠的单请求输入用于长上下文计价",async()=>
{
    var home=Fixture("request-pricing-context");
    var count=JsonSerializer.Serialize(new
    {
        type="event_msg",timestamp="2026-09-05T01:00:00Z",
        payload=new{type="token_count",info=new
        {
            total_token_usage=new{input_tokens=300000,cached_input_tokens=200000,output_tokens=1000},
            last_token_usage=new{input_tokens=300000,cached_input_tokens=200000,output_tokens=1000}
        }}
    });
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","a.jsonl"),[Meta("priced-request"),"{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6-astra\"}}",count]);
    var item=(await new HistoryScanner().ScanAsync(home,default)).Events.Single();
    Check(item.Pricing?.RequestInputTokens==300000&&item.Pricing?.ValuationDate==new DateOnly(2026,9,5));
    Check(Pricing.Calculate(item).Cost==2.475m);
});
AsyncTest("跨文件恢复与缺失子分类保持", async () =>
{
    var home = Fixture("resume");
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "a.jsonl"), [Meta("s2"), Model(), Count(80, 20, 10)]);
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "b.jsonl"), [Meta("s2"), Model(), Count(120, 30)]);
    var report = await new HistoryScanner().ScanAsync(home, default);
    Check(report.Total == new TokenUsage(120, 10, 0, 30));
});
AsyncTest("同一任务跨 rollout 的独立累计重置不会漏记", async () =>
{
    var home=Fixture("cross-rollout-reset");
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","a.jsonl"),[Meta("shared-reset"),Model(),Count(80,20)]);
    string ResetCount(long input,long output,long lastInput,long lastOutput,string timestamp)=>JsonSerializer.Serialize(new
    {
        type="event_msg",timestamp,
        payload=new{type="token_count",info=new
        {
            total_token_usage=new{input_tokens=input,output_tokens=output},
            last_token_usage=new{input_tokens=lastInput,output_tokens=lastOutput}
        }}
    });
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","b.jsonl"),
        [Meta("shared-reset"),Model(),ResetCount(8,2,8,2,"2026-09-05T02:00:00Z"),ResetCount(16,4,8,2,"2026-09-05T03:00:00Z")]);
    var report=await new HistoryScanner().ScanAsync(home,default);
    Check(report.Total.Total==120&&report.Events.Count==3&&report.Warnings==1);
});
AsyncTest("无法唯一归属的分类修正保持总量并报告缺口", async () =>
{
    var home = Fixture("ambiguous-correction");
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "a.jsonl"), [Meta("ambiguous"), Model(), Count(80, 20, 10), Count(160, 40, 20), Count(160, 40, 30)]);
    var result = await new HistoryScanner().ScanAsync(home, default);
    Check(result.Total.Total == 200 && result.Total.Cached == 20 && result.Warnings == 1);
});
AsyncTest("跨文件同总量修正修改唯一的既有事件", async () =>
{
    var home = Fixture("cross-file-correction");
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "a.jsonl"), [Meta("cross-file"), Model(), Count(80, 20, 10)]);
    await File.WriteAllLinesAsync(Path.Combine(home, "sessions", "b.jsonl"), [Meta("cross-file"), Model(), Count(80, 20, 15), Count(120, 30)]);
    var result = await new HistoryScanner().ScanAsync(home, default);
    Check(result.Total == new TokenUsage(120, 15, 0, 30));
});
AsyncTest("完整无换行尾行与半行后续补齐", async () =>
{
    var home = Fixture("partial"); var path = Path.Combine(home, "sessions", "a.jsonl");
    var record = Count(80, 20); var prefix = Meta("s3") + "\n" + Model() + "\n";
    await File.WriteAllTextAsync(path, prefix + record[..30]);
    Check((await new HistoryScanner().ScanAsync(home, default)).Total.Total == 0);
    await File.AppendAllTextAsync(path, record[30..]);
    Check((await new HistoryScanner().ScanAsync(home, default)).Total.Total == 100);
});
AsyncTest("fork 父前缀未完成不得记给子任务", async () =>
{
    var home = Fixture("fork"); var file = Path.Combine(home, "sessions", "a.jsonl");
    const string child = "019ffaca-c65e-78a3-8383-70d9d427eaf3";
    await File.WriteAllLinesAsync(file, [Meta(child, "parent"), Meta("parent"), Model(), Count(240, 60)]);
    Check((await new HistoryScanner().ScanAsync(home, default)).Total.Total == 0);
    await File.AppendAllLinesAsync(file, ["{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"019ffaca-cac2-7122-bd5c-e30f9b7c3715\"}}", Model(), Count(280, 70)]);
    Check((await new HistoryScanner().ScanAsync(home, default)).Total.Total == 50);
});
AsyncTest("父会话重复标记不重置累计基线，跨重启追加不重复计费",async()=>
{
    var home=Fixture("repeated-parent");var file=Path.Combine(home,"sessions","a.jsonl");
    var lines=new List<string>{Meta("child","parent"),Model(),Count(100,0),Meta("parent"),Count(110,0)};
    for(var i=1;i<=172;i++){lines.Add(Meta("parent"));lines.Add(Count(110+i*10,0));}
    await File.WriteAllLinesAsync(file,lines);
    var store=new HistoryStore(Path.Combine(home,"data"));
    var first=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(first.Report.Total.Total==1730&&first.Report.Events.All(e=>e.Tokens.Total==10));
    await File.AppendAllLinesAsync(file,[Meta("parent"),Count(1850,0)]);
    await new IncrementalHistory(store).ScanAsync(home,default);
    Check(store.Read().Sum(e=>e.Tokens.Total)==1750);
    var old=store.ReadIndexes().Values.Select(i=>i with{Version=5}).ToList();
    store.Save(new(store.Read(),1,0,DateTimeOffset.Now),indexes:old);
    var migrated=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(migrated.Migrated&&migrated.BackupPath is not null&&File.Exists(migrated.BackupPath));
    Check(store.Read().Sum(e=>e.Tokens.Total)==1750);
});
AsyncTest("现代 fork 已进入子任务后父标记不得退回旧前缀",async()=>
{
    var home=Fixture("modern-repeated-parent");var file=Path.Combine(home,"sessions","a.jsonl");
    const string child="019ffaca-c65e-78a3-8383-70d9d427eaf3";
    await File.WriteAllLinesAsync(file,[Meta(child,"parent"),Model(),Count(100,0),"{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"019ffaca-cac2-7122-bd5c-e30f9b7c3715\"}}",Count(110,0),Meta("parent"),Count(120,0)]);
    Check((await new HistoryScanner().ScanAsync(home,default)).Total.Total==20);
});
AsyncTest("父子连续历史前缀按关系去重且保留分叉后新用量",async()=>
{
    var home=Fixture("inherited-sequence");var child=Path.Combine(home,"sessions","a-child.jsonl");var parent=Path.Combine(home,"sessions","z-parent.jsonl");
    await File.WriteAllLinesAsync(parent,[Meta("parent"),Model(),Count(100,0),Count(200,0),Count(300,0)]);
    await File.WriteAllLinesAsync(child,[Meta("child","parent"),Model(),Count(100,0,timestamp:"2026-09-06T01:00:00Z"),Meta("parent"),Count(200,0,timestamp:"2026-09-06T01:00:00Z"),Count(300,0,timestamp:"2026-09-06T01:00:00Z"),Count(450,0,timestamp:"2026-09-06T01:01:00Z")]);
    var store=new HistoryStore(Path.Combine(home,"data"));await new IncrementalHistory(store).ScanAsync(home,default);
    Check(store.Read().Where(e=>e.Session=="child").Sum(e=>e.Tokens.Total)==150);
    Check(store.Read().Sum(e=>e.Tokens.Total)==450);
    await File.AppendAllLinesAsync(child,[Count(500,0,timestamp:"2026-09-06T01:02:00Z")]);
    await new IncrementalHistory(store).ScanAsync(home,default);
    Check(store.Read().Sum(e=>e.Tokens.Total)==500,"增量扫描必须保留父会话证据");
});
AsyncTest("序列匹配不越过未确认子任务边界，不跨中断拼接",async()=>
{
    foreach(var started in new[]{false,true})
    {
        var home=Fixture("prefix-boundary-"+started);
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","parent.jsonl"),[Meta("parent"),Model(),Count(100,0),Count(200,0),Count(300,0)]);
        var lines=started?new[]{Meta("child","parent"),Model(),Count(100,0),Meta("parent"),Count(250,0),Count(300,0)}:
            new[]{Meta("child","parent"),Meta("parent"),Model(),Count(100,0),Count(200,0),Count(300,0),Count(400,0)};
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","child.jsonl"),lines);
        Check((await new HistoryScanner().ScanAsync(home,default)).Events.Where(e=>e.Session=="child").Sum(e=>e.Tokens.Total)==(started?200:0));
    }
});
AsyncTest("相同异常计数仅作为序列证据不计费，异常字段不同即中断",async()=>
{
    foreach(var identical in new[]{true,false})
    {
        var home=Fixture("invalid-prefix-"+identical);
        string Bad(int n)=>$"{{\"type\":\"event_msg\",\"timestamp\":\"2026-09-05T01:00:00Z\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":150,\"output_tokens\":0,\"total_tokens\":{n}}}}}}}}}";
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","p.jsonl"),[Meta("parent"),Model(),Count(100,0),Bad(999),Count(200,0),Count(300,0)]);
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","c.jsonl"),[Meta("child","parent"),Model(),Count(100,0),Meta("parent"),Bad(identical?999:888),Count(200,0),Count(300,0),Count(400,0)]);
        var result=await new HistoryScanner().ScanAsync(home,default);
        Check(result.Events.Where(e=>e.Session=="child").Sum(e=>e.Tokens.Total)==(identical?100:300));
        Check(result.Warnings>=2);
    }
});
AsyncTest("循环父子关系不作为继承证据",async()=>
{
    var home=Fixture("cyclic-parent");
    foreach(var pair in new[]{("a","b"),("b","a")})
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions",pair.Item1+".jsonl"),[Meta(pair.Item1,pair.Item2),Model(),Count(100,0),Meta(pair.Item2),Count(200,0),Count(300,0)]);
    Check((await new HistoryScanner().ScanAsync(home,default)).Total.Total==400);
});
AsyncTest("循环父子关系不作为继承证据",async()=>
{
    var home=Fixture("cyclic-parent");
    foreach(var pair in new[]{("a","b"),("b","a")})
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions",pair.Item1+".jsonl"),[Meta(pair.Item1,pair.Item2),Model(),Count(100,0),Meta(pair.Item2),Count(200,0),Count(300,0)]);
    Check((await new HistoryScanner().ScanAsync(home,default)).Total.Total==400);
});
AsyncTest("相同计数无父子关系不去重，短序列和未来父记录不推定",async()=>
{
    foreach(var scenario in new[]{"unrelated","short","future"})
    {
        var home=Fixture("sequence-"+scenario);var later=scenario=="future"?"2026-09-07T01:00:00Z":"2026-09-05T01:00:00Z";
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","parent.jsonl"),[Meta("parent"),Model(),Count(100,0,timestamp:later),Count(200,0,timestamp:later),Count(300,0,timestamp:later)]);
        var rows=new List<string>{Meta("child",scenario=="unrelated"?null:"parent"),Model(),Count(100,0),Meta("parent"),Count(200,0)};
        if(scenario!="short")rows.Add(Count(300,0));
        await File.WriteAllLinesAsync(Path.Combine(home,"sessions","child.jsonl"),rows);
        var report=await new HistoryScanner().ScanAsync(home,default);
        Check(report.Events.Where(e=>e.Session=="child").Sum(e=>e.Tokens.Total)==(scenario=="unrelated"?300:scenario=="short"?100:200));
    }
});
AsyncTest("超大行不会阻止后续有效记录", async () =>
{
    var home = Fixture("large"); var file = Path.Combine(home, "sessions", "a.jsonl");
    await File.WriteAllLinesAsync(file, [Meta("s4"), "{\"type\":\"response_item\",\"text\":\"" + new string('x', 1_100_000) + "\"}", Count(8, 2)]);
    var report = await new HistoryScanner().ScanAsync(home, default);
    Check(report.Total.Total == 10 && report.Warnings > 0);
});
UsageEvent Event(string id, long input, long output) => new(id, "db-session", "project", "test", "main", DateTimeOffset.Now, "2026-09-05", new(input, 0, 0, output), false, "source-1");
AsyncTest("扫描缓存仅在文件清单未变化时命中",async()=>
{
    var home=Fixture("cache");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("cached"),Model(),Count(80,20)]);
    var scanner=new HistoryScanner();var first=await scanner.ScanIfChangedAsync(home,default);var second=await scanner.ScanIfChangedAsync(home,default);
    Check(!first.UsedCache&&second.UsedCache&&second.Total.Total==100);
    await File.AppendAllLinesAsync(file,[Count(160,40)]);var appended=await scanner.ScanIfChangedAsync(home,default);
    Check(!appended.UsedCache&&appended.Total.Total==200);
    Check(!(await scanner.ScanIfChangedAsync(home,default,force:true)).UsedCache);
});
AsyncTest("已取消的扫描不能返回缓存成功",async()=>
{
    var home=Fixture("canceled");var scanner=new HistoryScanner();await scanner.ScanIfChangedAsync(home,default);
    using var canceled=new CancellationTokenSource();canceled.Cancel();var thrown=false;
    try{await scanner.ScanIfChangedAsync(home,canceled.Token);}catch(OperationCanceledException){thrown=true;}Check(thrown);
});
Test("估算缓存跨数据库实例恢复且不配对停机用量", () =>
{
    var directory=Path.Combine(fixtureRoot,"capacity-cache");var store=new HistoryStore(directory);
    var reset=DateTimeOffset.Now.AddDays(3);
    QuotaState State(double used,string plan="plus",string account="a")=>new([new("codex:weekly","周",used,10080,reset)],null,DateTimeOffset.Now,"ok",true,account,plan);
    var tracker=new WeeklyCapacityEstimator();tracker.Observe(State(10),1000,new(10,1000,0,null));
    tracker.Observe(State(12),1200,new(12,1200,0,null));store.SaveCapacity(tracker.Export());
    var cache=new HistoryStore(directory).ReadCapacity();Check(cache is not null);
    var legacy=new WeeklyCapacityEstimator();legacy.Restore(cache! with{Version=1});
    Check(legacy.Observe(State(30),9000).Count==0,"旧版全机口径样本不能恢复为账号样本");
    var next=new WeeklyCapacityEstimator();next.Restore(cache);
    Check(next.Current.Count==0,"未经账号核验不能展示旧样本");
    var restored=next.Observe(State(30),9000,new(90,9000,0,null)).Single();
    Check(restored.ObservedTokens==200&&restored.ObservedPercent==2&&restored.EstimatedDollars==100);
    var continued=next.Observe(State(32),9200,new(92,9200,0,null)).Single();
    Check(continued.Samples==2&&continued.ObservedTokens==400&&next.RestoredAt is not null);
    Check(next.Observe(State(33,"pro"),9300).Count==0,"套餐变化立即清样本");
    foreach(var state in new[]{State(30,"pro"),State(30,"plus","b"),State(1),State(30) with{Windows=[new("codex:weekly","周",30,10080,reset.AddDays(7))]}})
    {var rejected=new WeeklyCapacityEstimator();rejected.Restore(cache);Check(rejected.Observe(state,9000).Count==0);}
    var eventRow=Event("keep",80,20);store.Save(new([eventRow],1,0,DateTimeOffset.Now));store.SaveCapacity(null);
    Check(store.ReadCapacity() is null&&store.Read().Count==1,"重置缓存不能删除Token历史");
});
Test("估算历史独立归档、去重、跨套餐和重置保留",()=>
{
    var directory=Path.Combine(fixtureRoot,"capacity-archive");var store=new HistoryStore(directory);
    var now=DateTimeOffset.Now;
    var cache=new CapacityCache(2,"a","prolite",Pricing.CatalogVersion,now,[new("weekly","周",now.AddDays(3),20,5,1000,2,800,2,0)]);
    store.SaveCapacity(cache);store.SaveCapacity(cache with{SavedAt=now.AddSeconds(1)});
    Check(store.ReadCapacityHistory().Count==1,"相同采样只归档一次");
    store.SaveCapacity(cache with{Plan="pro",Windows=[]});
    Check(store.ReadCapacityHistory().Count==1,"空采样不能覆盖旧历史");
    store.SaveCapacity(cache with{Plan="pro",SavedAt=now.AddMinutes(1)});
    store.SaveCapacity(cache with{Version=1,Account="b",SavedAt=now.AddMinutes(2)});
    store.SaveCapacity(null);
    var reopened=new HistoryStore(directory);Check(reopened.ReadCapacity() is null);
    Check(reopened.ReadCapacityHistory().Count==3,"重启、换套餐、换账号和旧口径都保留");
    Check(reopened.ReadCapacityHistory(1,1).Count==1&&reopened.ReadCapacityHistory(3,1).Count==0);
    reopened.Save(new([Event("archived-keep",80,20)],1,0,now));
    reopened.ReplaceWithBackup(new([Event("archived-keep",80,20)],1,0,now),[]);
    Check(reopened.ReadCapacityHistory().Count==3,"重建本地日志不删除估算历史");
});
Test("SQLite 重复保存不重计且修正保留日期", () =>
{
    var store = new HistoryStore(Path.Combine(fixtureRoot, "db1")); var e = Event("one", 80, 20);
    store.Save(new([e], 1, 0, DateTimeOffset.Now) { Sources = ["source-1"] });
    store.Save(new([e with { Tokens = new(80, 12, 0, 20), LocalDate = "2026-09-04" }], 1, 0, DateTimeOffset.Now) { Sources = ["source-1"] });
    var read = store.Read(); Check(read.Count == 1 && read[0].Tokens.Cached == 12 && read[0].LocalDate == "2026-09-05");
});
Test("SQLite 事务中断回滚全部批次", () =>
{
    var store = new HistoryStore(Path.Combine(fixtureRoot, "db2"));
    try { store.Save(new([Event("one", 8, 2), Event("two", 16, 4)], 1, 0, DateTimeOffset.Now), checkpoint: _ => throw new IOException("模拟中断")); }
    catch (IOException) { }
    Check(store.Read().Count == 0);
});
Test("SQLite 检测来源截断并保留历史", () =>
{
    var store = new HistoryStore(Path.Combine(fixtureRoot, "db3"));
    store.Save(new([Event("one", 8, 2)], 1, 0, DateTimeOffset.Now) { Sources = ["source-1"] });
    var rejected = false;
    try { store.Save(new([], 1, 0, DateTimeOffset.Now) { Sources = ["source-1"] }); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && store.Read().Single().Tokens.Total == 10);
});
Test("日志轮转与失败隔离", () =>
{
    var directory = Path.Combine(fixtureRoot, "log"); Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "runtime.log"), new string('x', 1_050_000));
    new DiagnosticLog(directory).Write("INFO", "test", "rotation");
    Check(File.Exists(Path.Combine(directory, "runtime.1.log")));
    var invalid = Path.Combine(fixtureRoot, "not-directory"); File.WriteAllText(invalid, "test");
    new DiagnosticLog(invalid).Write("INFO", "test", "must not throw");
});

AsyncTest("持久化字节索引跨重启只解析追加部分",async()=>
{
    var home=Fixture("indexed");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("indexed-session"),Model(),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"indexed-db"));
    var first=await new IncrementalHistory(store).ScanAsync(home,default);Check(first.FilesUpdated==1&&first.Report.Total.Total==100);
    var cache=await new IncrementalHistory(store).ScanAsync(home,default);Check(cache.BytesParsed==0&&cache.Report.UsedCache);
    var appended=Count(160,40)+"\n";await File.AppendAllTextAsync(file,appended);
    var second=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(second.BytesParsed==System.Text.Encoding.UTF8.GetByteCount(appended)&&store.Read().Sum(e=>e.Tokens.Total)==200);
});
AsyncTest("索引半行不前进，补全无换行尾行只记一次",async()=>
{
    var home=Fixture("indexed-tail");var file=Path.Combine(home,"sessions","a.jsonl");var record=Count(80,20);
    await File.WriteAllTextAsync(file,Meta("tail")+"\n"+record[..25]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"tail-db"));await new IncrementalHistory(store).ScanAsync(home,default);
    Check(store.Read().Count==0);await File.AppendAllTextAsync(file,record[25..]);
    await new IncrementalHistory(store).ScanAsync(home,default);Check(store.Read().Sum(e=>e.Tokens.Total)==100);
    Check((await new IncrementalHistory(store).ScanAsync(home,default)).BytesParsed==0);
});
AsyncTest("索引重写检测保留旧统计，正文不进入索引",async()=>
{
    var home=Fixture("indexed-rewrite");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("rewrite"),"{\"type\":\"response_item\",\"payload\":{\"text\":\"PRIVATE-CONTENT-MARKER\"}}",Count(80,20)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"rewrite-db"));await new IncrementalHistory(store).ScanAsync(home,default);
    Check(!JsonSerializer.Serialize(store.ReadIndexes()).Contains("PRIVATE-CONTENT-MARKER"));
    await File.WriteAllLinesAsync(file,[Meta("rewrite"),Count(8,2)]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","b.jsonl"),[Meta("new-session"),Count(40,10)]);
    var preserved=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(preserved.PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==150);
    var repeated=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(repeated.PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==150);
});
AsyncTest("变化来源同会话隔离，正常会话继续追加且旧索引不覆盖",async()=>
{
    var home=Fixture("quarantine-session");var a=Path.Combine(home,"sessions","a.jsonl");var b=Path.Combine(home,"sessions","b.jsonl");
    await File.WriteAllLinesAsync(a,[Meta("shared"),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"quarantine-session-db"));await new IncrementalHistory(store).ScanAsync(home,default);
    var old=store.Read().Single();var oldIndex=store.ReadIndexes()[a];
    await File.WriteAllLinesAsync(a,[Meta("shared"),Count(8,2)]);
    await File.WriteAllLinesAsync(b,[Meta("shared"),Count(160,40)]);
    var c=Path.Combine(home,"sessions","c.jsonl");await File.WriteAllLinesAsync(c,[Meta("independent"),Count(40,10)]);
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.PreservedFiles==2&&store.Read().Sum(e=>e.Tokens.Total)==150&&store.Read().Single(e=>e.Id==old.Id)==old);
    Check(store.ReadIndexes()[a].PrefixHash==oldIndex.PrefixHash&&!store.ReadIndexes().ContainsKey(b));
    await File.AppendAllLinesAsync(c,[Count(80,20)]);
    await new IncrementalHistory(store).ScanAsync(home,default);
    Check(store.Read().Sum(e=>e.Tokens.Total)==200);
});
AsyncTest("分页迁移保留原明细，重定时末次快照不重计，跨重启继续增量",async()=>
{
    var home=Fixture("paginated-ledger");var file=Path.Combine(home,"sessions","a.jsonl");
    string Row(long total,string time,long? ordinal=null){return JsonSerializer.Serialize(new{type="event_msg",timestamp=time,ordinal,payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=total,output_tokens=0},last_token_usage=new{input_tokens=100,output_tokens=0}}}});}
    string Header(string owner)=>JsonSerializer.Serialize(new{type="session_meta",ordinal=0,payload=new{id=owner,history_mode="paginated"}});
    await File.WriteAllLinesAsync(file,[Meta("page"),Row(100,"2026-09-08T01:00:00Z"),Row(200,"2026-09-08T02:00:00Z")]);
    var store=new HistoryStore(Fixture("page-db"));await new IncrementalHistory(store).ScanAsync(home,default);var original=store.Read();
    await File.WriteAllLinesAsync(file,[Header("page"),Row(200,"2026-09-08T00:00:00Z",1)]);
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.PreservedFiles==0&&store.Read().Sum(e=>e.Tokens.Total)==200&&store.Read().Count==2);
    Check(original.All(e=>store.Read().Single(n=>n.Id==e.Id)==e));
    await File.AppendAllLinesAsync(file,[Row(300,"2026-09-08T03:00:00Z",2)]);
    await new IncrementalHistory(store).ScanAsync(home,default);Check(store.Read().Sum(e=>e.Tokens.Total)==300);
    await new IncrementalHistory(store).ScanAsync(home,default);Check(store.Read().Sum(e=>e.Tokens.Total)==300);
    await File.WriteAllLinesAsync(file,[Header("page"),Row(300,"2026-09-08T03:00:00Z",1),Row(400,"2026-09-08T04:00:00Z",2)]);
    Check((await new IncrementalHistory(store).ScanAsync(home,default)).PreservedFiles==0&&store.Read().Sum(e=>e.Tokens.Total)==400);
    // A different owner is not a continuation even if all counters match.
    await File.WriteAllLinesAsync(file,[Header("other"),Row(400,"2026-09-08T00:00:00Z",1)]);
    Check((await new IncrementalHistory(store).ScanAsync(home,default)).PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==400);
});
AsyncTest("分页完整有序子集保留更多历史，跨重启追加必须重新接续",async()=>
{
    var home=Fixture("subset-ledger");var file=Path.Combine(home,"sessions","a.jsonl");
    string Row(int n,long? ordinal=null,int last=100)=>JsonSerializer.Serialize(new{type="event_msg",timestamp=$"2026-08-18T0{n}:00:00Z",ordinal,payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=n*100,output_tokens=0},last_token_usage=new{input_tokens=last,output_tokens=0}}}});
    string Header(string owner="subset")=>JsonSerializer.Serialize(new{type="session_meta",ordinal=0,payload=new{id=owner,history_mode="paginated"}});
    await File.WriteAllLinesAsync(file,[Meta("subset"),Row(1),Row(2),Row(3),Row(4)]);
    var store=new HistoryStore(Fixture("subset-db"));await new IncrementalHistory(store).ScanAsync(home,default);var before=store.Read();
    await File.WriteAllLinesAsync(file,[Header(),Row(1,1),Row(3,2)]);
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.PreservedFiles==0&&store.Read().Sum(e=>e.Tokens.Total)==400&&store.ReadIndexes()[file].RetainedAhead);
    Check(before.All(e=>store.Read().Single(n=>n.Id==e.Id)==e));
    await new IncrementalHistory(store).ScanAsync(home,default);Check(store.Read().Sum(e=>e.Tokens.Total)==400);
    // Without the retained tail, a genuinely different suffix cannot be silently appended.
    await File.AppendAllLinesAsync(file,[Row(5,3)]);
    Check((await new IncrementalHistory(store).ScanAsync(home,default)).PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==400);
    await File.WriteAllLinesAsync(file,[Header(),Row(1,1),Row(3,2),Row(4,3),Row(5,4)]);
    Check((await new IncrementalHistory(store).ScanAsync(home,default)).PreservedFiles==0&&store.Read().Sum(e=>e.Tokens.Total)==500);
    Check(!store.ReadIndexes()[file].RetainedAhead);
    await new IncrementalHistory(store).ScanAsync(home,default);Check(store.Read().Sum(e=>e.Tokens.Total)==500);
    foreach(var bad in new[]{new[]{Header(),Row(3,1),Row(1,2)},new[]{Header(),Row(1,1)},new[]{Header("other"),Row(1,1),Row(3,2)},new[]{Header(),Row(1,1),Row(3,2,999)}})
    {
        await File.WriteAllLinesAsync(file,bad);
        Check((await new IncrementalHistory(store).ScanAsync(home,default)).PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==500);
    }
});
Test("待核对历史只排除重叠估算区间，未知范围仍保守排除",()=>
{
    var at=DateTimeOffset.Parse("2026-09-09T00:00:00Z");
    QuotaObservation Q(int hour,double used)=>new(at.AddHours(hour),"a","pro",Pricing.CatalogVersion,[new("codex:weekly","weekly",used,10080,at.AddDays(6))]);
    var observations=new[]{Q(0,10),Q(1,13),Q(2,16),Q(3,19),Q(4,19)};
    var rows=Enumerable.Range(1,3).Select(n=>Event("range"+n,100,20) with{Timestamp=at.AddMinutes(n*60-10),AccountScope="a",Model="gpt-5.4"}).ToArray();
    var clean=TemporalCapacity.CalculateStable(observations,rows,at.AddHours(5));
    var old=TemporalCapacity.CalculateStable(observations,rows,at.AddHours(5),uncertainRanges:[new(at.AddDays(-20),at.AddDays(-10))]);
    Check(clean.Cache!.Windows.Single().Tokens==old.Cache!.Windows.Single().Tokens);
    var overlap=TemporalCapacity.CalculateStable(observations,rows,at.AddHours(5),uncertainRanges:[new(at.AddMinutes(70),at.AddMinutes(80))]);
    Check(overlap.Intervals.Count(i=>i.Exclusion=="uncertain-history")==1&&overlap.Intervals.Count(i=>i.Exclusion is null)==2);
    var unknown=TemporalCapacity.CalculateStable(observations,rows,at.AddHours(5),uncertainRanges:[new(null,null)]);
    Check(unknown.Intervals.All(i=>i.Exclusion=="uncertain-history"));
    Check(UsageUncertainty.FromRecords(["{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}"] ).IsUnknown);
});
Test("索引与事件同事务回滚",()=>
{
    var store=new HistoryStore(Path.Combine(fixtureRoot,"index-rollback"));
    var index=new IndexedFile("synthetic",IncrementalHistory.ParserVersion,10,10,1,1,"hash",[],0);
    try{store.Save(new([Event("e",8,2)],1,0,DateTimeOffset.Now),checkpoint:_=>throw new IOException("中断"),indexes:[index]);}catch(IOException){}
    Check(store.Read().Count==0&&store.ReadIndexes().Count==0);
});

Test("服务档位空值不会崩溃或默认为免费",()=>
{
    foreach(var tier in new string?[]{null,""," "})
    {
        var estimate=Pricing.Calculate("gpt-6-astra",new(100,0,0,0),context:new(ServiceTier:tier!));
        Check(!estimate.HasAmount&&estimate.Unpriced==100);
    }
});
AsyncTest("完整校验检测保留元数据的等长重写",async()=>
{
    var home=Fixture("integrity");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("integrity"),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"integrity-db"));var scanner=new IncrementalHistory(store);
    await scanner.ScanAsync(home,default);var written=File.GetLastWriteTimeUtc(file);
    var original=await File.ReadAllTextAsync(file);await File.WriteAllTextAsync(file,original.Replace("integrity","integriTy"));
    File.SetLastWriteTimeUtc(file,written);
    var preserved=await scanner.ScanAsync(home,default,verifyIntegrity:true);
    Check(preserved.PreservedFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==100);
});

long BackupTotal(string path)
{
    using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=path,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
    db.Open();using var command=db.CreateCommand();command.CommandText="SELECT payload FROM events";
    using var reader=command.ExecuteReader();long total=0;
    while(reader.Read())total+=JsonSerializer.Deserialize<UsageEvent>(reader.GetString(0))!.Tokens.Total;
    return total;
}
AsyncTest("截断后确认重建保留可读旧备份，新索引不重计",async()=>
{
    var home=Fixture("rebuild");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("rebuild"),Model(),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"rebuild-db"));var scanner=new IncrementalHistory(store);
    await scanner.ScanAsync(home,default);
    await File.WriteAllLinesAsync(file,[Meta("rebuild"),Model(),Count(8,2)]);
    var result=await scanner.RebuildAsync(home,default);
    Check(result.BackupPath is not null&&BackupTotal(result.BackupPath)==100);
    Check(store.Read().Sum(e=>e.Tokens.Total)==10&&store.ReadIndexes().Count==1);
    Check((await scanner.ScanAsync(home,default)).Report.Total.Total==10);
});
AsyncTest("旧索引版本自动备份并从原始日志安全迁移",async()=>
{
    var home=Fixture("index-migration");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("migrated"),Model(),Count(80,20)]);
    var directory=Path.Combine(fixtureRoot,"index-migration-db");var store=new HistoryStore(directory);
    var legacy=new IndexedFile(file,IncrementalHistory.ParserVersion-1,1,1,1,1,"legacy",[],0);
    store.Save(new([Event("legacy",24,6)],1,0,DateTimeOffset.Now),indexes:[legacy]);
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.Migrated&&result.PreviousParserVersions.SequenceEqual([IncrementalHistory.ParserVersion-1]));
    Check(result.BackupPath is not null&&BackupTotal(result.BackupPath)==30);
    Check(store.Read().Sum(item=>item.Tokens.Total)==100&&store.ReadIndexes().Values.All(index=>index.Version==IncrementalHistory.ParserVersion));
});
AsyncTest("早期有事件无索引数据库自动进入安全迁移",async()=>
{
    var home=Fixture("unversioned-migration");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("unversioned"),Model(),Count(160,40)]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"unversioned-migration-db"));
    store.Save(new([Event("early",80,20)],1,0,DateTimeOffset.Now));
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.Migrated&&result.PreviousParserVersions.SequenceEqual([0])&&result.BackupPath is not null);
    Check(BackupTotal(result.BackupPath!)==100&&store.Read().Sum(item=>item.Tokens.Total)==200);
    Check(store.ReadIndexes().Count==1&&store.ReadIndexes().Values.All(index=>index.Version==IncrementalHistory.ParserVersion));
});
AsyncTest("可恢复的累计重置提示不再永久阻断安全重建",async()=>
{
    var home=Fixture("rebuild-quality-warning");var file=Path.Combine(home,"sessions","a.jsonl");
    var reset=JsonSerializer.Serialize(new{type="event_msg",timestamp="2026-09-05T02:00:00Z",payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=8,output_tokens=2},last_token_usage=new{input_tokens=8,output_tokens=2}}}});
    await File.WriteAllLinesAsync(file,[Meta("quality-warning"),Model(),Count(80,20),reset]);
    var store=new HistoryStore(Path.Combine(fixtureRoot,"rebuild-quality-warning-db"));
    store.Save(new([Event("old-quality",40,10)],1,0,DateTimeOffset.Now));
    var result=await new IncrementalHistory(store).RebuildAsync(home,default);
    Check(result.Report.Warnings==1&&result.BackupPath is not null&&BackupTotal(result.BackupPath)==50);
    Check(store.Read().Sum(item=>item.Tokens.Total)==110);
});
Test("重建替换中断时旧事件和游标一并回滚",()=>
{
    var directory=Path.Combine(fixtureRoot,"rebuild-rollback-db");var store=new HistoryStore(directory);
    var old=new IndexedFile("old",1,1,1,1,1,"hash",[],0);
    store.Save(new([Event("old",80,20)],1,0,DateTimeOffset.Now),indexes:[old]);
    var failed=false;
    try{store.ReplaceWithBackup(new([Event("new",8,2)],1,0,DateTimeOffset.Now),[],checkpoint:_=>throw new IOException("synthetic interruption"));}
    catch(IOException){failed=true;}
    Check(failed&&store.Read().Single().Id=="old"&&store.ReadIndexes().ContainsKey("old"));
    Check(BackupTotal(Directory.GetFiles(Path.Combine(directory,"backups"),"*.sqlite").Single())==100);
});
AsyncTest("重建保留活动半行并在完成后增量补齐",async()=>
{
    var home=Fixture("rebuild-live-tail");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("live-tail"),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);await scanner.ScanAsync(home,default);
    var next=Count(160,40)+"\n";var split=next.Length/2;await File.AppendAllTextAsync(file,next[..split]);
    var rebuilt=await scanner.RebuildAsync(home,default);
    Check(rebuilt.DeferredFiles==1&&store.Read().Sum(e=>e.Tokens.Total)==100);
    await File.AppendAllTextAsync(file,next[split..]);
    Check((await scanner.ScanAsync(home,default)).Report.Total.Total==200);
});
AsyncTest("完整坏记录仍拒绝安全重建",async()=>
{
    var home=Fixture("rebuild-invalid");var file=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("invalid"),Count(80,20)]);
    var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);await scanner.ScanAsync(home,default);
    await File.AppendAllTextAsync(file,"invalid json\n");var rejected=false;
    try{await scanner.RebuildAsync(home,default);}catch(InvalidDataException){rejected=true;}
    Check(rejected&&store.Read().Sum(e=>e.Tokens.Total)==100);
});
AsyncTest("空目录和已取消重建不清除历史",async()=>
{
    var home=Fixture("rebuild-empty");var store=new HistoryStore(Path.Combine(home,"data"));
    store.Save(new([Event("retained",80,20)],1,0,DateTimeOffset.Now));var scanner=new IncrementalHistory(store);
    var rejected=false;try{await scanner.RebuildAsync(home,default);}catch(InvalidDataException){rejected=true;}
    Check(rejected);using var canceled=new CancellationTokenSource();canceled.Cancel();
    try{await scanner.RebuildAsync(home,canceled.Token);throw new Exception("应当取消");}catch(OperationCanceledException){}
    Check(store.Read().Sum(e=>e.Tokens.Total)==100);
});
Test("重建不改变仍可识别事件的既有本地日期",()=>
{
    var store=new HistoryStore(Path.Combine(fixtureRoot,"rebuild-date"));var item=Event("same",80,20);
    store.Save(new([item],1,0,DateTimeOffset.Now));
    store.ReplaceWithBackup(new([item with{LocalDate="2030-01-01"}],1,0,DateTimeOffset.Now),[]);
    Check(store.Read().Single().LocalDate==item.LocalDate);
});

Test("历史筛选使用保存的本地日期并支持多维搜索",()=>
{
    var rows=new[]{Event("a",80,20) with{LocalDate="2026-09-01",Model="MODEL-A",Timestamp=DateTimeOffset.Parse("2030-01-01T00:00:00Z")},Event("b",8,2) with{LocalDate="2026-09-02"},Event("c",8,2) with{LocalDate="日期未知"}};
    Check(HistoryQuery.Filter(rows,new(new(2026,9,1),new(2026,9,1),"model-a")).Single().Id=="a");
    Check(HistoryQuery.Filter(rows,new(Day:"2026-09-02")).Single().Id=="b");
    Check(HistoryQuery.Filter(rows,new(Search:"missing")).Count==0);
    Check(HistoryQuery.Filter(rows,new()).Count==3);
});
Test("Session 汇总排序确定且不重复计入子分类",()=>
{
    var rows=new[]{Event("a",80,20) with{Session="A"},Event("b",8,2) with{Session="B"},Event("c",8,2) with{Session="A"}};
    var summary=HistoryQuery.Sessions(rows);Check(summary[0].Session=="A"&&summary[0].Tokens==110&&summary[0].Events.Count==2);
});

AsyncTest("累计回退按末次用量分段补位并公开质量提示",async()=>
{
    var home=Fixture("reset-segment");
    var reset=JsonSerializer.Serialize(new{type="event_msg",timestamp="2026-09-05T02:00:00Z",payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=8,output_tokens=2},last_token_usage=new{input_tokens=8,output_tokens=2}}}});
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","a.jsonl"),[Meta("reset"),Model(),Count(80,20),reset,Count(16,4,timestamp:"2026-09-05T03:00:00Z")]);
    var result=await new HistoryScanner().ScanAsync(home,default);
    Check(result.Total.Total==120&&result.Events.Select(e=>e.Id).Distinct().Count()==3);
    Check(result.Events[1].QualityNote is not null&&result.Events[1].Segment==1&&result.Warnings==1);
});
AsyncTest("现代 fork 在子任务边界前不计父历史",async()=>
{
    var home=Fixture("modern-fork");var path=Path.Combine(home,"sessions","a.jsonl");
    const string parent="01990000-0000-7000-8000-000000000001",child="01990000-0001-7000-8000-000000000001",turn="01990000-0002-7000-8000-000000000001";
    await File.WriteAllLinesAsync(path,[Meta(child,parent),Meta(parent),Model(),Count(80,20)]);
    Check((await new HistoryScanner().ScanAsync(home,default)).Total.Total==0);
    await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new{type="event_msg",payload=new{type="task_started",turn_id=turn}})+"\n"+Count(88,22)+"\n");
    Check((await new HistoryScanner().ScanAsync(home,default)).Total.Total==10);
});
AsyncTest("增量重算只触及相关 Session，已移除前缀仍防重",async()=>
{
    var home=Fixture("incremental-session");var first=Path.Combine(home,"sessions","a.jsonl");
    await File.WriteAllLinesAsync(first,[Meta("shared"),Model(),Count(80,20)]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","unrelated.jsonl"),[Meta("other"),Model(),Count(800,200)]);
    var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);await scanner.ScanAsync(home,default);
    // Rename outside scanned directories models a removed rollout without deleting the fixture.
    File.Move(first,Path.Combine(home,"retained-test-source.jsonl"));
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","b.jsonl"),[Meta("shared"),Model(),Count(88,22)]);
    var result=await scanner.ScanAsync(home,default);
    Check(result.RecordsReplayed==6&&store.Read().Sum(e=>e.Tokens.Total)==1110);
});
AsyncTest("受影响 Session 高水位前移不会阻止当天增量",async()=>
{
    var home=Fixture("incremental-high-water");var first=Path.Combine(home,"sessions","a.jsonl");var second=Path.Combine(home,"sessions","b.jsonl");
    await File.WriteAllLinesAsync(first,[Meta("moving"),Model(),Count(80,20,timestamp:"2026-09-07T01:00:00Z")]);
    await File.WriteAllLinesAsync(second,[Meta("moving"),Model(),Count(160,40,timestamp:"2026-09-07T02:00:00Z")]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","other.jsonl"),[Meta("unrelated"),Model(),Count(800,200)]);
    var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);await scanner.ScanAsync(home,default);
    Check(store.Read().Where(item=>item.Session=="moving").Sum(item=>item.Tokens.Total)==200);
    await File.AppendAllTextAsync(first,Count(240,60,timestamp:"2026-09-07T03:00:00Z")+"\n");
    var result=await scanner.ScanAsync(home,default);var rows=store.Read();
    Check(result.FilesUpdated==1&&rows.Where(item=>item.Session=="moving").Sum(item=>item.Tokens.Total)==300);
    Check(rows.Where(item=>item.Session=="unrelated").Sum(item=>item.Tokens.Total)==1000&&rows.Any(item=>item.LocalDate=="2026-09-07"));
});
AsyncTest("账号用量只归属连续稳定登录后的增量",async()=>
{
    var home=Fixture("account-attribution");var file=Path.Combine(home,"sessions","a.jsonl");var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);
    await File.WriteAllLinesAsync(file,[Meta("account-session"),Model(),Count(80,20)]);
    await scanner.ScanAsync(home,default,accountScope:"local-v1:A");
    await File.AppendAllTextAsync(file,Count(160,40)+"\n");await scanner.ScanAsync(home,default,accountScope:"local-v1:A");
    await File.AppendAllTextAsync(file,Count(240,60)+"\n");await scanner.ScanAsync(home,default,accountScope:"local-v1:B");
    await File.AppendAllTextAsync(file,Count(320,80)+"\n");await scanner.ScanAsync(home,default,accountScope:"local-v1:B");
    var rows=store.Read();
    Check(rows.Where(item=>item.AccountScope is null).Sum(item=>item.Tokens.Total)==200);
    Check(rows.Where(item=>item.AccountScope=="local-v1:A").Sum(item=>item.Tokens.Total)==100);
    Check(rows.Where(item=>item.AccountScope=="local-v1:B").Sum(item=>item.Tokens.Total)==100);
});
AsyncTest("新会话首次扫描仅归属连续账号观察期间的新增记录",async()=>
{
    var home=Fixture("new-session-account");var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);
    await scanner.ScanAsync(home,default,accountScope:"A");
    // Windows creation timestamps can have coarser precision than UtcNow.
    // Keep this positive-case fixture away from the conservative boundary.
    await Task.Delay(50);
    var file=Path.Combine(home,"sessions","fresh.jsonl");
    await File.WriteAllLinesAsync(file,[Meta("fresh"),Model(),Count(80,20,timestamp:DateTimeOffset.UtcNow.ToString("O"))]);
    await scanner.ScanAsync(home,default,accountScope:"A");
    Check(store.Read().Single().AccountScope=="A","fresh scope missing");
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","import.jsonl"),[Meta("import"),Model(),Count(80,20)]);
    await scanner.ScanAsync(home,default,accountScope:"A");
    Check(store.Read().Single(e=>e.Session=="import").AccountScope is null,"import scope leaked");
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","switch.jsonl"),[Meta("switch"),Model(),Count(80,20,timestamp:DateTimeOffset.UtcNow.ToString("O"))]);
    await scanner.ScanAsync(home,default,accountScope:"B");
    Check(store.Read().Single(e=>e.Session=="switch").AccountScope is null);
    var restarted=new IncrementalHistory(store);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","restart.jsonl"),[Meta("restart"),Model(),Count(80,20,timestamp:DateTimeOffset.UtcNow.ToString("O"))]);
    await restarted.ScanAsync(home,default,accountScope:"B");
    Check(store.Read().Single(e=>e.Session=="restart").AccountScope is null);
});
AsyncTest("两万条事件性能样例与跨重启追加一致性",async()=>
{
    var home=Fixture("scale");const int files=200,rows=100;
    for(var f=0;f<files;f++)await File.WriteAllLinesAsync(Path.Combine(home,"sessions",$"{f:D4}.jsonl"),new[]{Meta("scale-"+f),Model()}.Concat(Enumerable.Range(1,rows).Select(i=>Count(i*10,0))));
    var store=new HistoryStore(Path.Combine(home,"data"));var watch=Stopwatch.StartNew();
    var initial=await new IncrementalHistory(store).ScanAsync(home,default);var firstMs=watch.ElapsedMilliseconds;
    await File.AppendAllTextAsync(Path.Combine(home,"sessions","0000.jsonl"),Count(1010,0)+"\n");watch.Restart();
    var append=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(initial.Report.Events.Count==20000&&append.RecordsReplayed==103&&store.Read().Sum(e=>e.Tokens.Total)==200010);
    Console.WriteLine($"BENCH events=20000 initialMs={firstMs} appendMs={watch.ElapsedMilliseconds} replayed={append.RecordsReplayed} managedBytes={GC.GetTotalMemory(false)}");
});

CodexClient FakeClient(string scenario)
{
    return new CodexClient(new DiagnosticLog(Path.Combine(fixtureRoot,"rpc-logs")),new LocalAccountFingerprint(Path.Combine(fixtureRoot,"rpc-account-fingerprint.key")))
    {
        RequestTimeout=TimeSpan.FromMilliseconds(1500),
        TestProcessFactory=(_,_)=>
        {
            var executable=Environment.ProcessPath??throw new InvalidOperationException("无法定位测试进程");
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(executable),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--fake-rpc");start.ArgumentList.Add(scenario);return start;
        }
    };
}
AsyncTest("模拟 RPC 响应穿插事件不会丢失",async()=>
{
    await using var client=FakeClient("event");var notifications=0;client.QuotaUpdated+=_=>Interlocked.Increment(ref notifications);
    var result=await client.ReadAsync(null,"unused",default);Check(result.Fresh&&result.ResetCount==2&&notifications==1);
    Check(client.ThreadNames.GetValueOrDefault("session-root")=="可读会话标题"&&!client.ThreadNames.Values.Contains("不应作为标题"));
    var again=await client.ReadAsync(null,"unused",default);Check(again.Fresh&&client.IsConnected);
});
AsyncTest("独立后端未登录时不借用桌面身份且撤下旧额度",async()=>
{
    await using var client=FakeClient("signed-out");var invalidated=false;client.AccountInvalidated+=hard=>{Check(!hard);invalidated=true;};
    var result=await client.ReadAsync(null,"unused",default);
    Check(invalidated&&!result.Fresh&&result.Windows.Count==0&&result.AccountKey is null&&result.IsLocalAccount&&result.AccountLabel=="本地账户"&&result.ResetCount is null);
});
AsyncTest("本地账户无需凭据即可扫描和持久化 Token",async()=>
{
    var home=Fixture("local-account");
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","a.jsonl"),[Meta("local-only"),Model(),Count(80,20)]);
    Check(!File.Exists(Path.Combine(home,"auth.json")));
    var store=new HistoryStore(Path.Combine(fixtureRoot,"local-account-db"));
    var result=await new IncrementalHistory(store).ScanAsync(home,default);
    Check(result.Report.Total.Total==100&&store.Read().Count==1);
    Check(Pricing.Summarize(store.Read()).HasAmount);
    Check(!QuotaState.LocalAccount.Fresh&&QuotaState.LocalAccount.AccountKey is null);
});
AsyncTest("模拟 RPC stderr 写满仍能完成",async()=>
{
    await using var client=FakeClient("stderr");Check((await client.ReadAsync(null,"unused",default)).Fresh);
});
AsyncTest("额度网络失败只重试一次且错误信息保持安全",async()=>
{
    await using(var recovered=FakeClient("network-once"))Check((await recovered.ReadAsync(null,"unused",default)).Fresh);
    await using var failed=FakeClient("network-always");var message="";
    try{await failed.ReadAsync(null,"unused",default);}catch(CodexRpcException ex){message=ex.Message;Check(ex.Kind==RpcFailureKind.Dns);}
    Check(message.Contains("DNS")&&!message.Contains("http")&&!failed.IsConnected);
});
AsyncTest("明确认证失败只刷新已有缓存并重试额度",async()=>
{
    await using var client=FakeClient("auth-once");var result=await client.ReadAsync(null,"unused",default);
    Check(result.Fresh&&client.IsConnected);
});
AsyncTest("模拟 RPC 身份变化撤下数值",async()=>
{
    foreach(var scenario in new[]{"switch","no-identity-switch","no-identity-event","invalidate"})
    {await using var client=FakeClient(scenario);var result=await client.ReadAsync(null,"unused",default);Check(!result.Fresh&&result.Windows.Count==0,scenario);}
});
AsyncTest("模拟 RPC 超时与取消释放进程",async()=>
{
    await using var client=FakeClient("timeout");var timedOut=false;
    try{await client.ReadAsync(null,"unused",default);}catch(CodexRpcException ex){timedOut=ex.Kind==RpcFailureKind.Timeout&&ex.Message.Contains("超时");}
    Check(timedOut&&!client.IsConnected);
    using var cancellation=new CancellationTokenSource(150);var canceled=false;
    try{await client.ReadAsync(null,"unused",cancellation.Token);}catch(OperationCanceledException){canceled=true;}
    Check(canceled&&!client.IsConnected);
});
AsyncTest("模拟 RPC 错误格式释放等待",async()=>
{
    await using var client=FakeClient("malformed");var failed=false;
    try{await client.ReadAsync(null,"unused",default);}catch(IOException){failed=true;}
    Check(failed&&!client.IsConnected);
});

var failures = 0;
Test("未登录隐藏额度，Pro 按实际窗口而非套餐名展示",()=>
{
    Check(!QuotaState.LocalAccount.HasQuotaDisplay);
    var weekly=QuotaParser.Parse(Json("{\"rateLimits\":{\"secondary\":{\"usedPercent\":18,\"windowDurationMins\":10080}}}"),DateTimeOffset.Now,null,"pro") with{IsCachedAccount=true};
    Check(weekly.HasQuotaDisplay&&weekly.Windows.Count==1&&weekly.Windows[0].Minutes==10080);
    var both=QuotaParser.Parse(Json("{\"rateLimits\":{\"primary\":{\"usedPercent\":30,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":18,\"windowDurationMins\":10080}}}"),DateTimeOffset.Now,null,"pro") with{IsCachedAccount=true};
    Check(both.Windows.Count==2&&both.HasQuotaDisplay);
    Check(!both.ClearUnverifiedSnapshot("过期").HasQuotaDisplay);
});
AsyncTest("缓存登录缺 ID 时用本地指纹隔离额度与通知",async()=>
{
    await using var client=FakeClient("no-identity");var result=await client.ReadAsync(null,"unused",default);
    Check(result.Fresh&&result.IsCachedAccount&&result.AccountKey is not null&&!result.SnapshotOnly&&result.Windows.Count==1&&result.ResetCount==2);
    Check(result.AccountLabel=="本机缓存账号");
    Check(new NotificationPolicy().Evaluate(result,new(true,true,99,120),DateTimeOffset.Now,TimeSpan.FromMinutes(5)).Count==1);
    var cleared=result.ClearUnverifiedSnapshot("重新查询");
    Check(cleared.Fresh&&cleared.Windows.Count==1&&cleared.ResetCount==2&&cleared.FetchedAt is not null);
});
AsyncTest("缓存无有效窗口不伪报成功，共享 proxy 使用本地指纹",async()=>
{
    await using var empty=FakeClient("no-identity-empty");var result=await empty.ReadAsync(null,"unused",default);
    Check(!result.Fresh&&result.Windows.Count==0&&result.Status.Contains("未提供有效额度窗口"));
    await using var proxy=FakeClient("no-identity");var shared=await proxy.ReadAsync(null,"unused",default,reuseBackend:true);
    Check(shared.Fresh&&shared.AccountKey is not null&&shared.IsCachedAccount);
});
AsyncTest("缓存登录检测区分未登录和缺稳定 ID，不执行 OAuth",async()=>
{
    await using var missing=FakeClient("signed-out");await missing.ReadAsync(null,"unused",default);
    Check(missing.AccountObservation=="后端未识别到已有登录");
    await using var found=FakeClient("no-identity");await found.ReadAsync(null,"unused",default);
    Check(found.AccountObservation.Contains("已识别 ChatGPT 登录")&&found.AccountObservation.Contains("本地指纹")&&!found.AccountObservation.Contains("synthetic@example.com"));
});
AsyncTest("授权期间后端断开及时失败，专用登出关闭进程",async()=>
{
    await using var client=FakeClient("login-disconnect");using var timeout=new CancellationTokenSource(5000);var failed=false;
    try{await client.LoginAsync(null,Path.Combine(fixtureRoot,"disconnect-home"),_=>Task.FromResult(true),timeout.Token);}catch(IOException){failed=true;}
    Check(failed&&!client.IsConnected);
    await using var logout=FakeClient("login-logout");await logout.LogoutAuthorizedAsync(null,Path.Combine(fixtureRoot,"logout-home"),default);Check(!logout.IsConnected);
});
AsyncTest("官方登录通知早于响应仍关联成功，授权结束释放子进程",async()=>
{
    await using var client=FakeClient("login-success");var opened=false;
    await client.LoginAsync(null,Path.Combine(fixtureRoot,"login-home"),uri=>{opened=uri.Host=="auth.openai.com";return Task.FromResult(true);},default);
    Check(opened&&!client.IsConnected);
});
AsyncTest("授权拒绝非官方链接且取消时释放连接",async()=>
{
    await using var client=FakeClient("login-url");var opened=false;var rejected=false;
    try{await client.LoginAsync(null,Path.Combine(fixtureRoot,"bad-login-home"),_=>{opened=true;return Task.FromResult(true);},default);}catch(InvalidDataException){rejected=true;}
    Check(rejected&&!opened&&!client.IsConnected);
    await using var waiting=FakeClient("login-wait");using var ct=new CancellationTokenSource();
    try{await waiting.LoginAsync(null,Path.Combine(fixtureRoot,"cancel-login-home"),_=>{ct.Cancel();return Task.FromResult(true);},ct.Token);throw new Exception("应当取消");}catch(OperationCanceledException){}
    Check(!waiting.IsConnected);
});
Test("授权 URL 仅允许官方 HTTPS 默认端口",()=>
{
    foreach(var url in new[]{"http://auth.openai.com/","https://auth.openai.com.example.com/","https://user@auth.openai.com/","https://chatgpt.com:8080/"})
    {var rejected=false;try{CodexClient.ValidateLoginUrl(url);}catch(InvalidDataException){rejected=true;}Check(rejected);}
});
AsyncTest("专用授权后端无官方 ID 时使用本地账户指纹",async()=>
{
    await using var client=FakeClient("no-identity");var result=await client.ReadAsync(null,"unused",default,managedAccount:true);
    Check(result.Fresh&&result.AccountKey is not null&&result.IsAuthorizedAccount&&result.AccountLabel=="UsageLoom 授权账号");
});
Test("复用后端仅启动官方代理，不含独立监听或服务控制参数",()=>
{
    Check(CodexClient.ConnectionArguments(true).SequenceEqual(new[]{"app-server","proxy"}));
    Check(CodexClient.ConnectionArguments(false).Contains("stdio://"));
});
AsyncTest("代理握手失败不回退独立后端",async()=>
{
    await using var client=FakeClient("malformed");var failed=false;
    try{await client.ReadAsync(null,"unused",default,reuseBackend:true);}catch(IOException){failed=true;}
    Check(failed&&!client.IsConnected);
});
AsyncTest("无 CLI 时进入本地账户模式而非查询失败",async()=>
{
    await using var client=new CodexClient(new DiagnosticLog(Path.Combine(fixtureRoot,"no-cli-log"))){ExecutableResolver=_=>null};
    var result=await client.ReadAsync(Path.Combine(fixtureRoot,"missing-codex.exe"),fixtureRoot,default);
    Check(result.IsLocalAccount&&!result.Fresh&&result.Windows.Count==0&&result.ResetCount is null&&!client.IsConnected);
    Check(result.Status.Contains("未找到额度查询后端"));
});
Test("后端路径失效后重新发现桌面版本且有效手动路径优先",()=>
{
    var root=Fixture("backend-discovery");var bin=Path.Combine(root,"bin");
    var old=Path.Combine(bin,"old","codex.exe");var newer=Path.Combine(bin,"new","codex.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(old)!);Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
    File.WriteAllText(old,"");File.WriteAllText(newer,"");
    File.SetLastWriteTimeUtc(old,new DateTime(2026,9,1));File.SetLastWriteTimeUtc(newer,new DateTime(2026,9,7));
    Check(CodexClient.FindExecutable(null,"",bin)==newer);
    Check(CodexClient.FindExecutable(Path.Combine(root,"missing.exe"),"",bin)==newer);
    Check(CodexClient.FindExecutable(old,"",bin)==old);
    Check(CodexClient.FindExecutable(null,Path.GetDirectoryName(old),Path.Combine(root,"missing"))==old);
    Check(CodexClient.FindExecutable(null,"",Path.Combine(root,"missing")) is null);
});
AsyncTest("标题响应损坏不影响账号与额度连接",async()=>
{
    await using var client=FakeClient("bad-thread-json");
    var result=await client.ReadAsync(null,"unused",default);
    Check(result.Fresh&&client.IsConnected&&result.Windows.Count==1);
});
Test("紧凑数字边界及精度",()=>
{
    Check(UsageNumbers.Compact(1000)=="1K");Check(UsageNumbers.Compact(197580900)=="197.58M");
    Check(UsageNumbers.Compact(999999)=="1M");Check(UsageNumbers.Compact(1000000000)=="1B");
    Check(UsageNumbers.Compact(0)=="0");Check(UsageNumbers.Compact(double.NaN)=="—");
});
Test("周容量不混入其他账号和未归属历史",()=>
{
    var own=Event("own",80,20) with {AccountScope="a"};
    var other=own with {AccountScope="b"};var unknown=own with {AccountScope=null};
    var spark=own with {Model="gpt-5.3-codex-spark"};
    Check(CapacityUsage.ForAccount([own,other,unknown,spark],"a").Count==1);
    Check(CapacityUsage.ForAccount([own,other,unknown],null).Count==0);
});
AsyncTest("跨重启只推定同账号边界内新记录并持久保留标记",async()=>
{
    var home=Fixture("restart-inference");var file=Path.Combine(home,"sessions","existing.jsonl");
    var store=new HistoryStore(Path.Combine(home,"data"));var scanner=new IncrementalHistory(store);
    await File.WriteAllLinesAsync(file,[Meta("existing"),Model(),Count(80,20)]);
    await scanner.ScanAsync(home,default,accountScope:"A");
    scanner.SaveExitCheckpoint("A",home,DateTimeOffset.UtcNow);
    var checkpoint=store.TakeRestartCheckpoint()!;Check(checkpoint is not null);Check(store.TakeRestartCheckpoint() is null);
    await Task.Delay(50);
    await File.AppendAllTextAsync(file,Count(160,40,timestamp:DateTimeOffset.UtcNow.ToString("O"))+"\n");
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","new.jsonl"),[Meta("new"),Model(),Count(80,20,timestamp:DateTimeOffset.UtcNow.ToString("O"))]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","import.jsonl"),[Meta("import"),Model(),Count(80,20)]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","unknown.jsonl"),[Meta("unknown"),Model(),Count(80,20,timestamp:"invalid")]);
    await File.WriteAllLinesAsync(Path.Combine(home,"sessions","future.jsonl"),[Meta("future"),Model(),Count(80,20,timestamp:DateTimeOffset.UtcNow.AddHours(1).ToString("O"))]);
    var opened=DateTimeOffset.UtcNow;var restarted=new IncrementalHistory(store);
    await restarted.ScanAsync(home,default,accountScope:"A");
    var before=store.Read().Sum(e=>e.Tokens.Total);var created=store.ReadIndexes().ToDictionary(p=>p.Key,p=>(p.Value.Created,p.Value.Written));
    Check(store.AttributeRestartGap(checkpoint!,"B",home,opened)==0);
    Check(store.AttributeRestartGap(checkpoint!,"A",home,checkpoint!.ClosedAt.AddSeconds(-1))==0);
    using(var canceled=new CancellationTokenSource())
    {
        canceled.Cancel();try{store.AttributeRestartGap(checkpoint!,"A",home,opened,canceled.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
    }
    Check(store.Read().All(e=>e.AccountScope is null));
    Check(store.AttributeRestartGap(checkpoint!,"A",home,opened)==2);
    var after=store.Read();Check(after.Sum(e=>e.Tokens.Total)==before);
    Check(after.Where(e=>e.AccountAttribution=="restart-inferred").Sum(e=>e.Tokens.Total)==200);
    Check(CapacityUsage.ForAccount(after,"A").Count==0);
    Check(after.Where(e=>e.Session is "import" or "unknown" or "future").All(e=>e.AccountScope is null));
    foreach(var (key,index) in store.ReadIndexes())Check(created[key]==(index.Created,index.Written));
    Check(store.AttributeRestartGap(checkpoint!,"A",home,opened)==0);
    await File.AppendAllTextAsync(file,Count(240,60,timestamp:DateTimeOffset.UtcNow.ToString("O"))+"\n");
    await restarted.ScanAsync(home,default,accountScope:"A");
    Check(store.Read().Count(e=>e.AccountAttribution=="restart-inferred")==2);
    Check(CapacityUsage.ForAccount(store.Read(),"A").Sum(e=>e.Tokens.Total)==100);
});
Test("双语资源键与格式占位符完整一致",()=>
{
    var assembly=typeof(L10n).Assembly;
    Dictionary<string,string> Read(string language){using var stream=assembly.GetManifestResourceStream($"UsageLoom.Core.Localization.{language}.json")!;return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;}
    var zh=Read("zh-CN");var en=Read("en-US");
    Check(zh.Count>=446&&zh.Keys.Order().SequenceEqual(en.Keys.Order()));
    foreach(var key in zh.Keys)
    {
        Check(!System.Text.RegularExpressions.Regex.IsMatch(en[key],"[\\u4e00-\\u9fff]"),key);
        Check(System.Text.CompositeFormat.Parse(zh[key]).MinimumArgumentCount==System.Text.CompositeFormat.Parse(en[key]).MinimumArgumentCount,key);
        string[] Fields(string text)=>System.Text.RegularExpressions.Regex.Matches(text,@"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m=>m.Value).Order().ToArray();
        Check(Fields(zh[key]).SequenceEqual(Fields(en[key])),key);
    }
});
Test("语言仅改变展示且不改变账号窗口与计价",()=>
{
    var before=L10n.Language;
    try
    {
        var data=Event("language",100,20);var cost=Pricing.Summarize([data]).Cost;
        L10n.Language="en-US";
        Check(L10n.T("sDF3D58C7D84B")=="Settings");
        Check(QuotaState.LocalAccount.AccountLabel=="Local account");
        Check(HistoryQuery.ResolveRange(HistoryRangeKind.Day,DateOnly.FromDateTime(DateTime.Today)).Label=="Today");
        Check(Pricing.Summarize([data]).Cost==cost);
        Check(L10n.F("s1F2CD6B8A261",338.7m).Contains("week"));
    }
    finally{L10n.Language=before;}
});
try
{
    foreach (var entry in tests)
    {
        try { await entry.Run(); Console.WriteLine("PASS " + entry.Name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + entry.Name + ": " + Privacy.Redact(ex.Message)); }
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var checkedRoot = Path.GetFullPath(fixtureRoot);
    if (Path.GetDirectoryName(checkedRoot) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(checkedRoot).StartsWith("UsageLoom-tests-"))
        throw new InvalidOperationException("拒绝清理非测试目录");
    Directory.Delete(checkedRoot, true);
}
Console.WriteLine($"结果：{tests.Count - failures}/{tests.Count} 通过");
Environment.ExitCode = failures == 0 ? 0 : 1;
