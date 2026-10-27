using UsageLoom.Core;

static class ClaudeWeeklyCapacityTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Claude weekly capacity assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var now=new DateTimeOffset(2026,9,23,12,0,0,TimeSpan.Zero);
        var reset=now.AddDays(2);
        ClaudeQuotaObservation Point(int minutes,double used,string scope="a",DateTimeOffset? resets=null)=>
            new(scope,now.AddMinutes(minutes),"seven_day",used,resets,"history");
        ClaudeCodeUsage Row(int minutes,long tokens,string id="one",string model="claude-sonnet-4-6")=>
            new(id,id,"session","project",model,now.AddMinutes(minutes),tokens,0,0,0);
        ClaudeQuotaSnapshot Quota(params ClaudeQuotaObservation[] history)=>
            new("snapshot",[new("seven_day",18,reset)],now,"a",["a"]){History=history};
        var history=new[]{Point(-20,10),Point(-12,13),Point(-4,17)};
        var code=new ClaudeCodeSnapshot("ready",[Row(-16,300_000),Row(-8,400_000,"two"),Row(-2,100_000,"three")]);

        test("Claude 每周参考：同源同周期连续样本与本机 Code 用量配对",() =>
        {
            var estimate=ClaudeCapacityEstimator.Estimate(Quota(history),code,now);
            Check(estimate.Ready&&estimate.Intervals==3&&Math.Abs(estimate.PercentagePoints-8)<.001);
            Check(estimate.LocalTokens==800_000&&estimate.ProjectedTokens==10_000_000);
            Check(estimate.HasApiEquivalent&&estimate.ProjectedApiEquivalent>0&&estimate.ResetsAt==reset);
        });
        test("Claude 每周参考：连续小幅百分比增长合并成采样段",() =>
        {
            var small=Enumerable.Range(0,10).Select(i=>Point(-20+i*2,10+i*.5)).ToArray();
            var smallRows=Enumerable.Range(0,10).Select(i=>Row(-19+i*2,50_000,"small-"+i)).ToArray();
            var quota=Quota(small) with{Windows=[new("seven_day",15,reset)]};
            var estimate=ClaudeCapacityEstimator.Estimate(quota,new("ready",smallRows),now);
            Check(estimate.Ready&&estimate.Intervals>=2&&Math.Abs(estimate.PercentagePoints-5)<.001);
            Check(estimate.LocalTokens==500_000&&estimate.ProjectedTokens==10_000_000);
        });
        test("Claude 每周参考：缺少当前重置时间、过期快照或日志时不估算",() =>
        {
            Check(ClaudeCapacityEstimator.Estimate(Quota(history) with{Windows=[new("seven_day",18,null)]},code,now).Status=="resetUnavailable");
            Check(ClaudeCapacityEstimator.Estimate(Quota(history) with{ObservedAt=now.AddMinutes(-20)},code,now).Status=="quotaUnavailable");
            Check(ClaudeCapacityEstimator.Estimate(Quota(history),code with{Status="partial",Skipped=1},now).Status=="partialLogs");
            Check(ClaudeCapacityEstimator.Estimate(Quota(history),ClaudeCodeSnapshot.Empty("missing"),now).Status=="codeUnavailable");
        });
        test("Claude 每周参考：其他渠道、异常比例和来源冲突不生成容量",() =>
        {
            var other=code with{Rows=[Row(-16,300_000),Row(-2,100_000,"three")]};
            Check(ClaudeCapacityEstimator.Estimate(Quota(history),other,now).Status=="otherUsage");
            var unstable=code with{Rows=[Row(-16,30_000),Row(-8,400_000,"two"),Row(-2,100_000,"three")]};
            Check(ClaudeCapacityEstimator.Estimate(Quota(history),unstable,now).Status=="unstable");
            Check(ClaudeCapacityEstimator.Estimate(Quota([Point(-20,10,"b"),Point(-12,13,"b"),history[2]]),code,now).Status=="insufficient");
            var conflict=history.Append(Point(-12,14)).ToArray();
            Check(ClaudeCapacityEstimator.Estimate(Quota(conflict),code,now).Status=="insufficient");
            var foreignReset=new[]{history[0],history[1] with{ResetsAt=reset.AddMinutes(-5)},history[2]};
            Check(ClaudeCapacityEstimator.Estimate(Quota(foreignReset),code,now).Status=="insufficient");
        });
        test("Claude 5 小时参考：只用 5 小时窗口的同周期样本，与每周窗口互不混用",() =>
        {
            var fiveReset=now.AddHours(2);
            ClaudeQuotaObservation Five(int minutes,double used,DateTimeOffset? resets=null)=>
                new("a",now.AddMinutes(minutes),"five_hour",used,resets??fiveReset,"cache");
            var fiveHistory=new[]{Five(-20,20),Five(-12,32),Five(-4,48)};
            var quota=new ClaudeQuotaSnapshot("snapshot",[new("five_hour",52,fiveReset),new("seven_day",18,reset)],now,"a",["a"])
                {History=[..fiveHistory,..history]};
            var five=ClaudeCapacityEstimator.Estimate(quota,code,now,"five_hour");
            Check(five.Window=="five_hour"&&five.Ready&&five.Intervals==3&&Math.Abs(five.PercentagePoints-32)<.001);
            Check(five.LocalTokens==800_000&&five.ProjectedTokens==2_500_000&&five.ResetsAt==fiveReset);
            // The weekly estimate over the same snapshot still reads only weekly points.
            var week=ClaudeCapacityEstimator.Estimate(quota,code,now);
            Check(week.Window=="seven_day"&&week.Ready&&Math.Abs(week.PercentagePoints-8)<.001&&week.ProjectedTokens==10_000_000);
            // A 5-hour reset more than 5 hours away is not a current 5-hour cycle.
            Check(ClaudeCapacityEstimator.Estimate(quota with{Windows=[new("five_hour",48,now.AddHours(6))]},code,now,"five_hour").Status=="resetUnavailable");
            // Points from the previous 5-hour cycle are excluded by the cycle start.
            var previous=new[]{Five(-400,10,now.AddMinutes(-60)),Five(-392,40,now.AddMinutes(-60)),Five(-384,70,now.AddMinutes(-60))};
            Check(ClaudeCapacityEstimator.Estimate(quota with{History=previous},code,now,"five_hour").Status=="insufficient");
            try{ClaudeCapacityEstimator.Estimate(quota,code,now,"monthly");throw new Exception("unknown window accepted");}catch(ArgumentOutOfRangeException){}
        });
        test("Claude 每周参考：不以未知模型假冒 API 价值",() =>
        {
            var unknown=code with{Rows=code.Rows.Select(r=>r with{Model="unknown"}).ToArray()};
            var estimate=ClaudeCapacityEstimator.Estimate(Quota(history),unknown,now);
            Check(estimate.Ready&&estimate.ProjectedTokens==10_000_000&&!estimate.HasApiEquivalent&&estimate.ProjectedApiEquivalent==0);
        });
    }
}
