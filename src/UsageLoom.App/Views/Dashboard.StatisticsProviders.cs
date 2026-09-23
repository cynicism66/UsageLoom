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
    private bool CodexStatisticsActive=>statisticsProvider=="codex"&&app.Config.CodexEnabled;

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
        statisticsTabs.Visibility=!compact&&IsStatisticsPage?Visibility.Visible:Visibility.Collapsed;
        foreach(var (provider,tab) in statisticsTabButtons)
        {
            var enabled=provider=="codex"?app.Config.CodexEnabled:app.Config.ClaudeEnabled;
            tab.Content=(provider=="codex"?"Codex":"Claude")+(enabled?"":" · "+L10n.T("stats.disabled"));
            tab.IsChecked=statisticsProvider==provider;
        }
    }

    private UIElement StatisticsUnavailable()
    {
        var provider=statisticsProvider=="codex"?"Codex":"Claude";
        var enabled=statisticsProvider=="codex"?app.Config.CodexEnabled:app.Config.ClaudeEnabled;
        if(statisticsProvider=="claude"&&enabled)return ClaudeCodePanel();
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(ClaudeText(enabled?L10n.T("stats.claudeUnavailable"):L10n.F("stats.sourceDisabled",provider),18));
        panel.Children.Add(ClaudeText(enabled?L10n.T("stats.claudeNotice"):L10n.T("stats.enableNotice"),14));
        if(statisticsProvider=="claude"&&enabled)
            panel.Children.Add(Button(L10n.T("claude.viewHistory"),()=>{Navigate("overview");return Task.CompletedTask;}));
        panel.Children.Add(Button(L10n.T("sDF3D58C7D84B"),()=>{Navigate("settings");return Task.CompletedTask;}));
        var card=Card(panel);AutomationProperties.SetAutomationId(card,"stats-unavailable");return card;
    }

    private void UpdateStatisticsActions()
    {
        UpdateStatisticsTabs();
        if(!IsStatisticsPage)return;
        actions.Visibility=CodexStatisticsActive?Visibility.Visible:Visibility.Collapsed;
        status.Text=statisticsProvider=="claude"?(app.Config.ClaudeEnabled?"Claude · "+app.ClaudeQuota.Describe(DateTimeOffset.Now):L10n.F("stats.sourceDisabled","Claude")):
            app.Config.CodexEnabled?"Codex · "+app.HistoryStatus:L10n.F("stats.sourceDisabled","Codex");
        ToolTipService.SetToolTip(status,status.Text);
    }
}
