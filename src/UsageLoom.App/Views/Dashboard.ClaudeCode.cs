using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private int claudeCodeDays=7,claudeCodePage;
    private bool claudeHistoryExpanded;
    private UIElement ClaudeCodePanel()
    {
        var snapshot=app.ClaudeCode;var panel=new StackPanel{Spacing=16};
        AutomationProperties.SetAutomationId(panel,"claude-code-statistics");
        var toolbar=new StackPanel{Spacing=10};
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
        var start=DateTime.Today.AddDays(1-claudeCodeDays);var now=DateTimeOffset.Now;
        var rows=snapshot.Rows.Where(r=>r.At.LocalDateTime>=start&&r.At<=now).ToArray();
        if(selectedPage=="overview")
        {
            panel.Children.Add(ClaudeOverviewStatistics(rows,start));
        }
        else if(snapshot.Rows.Count>0)
        {
            var metrics=new StackPanel{Spacing=10};
            metrics.Children.Add(ClaudeText(L10n.F("claude.code.total",UsageNumbers.Compact(rows.Sum(r=>r.Total)),rows.Length,rows.Select(r=>r.Session).Distinct().Count()),22));
            metrics.Children.Add(ClaudeText(L10n.F("claude.code.counters",rows.Sum(r=>r.Input),rows.Sum(r=>r.Output),rows.Sum(r=>r.CacheRead),rows.Sum(r=>r.CacheWrite))));
            ToolTipService.SetToolTip(metrics,L10n.T("claude.code.definition"));panel.Children.Add(Card(metrics));
            if(selectedPage!="overview")
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

    private UIElement ClaudeOverviewStatistics(IReadOnlyList<ClaudeCodeUsage> rows,DateTime start)
    {
        var panel=new StackPanel{Spacing=16};
        var hasRows=rows.Count>0;
        var total=rows.Sum(r=>r.Total);
        var sessions=rows.Select(r=>r.Session).Distinct(StringComparer.Ordinal).Count();
        var cached=rows.Sum(r=>r.CacheRead+r.CacheWrite);
        var observed=hasRows?L10n.T("claude.code.observed"):L10n.T("claude.code.unobserved");
        var metricsCard=Card(ResponsiveCards(new UIElement[]{
            Metric(L10n.T("claude.code.metricTokens"),hasRows?total.ToString("N0"):"—",observed),
            Metric("Session",hasRows?sessions.ToString("N0"):"—",L10n.T("claude.code.sessionScope")),
            Metric(L10n.T("claude.code.metricResponses"),hasRows?rows.Count.ToString("N0"):"—",L10n.T("claude.code.responseScope")),
            Metric(L10n.T("claude.code.metricCache"),hasRows?cached.ToString("N0"):"—",L10n.T("claude.code.cacheScope"))},4,190));
        AutomationProperties.SetAutomationId(metricsCard,"claude-overview-metrics");panel.Children.Add(metricsCard);

        var buckets=new List<HistoryTrendBucket>();
        if(hasRows)
        {
            var hourly=claudeCodeDays==1;
            var count=hourly?DateTime.Now.Hour+1:claudeCodeDays;
            for(var i=0;i<count;i++)
            {
                var from=hourly?DateTime.Today.AddHours(i):start.AddDays(i);
                var through=from.Add(hourly?TimeSpan.FromHours(1):TimeSpan.FromDays(1));
                var matching=rows.Where(r=>r.At.LocalDateTime>=from&&r.At.LocalDateTime<through).ToArray();
                var day=DateOnly.FromDateTime(from);
                buckets.Add(new(day,day,hourly?$"{i:00}:00":from.ToString("MM-dd"),matching.Sum(r=>r.Total),matching.Length,0));
            }
        }
        var trend=UsageCharts.Trend(buckets,(_,_)=>{},claudeCodeDays==1,tokenOnly:true);
        panel.Children.Add(Card(trend));

        var models=new StackPanel{Spacing=12};
        models.Children.Add(ClaudeText(L10n.T("s39F1A54A74FF"),19));
        models.Children.Add(UsageCharts.Models(rows.GroupBy(r=>r.Model).Select(g=>(Name:g.Key,Total:g.Sum(r=>r.Total)))));
        var recent=new StackPanel{Spacing=12};
        recent.Children.Add(ClaudeText(L10n.T("sEB6D48FEBAB7"),19));
        foreach(var session in rows.GroupBy(r=>r.Session).OrderByDescending(g=>g.Max(r=>r.At)).Take(4))
            recent.Children.Add(ClaudeText($"Session · {session.Key[..Math.Min(12,session.Key.Length)]} · {session.Sum(r=>r.Total):N0} Token · {session.Count():N0} {L10n.T("claude.code.responseUnit")}",13));
        if(!hasRows)recent.Children.Add(ClaudeText(L10n.T("claude.code.rangeEmpty")));
        recent.Children.Add(Button(L10n.T("sD24CA3C355C7"),()=>{Navigate("sessions");return Task.CompletedTask;}));
        var modelsCard=Card(models);var recentCard=Card(recent);
        AutomationProperties.SetAutomationId(modelsCard,"claude-overview-models");
        AutomationProperties.SetAutomationId(recentCard,"claude-overview-sessions");
        panel.Children.Add(ResponsiveCards(new UIElement[]{modelsCard,recentCard},2,300));

        var composition=new StackPanel{Spacing=10};
        composition.Children.Add(ClaudeText(L10n.T("s719CAC683641"),19));
        composition.Children.Add(ClaudeText(hasRows?L10n.F("claude.code.counters",rows.Sum(r=>r.Input),rows.Sum(r=>r.Output),rows.Sum(r=>r.CacheRead),rows.Sum(r=>r.CacheWrite)):L10n.T("claude.code.unobserved"),15));
        composition.Children.Add(ClaudeText(L10n.T("claude.code.definition")));
        panel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sA29E4482CC69"),Content=composition,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        panel.Children.Add(ClaudeText(L10n.F("claude.code.quality",app.ClaudeCode.Files,app.ClaudeCode.Skipped)));
        return panel;
    }

}
