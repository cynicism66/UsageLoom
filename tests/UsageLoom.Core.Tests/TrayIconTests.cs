using UsageLoom.Core;

static class TrayIconTests
{
    private static void Check(bool value){if(!value)throw new Exception("Tray icon assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var now=new DateTimeOffset(2026,9,30,12,0,0,TimeSpan.Zero);
        var codex=new QuotaState([new("codex:five","5h",81,300,now.AddHours(2)),new("codex:weekly","week",50,10080,now.AddDays(3))],null,now,"ok",true,"account");
        var claude=new ClaudeQuotaSnapshot("snapshot",[new("five_hour",92,now.AddHours(2)),new("seven_day",30,now.AddDays(3))],now,"scope");
        test("托盘来源：默认 Codex、仅 Claude 与最紧张来源",() =>
        {
            Check(TrayQuotaSelection.Select("auto",true,true,codex,claude,now).Source=="codex");
            Check(TrayQuotaSelection.Select("auto",false,true,codex,claude,now).Source=="claude");
            var tight=TrayQuotaSelection.Select("tightest",true,true,codex,claude,now);
            Check(tight.Source=="claude"&&tight.DisplayRemaining==8&&tight.FiveHour?.Pace!=QuotaPaceKind.Unavailable);
            Check(TrayQuotaSelection.Normalize("unexpected")=="auto");
        });
        test("托盘来源：过期、停用和身份不确定均无旧数字",() =>
        {
            Check(TrayQuotaSelection.Select("codex",true,true,codex with{Fresh=false},claude,now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("codex",false,true,codex,claude,now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,true,codex,claude with{ObservedAt=now.AddMinutes(-16)},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,true,codex,claude with{Scope=null},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("tightest",false,false,codex,claude,now).DisplayRemaining is null);
            var noReset=codex with{Windows=[new("codex:five","5h",81,300,null)]};
            var partial=TrayQuotaSelection.Select("codex",true,true,noReset,claude,now);
            Check(partial.DisplayRemaining==19&&partial.FiveHour?.Pace==QuotaPaceKind.Unavailable);
        });
        test("托盘图标：颜色阈值、DPI 尺寸与灰环无数字",() =>
        {
            Check(TrayIconRaster.Tier(null)==TrayIconTier.Unavailable);
            Check(TrayIconRaster.Tier(10)==TrayIconTier.Red&&TrayIconRaster.Tier(11)==TrayIconTier.Orange);
            Check(TrayIconRaster.Tier(20)==TrayIconTier.Orange&&TrayIconRaster.Tier(21)==TrayIconTier.Normal);
            foreach(var (dpi,size) in new[]{(96u,16),(120u,20),(144u,24),(192u,32)})
            {
                Check(TrayIconRaster.SizeForDpi(dpi)==size);
                var active=TrayIconRaster.Render(size,19);
                var unavailable=TrayIconRaster.Render(size,null);
                Check(active.Bgra.Length==size*size*4&&active.Mask.Length==((size+15)/16)*2*size);
                Check(!active.Bgra.SequenceEqual(unavailable.Bgra));
                var center=(size/2*size+size/2)*4;
                Check(unavailable.Bgra[center+3]==0);
            }
        });
    }
}
