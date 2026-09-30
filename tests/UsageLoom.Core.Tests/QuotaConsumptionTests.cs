using UsageLoom.Core;

static class QuotaConsumptionTests
{
    private static void Check(bool value){if(!value)throw new Exception("Quota consumption assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var local=new DateTime(2026,9,30,10,0,0);
        var offset=TimeZoneInfo.Local.GetUtcOffset(local);
        DateTimeOffset At(int hour,int minute=0)=>new(2026,9,30,hour,minute,0,offset);
        var day=new DateOnly(2026,9,30);
        HistoryTrendBucket Bucket(int? hour=null)=>new(day,day,hour is {} h?$"{h:00}:00":"09/30",0,0,0){Hour=hour};
        QuotaObservation Codex(int hour,int minute,double used,DateTimeOffset reset,string account="a",bool barrier=false)=>
            new(At(hour,minute),account,"pro",Pricing.CatalogVersion,[new("codex:five","5h",used,300,reset)],barrier);
        ClaudeQuotaObservation Claude(int hour,int minute,double used,DateTimeOffset reset,string scope="a")=>
            new(scope,At(hour,minute),"five_hour",used,reset,"history");

        test("额度消耗：30 + 跨重置 50 = 80，旧周期尾部标部分缺失",() =>
        {
            var old=At(11);var next=old.AddHours(5);
            var rows=new[]{Codex(10,0,50,old),Codex(10,10,80,old),Codex(11,1,0,next),Codex(11,10,50,next)};
            var result=QuotaConsumption.Codex(rows,"a",300,[Bucket()])[0];
            Check(result.Points==80&&result.Missing.HasFlag(QuotaConsumptionMissing.ResetTail));
            var hourly=QuotaConsumption.Codex(rows,"a",300,[Bucket(10),Bucket(11)]);
            Check(hourly[0].Points==30&&hourly[1].Points==50&&hourly[1].Partial);
            var claudeOld=At(10,20);var claudeNext=claudeOld.AddHours(5);
            var claude=QuotaConsumption.Claude([Claude(10,0,50,claudeOld),Claude(10,10,80,claudeOld),
                Claude(10,21,0,claudeNext),Claude(10,30,50,claudeNext)],"a","five_hour",[Bucket()])[0];
            Check(claude.Points==80&&claude.Missing.HasFlag(QuotaConsumptionMissing.ResetTail));
        });
        test("额度消耗：不相接重置不以零起算，周期内回退不算负量",() =>
        {
            var old=At(11);var next=At(16,30);
            var reset=QuotaConsumption.Codex([Codex(10,0,50,old),Codex(11,1,50,next),Codex(11,10,60,next)],"a",300,[Bucket()])[0];
            if(reset.Points!=10||!reset.Missing.HasFlag(QuotaConsumptionMissing.ResetUnknown))
                throw new Exception($"Nonadjacent reset points={reset.Points}, missing={reset.Missing}");
            var regression=QuotaConsumption.Codex([Codex(10,0,50,old),Codex(10,5,40,old),Codex(10,10,70,old)],"a",300,[Bucket()])[0];
            if(regression.Points!=0||!regression.Missing.HasFlag(QuotaConsumptionMissing.Barrier))
                throw new Exception($"Regression points={regression.Points}, missing={regression.Missing}");
        });
        test("额度消耗：账号与 Scope 切换是屏障，Claude 超 15 分钟断档不接续",() =>
        {
            var reset=At(15);
            var accounts=QuotaConsumption.Codex([Codex(10,0,10,reset),Codex(10,5,20,reset,"b"),Codex(10,10,30,reset)],"a",300,[Bucket()])[0];
            Check(accounts.Points==0&&accounts.Missing.HasFlag(QuotaConsumptionMissing.Barrier));
            var scopes=QuotaConsumption.Claude([Claude(10,0,10,reset),Claude(10,5,20,reset,"b"),Claude(10,10,30,reset)],"a","five_hour",[Bucket()])[0];
            Check(scopes.Points==0&&scopes.Missing.HasFlag(QuotaConsumptionMissing.Barrier));
            var gap=QuotaConsumption.Claude([Claude(10,0,10,reset),Claude(10,16,50,reset)],"a","five_hour",[Bucket()])[0];
            Check(gap.Points==0&&gap.Missing.HasFlag(QuotaConsumptionMissing.Gap));
        });
        test("额度消耗：空桶为 null、同量为零、跨桶增量归后一个观测",() =>
        {
            var reset=At(15);
            var rows=new[]{Codex(10,40,10,reset),Codex(10,50,10,reset),Codex(11,5,30,reset)};
            var result=QuotaConsumption.Codex(rows,"a",300,[Bucket(9),Bucket(10),Bucket(11),Bucket(12)]);
            Check(result[0].Points is null&&result[1].Points==0&&result[2].Points==20&&result[3].Points is null);
            Check(QuotaConsumption.Claude([Claude(10,0,10,reset)],"a","unknown",[Bucket()])[0].Points is null);
        });
    }
}
