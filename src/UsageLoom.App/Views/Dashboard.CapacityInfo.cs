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
    private Button? capacityEvidenceButton;
    private bool capacityEvidenceDialogOpen;

    private void UpdateCapacityInfo()
    {
        if(capacityInfoFlyout is null)return;
        if(!app.Config.CapacityEnabled){capacityInfoFlyout.Hide();return;}
        var estimate=app.Quota.Fresh?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        var evidenceEstimate=app.Quota.Fresh?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedTokens>0):null;
        if(compact)
        {
            var summary=estimate is null?"":L10n.T(app.CapacityRevalidationPending?"capacity.compactRevalidatingEstimate":estimate.HistoricalAt is null?"capacity.compactCurrentEstimate":"capacity.compactHistoricalEstimate")+"\n"+
                L10n.F("s99933FEC8200",estimate.DollarDisplay,UsageNumbers.Compact(estimate.EstimatedTokens),estimate.PricingCoverage)+"\n";
            if(estimate?.HistoricalAt is {} time)summary+=L10n.F("capacity.compactHistoricalAt",time.ToLocalTime())+"\n";
            var evidence=evidenceEstimate is null?"":evidenceEstimate.HasCurrentPricingEvidence?
                L10n.F("capacity.compactEvidence",evidenceEstimate.PricingProfile!.ActualCoverage,evidenceEstimate.PricingProfile.RequestedCoverage,evidenceEstimate.PricingProfile.UnknownCoverage):L10n.T("capacity.compactEvidenceLegacy");
            capacityInfoText.Text=summary+app.WeeklyCapacityCompactProgress+(evidence.Length==0?"":"\n"+evidence);
        }
        else
        capacityInfoText.Text=(estimate is null?"":(app.CapacityRevalidationPending?L10n.T("attribution.previousEstimate")+"\n":
            estimate.HistoricalAt is not null?L10n.T("capacity.historyReference")+"\n":"")+
            L10n.F("s99933FEC8200",estimate.DollarDisplay,UsageNumbers.Compact(estimate.EstimatedTokens),estimate.PricingCoverage)+"\n"+
            (estimate.HistoricalAt is {} saved?L10n.F("capacity.previousAt",saved.ToLocalTime())+"\n":""))+
            L10n.T("capacity.currentSamplingTitle")+"\n"+app.WeeklyCapacityProgress+
            (evidenceEstimate is null?"":"\n"+evidenceEstimate.EvidenceSummary);
        if(capacityEvidenceButton is not null)capacityEvidenceButton.Visibility=Visibility.Visible;
    }

    private async Task ShowCapacityPricingDetails(WeeklyCapacityEstimate? selected=null)
    {
        var estimate=selected??app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedTokens>0);
        if(capacityEvidenceDialogOpen)return;
        capacityEvidenceDialogOpen=true;
        try
        {
            capacityInfoFlyout?.Hide();
            var body=new StackPanel{Spacing=10};
            void Line(string text,bool heading=false)=>body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,
                FontSize=13,FontWeight=heading?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal});
            if(estimate?.HistoricalAt is {} saved)Line(L10n.T("capacity.historyReference")+"\n"+L10n.F("capacity.previousAt",saved.ToLocalTime()),true);
            Line(L10n.T("capacity.currentSamplingTitle"),true);
            Line(app.WeeklyCapacityProgress);
            Line(L10n.T("capacity.valuationWorkload"));
            if(estimate is not null)
            {
            Line(L10n.F("capacity.valuationCoverage",estimate.PricingCoverage));
            Line(estimate.EvidenceSummary,true);
            if(!estimate.HasCurrentPricingEvidence)Line(L10n.T("capacity.legacyEvidenceDetails"));
            else
            {
                var profile=estimate.PricingProfile!;
                if(profile.ConditionalTokens>0||profile.UnknownTokens>0||profile.RequestedTokens>0)
                    Line(L10n.T("capacity.valuationConditional"));
                Line(L10n.F("capacity.evidenceCounts",profile.ActualTokens,profile.RequestedTokens,profile.UnknownTokens));
                Line(L10n.F("capacity.valuationConditionalCount",profile.ConditionalTokens,100d*profile.ConditionalTokens/profile.Tokens));
                Line(L10n.T(profile.IsMixed?"capacity.workloadMixed":"capacity.workloadSingle"));
                void Composition(string title,IEnumerable<KeyValuePair<string,long>> values,bool modes=false)
                {
                    Line(L10n.T(title),true);
                    foreach(var item in values.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal))
                    {
                        var label=item.Key=="unknown"?L10n.T("capacity.mode.unknown"):
                            modes&&item.Key is "standard" or "fast"?L10n.T("capacity.mode."+item.Key):
                            modes?L10n.T("capacity.mode.other")+" ("+item.Key+")":item.Key;
                        Line(L10n.F("capacity.compositionItem",label,item.Value,100d*item.Value/profile.Tokens));
                    }
                }
                Composition("capacity.modeComposition",profile.Modes,true);
                Composition("capacity.modelComposition",profile.Models);
                Composition("capacity.reasoningComposition",profile.Reasoning);
            }
            }
            else Line(L10n.T("capacity.noValuationEvidence"));
            var host=(FrameworkElement)Content;
            await new ContentDialog{XamlRoot=host.XamlRoot,Title=L10n.T("capacity.valuationDetails"),
                Content=new ScrollViewer{Content=body,MaxHeight=Math.Max(120,Math.Min(440,host.ActualHeight-180)),
                    HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},
                CloseButtonText=L10n.T("s3FD47EDCE45B")}.ShowAsync();
        }
        finally{capacityEvidenceDialogOpen=false;}
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
                var content=new StackPanel{Spacing=8};content.Children.Add(capacityInfoText);
                capacityEvidenceButton=new Button{Content=new TextBlock{Text=L10n.T(compact?"capacity.compactDetails":"capacity.valuationDetails"),FontSize=12,TextWrapping=TextWrapping.Wrap},
                    HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(8,4,8,4)};
                capacityEvidenceButton.Click+=async(_,_)=>await ShowCapacityPricingDetails();
                content.Children.Add(capacityEvidenceButton);
                capacityInfoFlyout=new Flyout{Content=content,FlyoutPresenterStyle=presenterStyle};
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
