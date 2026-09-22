using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private ToggleSwitch? codexEnabledChoice;
    private StackPanel? codexSettingsPanel;
    private Button? codexApplyButton;
    private static StackPanel SourceSettingsGroup(string title,ToggleSwitch enabled,params UIElement[] controls)
    {
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(ClaudeText(title,16));
        enabled.Header=L10n.T("source.enable");
        enabled.OnContent=L10n.Language=="en-US"?"On":"开";enabled.OffContent=L10n.Language=="en-US"?"Off":"关";
        panel.Children.Add(enabled);
        foreach(var control in controls)panel.Children.Add(control);
        return panel;
    }
    private UIElement CodexSettings(TextBox executable,TextBox home)
    {
        var enabled=codexEnabledChoice=new ToggleSwitch{IsOn=app.Config.CodexEnabled};
        var panel=codexSettingsPanel=SourceSettingsGroup("Codex",enabled,executable,home);
        panel.Children.Add(ClaudeText(L10n.T("source.codexNotice")));
        codexApplyButton=Button(L10n.T("claude.apply"),()=>app.ConfigureCodexAsync(enabled.IsOn,executable.Text,home.Text));
        panel.Children.Add(codexApplyButton);
        panel.Children.Add(Button(L10n.T("s637A0D380AEC"),()=>app.RefreshQuotaAsync(true,false)));
        return panel;
    }
    private UIElement DisabledSourcesMessage()=>new TextBlock{Text=L10n.T("source.none"),TextWrapping=TextWrapping.Wrap,Opacity=.7};
}
