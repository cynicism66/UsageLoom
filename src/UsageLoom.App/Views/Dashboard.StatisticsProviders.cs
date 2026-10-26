using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private string statisticsProvider="codex";
    private readonly StackPanel statisticsBody=new(){Spacing=16};
    private readonly StackPanel statisticsTabs=new(){Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};
    private readonly Dictionary<string,ToggleButton> statisticsTabButtons=[];
    private Border? statisticsFrame;
    private bool IsStatisticsPage=>selectedPage is "overview" or "breakdown" or "sessions";
    private bool CodexStatisticsActive=>statisticsProvider=="codex"&&app.CodexShown;

    private void SelectStatisticsProvider(string provider)
    {
        if(provider is not ("codex" or "claude"))throw new ArgumentException("Unknown statistics provider");
        if(statisticsProvider==provider)return;
        statisticsProvider=provider;renderedFilter=null;
        capacityInfoFlyout?.Hide();UpdateStatisticsTabs();Render();
        pageScroll.ChangeView(null,0,null);
    }

    private void InitializeStatisticsTabs()
    {
        foreach(var provider in new[]{"codex","claude"})
        {
            var label=provider=="codex"?"Codex":"Claude";
            var tab=new ToggleButton{Content=label,IsChecked=statisticsProvider==provider,MinWidth=100,Padding=new Thickness(14,8,14,8)};
            AutomationProperties.SetAutomationId(tab,"stats-provider-"+provider);
            AutomationProperties.SetName(tab,L10n.F("stats.view",label));
            tab.Checked+=(_,_)=>SelectStatisticsProvider(provider);
            tab.Unchecked+=(_,_)=>{if(statisticsProvider==provider)tab.IsChecked=true;};
            tab.Click+=(_,_)=>{if(statisticsProvider==provider)tab.IsChecked=true;else SelectStatisticsProvider(provider);};
            statisticsTabButtons.Add(provider,tab);statisticsTabs.Children.Add(tab);
        }
        UpdateStatisticsTabs();
    }

    private void UpdateStatisticsTabs()
    {
        // The selector exists only to choose between two shown sources; a single
        // shown source is selected directly, and hidden or disabled ones get no tab.
        var codexShown=app.CodexShown;var claudeShown=app.ClaudeShown;
        if(statisticsProvider=="codex"?!codexShown&&claudeShown:!claudeShown&&codexShown)
        {
            statisticsProvider=statisticsProvider=="codex"?"claude":"codex";
            renderedFilter=null;capacityInfoFlyout?.Hide();
        }
        statisticsTabs.Visibility=!compact&&IsStatisticsPage&&codexShown&&claudeShown?Visibility.Visible:Visibility.Collapsed;
        foreach(var (provider,tab) in statisticsTabButtons)tab.IsChecked=statisticsProvider==provider;
        FitHeadingText();
    }

    // The title column is Auto-sized, so a long subtitle would otherwise take the
    // selector's star column. Cap the title block so the subtitle wraps instead.
    private void FitHeadingText()
    {
        if(compact||double.IsNaN(heading.Width)||Grid.GetRow(statisticsTabs)!=0){headingText.MaxWidth=double.PositiveInfinity;return;}
        double Natural(FrameworkElement element)
        {
            if(element.Visibility!=Visibility.Visible)return 0;
            element.Measure(new Windows.Foundation.Size(double.PositiveInfinity,double.PositiveInfinity));
            return element.DesiredSize.Width;
        }
        var available=heading.Width-heading.ColumnSpacing*2-Natural(statisticsTabs)-Natural(actions);
        headingText.MaxWidth=Math.Max(Natural(pageTitle),available);
    }

    private UIElement StatisticsUnavailable()
    {
        if(statisticsProvider=="claude"&&app.ClaudeShown)return ClaudeCodePanel();
        // UpdateStatisticsTabs selects any shown source, so only "none shown" remains.
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(ClaudeText(L10n.T("stats.noneShown"),18));
        panel.Children.Add(ClaudeText(L10n.T("source.none"),14));
        panel.Children.Add(Button(L10n.T("sDF3D58C7D84B"),()=>{Navigate("settings");return Task.CompletedTask;}));
        var card=Card(panel);AutomationProperties.SetAutomationId(card,"stats-unavailable");return card;
    }

    private void UpdateStatisticsActions()
    {
        UpdateStatisticsTabs();
        if(!IsStatisticsPage)return;
        actions.Visibility=CodexStatisticsActive?Visibility.Visible:Visibility.Collapsed;FitHeadingText();
        status.Text=statisticsProvider=="claude"?(app.ClaudeShown?"Claude · "+app.ClaudeQuota.Describe(DateTimeOffset.Now):L10n.T("source.none")):
            app.CodexShown?"Codex · "+app.HistoryStatus:L10n.T("source.none");
        ToolTipService.SetToolTip(status,status.Text);
    }
}
