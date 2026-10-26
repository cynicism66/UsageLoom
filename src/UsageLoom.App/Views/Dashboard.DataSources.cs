using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private ToggleSwitch? codexEnabledChoice,codexVisibleChoice,claudeVisibleChoice;
    private StackPanel? codexSettingsPanel;
    private Button? codexApplyButton;
    private static StackPanel SourceSettingsGroup(string title,ToggleSwitch enabled,ToggleSwitch visible,params UIElement[] controls)
    {
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(ClaudeText(title,16));
        enabled.Header=L10n.T("source.enable");visible.Header=L10n.T("source.visible");
        foreach(var choice in new[]{enabled,visible}){choice.OnContent=L10n.Language=="en-US"?"On":"开";choice.OffContent=L10n.Language=="en-US"?"Off":"关";}
        panel.Children.Add(enabled);panel.Children.Add(visible);panel.Children.Add(ClaudeText(L10n.T("source.visibleNotice")));
        foreach(var control in controls)panel.Children.Add(control);
        return panel;
    }
    // Unlike the enable switch, visibility applies immediately: it never stops
    // collection or writes a sampling boundary, so it needs no apply step.
    private ToggleSwitch SourceVisibleChoice(string provider)
    {
        var choice=new ToggleSwitch{IsOn=provider=="codex"?app.Config.CodexVisible:app.Config.ClaudeVisible};
        AutomationProperties.SetAutomationId(choice,provider+"-visible");
        choice.Toggled+=(_,_)=>
        {
            try{app.SetSourceVisible(provider,choice.IsOn);}
            catch(Exception ex){Program.Log.Write("ERROR","UI",ex.Message);status.Text=Privacy.Redact(ex.Message);choice.IsOn=!choice.IsOn;}
        };
        return choice;
    }
    private UIElement CodexSettings(TextBox executable,TextBox home)
    {
        var enabled=codexEnabledChoice=new ToggleSwitch{IsOn=app.Config.CodexEnabled};
        var panel=codexSettingsPanel=SourceSettingsGroup("Codex",enabled,codexVisibleChoice=SourceVisibleChoice("codex"),executable,home);
        panel.Children.Add(ClaudeText(L10n.T("source.codexNotice")));
        codexApplyButton=Button(L10n.T("claude.apply"),()=>app.ConfigureCodexAsync(enabled.IsOn,executable.Text,home.Text));
        panel.Children.Add(codexApplyButton);
        panel.Children.Add(Button(L10n.T("s637A0D380AEC"),()=>app.RefreshQuotaAsync(true,false)));
        return panel;
    }
    private UIElement DisabledSourcesMessage()=>new TextBlock{Text=L10n.T("source.none"),TextWrapping=TextWrapping.Wrap,Opacity=.7};
}
