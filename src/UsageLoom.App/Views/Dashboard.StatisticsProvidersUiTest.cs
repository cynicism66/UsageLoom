using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private void VerifyStatisticsProviders()
    {
        var savedPage=selectedPage;var savedProvider=statisticsProvider;
        var codexEnabled=app.Config.CodexEnabled;var claudeEnabled=app.Config.ClaudeEnabled;
        var range=historyRangeIndex;var from=historyFrom.Date;var through=historyThrough.Date;
        var search=historySearch.Text;var order=sessionOrder.SelectedIndex;var grouping=breakdownKind.SelectedIndex;
        var savedSessionPage=sessionPage;
        var events=app.Events;var snapshot=app.ClaudeQuota;
        var root=(FrameworkElement)Content;
        void Switch(string provider)
        {
            root.UpdateLayout();
            var tab=CapacityTestDescendants(root).OfType<ToggleButton>().Single(t=>AutomationProperties.GetAutomationId(t)=="stats-provider-"+provider);
            tab.IsChecked=true;root.UpdateLayout();
            if(statisticsProvider!=provider)throw new InvalidOperationException("Provider selector did not switch the view");
        }
        void CheckUnavailable()
        {
            root.UpdateLayout();
            var workspace=selectedPage=="sessions"?(DependencyObject)sessionWorkspace:statisticsBody;
            if(!CapacityTestDescendants(workspace).OfType<FrameworkElement>().Any(e=>AutomationProperties.GetAutomationId(e)=="stats-unavailable")||
                CapacityTestDescendants(workspace).Any(e=>ReferenceEquals(e,filterBar))||actions.Visibility!=Visibility.Collapsed)
                throw new InvalidOperationException("Unavailable provider leaked Codex statistics, filters or scan action");
        }
        try
        {
            historySearch.Text="provider-filter-fixture";sessionOrder.SelectedIndex=1;breakdownKind.SelectedIndex=1;
            foreach(var pageName in new[]{"overview","breakdown","sessions"})
            {
                ShowPage(pageName);Switch("claude");CheckUnavailable();
                if(app.Config.CodexEnabled!=codexEnabled||app.Config.ClaudeEnabled!=claudeEnabled||!ReferenceEquals(events,app.Events)||!ReferenceEquals(snapshot,app.ClaudeQuota))
                    throw new InvalidOperationException("View selection changed collection or source data");
                Render();CheckUnavailable();Switch("codex");VerifyHistoryFilterAttached();
                if(historyRangeIndex!=range||historyFrom.Date!=from||historyThrough.Date!=through||historySearch.Text!="provider-filter-fixture"||sessionOrder.SelectedIndex!=1||breakdownKind.SelectedIndex!=1)
                    throw new InvalidOperationException("Provider switch lost Codex filters");
            }
            Switch("claude");ShowPage("breakdown");CheckUnavailable();ShowPage("overview");CheckUnavailable();
            Switch("codex");
            var date=new DateOnly(2026,9,8);DrillIntoRange(date,date);root.UpdateLayout();
            if(statisticsProvider!="codex"||selectedPage!="sessions"||!SelectedHistoryRange().IsSingleDay)
                throw new InvalidOperationException("Trend drilldown lost provider or date range");
            app.Config.CodexEnabled=false;renderedFilter=null;Render();CheckUnavailable();
            Switch("claude");app.Config.ClaudeEnabled=false;renderedFilter=null;Render();CheckUnavailable();
            app.Config.ClaudeEnabled=true;renderedFilter=null;Render();CheckUnavailable();
            if(!CapacityTestDescendants(sessionWorkspace).OfType<TextBlock>().Any(t=>t.Text==L10n.T("stats.claudeNotice")))
                throw new InvalidOperationException("Claude unsupported statistics explanation missing");
            foreach(var key in new[]{"attribution.inspect","maintenance.repair","maintenance.clear","maintenance.restart","sD9EBFF4C171F"})
                if(!L10n.T(key).Contains("Codex",StringComparison.Ordinal))throw new InvalidOperationException("Operation has ambiguous provider: "+key);
            Program.Log.Write("INFO","NavigationTest","Statistics providers: isolated views, retained filters, drilldown and disabled sources passed");
        }
        finally
        {
            app.Config.CodexEnabled=codexEnabled;app.Config.ClaudeEnabled=claudeEnabled;
            updatingHistoryRangeControls=true;
            try{historyFrom.Date=from;historyThrough.Date=through;historyRangeIndex=range;UpdateHistoryRangeControls();}
            finally{updatingHistoryRangeControls=false;}
            historySearch.Text=search;sessionOrder.SelectedIndex=order;breakdownKind.SelectedIndex=grouping;sessionPage=savedSessionPage;
            statisticsProvider=savedProvider;renderedFilter=null;ShowPage(savedPage);Render();
        }
    }
}
