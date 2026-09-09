using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private UIElement AboutPanel()
    {
        var panel=new StackPanel{Spacing=16};
        panel.Children.Add(new TextBlock{Text="UsageLoom  "+Program.Version+L10n.T("s150A42617962"),FontSize=24,TextWrapping=TextWrapping.Wrap});
        var health=new StackPanel{Spacing=12};health.Children.Add(new TextBlock{Text=L10n.T("sE46B0272BD64"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        health.Children.Add(new TextBlock{Text=app.HistoryStatus,TextWrapping=TextWrapping.Wrap});health.Children.Add(new TextBlock{Text=L10n.T("s58D76C9D3286")+app.Quota.AccountLabel,TextWrapping=TextWrapping.Wrap});panel.Children.Add(Card(health));
        panel.Children.Add(Card(new TextBlock{Text=app.DiagnosticSummary,FontFamily=new FontFamily("Cascadia Mono"),FontSize=12,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true}));
        panel.Children.Add(new TextBlock{Text=L10n.T("s89327228F8FF"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        panel.Children.Add(Button(L10n.T("s1481ECABDF10"),async()=>await app.ScanAsync(verifyIntegrity:true)));
        panel.Children.Add(Button(L10n.T("s64525FE00BF7"),async()=>
        {
            var dialog=new ContentDialog
            {
                XamlRoot=((FrameworkElement)Content).XamlRoot,
                Title=L10n.T("sE135A1996A45"),
                Content=L10n.T("sC929716D2A4A"),
                PrimaryButtonText=L10n.T("s3A52FE1583F8"),CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close
            };
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await app.ScanAsync(rebuild:true);
        }));
        panel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sAA0BA9B301E8"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=L10n.T("sB22119FAE065")+Pricing.VerifiedDate+L10n.T("sF6F917118B67"),TextWrapping=TextWrapping.Wrap}}));
        actions.Children.Add(Button(L10n.T("s6F7EA9FAD61C"),()=>{var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(app.DiagnosticSummary);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);return Task.CompletedTask;}));
        return panel;
    }
}
