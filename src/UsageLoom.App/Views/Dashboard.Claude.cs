using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private StackPanel? claudeOverview,claudeDetails;
    private TextBlock? claudeSettingsStatus;
    private ComboBox? claudeScopeChoice;
    private string claudeScopesKey="";
    private static TextBlock ClaudeText(string text,double size=12)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
    private void FillClaudeCard(StackPanel panel)
    {
        var snapshot=app.ClaudeQuota;var now=DateTimeOffset.Now;panel.Children.Clear();
        panel.Children.Add(ClaudeText(L10n.T("claude.title"),18));
        panel.Children.Add(ClaudeText(snapshot.Describe(now)));
        foreach(var key in new[]{"five_hour","seven_day"})
        {
            var window=snapshot.Windows.FirstOrDefault(w=>w.Key==key);
            panel.Children.Add(ClaudeText(L10n.T("claude."+key)+" · "+L10n.F("claude.remaining",window?.RemainingText(now)??"—"),14));
            panel.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window is not null&&!window.Expired(now)?100-window.Used:0,Height=4,Foreground=accent,Opacity=(window is null||window.Expired(now))?0.3:1});
            panel.Children.Add(ClaudeText(window?.Countdown(now)??L10n.T("claude.noReset")));
        }
        panel.Children.Add(ClaudeText(snapshot.TimestampText));
    }
    private UIElement ClaudeDetailsCard()
    {
        claudeDetails=new(){Spacing=8};FillClaudeCard(claudeDetails);return Card(claudeDetails);
    }
    private void UpdateClaudeViews()
    {
        if(released)return;
        if(!DispatcherQueue.HasThreadAccess){DispatcherQueue.TryEnqueue(UpdateClaudeViews);return;}
        if(compact){if(compactContent is not null)UpdateCompactClaude();return;}
        if(claudeOverview is not null)FillClaudeCard(claudeOverview);
        if(claudeDetails is not null)FillClaudeCard(claudeDetails);
        if(claudeSettingsStatus is not null)claudeSettingsStatus.Text=app.ClaudeQuota.Describe(DateTimeOffset.Now)+"\n"+app.ClaudeQuota.TimestampText+"\n"+L10n.T("claude.identityNotice");
        var scopes=app.ClaudeQuota.Scopes??[];var signature=string.Join("|",scopes)+"|"+app.Config.ClaudeScope;
        if(claudeScopeChoice is not null&&signature!=claudeScopesKey)
        {
            claudeScopesKey=signature;claudeScopeChoice.Items.Clear();
            claudeScopeChoice.Items.Add(new ComboBoxItem{Content=L10n.T("claude.automatic"),Tag=""});
            foreach(var scope in scopes.Concat(app.Config.ClaudeScope is {} selected?[selected]:[]).Distinct())
                claudeScopeChoice.Items.Add(new ComboBoxItem{Content=L10n.F("claude.source",scope),Tag=scope});
            claudeScopeChoice.SelectedIndex=0;
            foreach(ComboBoxItem item in claudeScopeChoice.Items)if((string)item.Tag==app.Config.ClaudeScope)claudeScopeChoice.SelectedItem=item;
        }
    }
    private UIElement ClaudeSettings()
    {
        var panel=new StackPanel{Spacing=12};
        var enabled=new ToggleSwitch{Header=L10n.T("claude.enable"),IsOn=app.Config.ClaudeEnabled};
        var directory=new TextBox{Header=L10n.T("claude.path"),Text=app.Config.ClaudeDataDirectory??"",IsReadOnly=app.IsDemo};
        claudeScopeChoice=new ComboBox{Header=L10n.T("claude.scope"),HorizontalAlignment=HorizontalAlignment.Stretch};claudeScopesKey="!";
        claudeSettingsStatus=ClaudeText("");
        panel.Children.Add(enabled);panel.Children.Add(directory);panel.Children.Add(claudeScopeChoice);
        panel.Children.Add(ClaudeText(L10n.T("claude.notice")));panel.Children.Add(claudeSettingsStatus);
        panel.Children.Add(Button(L10n.T("claude.apply"),async()=>
        {
            var scope=(claudeScopeChoice.SelectedItem as ComboBoxItem)?.Tag as string;
            if(directory.Text.Trim()!=(app.Config.ClaudeDataDirectory??""))scope=null;
            await app.ConfigureClaudeAsync(enabled.IsOn,directory.Text,scope);
        }));
        panel.Children.Add(Button(L10n.T("claude.detect"),()=>app.RefreshClaudeAsync(true)));
        AutomationProperties.SetAutomationId(panel,"claude-settings");UpdateClaudeViews();
        return StableExpander.Configure(new Expander{Header=L10n.T("claude.title"),Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch});
    }
    private void UpdateCompactClaude()
    {
        var view=compactContent!;var visibility=app.Config.ClaudeEnabled?Visibility.Visible:Visibility.Collapsed;
        if(view.ClaudeCard.Visibility!=visibility){view.ClaudeCard.Visibility=visibility;displayState=null;}
        if(!app.Config.ClaudeEnabled)return;
        var snapshot=app.ClaudeQuota;var now=DateTimeOffset.Now;
        view.ClaudeTitle.Text="Claude · "+L10n.T(snapshot.Status=="snapshot"&&snapshot.ObservedAt>=now.AddMinutes(-15)&&snapshot.ObservedAt<=now?"claude.compactFresh":"claude.compactStale");
        for(var i=0;i<2;i++)
        {
            var key=i==0?"five_hour":"seven_day";var window=snapshot.Windows.FirstOrDefault(w=>w.Key==key);
            view.ClaudeValues[i].Text=L10n.T("claude."+key)+" · "+L10n.F("claude.remaining",window?.RemainingText(now)??"—");
            view.ClaudeResets[i].Text=window?.Countdown(now)??snapshot.Describe(now);
        }
        ToolTipService.SetToolTip(view.ClaudeCard,snapshot.Describe(now)+"\n"+snapshot.TimestampText);
    }
}
