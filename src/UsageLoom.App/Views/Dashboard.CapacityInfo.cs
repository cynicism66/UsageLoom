using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private readonly TextBlock capacityInfoText=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=340,FontSize=12};
    private Flyout? capacityInfoFlyout;

    private void UpdateCapacityInfo()
    {
        if(capacityInfoFlyout is null)return;
        if(!app.Config.CapacityEnabled){capacityInfoFlyout.Hide();return;}
        var estimate=app.Quota.Fresh?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        capacityInfoText.Text=(estimate is null?"":(app.CapacityRevalidationPending?L10n.T("attribution.previousEstimate")+"\n":
            estimate.HistoricalAt is not null?L10n.T("capacity.historyReference")+"\n":"")+
            L10n.F("s99933FEC8200",estimate.DollarDisplay,UsageNumbers.Compact(estimate.EstimatedTokens),estimate.PricingCoverage)+"\n"+
            (estimate.HistoricalAt is {} saved?L10n.F("capacity.previousAt",saved.ToLocalTime())+"\n":""))+
            L10n.T("capacity.currentSamplingTitle")+"\n"+app.WeeklyCapacityProgress;
    }

    private Button CapacityInfoButton()
    {
        var button=new Button{Content=new FontIcon{Glyph="\uE946",FontSize=12},Width=22,Height=22,MinWidth=0,MinHeight=0,
            Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Center,
            Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(4)};
        AutomationProperties.SetName(button,L10n.T("capacity.info"));
        button.Click+=(_,_)=>
        {
            if(capacityInfoFlyout?.IsOpen==true){capacityInfoFlyout.Hide();return;}
            if(capacityInfoFlyout is null)
            {
                var presenterStyle=new Style(typeof(FlyoutPresenter));
                presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty,ScrollMode.Disabled));
                presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));
                presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty,ScrollMode.Disabled));
                presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));
                presenterStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12)));
                capacityInfoFlyout=new Flyout{Content=capacityInfoText,FlyoutPresenterStyle=presenterStyle};
            }
            UpdateCapacityInfo();
            // Anchor to the persistent window root, not the rebuilt quota card.
            // Refreshes can replace the button without dismissing the explanation.
            var host=(FrameworkElement)Content;
            // Constrain the content before measuring the presenter. Its padding
            // must fit as well, including in the compact window at high DPI.
            capacityInfoText.Width=Math.Max(1,Math.Min(340,host.ActualWidth-56));
            var position=button.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(button.ActualWidth/2,button.ActualHeight));
            capacityInfoFlyout.ShowAt(host,new FlyoutShowOptions{Position=position,Placement=FlyoutPlacementMode.Bottom});
        };
        return button;
    }
}
