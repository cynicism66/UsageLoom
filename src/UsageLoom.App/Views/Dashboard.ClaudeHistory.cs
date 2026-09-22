using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private int claudeHistoryDays=7;
    private ClaudeQuotaSnapshot? renderedClaudeStatistics;
    private ClaudeCodeSnapshot? renderedClaudeCode;
    private string? renderedClaudeDescription,renderedClaudeHistoryError;
    private bool ClaudeHistoryViewChanged()
    {
        var current=app.ClaudeQuota;var previous=renderedClaudeStatistics;
        return !ReferenceEquals(renderedClaudeCode,app.ClaudeCode)||previous is null||previous.Status!=current.Status||previous.Scope!=current.Scope||previous.ObservedAt!=current.ObservedAt||
            previous.HistoryOnly!=current.HistoryOnly||!previous.Windows.SequenceEqual(current.Windows)||!previous.History.SequenceEqual(current.History)||
            renderedClaudeDescription!=current.Describe(DateTimeOffset.Now)||renderedClaudeHistoryError!=app.ClaudeHistoryError;
    }

    private UIElement ClaudeHistoryPanel()
    {
        var snapshot=app.ClaudeQuota;var now=DateTimeOffset.Now;
        var panel=new StackPanel{Spacing=16};AutomationProperties.SetAutomationId(panel,"claude-history");
        var toolbar=new StackPanel{Spacing=10};
        toolbar.Children.Add(ClaudeText(L10n.T("claude.historyTitle"),20));
        var ranges=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var days in new[]{1,7,30})
        {
            var choice=new ToggleButton{Content=L10n.F("claude.historyDays",days),IsChecked=claudeHistoryDays==days,Padding=new Thickness(12,7,12,7)};
            AutomationProperties.SetAutomationId(choice,"claude-history-days-"+days);
            choice.Click+=(_,_)=>{claudeHistoryDays=days;renderedFilter=null;RenderStats();};ranges.Children.Add(choice);
        }
        toolbar.Children.Add(ranges);
        toolbar.Children.Add(ClaudeText(L10n.T("claude.historyScope")));
        toolbar.Children.Add(Button(L10n.T("claude.detect"),()=>app.RefreshClaudeAsync(true)));
        panel.Children.Add(Card(toolbar));
        if(app.ClaudeHistoryError is {} error)panel.Children.Add(ClaudeText(error,14));
        if(snapshot.Status!="snapshot"||snapshot.Scope is null)
        {
            var empty=new StackPanel{Spacing=10};empty.Children.Add(ClaudeText(snapshot.Describe(now),16));
            empty.Children.Add(ClaudeText(L10n.T("claude.historyUnavailable")));
            empty.Children.Add(Button(L10n.T("sDF3D58C7D84B"),()=>{Navigate("settings");return Task.CompletedTask;}));
            panel.Children.Add(Card(empty));return panel;
        }
        var points=ClaudeQuotaHistory.Normalize(ClaudeQuotaHistory.Observations(snapshot).Where(p=>p.Scope==snapshot.Scope&&p.At>=now.AddDays(-claudeHistoryDays)&&p.At<=now));
        var times=points.Select(p=>p.At).Distinct().Order().ToArray();
        var quality=new StackPanel{Spacing=8};
        quality.Children.Add(ClaudeText(L10n.F("claude.historyCount",times.Length),18));
        quality.Children.Add(ClaudeText(times.Length==0?L10n.T("claude.historyEmpty"):L10n.F("claude.historySpan",times[0].ToLocalTime(),times[^1].ToLocalTime())));
        quality.Children.Add(ClaudeText(snapshot.Describe(now)+" · "+snapshot.TimestampText));
        quality.Children.Add(ClaudeText(L10n.F("claude.historySource",snapshot.Scope)));
        panel.Children.Add(Card(quality));
        if(times.Length==0)return panel;
        var charts=new List<UIElement>();
        foreach(var key in new[]{"five_hour","seven_day"})
        {
            var steps=ClaudeQuotaHistory.Series(points,snapshot.Scope,key,now.AddDays(-claudeHistoryDays),now);
            var chart=new StackPanel{Spacing=10};chart.Children.Add(ClaudeText(L10n.T("claude."+key)+" · "+L10n.T("claude.usedPercent"),17));
            if(steps.Count==0)chart.Children.Add(ClaudeText(L10n.T("claude.historyEmpty")));
            else
            {
                chart.Children.Add(ClaudeHistoryChart(steps));
                var pairs=steps.Count(s=>s.Connected);var delta=pairs>0?steps.Sum(s=>s.Increase??0).ToString("0.#"):"—";
                var summary=ClaudeText(L10n.F("claude.historyIncrease",delta,pairs));
                ToolTipService.SetToolTip(summary,L10n.F("claude.historyQuality",steps.Count(s=>s.Point.ResetsAt is null),steps.Count(s=>s.Point.Used is null),steps.Skip(1).Count(s=>!s.Connected))+"\n"+L10n.T("claude.historyLegend"));
                chart.Children.Add(summary);
                if(steps.Count>600)chart.Children.Add(ClaudeText(L10n.T("claude.historyChartLimit")));
                var cycles=new StackPanel{Spacing=8};
                foreach(var cycle in steps.Where(s=>s.Point.ResetsAt is not null&&s.Point.At<s.Point.ResetsAt).GroupBy(s=>s.Point.ResetsAt!.Value).OrderByDescending(g=>g.Key).Take(6))
                {
                    var cyclePairs=cycle.Count(s=>s.Connected);
                    cycles.Children.Add(ClaudeText(L10n.F("claude.historyCycle",cycle.Key.ToLocalTime(),cycle.Count(),cyclePairs>0?cycle.Sum(s=>s.Increase??0).ToString("0.#"):"—")));
                }
                if(cycles.Children.Count>0)chart.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("claude.historyCycles"),Content=cycles,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
            }
            charts.Add(Card(chart));
        }
        panel.Children.Add(ResponsiveCards(charts,2,360));
        var details=new StackPanel{Spacing=8};
        details.Children.Add(ClaudeText(L10n.T("claude.historyRecentNotice")));
        foreach(var p in points.OrderByDescending(p=>p.At).Take(20))
            details.Children.Add(ClaudeText(L10n.F("claude.historyRow",p.At.ToLocalTime(),L10n.T("claude."+p.Window),p.Used is {} u?u.ToString("0.#")+"%":L10n.T("claude.historyConflict"),L10n.T("claude.historyOrigin."+p.Origin))));
        panel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("claude.historyRecent"),Content=details,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        panel.Children.Add(ClaudeText(L10n.T("claude.historyCapabilities")));
        return panel;
    }

    private FrameworkElement ClaudeHistoryChart(IReadOnlyList<ClaudeQuotaStep> series)
    {
        var canvas=new Canvas{Height=210,HorizontalAlignment=HorizontalAlignment.Stretch};
        var points=series.TakeLast(600).ToArray();
        void Draw(double width)
        {
            canvas.Children.Clear();if(width<100)return;
            const double left=36,top=12,plotHeight=160;
            var plotWidth=Math.Max(1,width-left-12);
            foreach(var percentage in new[]{0,50,100})
            {
                var y=top+(100-percentage)*plotHeight/100;
                var label=ClaudeText(percentage+"%",10);Canvas.SetTop(label,y-7);canvas.Children.Add(label);
                canvas.Children.Add(new Line{X1=left,X2=left+plotWidth,Y1=y,Y2=y,Stroke=new SolidColorBrush(ColorHelper.FromArgb(60,140,145,160)),StrokeThickness=1});
            }
            var first=points[0].Point.At;var seconds=Math.Max(1,(points[^1].Point.At-first).TotalSeconds);
            double X(ClaudeQuotaObservation p)=>left+(points.Length==1?plotWidth/2:(p.At-first).TotalSeconds/seconds*plotWidth);
            double Y(double used)=>top+(100-used)*plotHeight/100;
            for(var i=0;i<points.Length;i++)
            {
                var s=points[i];var x=X(s.Point);
                if(s.Point.Used is not {} used)
                {
                    var mark=ClaudeText("!",14);Canvas.SetLeft(mark,x-3);Canvas.SetTop(mark,top+plotHeight/2);ToolTipService.SetToolTip(mark,L10n.T("claude.historyConflict"));canvas.Children.Add(mark);continue;
                }
                var y=Y(used);
                if(i>0&&s.Connected&&points[i-1].Point.Used is {} old)
                {
                    canvas.Children.Add(new Line{X1=X(points[i-1].Point),Y1=Y(old),X2=x,Y2=Y(old),Stroke=accent,StrokeThickness=2});
                    canvas.Children.Add(new Line{X1=x,Y1=Y(old),X2=x,Y2=y,Stroke=accent,StrokeThickness=2});
                }
                var dot=new Ellipse{Width=6,Height=6,Fill=accent};Canvas.SetLeft(dot,x-3);Canvas.SetTop(dot,y-3);
                ToolTipService.SetToolTip(dot,L10n.F("claude.historyPoint",s.Point.At.ToLocalTime(),used));canvas.Children.Add(dot);
            }
            var start=ClaudeText(first.ToLocalTime().ToString("MM-dd HH:mm"),10);Canvas.SetLeft(start,left);Canvas.SetTop(start,186);canvas.Children.Add(start);
            if(points.Length>1){var end=ClaudeText(points[^1].Point.At.ToLocalTime().ToString("MM-dd HH:mm"),10);end.Width=100;end.TextAlignment=TextAlignment.Right;Canvas.SetLeft(end,Math.Max(left,width-112));Canvas.SetTop(end,186);canvas.Children.Add(end);}
        }
        canvas.SizeChanged+=(_,e)=>Draw(e.NewSize.Width);
        AutomationProperties.SetName(canvas,L10n.T("claude.historyTitle"));
        return canvas;
    }
}
