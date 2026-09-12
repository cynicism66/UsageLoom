using System.Text.Json;
using UsageLoom.Core;

internal static class ModeScannerTests
{
    private static readonly DateTimeOffset Start=new(2026,9,12,10,0,0,TimeSpan.Zero);
    private static readonly string Home=Path.Combine(Path.GetTempPath(),"UsageLoom-mode-fixtures");
    private static string PathFor(string name)=>Path.Combine(Home,"sessions",name+".jsonl");
    private static string Row(double seconds,string type,object payload)=>JsonSerializer.Serialize(new
        {timestamp=Start.AddSeconds(seconds).ToString("O"),type,account_scope="account-a",payload});
    private static string Meta(string owner,string? parent=null)=>Row(-10,"session_meta",new{id=owner,parent_thread_id=parent,cwd="fixture"});
    private static string Settings(double seconds,string owner,string? tier,string effort="ultra",string model="gpt-6-astra")=>
        Row(seconds,"event_msg",new{type="thread_settings_applied",thread_id=owner,thread_settings=new{model,service_tier=tier,reasoning_effort=effort}});
    private static string Turn(double seconds,string? effort="ultra",string model="gpt-6-astra",string? thread=null)=>
        Row(seconds,"turn_context",new{model,effort,thread_id=thread});
    private static string Count(double seconds,long total,long last)=>Row(seconds,"event_msg",new{type="token_count",info=new
        {total_token_usage=new{input_tokens=total,cached_input_tokens=0,output_tokens=0,total_tokens=total},
            last_token_usage=new{input_tokens=last,cached_input_tokens=0,output_tokens=0,total_tokens=last}}});
    private static Task<ScanReport> Scan(params (string Name,string[] Rows)[] files)=>new HistoryScanner().ScanAsync(Home,default,
        indexedRecords:files.ToDictionary(f=>PathFor(f.Name),f=>(IReadOnlyList<string>)f.Rows,StringComparer.OrdinalIgnoreCase));
    private static void Check(bool result,string message){if(!result)throw new InvalidOperationException(message);}
    private static string Identity(UsageEvent row)=>JsonSerializer.Serialize(row with{Pricing=null});

    public static async Task Run()
    {
        var owner="mode-owner";
        var switching=new[]{Meta(owner),Settings(0,owner,"standard"),Turn(1),Count(2,100,100),
            Settings(3,owner,"priority"),Turn(4),Count(5,200,100),Settings(6,owner,"default"),Turn(7,"medium"),Count(8,300,100)};
        var result=await Scan(("switch",switching));
        Check(result.Events.Select(e=>e.Pricing?.RequestedServiceTier).SequenceEqual(["standard","priority","default"]),"普通-Fast-普通请求时间线错误");
        Check(result.Events.Select(e=>e.Pricing?.ReasoningEffort).SequenceEqual(["ultra","ultra","medium"]),"Ultra与medium应保存为思考强度而非档位");
        Check(result.Events.All(e=>e.Pricing is {ActualServiceTier:null,ServiceTierEvidence:"request-setting"}),"请求设置不能冒充实际响应");
        Check(result.Events.All(e=>e.AccountScope=="account-a")&&result.Total.Total==300,"模式元数据不能修改Token与归属");
        var legacy=switching.Where(line=>!line.Contains("thread_settings_applied",StringComparison.Ordinal)).ToArray();
        var previous=await Scan(("switch",legacy));
        Check(result.Events.Select(Identity).SequenceEqual(previous.Events.Select(Identity)),"模式补齐必须保留原事件ID、模型、Token和归属");
        var again=await Scan(("switch",switching));
        Check(result.Events.SequenceEqual(again.Events),"重复扫描不能重复计费或改变模式");

        // Sidecar records are physically appended after the counter ledger, but retain effective timestamps.
        var modelOnly=Row(1,"turn_context",new{model="gpt-6-astra"});
        var sidecar=await Scan(("sidecar",[Meta(owner),modelOnly,Count(2,100,100),Count(3,200,100),
            Settings(.5,owner,"priority"),Turn(1,"ultra"),Settings(5,owner,"default")]));
        Check(sidecar.Events.All(e=>e.Pricing is {RequestedServiceTier:"priority",ReasoningEffort:"ultra"}),"后置sidecar应按有效时间补齐，而未来设置不能倒灌旧Token");
        var duplicate=await Scan(("sidecar",[Meta(owner),modelOnly,Count(2,100,100),Count(3,200,100),
            Settings(.5,owner,"priority"),Turn(1,"ultra"),Settings(5,owner,"default"),Turn(1,"ultra"),Settings(.5,owner,"priority")]));
        Check(sidecar.Events.SequenceEqual(duplicate.Events),"重复模式元数据不应形成冲突");

        var changingInFlight=await Scan(("in-flight",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),
            Settings(3,owner,"fast"),Count(4,200,100),Settings(5,owner,"default"),Count(6,300,100),Turn(7,"medium"),Count(8,400,100)]));
        Check(changingInFlight.Events[1].Pricing?.RequestedServiceTier=="priority","等价档位重复设置不应损失已知证据");
        Check(changingInFlight.Events[2].Pricing is {RequestedServiceTier:null,ReasoningEffort:"ultra",ServiceTierEvidence:"unknown"},"无新请求边界的途中切换必须保留档位未知");
        Check(changingInFlight.Events[3].Pricing?.RequestedServiceTier=="default","新context应重新建立模式证据");

        var aggregate=await Scan(("aggregate",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),
            Settings(3,owner,"default"),Turn(4),Count(5,400,100),Count(6,500,100)]));
        Check(aggregate.Events[1].Tokens.Total==300&&aggregate.Events[1].Pricing is null,"跨请求累计增量不能套末尾请求档位");
        Check(aggregate.Events[2].Pricing?.RequestedServiceTier=="default"&&aggregate.Total.Total==500,"聚合未知不能丢Token或影响后续单请求");

        var conflicting=await Scan(("conflicts",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),
            Settings(3,owner,"priority"),Settings(3,owner,"default"),Turn(4),Count(5,200,100),
            Settings(6,owner,null),Turn(7),Count(8,300,100)]));
        Check(conflicting.Events.Skip(1).All(e=>e.Pricing is {RequestedServiceTier:null,ServiceTierEvidence:"unknown"}),"冲突或显式未知设置不能默认为普通模式");
        var direct=await Scan(("direct",[Meta(owner),Settings(0,owner,"priority"),
            Row(1,"turn_context",new{model="gpt-6-astra",reasoning_effort="high",service_tier="default"}),Count(2,100,100)]));
        Check(direct.Events.Single().Pricing is {RequestedServiceTier:"default",ReasoningEffort:"high",ActualServiceTier:null},"明确turn_context字段优先于会话设置");
        foreach(var tier in new object?[]{null,123})
        {
            var cleared=await Scan(("explicit-clear",[Meta(owner),Settings(0,owner,"priority"),
                Row(1,"turn_context",new{model="gpt-6-astra",effort="ultra",service_tier=tier}),Count(2,100,100)]));
            Check(cleared.Events.Single().Pricing is {RequestedServiceTier:null,ServiceTierEvidence:"unknown"},"显式空或非法tier不能回退继承priority");
        }
        // A contradictory/cleared context must also invalidate inheritance by later
        // tier-less contexts. Do not turn a one-turn override into a global default.
        foreach(var (name,boundary,expected) in new (string,string[],string)[]
        {
            ("default",[Row(3,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier="default"})],"standard"),
            ("null",[Row(3,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier=(string?)null})],"unknown"),
            ("invalid",[Row(3,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier=123})],"unknown"),
            ("conflict",[Row(3,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier="priority"}),
                Row(3,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier="default"})],"unknown"),
            ("missing-model",[Row(3,"turn_context",new{effort="medium"})],"unknown"),
            ("changed-model",[Turn(3,"medium","gpt-5.6-sol")],"unknown"),
            ("conflicting-model",[Turn(3,"medium"),Turn(3,"medium","gpt-5.6-sol")],"unknown")
        })
        {
            string[] rows=[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),..boundary,
                Count(4,200,100),Turn(5),Count(6,300,100),
                Row(7,"turn_context",new{model="gpt-6-astra",effort="ultra",service_tier="priority"}),Count(8,400,100),
                Turn(9),Count(10,500,100),Settings(11,owner,"default"),Turn(12),Count(13,600,100)];
            var cleared=await Scan(("stale-fallback-"+name,rows));
            Check(cleared.Events.Select(e=>Pricing.ResolveMode(e.Pricing).Tier)
                .SequenceEqual(["fast",expected,"unknown","fast","unknown","standard"]),
                name+": 新context不能复活被推翻的Fast默认值，只有新设置才能重建继承");
            Check(cleared.Events[2].Pricing?.ServiceTierEvidence=="unknown"&&cleared.Events[4].Pricing?.ServiceTierEvidence=="unknown",
                name+": 失效继承必须如实标为未知");
            var counterOnly=await Scan(("stale-fallback-"+name,rows.Where(line=>!line.Contains("thread_settings_applied",StringComparison.Ordinal)).ToArray()));
            Check(cleared.Total.Total==600&&cleared.Events.Count==6&&cleared.Events.All(e=>e.AccountScope=="account-a")&&
                cleared.Events.Select(Identity).SequenceEqual(counterOnly.Events.Select(Identity)),name+": 失效模式不能修改Token、事件ID、模型或归属");
        }
        foreach(var (configured,overridden) in new[]{("priority","fast"),("standard","default")})
        {
            var equivalent=await Scan(("equivalent-fallback",[Meta(owner),Settings(0,owner,configured),
                Row(1,"turn_context",new{model="gpt-6-astra",effort="ultra",service_tier=overridden}),Count(2,100,100),
                Turn(3),Count(4,200,100)]));
            Check(equivalent.Events[1].Pricing?.RequestedServiceTier==configured,"等价档位的明确context不应抹掉可靠默认设置");
        }
        var effortConflict=await Scan(("effort-conflict",[Meta(owner),Settings(0,owner,"priority"),
            Row(1,"turn_context",new{model="gpt-6-astra",effort="medium",reasoning_effort="ultra"}),Count(2,100,100)]));
        Check(effortConflict.Events.Single().Pricing is {RequestedServiceTier:"priority",ReasoningEffort:null},"思考强度字段冲突不应伪造强度或抹掉独立档位证据");
        foreach(var broken in new[]{Row(3,"event_msg",new{type="thread_settings_applied",thread_id=owner,thread_settings=new{service_tier="default"}}),
            Row(3,"turn_context",new{effort="medium"})})
        {
            var invalidModel=await Scan(("invalid-model",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),broken,Count(4,200,100)]));
            Check(invalidModel.Events[1].Pricing?.RequestedServiceTier is null,"新的模式记录缺失模型时不得继续冒用旧priority");
            Check(invalidModel.Total.Total==200&&invalidModel.Events[1].Model=="gpt-6-astra","未知模式边界不能修改原计数和模型字段");
        }
        var turnConflict=await Scan(("turn-conflict",[Meta(owner),Settings(0,owner,"priority"),
            Row(1,"turn_context",new{model="gpt-6-astra",effort="ultra",service_tier="priority"}),
            Row(1,"turn_context",new{model="gpt-6-astra",effort="medium",service_tier="default"}),Count(2,100,100)]));
        Check(turnConflict.Events.Single().Pricing is {RequestedServiceTier:null,ReasoningEffort:null,ServiceTierEvidence:"unknown"},"同一有效时刻的真实冲突必须降为未知");

        var multiple=await Scan(("z-settings",[Meta(owner),Settings(0,owner,"priority")]),
            ("a-counts",[Meta(owner),Turn(1),Count(2,100,100)]),
            ("other-owner",[Meta("another-owner"),Turn(1),Count(2,100,100)]));
        Check(multiple.Events.Single(e=>e.Session==owner).Pricing?.RequestedServiceTier=="priority","同会话多文件不能取决于目录排列顺序");
        Check(multiple.Events.Single(e=>e.Session=="another-owner").Pricing?.RequestedServiceTier is null,"档位不能跨任务串用");
        var modelSwitch=await Scan(("model-switch",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),
            Turn(3,"medium","gpt-5.6-sol"),Count(4,200,100)]));
        Check(modelSwitch.Events[1].Model=="gpt-5.6-sol"&&modelSwitch.Events[1].Pricing?.RequestedServiceTier is null,"设置模型不匹配不能套上另一模型的Fast证据");

        // Parent replay precedes the child boundary. Its explicit settings name the parent and must be ignored.
        var parent="01990000-0000-7000-8000-000000000001";var child="01990000-0001-7000-8000-000000000001";
        var childTurn="01990000-0002-7000-8000-000000000001";
        var fork=await Scan(("fork",[Meta(child,parent),Settings(0,parent,"priority"),Turn(1),Count(2,100,100),
            Row(3,"event_msg",new{type="task_started",turn_id=childTurn}),Turn(4,"medium"),Count(5,200,100),
            Settings(6,child,"default"),Turn(7,"ultra"),Count(8,300,100),
            Settings(0,parent,"priority"),Turn(1,"ultra")]));
        Check(fork.Events.Count==2&&fork.Events[0].Pricing is {RequestedServiceTier:null,ReasoningEffort:"medium"},"fork父模式不能经sidecar物理位置流入child");
        Check(fork.Events[1].Pricing is {RequestedServiceTier:"default",ReasoningEffort:"ultra"},"child明确设置应正常生效");
        Check(fork.Events.All(e=>e.Session==child)&&fork.Total.Total==200,"模式解析不能改变fork去重和子任务Token");

        // Foreign sidecars must suppress their model-only counter copies as well.
        foreach(var foreign in new[]{Row(3,"turn_context",new{model="gpt-6-astra",thread_id="other-owner"}),
            Row(3,"turn_context",new{model="gpt-6-astra",thread_id="other-owner",service_tier="priority"})})
        {
            var protectedMode=await Scan(("foreign-copy",[Meta(owner),Settings(0,owner,"priority"),
                Row(1,"turn_context",new{model="gpt-6-astra",thread_id=owner,service_tier="default"}),Count(2,100,100),
                Row(3,"turn_context",new{model="gpt-6-astra"}),Count(4,200,100),foreign]));
            Check(protectedMode.Events.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="standard"),"他人context不能经紧凑副本继承本线程Fast默认值");
            var ownAtSameTime=await Scan(("foreign-copy",[Meta(owner),Settings(0,owner,"priority"),
                Row(1,"turn_context",new{model="gpt-6-astra",service_tier="default"}),Count(2,100,100),
                Row(3,"turn_context",new{model="gpt-6-astra"}),Count(4,200,100),foreign,
                Row(3,"turn_context",new{model="gpt-6-astra",thread_id=owner,service_tier="priority"})]));
            Check(Pricing.ResolveMode(ownAtSameTime.Events[1].Pricing).Tier=="fast","同时间合法的本线程context仍应覆盖");
        }
        var lostMode=await Scan(("lost-sidecar",[Meta(owner),Settings(0,owner,"priority"),
            Row(1,"turn_context",new{model="gpt-6-astra"}),Count(2,100,100)]));
        Check(Pricing.ResolveMode(lostMode.Events.Single().Pricing).Tier=="unknown","无原始侧边证据的旧compact不能证明线程模式归属");
        foreach(var stamp in new string?[]{null,"not-a-timestamp"})
        {
            var untimed=JsonSerializer.Serialize(new{timestamp=stamp,type="event_msg",payload=new{type="thread_settings_applied",thread_id=owner,
                thread_settings=new{model="gpt-6-astra",service_tier="default"}}});
            var unknownTime=await Scan(("untimed",[Meta(owner),Settings(0,owner,"priority"),Turn(1),Count(2,100,100),untimed,Count(4,200,100)]));
            Check(unknownTime.Events.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="unknown")&&unknownTime.Total.Total==200,"未知生效时刻不能沿用旧Fast或丢Token");
        }

        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{await new HistoryScanner().ScanAsync(Home,canceled.Token,indexedRecords:new Dictionary<string,IReadOnlyList<string>>{{PathFor("canceled"),switching}});throw new InvalidOperationException("已取消扫描不应产出结果");}
        catch(OperationCanceledException){}
    }
}
