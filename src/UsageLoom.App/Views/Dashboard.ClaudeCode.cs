using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private int claudeCodeRangeIndex=1,claudeCodePage;
    private DateOnly claudeCodeFrom=DateOnly.FromDateTime(DateTime.Today.AddDays(-6)),claudeCodeThrough=DateOnly.FromDateTime(DateTime.Today);
    private bool claudeHistoryExpanded;
    private HistoryDateRange ClaudeCodeRange()=>HistoryQuery.ResolveRange(claudeCodeRangeIndex switch{0=>HistoryRangeKind.Day,1=>HistoryRangeKind.Rolling7Days,2=>HistoryRangeKind.Week,3=>HistoryRangeKind.Month,_=>HistoryRangeKind.Custom},DateOnly.FromDateTime(DateTime.Today),claudeCodeFrom,claudeCodeThrough);
    private UIElement ClaudeCodePanel()
    {
        var snapshot=app.ClaudeCode;var panel=new StackPanel{Spacing=16};
        AutomationProperties.SetAutomationId(panel,"claude-code-statistics");
        var toolbar=new StackPanel{Spacing=10};
        var choices=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        var range=ClaudeCodeRange();
        for(var index=0;index<5;index++)
        {
            var selected=index;
            var key=index switch{0=>"s85217F7AFF77",1=>"s2261B06712A3",2=>"s5C553EC3F6DB",3=>"s1625179BADC0",_=>"s4EAFA9E925B3"};
            var button=new ToggleButton{Content=L10n.T(key),IsChecked=claudeCodeRangeIndex==index,Padding=new Thickness(12,6,12,6),CornerRadius=new CornerRadius(8),MinWidth=42};
            AutomationProperties.SetAutomationId(button,"claude-code-range-"+index);
            button.Click+=(_,_)=>{claudeCodeRangeIndex=selected;claudeCodePage=0;renderedFilter=null;RenderStats();};choices.Children.Add(button);
        }
        var rangeHeader=new Grid{ColumnSpacing=12};
        rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        rangeHeader.Children.Add(choices);
        var rangeLabel=ClaudeText(range.Label,12);rangeLabel.Opacity=.62;rangeLabel.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(rangeLabel,1);rangeHeader.Children.Add(rangeLabel);
        toolbar.Children.Add(rangeHeader);
        if(claudeCodeRangeIndex==4)
        {
            var dates=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            var from=new CalendarDatePicker{Date=new DateTimeOffset(claudeCodeFrom.ToDateTime(TimeOnly.MinValue))};
            var through=new CalendarDatePicker{Date=new DateTimeOffset(claudeCodeThrough.ToDateTime(TimeOnly.MinValue))};
            from.DateChanged+=(_,_)=>{if(from.Date is {} value){claudeCodeFrom=DateOnly.FromDateTime(value.LocalDateTime);renderedFilter=null;RenderStats();}};
            through.DateChanged+=(_,_)=>{if(through.Date is {} value){claudeCodeThrough=DateOnly.FromDateTime(value.LocalDateTime);renderedFilter=null;RenderStats();}};
            dates.Children.Add(from);dates.Children.Add(through);toolbar.Children.Add(dates);
        }
        ToolTipService.SetToolTip(toolbar,L10n.T("claude.code.scope"));panel.Children.Add(Card(toolbar));
        if(snapshot.Status is not "ready")
        {
            var notice=new StackPanel{Spacing=10};notice.Children.Add(ClaudeText(L10n.T("claude.code.status."+snapshot.Status),15));
            notice.Children.Add(Button(L10n.T("sDF3D58C7D84B"),()=>{Navigate("settings");return Task.CompletedTask;}));panel.Children.Add(Card(notice));
        }
        var start=range.From?.ToDateTime(TimeOnly.MinValue)??DateTime.MaxValue;
        var end=range.Through?.ToDateTime(TimeOnly.MaxValue)??DateTime.MinValue;
        var rows=snapshot.Rows.Where(r=>r.At.LocalDateTime>=start&&r.At.LocalDateTime<=end).ToArray();
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
        var range=ClaudeCodeRange();
        var end=range.Through?.ToDateTime(TimeOnly.MaxValue)??start;
        var hasRows=rows.Count>0;
        var total=rows.Sum(r=>r.Total);
        var all=app.ClaudeCode.Rows.Sum(r=>r.Total);
        var hasHistory=app.ClaudeCode.Rows.Count>0;
        var sessions=rows.Select(r=>r.Session).Distinct(StringComparer.Ordinal).Count();
        var estimate=ClaudeCodePricing.Summarize(rows);
        var observed=hasRows?L10n.T("claude.code.observed"):L10n.T("claude.code.unobserved");
        var attributionCard=Card(ResponsiveCards(new UIElement[]{
            Metric(L10n.T("claude.code.allHistory"),hasHistory?all.ToString("N0"):"—",L10n.T("claude.code.allScope")),
            Metric(L10n.T("claude.code.selectedTokens"),hasRows?total.ToString("N0"):"—",observed),
            Metric(L10n.T("claude.code.outsideRange"),hasHistory?Math.Max(0,all-total).ToString("N0"):"—",L10n.T("claude.code.outsideScope"))},3,210));
        AutomationProperties.SetAutomationId(attributionCard,"claude-overview-attribution");panel.Children.Add(attributionCard);
        panel.Children.Add(Button(L10n.T("claude.code.refresh"),()=>app.RefreshClaudeAsync(true)));
        var metricsCard=Card(ResponsiveCards(new UIElement[]{
            Metric("Session",hasRows?sessions.ToString("N0"):"—",L10n.T("claude.code.sessionScope")),
            Metric(L10n.T("claude.code.metricResponses"),hasRows?rows.Count.ToString("N0"):"—",L10n.T("claude.code.responseScope")),
            Metric(L10n.T("claude.code.estimate"),estimate.Priced>0?"$"+estimate.Cost.ToString("N4"):"—",L10n.T("claude.code.estimateScope")),
            Metric(L10n.T("claude.code.coverage"),total>0?$"{estimate.Coverage:0.#}%":"—",L10n.F("claude.code.unpriced",estimate.Unpriced))},4,190));
        AutomationProperties.SetAutomationId(metricsCard,"claude-overview-metrics");panel.Children.Add(metricsCard);

        var buckets=new List<HistoryTrendBucket>();
        if(hasRows)
        {
            var hourly=range.IsSingleDay;
            var count=hourly?24:Math.Min(60,(end.Date-start.Date).Days+1);
            var daysPerBucket=hourly?1:Math.Max(1,(int)Math.Ceiling(((end.Date-start.Date).Days+1)/(double)count));
            var grouped=rows.GroupBy(r=>hourly?r.At.LocalDateTime.Hour:Math.Min(count-1,(r.At.LocalDateTime.Date-start.Date).Days/daysPerBucket))
                .ToDictionary(g=>g.Key,g=>(Tokens:g.Sum(r=>r.Total),Requests:g.Count(),Cost:ClaudeCodePricing.Summarize(g).Cost));
            for(var i=0;i<count;i++)
            {
                var from=hourly?start:start.AddDays(i*daysPerBucket);
                var day=DateOnly.FromDateTime(from);
                var through=hourly?day:DateOnly.FromDayNumber(Math.Min(range.Through!.Value.DayNumber,day.DayNumber+daysPerBucket-1));
                var values=grouped.GetValueOrDefault(i);
                buckets.Add(new(day,through,hourly?$"{i:00}:00":from.ToString("MM-dd"),values.Tokens,values.Requests,values.Cost));
            }
        }
        var trend=UsageCharts.Trend(buckets,(_,_)=>{},range.IsSingleDay,tokenOnly:true);
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
        var pricing=new StackPanel{Spacing=8};
        pricing.Children.Add(ClaudeText(L10n.T("claude.code.estimateExplain")));
        if(estimate.Assumed5m)pricing.Children.Add(ClaudeText(L10n.T("claude.code.cacheAssumption")));
        pricing.Children.Add(new HyperlinkButton{Content=L10n.T("claude.code.priceSource"),NavigateUri=new Uri(ClaudeCodePricing.Source)});
        panel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("claude.code.estimateDetails"),Content=pricing,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        panel.Children.Add(ClaudeText(L10n.F("claude.code.quality",app.ClaudeCode.Files,app.ClaudeCode.Skipped)));
        return panel;
    }

}
