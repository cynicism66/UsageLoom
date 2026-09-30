using UsageLoom.Core;

static class OpenRefreshPolicyTests
{
    private static void Check(bool value){if(!value)throw new Exception("Open refresh policy assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var now=new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero);
        test("打开小窗：60 秒边界、空快照和未来时间",() =>
        {
            var oldAttempt=now.AddMinutes(-10);
            Check(!OpenRefreshPolicy.ShouldRefresh(true,true,now.AddSeconds(-60),oldAttempt,TimeSpan.FromSeconds(30),now));
            Check(OpenRefreshPolicy.ShouldRefresh(true,true,now.AddMilliseconds(-60001),oldAttempt,TimeSpan.FromSeconds(30),now));
            Check(OpenRefreshPolicy.ShouldRefresh(true,true,null,oldAttempt,TimeSpan.FromSeconds(30),now));
            Check(OpenRefreshPolicy.ShouldRefresh(true,true,now.AddSeconds(1),oldAttempt,TimeSpan.FromSeconds(30),now));
            Check(OpenRefreshPolicy.ShouldRefresh(true,false,now,oldAttempt,TimeSpan.FromSeconds(30),now));
        });
        test("打开小窗：开关关闭和重试退避均阻止补读",() =>
        {
            Check(!OpenRefreshPolicy.ShouldRefresh(false,false,null,now.AddMinutes(-10),TimeSpan.Zero,now));
            Check(!OpenRefreshPolicy.ShouldRefresh(true,false,null,now.AddSeconds(-29),TimeSpan.FromSeconds(30),now));
            Check(OpenRefreshPolicy.ShouldRefresh(true,false,null,now.AddSeconds(-30),TimeSpan.FromSeconds(30),now));
        });
    }
}
