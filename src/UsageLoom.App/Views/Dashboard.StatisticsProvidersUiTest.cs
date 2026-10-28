using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private void VerifyStatisticsProviders()
    {
        var consumptionHint=UsageCharts.QuotaTooltip(new QuotaConsumptionBucket(80,QuotaConsumptionMissing.ResetTail),L10n.T("quota.chart.fiveHour"));
        var missingHint=UsageCharts.QuotaTooltip(null,L10n.T("quota.chart.weekly"));
        if(!consumptionHint.Contains(L10n.T("quota.chart.fiveHour"),StringComparison.Ordinal)||
            !consumptionHint.Contains("80",StringComparison.Ordinal)||!consumptionHint.Contains(L10n.T("quota.chart.partial"),StringComparison.Ordinal)||
            !missingHint.Contains("--",StringComparison.Ordinal)||!missingHint.Contains(L10n.T("quota.chart.weekly"),StringComparison.Ordinal))
            throw new InvalidOperationException("Token chart quota hover lacks window, percentage points or missing-value explanation");
        var savedPage=selectedPage;var savedProvider=statisticsProvider;
        var codexEnabled=app.Config.CodexEnabled;var claudeEnabled=app.Config.ClaudeEnabled;
        var codexVisible=app.Config.CodexVisible;var claudeVisible=app.Config.ClaudeVisible;
        var range=historyRangeIndex;var from=historyFrom.Date;var through=historyThrough.Date;
        var search=historySearch.Text;var order=sessionOrder.SelectedIndex;var grouping=breakdownKind.SelectedIndex;
        var savedSessionPage=sessionPage;
        var savedClaudeDays=claudeHistoryDays;var savedClaudeCodeRange=claudeCodeRangeIndex;
        var events=app.Events;var snapshot=app.ClaudeQuota;
        var root=(FrameworkElement)Content;
        void Switch(string provider)
        {
            root.UpdateLayout();
            var tab=CapacityTestDescendants(root).OfType<ToggleButton>().Single(t=>AutomationProperties.GetAutomationId(t)=="stats-provider-"+provider);
            tab.IsChecked=true;root.UpdateLayout();
            if(statisticsProvider!=provider)throw new InvalidOperationException("Provider selector did not switch the view");
            for(DependencyObject? parent=tab;parent is not null;parent=VisualTreeHelper.GetParent(parent))
                if(ReferenceEquals(parent,pageScroll)||ReferenceEquals(parent,sessionWorkspace))
                    throw new InvalidOperationException("Provider selector moved into scrolling content");
        }
        void CheckUnavailable()
        {
            root.UpdateLayout();
            var workspace=selectedPage=="sessions"?(DependencyObject)sessionWorkspace:statisticsBody;
            var expected=statisticsProvider=="claude"&&app.ClaudeShown?"claude-code-statistics":"stats-unavailable";
            if(!CapacityTestDescendants(workspace).OfType<FrameworkElement>().Any(e=>AutomationProperties.GetAutomationId(e)==expected)||
                CapacityTestDescendants(workspace).Any(e=>ReferenceEquals(e,filterBar))||actions.Visibility!=Visibility.Collapsed)
                throw new InvalidOperationException("Unavailable provider leaked Codex statistics, filters or scan action");
        }
        void ExpectView(string? provider)
        {
            renderedFilter=null;Render();root.UpdateLayout();
            if((statisticsTabs.Visibility==Visibility.Visible)!=(app.CodexShown&&app.ClaudeShown))
                throw new InvalidOperationException("Provider selector visibility does not match shown sources");
            if(provider is null)
            {
                CheckUnavailable();
                var workspace=selectedPage=="sessions"?(DependencyObject)sessionWorkspace:statisticsBody;
                if(!CapacityTestDescendants(workspace).OfType<TextBlock>().Any(t=>t.Text==L10n.T("stats.noneShown")))
                    throw new InvalidOperationException("No shown source guidance missing");
                return;
            }
            if(statisticsProvider!=provider)throw new InvalidOperationException("Shown source was not selected: "+provider);
            if(provider=="codex")VerifyHistoryFilterAttached();else CheckUnavailable();
        }
        void CheckHeadingFits()
        {
            // Only the wide layout keeps the selector beside the subtitle and actions.
            root.UpdateLayout();
            var saved=heading.Width;
            if(double.IsNaN(saved)||Grid.GetRow(statisticsTabs)!=0||pageSubtitle.Visibility!=Visibility.Visible)return;
            try
            {
                foreach(var width in new[]{712d,760,820,900})
                {
                    if(width>saved)continue;
                    heading.Width=width;FitHeadingText();root.UpdateLayout();
                    var tabs=statisticsTabs.Children.OfType<FrameworkElement>().ToArray();
                    var needed=tabs.Sum(t=>t.DesiredSize.Width)+statisticsTabs.Spacing*(tabs.Length-1);
                    if(heading.ColumnDefinitions[1].ActualWidth+.5<needed)
                        throw new InvalidOperationException($"Provider selector clipped on {selectedPage} at {width} DIP heading width: {heading.ColumnDefinitions[1].ActualWidth:0}/{needed:0}");
                }
            }
            finally{heading.Width=saved;FitHeadingText();root.UpdateLayout();}
        }
        try
        {
            historySearch.Text="provider-filter-fixture";sessionOrder.SelectedIndex=1;breakdownKind.SelectedIndex=1;
            // Tabs exist only while both sources are shown; otherwise the single shown source is selected.
            if(app.CodexShown&&app.ClaudeShown)
            {
                foreach(var pageName in new[]{"overview","breakdown","sessions"})
                {
                    ShowPage(pageName);Switch("claude");CheckUnavailable();
                    if(app.Config.CodexEnabled!=codexEnabled||app.Config.ClaudeEnabled!=claudeEnabled||!ReferenceEquals(events,app.Events)||!ReferenceEquals(snapshot,app.ClaudeQuota))
                        throw new InvalidOperationException("View selection changed collection or source data");
                    Render();CheckUnavailable();Switch("codex");VerifyHistoryFilterAttached();CheckHeadingFits();
                    if(historyRangeIndex!=range||historyFrom.Date!=from||historyThrough.Date!=through||historySearch.Text!="provider-filter-fixture"||sessionOrder.SelectedIndex!=1||breakdownKind.SelectedIndex!=1)
                        throw new InvalidOperationException("Provider switch lost Codex filters");
                }
                Switch("claude");ShowPage("breakdown");CheckUnavailable();ShowPage("overview");CheckUnavailable();
                if(CapacityTestDescendants(statisticsBody).OfType<TextBlock>().Any(t=>t.Text==L10n.F("stats.title","Claude")||t.Text==L10n.T("stats.scope")))
                    throw new InvalidOperationException("Removed provider title or instructions remain in the statistics body");
                if(app.Config.ClaudeEnabled)
                {
                    foreach(var id in new[]{"claude-overview-attribution","claude-overview-metrics","claude-overview-models","claude-overview-sessions"})
                        if(!CapacityTestDescendants(statisticsBody).OfType<FrameworkElement>().Any(e=>AutomationProperties.GetAutomationId(e)==id))
                            throw new InvalidOperationException("Claude overview is missing Codex-style section: "+id);
                    for(var i=0;i<5;i++)
                        if(!CapacityTestDescendants(statisticsBody).OfType<ToggleButton>().Any(e=>AutomationProperties.GetAutomationId(e)=="claude-code-range-"+i))
                            throw new InvalidOperationException("Claude overview is missing a shared date-range choice: "+i);
                    var claudeRangeButton=CapacityTestDescendants(statisticsBody).OfType<ToggleButton>()
                        .Single(e=>AutomationProperties.GetAutomationId(e)=="claude-code-range-0");
                    if(VisualTreeHelper.GetParent(claudeRangeButton) is not StackPanel claudeRangeButtons||
                        claudeRangeButtons.Spacing!=historyRangeButtons.Spacing)
                        throw new InvalidOperationException("Claude date-range button spacing differs from Codex");
                    claudeCodeRangeIndex=4;renderedFilter=null;RenderStats();root.UpdateLayout();
                    var claudeDates=CapacityTestDescendants(statisticsBody).OfType<CalendarDatePicker>().ToArray();
                    if(claudeDates.Length!=2||
                        !Equals(claudeDates[0].Header,historyFrom.Header)||!Equals(claudeDates[1].Header,historyThrough.Header)||
                        claudeDates[0].PlaceholderText!=historyFrom.PlaceholderText||claudeDates[1].PlaceholderText!=historyThrough.PlaceholderText||
                        claudeDates[0].DateFormat!=historyFrom.DateFormat||claudeDates[1].DateFormat!=historyThrough.DateFormat||
                        claudeDates[0].MinWidth!=historyFrom.MinWidth||claudeDates[1].MinWidth!=historyThrough.MinWidth)
                        throw new InvalidOperationException("Claude custom date pickers differ from Codex labels or ISO date format");
                    claudeCodeRangeIndex=savedClaudeCodeRange;renderedFilter=null;RenderStats();
                }
                claudeHistoryDays=30;renderedFilter=null;RenderStats();CheckUnavailable();
                ShowPage("sessions");ShowPage("overview");root.UpdateLayout();
                if(claudeHistoryDays!=30)
                    throw new InvalidOperationException("Claude history date selection lost");
                if(app.Config.ClaudeEnabled)
                {
                    var history=CapacityTestDescendants(root).OfType<Expander>().Single(e=>Equals(e.Header,L10n.T("claude.code.quotaHistory")));
                    history.IsExpanded=true;root.UpdateLayout();
                    if(!CapacityTestDescendants(history).OfType<ToggleButton>().Any(t=>AutomationProperties.GetAutomationId(t)=="claude-history-days-30"&&t.IsChecked==true))throw new InvalidOperationException("Collapsed quota history lost its date selector");
                    if(app.ClaudeQuota.History.Count>0&&!CapacityTestDescendants(history).OfType<Canvas>().Any())throw new InvalidOperationException("Auxiliary quota history chart missing");
                    history.IsExpanded=false;
                }
                if(app.Config.ClaudeEnabled&&app.ClaudeCode.Rows.Count>0&&!CapacityTestDescendants(root).OfType<Canvas>().Any(c=>AutomationProperties.GetAutomationId(c)=="claude-token-chart"))
                    throw new InvalidOperationException("Claude Token chart missing");
                var oldHistoryPanel=CapacityTestDescendants(root).OfType<FrameworkElement>().FirstOrDefault(e=>AutomationProperties.GetAutomationId(e)=="claude-code-statistics");
                UpdateClaudeViews();root.UpdateLayout();
                if(app.Config.ClaudeEnabled&&!ReferenceEquals(oldHistoryPanel,CapacityTestDescendants(root).OfType<FrameworkElement>().FirstOrDefault(e=>AutomationProperties.GetAutomationId(e)=="claude-code-statistics")))
                    throw new InvalidOperationException("Unchanged Claude snapshot rebuilt the history view");
                Switch("codex");
            }
            else{ShowPage("overview");ExpectView(app.CodexShown?"codex":app.ClaudeShown?"claude":null);}
            var date=new DateOnly(2026,9,8);DrillIntoRange(date,date);root.UpdateLayout();
            if(statisticsProvider!="codex"||selectedPage!="sessions"||!SelectedHistoryRange().IsSingleDay)
                throw new InvalidOperationException("Trend drilldown lost provider or date range");
            var claudeOn=claudeEnabled;
            ExpectView("codex");
            app.Config.CodexVisible=false;ExpectView(claudeOn?"claude":null);
            if(!app.Config.CodexEnabled||!ReferenceEquals(events,app.Events))throw new InvalidOperationException("Hiding Codex changed collection or source data");
            app.Config.CodexVisible=true;ExpectView(claudeOn?"claude":"codex");
            app.Config.ClaudeVisible=false;ExpectView("codex");
            app.Config.CodexVisible=false;ExpectView(null);
            app.Config.CodexVisible=true;app.Config.ClaudeVisible=true;
            app.Config.CodexEnabled=false;ExpectView(claudeOn?"claude":null);
            app.Config.ClaudeEnabled=false;ExpectView(null);
            app.Config.ClaudeEnabled=true;ExpectView("claude");
            if(!CapacityTestDescendants(sessionWorkspace).OfType<FrameworkElement>().Any(t=>ToolTipService.GetToolTip(t) as string==L10n.T("claude.code.scope")))
                throw new InvalidOperationException("Claude local Token scope explanation missing");
            foreach(var key in new[]{"attribution.inspect","maintenance.repair","maintenance.clear","maintenance.restart"})
                if(!L10n.T(key).Contains("Codex",StringComparison.Ordinal))throw new InvalidOperationException("Operation has ambiguous provider: "+key);
            Program.Log.Write("INFO","NavigationTest","Statistics providers: isolated views, retained filters, drilldown and disabled sources passed");
            Program.Log.Write("INFO","NavigationTest","Claude history: scoped observations, independent date filter and disabled source boundaries passed");
        }
        finally
        {
            app.Config.CodexEnabled=codexEnabled;app.Config.ClaudeEnabled=claudeEnabled;
            app.Config.CodexVisible=codexVisible;app.Config.ClaudeVisible=claudeVisible;
            updatingHistoryRangeControls=true;
            try{historyFrom.Date=from;historyThrough.Date=through;historyRangeIndex=range;UpdateHistoryRangeControls();}
            finally{updatingHistoryRangeControls=false;}
            historySearch.Text=search;sessionOrder.SelectedIndex=order;breakdownKind.SelectedIndex=grouping;sessionPage=savedSessionPage;
            statisticsProvider=savedProvider;renderedFilter=null;ShowPage(savedPage);Render();
            claudeHistoryDays=savedClaudeDays;claudeCodeRangeIndex=savedClaudeCodeRange;renderedFilter=null;RenderStats();
        }
    }
}
