using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private readonly TextBlock capacityInfoText=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=340};
    private Flyout? capacityInfoFlyout;

    private void UpdateCapacityInfo()
    {
        if(capacityInfoFlyout is null)return;
        if(!app.Config.CapacityEnabled){capacityInfoFlyout.Hide();return;}
        var estimate=app.Quota.Fresh?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        capacityInfoText.Text=(estimate is null?"":L10n.F("s99933FEC8200",estimate.DollarDisplay,UsageNumbers.Compact(estimate.EstimatedTokens),estimate.PricingCoverage)+"\n"+
            (estimate.HistoricalAt is {} saved?L10n.F("capacity.previousAt",saved.ToLocalTime())+"\n":""))+app.WeeklyCapacityProgress;
    }

    private Button CapacityInfoButton()
    {
        var button=new Button{Content=new FontIcon{Glyph="\uE946",FontSize=16},Width=32,Height=32,
            Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(button,L10n.T("capacity.info"));
        button.Click+=(_,_)=>
        {
            if(capacityInfoFlyout?.IsOpen==true){capacityInfoFlyout.Hide();return;}
            capacityInfoFlyout??=new Flyout{Content=capacityInfoText};
            UpdateCapacityInfo();
            // Anchor to the persistent window root, not the rebuilt quota card.
            // Refreshes can replace the button without dismissing the explanation.
            var host=(FrameworkElement)Content;
            var position=button.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(button.ActualWidth/2,button.ActualHeight));
            capacityInfoFlyout.ShowAt(host,new FlyoutShowOptions{Position=position,Placement=FlyoutPlacementMode.Bottom});
        };
        return button;
    }
}
