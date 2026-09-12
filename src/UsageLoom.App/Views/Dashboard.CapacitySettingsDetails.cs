using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private Expander? capacitySettingsExpander;
    private Expander? capacityValuationExpander;
    private StackPanel? capacityValuationContent;
    private StackPanel? capacityEstimateContent;
    private TextBlock? capacityProgressText;
    private TextBlock? capacityCacheText;
    private string? capacityValuationState;
    private string? capacityEstimateState;
    private bool capacitySettingsRevealPending;

    internal void ShowCapacitySettings()
    {
        capacitySettingsRevealPending=true;
        ShowPage("settings");
        RevealCapacitySettings();
    }

    private void RevealCapacitySettings()
    {
        if(released||!capacitySettingsRevealPending||selectedPage!="settings"||
            capacitySettingsExpander is not {} section||capacityValuationExpander is not {} details)return;
        section.IsExpanded=true;
        details.IsExpanded=true;
        if(!section.IsLoaded)return;
        capacitySettingsRevealPending=false;
        // Run after the page is attached and the expanded content is measured.
        // Ordinary data refreshes never scroll the user's settings page.
        DispatcherQueue.TryEnqueue(()=>
        {
            if(released||selectedPage!="settings"||!ReferenceEquals(details,capacityValuationExpander))return;
            ((FrameworkElement)Content).UpdateLayout();
            details.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=0});
        });
    }

    private void UpdateCapacitySettingsDetails()
    {
        if(compact||selectedPage!="settings"||capacityValuationContent is null)return;
        if(capacityProgressText is not null)capacityProgressText.Text=app.WeeklyCapacityCompactProgress;
        if(capacityCacheText is not null)capacityCacheText.Text=app.CapacityCacheStatus;
        UpdateCapacitySettingsEstimate();

        var rows=new List<(string Text,bool Heading)>();
        void Line(string text,bool heading=false)=>rows.Add((text,heading));
        var estimate=app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedTokens>0);
        if(estimate is not null&&!app.Quota.Fresh)Line(L10n.T("capacity.cachedEvidence"),true);
        if(app.CapacityRevalidationPending)Line(L10n.T("capacity.revalidating"),true);
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
                double Percent(long tokens)=>profile.Tokens>0?100d*tokens/profile.Tokens:0;
                if(profile.ConditionalTokens>0||profile.UnknownTokens>0||profile.RequestedTokens>0)
                    Line(L10n.T("capacity.valuationConditional"));
                Line(L10n.F("capacity.evidenceCounts",profile.ActualTokens,profile.RequestedTokens,profile.UnknownTokens));
                Line(L10n.F("capacity.valuationConditionalCount",profile.ConditionalTokens,Percent(profile.ConditionalTokens)));
                Line(L10n.T(profile.IsMixed?"capacity.workloadMixed":"capacity.workloadSingle"));
                void Composition(string title,IEnumerable<KeyValuePair<string,long>> values,bool modes=false)
                {
                    Line(L10n.T(title),true);
                    foreach(var item in values.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.Ordinal))
                    {
                        var label=item.Key=="unknown"?L10n.T("capacity.mode.unknown"):
                            modes&&item.Key is "standard" or "fast"?L10n.T("capacity.mode."+item.Key):
                            modes?L10n.T("capacity.mode.other")+" ("+item.Key+")":item.Key;
                        Line(L10n.F("capacity.compositionItem",label,item.Value,Percent(item.Value)));
                    }
                }
                Composition("capacity.modeComposition",profile.Modes,true);
                Composition("capacity.modelComposition",profile.Models);
                Composition("capacity.reasoningComposition",profile.Reasoning);
            }
        }
        else Line(L10n.T("capacity.noValuationEvidence"));
        var state=string.Join("\n",rows.Select(row=>$"{row.Heading}:{row.Text}"));
        if(state==capacityValuationState)return;
        capacityValuationState=state;
        // Only replace the read-only details. Keep expanders and unsaved settings.
        capacityValuationContent.Children.Clear();
        foreach(var row in rows)capacityValuationContent.Children.Add(new TextBlock{Text=row.Text,TextWrapping=TextWrapping.Wrap,
            FontSize=13,FontWeight=row.Heading?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal});
    }

    private void UpdateCapacitySettingsEstimate()
    {
        if(capacityEstimateContent is null)return;
        var estimates=app.Config.CapacityEnabled&&app.Quota.Fresh&&app.Quota.HasQuotaDisplay?
            app.WeeklyCapacity.Where(e=>e.ObservedPercent>=5&&e.Samples>=2).ToArray():[];
        var state=L10n.Language+"|"+app.CapacityRevalidationPending+"|"+string.Join("\n",estimates.Select(e=>
            $"{e.HistoricalAt:O}|{e.DollarDisplay}|{e.EstimatedTokens}|{e.PricingCoverage}|{e.Confidence}|{e.Samples}|{e.ObservedTokens}|{e.ObservedPercent}|{e.ExcludedIntervals}|{e.DollarLow}|{e.DollarHigh}"));
        if(state==capacityEstimateState)return;
        capacityEstimateState=state;
        capacityEstimateContent.Children.Clear();
        if(app.CapacityRevalidationPending)capacityEstimateContent.Children.Add(new TextBlock{Text=L10n.T("capacity.revalidating"),TextWrapping=TextWrapping.Wrap});
        foreach(var estimate in estimates)
        {
            if(estimate.HistoricalAt is {} saved)capacityEstimateContent.Children.Add(new TextBlock{Text=L10n.F("capacity.previousAt",saved.ToLocalTime()),TextWrapping=TextWrapping.Wrap});
            capacityEstimateContent.Children.Add(ResponsiveCards(new List<UIElement>{
                Metric(L10n.T("s53D9E8B59877"),estimate.DollarDisplay,L10n.F("s1EA0E159E65E",estimate.PricingCoverage)),
                Metric(L10n.T("s758E9EDBBD77"),$"{estimate.EstimatedTokens:N0}",L10n.F("s46B941557FE8",estimate.Confidence))},2,300));
            capacityEstimateContent.Children.Add(new TextBlock{Text=L10n.F("s779F484C5099",estimate.Samples,estimate.ObservedTokens,estimate.ObservedPercent)+
                (estimate.ExcludedIntervals>0?L10n.F("sBF4A33F67127",estimate.ExcludedIntervals):""),TextWrapping=TextWrapping.Wrap,Opacity=.7});
            capacityEstimateContent.Children.Add(new TextBlock{Text=estimate.DollarLow is {} low&&estimate.DollarHigh is {} high?
                L10n.F("capacity.range",low,high):L10n.T("capacity.rangePending"),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        }
    }
}
