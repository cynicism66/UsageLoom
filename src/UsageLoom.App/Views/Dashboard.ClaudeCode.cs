using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UsageLoom.Core;
using Windows.Foundation;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private int claudeCodeDays=7,claudeCodePage;
    private bool claudeHistoryExpanded;
    private UIElement ClaudeCodePanel()
    {
        var snapshot=app.ClaudeCode;var panel=new StackPanel{Spacing=16};
        AutomationProperties.SetAutomationId(panel,"claude-code-statistics");
        var toolbar=new StackPanel{Spacing=10};toolbar.Children.Add(ClaudeText(L10n.T("claude.code.title"),20));
        var choices=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var days in new[]{1,7,30})
        {
            var button=new ToggleButton{Content=L10n.T(days==1?"claude.code.today":days==7?"claude.code.week":"claude.code.month"),IsChecked=claudeCodeDays==days,Padding=new Thickness(12,7,12,7)};
            button.Click+=(_,_)=>{claudeCodeDays=days;claudeCodePage=0;renderedFilter=null;RenderStats();};choices.Children.Add(button);
        }
        toolbar.Children.Add(choices);toolbar.Children.Add(ClaudeText(L10n.T("claude.code.scope")));
        toolbar.Children.Add(Button(L10n.T("claude.code.refresh"),()=>app.RefreshClaudeAsync(true)));panel.Children.Add(Card(toolbar));
        if(snapshot.Status is not "ready")
        {
            var notice=new StackPanel{Spacing=10};notice.Children.Add(ClaudeText(L10n.T("claude.code.status."+snapshot.Status),15));
            notice.Children.Add(Button(L10n.T("sDF3D58C7D84B"),()=>{Navigate("settings");return Task.CompletedTask;}));panel.Children.Add(Card(notice));
        }
        if(snapshot.Rows.Count>0)
        {
            var start=DateTime.Today.AddDays(1-claudeCodeDays);var now=DateTimeOffset.Now;
            var rows=snapshot.Rows.Where(r=>r.At.LocalDateTime>=start&&r.At<=now).ToArray();
            var metrics=new StackPanel{Spacing=10};
            metrics.Children.Add(ClaudeText(L10n.F("claude.code.total",UsageNumbers.Compact(rows.Sum(r=>r.Total)),rows.Length,rows.Select(r=>r.Session).Distinct().Count()),22));
            metrics.Children.Add(ClaudeText(L10n.F("claude.code.counters",rows.Sum(r=>r.Input),rows.Sum(r=>r.Output),rows.Sum(r=>r.CacheRead),rows.Sum(r=>r.CacheWrite))));
            ToolTipService.SetToolTip(metrics,L10n.T("claude.code.definition"));panel.Children.Add(Card(metrics));
            if(selectedPage=="overview")
            {
                var chart=new StackPanel{Spacing=10};chart.Children.Add(ClaudeText(L10n.T("claude.code.trend"),18));
                chart.Children.Add(ClaudeTokenChart(rows,start,claudeCodeDays));chart.Children.Add(ClaudeText(L10n.T("claude.code.chartNotice")));
                panel.Children.Add(Card(chart));
            }
            else
            {
                var table=new StackPanel{Spacing=12};var sessions=selectedPage=="sessions";
                table.Children.Add(ClaudeText(L10n.T(sessions?"claude.code.sessions":"claude.code.models"),18));
                var groups=rows.GroupBy(r=>sessions?r.Session:r.Model).OrderByDescending(g=>g.Sum(r=>r.Total)).ToArray();
                var pages=Math.Max(1,(groups.Length+49)/50);claudeCodePage=Math.Clamp(claudeCodePage,0,pages-1);
                foreach(var group in groups.Skip(claudeCodePage*50).Take(50))
                {
                    var name=sessions?"Session · "+group.Key[..12]:group.Key;
                    var text=ClaudeText(name+"\n"+L10n.F("claude.code.group",group.Sum(r=>r.Total),group.Count()),15);
                    ToolTipService.SetToolTip(text,L10n.F("claude.code.counters",group.Sum(r=>r.Input),group.Sum(r=>r.Output),group.Sum(r=>r.CacheRead),group.Sum(r=>r.CacheWrite)));
                    table.Children.Add(text);
                }
                if(groups.Length==0)table.Children.Add(ClaudeText(L10n.T("claude.code.rangeEmpty")));
                if(pages>1)
                {
                    var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
                    var previous=Button("‹",()=>{claudeCodePage--;renderedFilter=null;RenderStats();return Task.CompletedTask;});previous.IsEnabled=claudeCodePage>0;
                    var next=Button("›",()=>{claudeCodePage++;renderedFilter=null;RenderStats();return Task.CompletedTask;});next.IsEnabled=claudeCodePage<pages-1;
                    paging.Children.Add(previous);paging.Children.Add(ClaudeText($"{claudeCodePage+1} / {pages}"));paging.Children.Add(next);table.Children.Add(paging);
                }
                panel.Children.Add(Card(table));
                if(!sessions)
                {
                    var projects=new StackPanel{Spacing=10};projects.Children.Add(ClaudeText(L10n.T("claude.code.projects"),18));
                    foreach(var group in rows.GroupBy(r=>r.Project).OrderByDescending(g=>g.Sum(r=>r.Total)).Take(50))
                        projects.Children.Add(ClaudeText(group.Key+" · "+L10n.F("claude.code.group",group.Sum(r=>r.Total),group.Count())));
                    panel.Children.Add(Card(projects));
                }
            }
            panel.Children.Add(ClaudeText(L10n.F("claude.code.quality",snapshot.Files,snapshot.Skipped)));
        }
        if(selectedPage=="overview")
        {
            // Keep quota evidence accessible, but do not compete with actual Token activity.
            var history=new Expander{Header=L10n.T("claude.code.quotaHistory"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
            history.Expanding+=(_,_)=>{claudeHistoryExpanded=true;if(history.Content is null)history.Content=ClaudeHistoryPanel();};
            history.Collapsed+=(_,_)=>claudeHistoryExpanded=false;
            if(claudeHistoryExpanded){history.Content=ClaudeHistoryPanel();history.IsExpanded=true;}
            panel.Children.Add(StableExpander.Configure(history));
        }
        return panel;
    }

    private FrameworkElement ClaudeTokenChart(IReadOnlyList<ClaudeCodeUsage> rows,DateTime start,int days)
    {
        var hourly=days==1;var count=hourly?DateTime.Now.Hour+1:days;
        var values=new long[count];
        foreach(var row in rows){var index=hourly?row.At.LocalDateTime.Hour:(row.At.LocalDateTime.Date-start).Days;if(index>=0&&index<count)values[index]+=row.Total;}
        var canvas=new Canvas{Height=260,HorizontalAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetAutomationId(canvas,"claude-token-chart");
        void Draw(double width)
        {
            canvas.Children.Clear();if(width<100)return;
            const double left=52,top=24,height=182;var plotWidth=Math.Max(1,width-left-16);var maximum=Math.Max(1d,values.Max()*1.12);
            foreach(var fraction in new[]{0d,.5,1d})
            {
                var y=top+(1-fraction)*height;var label=ClaudeText(UsageNumbers.Compact(maximum*fraction),10);Canvas.SetTop(label,y-7);canvas.Children.Add(label);
                canvas.Children.Add(new Line{X1=left,X2=width-16,Y1=y,Y2=y,Stroke=new SolidColorBrush(ColorHelper.FromArgb(60,140,145,160)),StrokeThickness=1});
            }
            var points=values.Select((v,i)=>new Point(left+(count==1?plotWidth/2:i*plotWidth/(count-1)),top+(1-v/maximum)*height)).ToArray();
            var line=new Polyline{Stroke=accent,StrokeThickness=2.5};foreach(var p in points)line.Points.Add(p);
            var fill=new Polygon{Fill=new SolidColorBrush(ColorHelper.FromArgb(38,163,138,245))};fill.Points.Add(new(points[0].X,top+height));foreach(var p in points)fill.Points.Add(p);fill.Points.Add(new(points[^1].X,top+height));
            canvas.Children.Add(fill);canvas.Children.Add(line);
            var step=Math.Max(1,(int)Math.Ceiling(count/Math.Max(2,width/100)));
            for(var i=0;i<count;i++)
            {
                var label=hourly?$"{i:00}:00":start.AddDays(i).ToString("MM-dd");
                var dot=new Ellipse{Width=8,Height=8,Fill=accent};Canvas.SetLeft(dot,points[i].X-4);Canvas.SetTop(dot,points[i].Y-4);ToolTipService.SetToolTip(dot,$"{label} · {values[i]:N0} Token");canvas.Children.Add(dot);
                if(i%step==0||i==count-1){var text=ClaudeText(label+"\n"+UsageNumbers.Compact(values[i]),10);text.Width=80;text.TextAlignment=TextAlignment.Center;Canvas.SetLeft(text,Math.Clamp(points[i].X-40,0,width-80));Canvas.SetTop(text,216);canvas.Children.Add(text);}
            }
        }
        canvas.SizeChanged+=(_,e)=>Draw(e.NewSize.Width);return canvas;
    }
}
