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
            var estimate=ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),code,now);
            Check(estimate.Ready&&estimate.Intervals==3&&Math.Abs(estimate.PercentagePoints-8)<.001);
            Check(estimate.LocalTokens==800_000&&estimate.ProjectedTokens==10_000_000);
            Check(estimate.HasApiEquivalent&&estimate.ProjectedApiEquivalent>0&&estimate.ResetsAt==reset);
        });
        test("Claude 每周参考：连续小幅百分比增长合并成采样段",() =>
        {
            var small=Enumerable.Range(0,10).Select(i=>Point(-20+i*2,10+i*.5)).ToArray();
            var smallRows=Enumerable.Range(0,10).Select(i=>Row(-19+i*2,50_000,"small-"+i)).ToArray();
            var quota=Quota(small) with{Windows=[new("seven_day",15,reset)]};
            var estimate=ClaudeWeeklyCapacityEstimator.Estimate(quota,new("ready",smallRows),now);
            Check(estimate.Ready&&estimate.Intervals>=2&&Math.Abs(estimate.PercentagePoints-5)<.001);
            Check(estimate.LocalTokens==500_000&&estimate.ProjectedTokens==10_000_000);
        });
        test("Claude 每周参考：缺少当前重置时间、过期快照或日志时不估算",() =>
        {
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history) with{Windows=[new("seven_day",18,null)]},code,now).Status=="resetUnavailable");
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history) with{ObservedAt=now.AddMinutes(-20)},code,now).Status=="quotaUnavailable");
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),code with{Status="partial",Skipped=1},now).Status=="partialLogs");
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),ClaudeCodeSnapshot.Empty("missing"),now).Status=="codeUnavailable");
        });
        test("Claude 每周参考：其他渠道、异常比例和来源冲突不生成容量",() =>
        {
            var other=code with{Rows=[Row(-16,300_000),Row(-2,100_000,"three")]};
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),other,now).Status=="otherUsage");
            var unstable=code with{Rows=[Row(-16,30_000),Row(-8,400_000,"two"),Row(-2,100_000,"three")]};
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),unstable,now).Status=="unstable");
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota([Point(-20,10,"b"),Point(-12,13,"b"),history[2]]),code,now).Status=="insufficient");
            var conflict=history.Append(Point(-12,14)).ToArray();
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(conflict),code,now).Status=="insufficient");
            var foreignReset=new[]{history[0],history[1] with{ResetsAt=reset.AddMinutes(-5)},history[2]};
            Check(ClaudeWeeklyCapacityEstimator.Estimate(Quota(foreignReset),code,now).Status=="insufficient");
        });
        test("Claude 每周参考：不以未知模型假冒 API 价值",() =>
        {
            var unknown=code with{Rows=code.Rows.Select(r=>r with{Model="unknown"}).ToArray()};
            var estimate=ClaudeWeeklyCapacityEstimator.Estimate(Quota(history),unknown,now);
            Check(estimate.Ready&&estimate.ProjectedTokens==10_000_000&&!estimate.HasApiEquivalent&&estimate.ProjectedApiEquivalent==0);
        });
    }
}
