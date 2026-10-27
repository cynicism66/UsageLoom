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
        test("托盘来源：Codex 过期、停用和身份不确定均无旧数字",() =>
        {
            Check(TrayQuotaSelection.Select("codex",true,true,codex with{Fresh=false},claude,now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("codex",false,true,codex,claude,now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,true,codex,claude with{Scope=null},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,true,codex,claude with{ObservedAt=null},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,true,codex,claude with{ObservedAt=now.AddMinutes(1)},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("claude",true,false,codex,claude with{ObservedAt=now.AddMinutes(-16)},now).DisplayRemaining is null);
            Check(TrayQuotaSelection.Select("tightest",false,false,codex,claude,now).DisplayRemaining is null);
            var noReset=codex with{Windows=[new("codex:five","5h",81,300,null)]};
            var partial=TrayQuotaSelection.Select("codex",true,true,noReset,claude,now);
            Check(partial.DisplayRemaining==19&&partial.FiveHour?.Pace==QuotaPaceKind.Unavailable);
        });
        test("托盘来源：Claude 旧快照保留最后数值并标记，过了重置时间按满额",() =>
        {
            var fresh=TrayQuotaSelection.Select("claude",true,true,codex,claude with{ObservedAt=now.AddMinutes(-15)},now);
            Check(fresh.DisplayRemaining==8&&!fresh.DisplayStale&&fresh.StaleSince is null);
            var observed=now.AddMinutes(-16);
            var stale=TrayQuotaSelection.Select("claude",true,true,codex,claude with{ObservedAt=observed},now);
            Check(stale.DisplayRemaining==8&&stale.DisplayStale&&stale.StaleSince==observed);
            Check(stale.FiveHour?.Pace==QuotaPaceKind.Unavailable&&stale.Weekly?.Reset==now.AddDays(3));
            // A fresh snapshot whose 5-hour window already reset: that window is full, the weekly one decides.
            var reset=claude with{Windows=[new("five_hour",92,now.AddMinutes(-1)),new("seven_day",30,now.AddDays(3))]};
            var afterReset=TrayQuotaSelection.Select("claude",true,true,codex,reset,now);
            Check(afterReset.FiveHour is {Remaining:100,Reset:null,Pace:QuotaPaceKind.Unavailable}&&afterReset.DisplayRemaining==70&&!afterReset.DisplayStale);
            var dayOld=claude with{ObservedAt=now.AddDays(-1),Windows=[new("five_hour",92,now.AddHours(-20)),new("seven_day",30,now.AddDays(3))]};
            var old=TrayQuotaSelection.Select("claude",true,true,codex,dayOld,now);
            Check(old.FiveHour?.Remaining==100&&old.DisplayRemaining==70&&old.DisplayStale);
            var allReset=TrayQuotaSelection.Select("claude",true,true,codex,dayOld with{Windows=[new("five_hour",92,now.AddHours(-20)),new("seven_day",30,now.AddHours(-1))]},now);
            Check(allReset.DisplayRemaining==100&&allReset.DisplayStale);
            Check(TrayQuotaSelection.Select("tightest",true,true,codex,claude with{ObservedAt=observed},now) is {Source:"claude",DisplayStale:true});
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
                var stale=TrayIconRaster.Render(size,19,stale:true);
                Check(!stale.Bgra.SequenceEqual(active.Bgra)&&stale.Mask.SequenceEqual(active.Mask));
                // Dimmed: no pixel of the stale icon is brighter than the matching fresh one.
                for(var i=0;i<stale.Bgra.Length;i+=4)Check(stale.Bgra[i]+stale.Bgra[i+1]+stale.Bgra[i+2]<=active.Bgra[i]+active.Bgra[i+1]+active.Bgra[i+2]);
                Check(TrayIconRaster.Render(size,null,stale:true).Bgra.SequenceEqual(unavailable.Bgra));
                var center=(size/2*size+size/2)*4;
                Check(unavailable.Bgra[center+3]==0);
            }
        });
        test("托盘图标：数字按尺寸放大且不压住圆环",() =>
        {
            // Text pixels are the only opaque near-white ones; measure their bounding box.
            (int Height,double Reach) Digits(int size,int value)
            {
                var icon=TrayIconRaster.Render(size,value);int top=size,bottom=-1;double reach=0;
                for(var y=0;y<size;y++)for(var x=0;x<size;x++)
                {
                    var o=(y*size+x)*4;
                    if(icon.Bgra[o+3]!=255||icon.Bgra[o]!=250||icon.Bgra[o+1]!=246||icon.Bgra[o+2]!=245)continue;
                    top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    reach=Math.Max(reach,Math.Sqrt(Math.Pow(x+.5-size/2d,2)+Math.Pow(y+.5-size/2d,2)));
                }
                return (bottom-top+1,reach);
            }
            foreach(var (size,twoDigits,full) in new[]{(16,7,5),(20,9,7),(24,10,7),(32,14,10)})
            {
                var two=Digits(size,96);var one=Digits(size,7);var hundred=Digits(size,100);
                Check(two.Height==twoDigits&&one.Height==twoDigits&&hundred.Height==full);
                // Every digit pixel stays inside the ring's inner edge.
                foreach(var item in new[]{two,one,hundred})Check(item.Reach<=size*.38+.3);
            }
        });
    }
}
