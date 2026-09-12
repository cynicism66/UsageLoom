using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private UIElement CapacitySettings()
    {
        var quota=app.Quota;
        var estimates=new StackPanel{Spacing=10};
        var enabled=new ToggleSwitch{Header=L10n.T("s290DF536AEA0"),IsOn=app.Config.CapacityEnabled,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        enabled.Toggled+=async(_,_)=>await app.SetCapacityEnabledAsync(enabled.IsOn);
        estimates.Children.Add(enabled);
        estimates.Children.Add(new TextBlock{Text=L10n.T("s12A977A8C9EB"),TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(new TextBlock{Text=L10n.T("s49E8E1E2DCE1"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        if(app.Config.CapacityEnabled&&quota.Fresh&&quota.HasQuotaDisplay&&app.WeeklyCapacity.Any(e=>e.ObservedPercent>=5&&e.Samples>=2))
        {
            foreach(var estimate in app.WeeklyCapacity.Where(e=>e.ObservedPercent>=5&&e.Samples>=2))
            {
                if(estimate.HistoricalAt is {} historical)estimates.Children.Add(new TextBlock{Text=L10n.F("capacity.previousAt",historical.ToLocalTime()),TextWrapping=TextWrapping.Wrap});
                estimates.Children.Add(ResponsiveCards(new List<UIElement>{
                    Metric(L10n.T("s53D9E8B59877"),estimate.DollarDisplay,L10n.F("s1EA0E159E65E", estimate.PricingCoverage)),
                    Metric(L10n.T("s758E9EDBBD77"),$"{estimate.EstimatedTokens:N0}",L10n.F("s46B941557FE8", estimate.Confidence))},2,300));
                estimates.Children.Add(new TextBlock{Text=L10n.F("s779F484C5099", estimate.Samples, estimate.ObservedTokens, estimate.ObservedPercent)+(estimate.ExcludedIntervals>0?L10n.F("sBF4A33F67127", estimate.ExcludedIntervals):""),TextWrapping=TextWrapping.Wrap,Opacity=.7});
                estimates.Children.Add(new TextBlock{Text=estimate.DollarLow is {} low&&estimate.DollarHigh is {} high?L10n.F("capacity.range",low,high):L10n.T("capacity.rangePending"),TextWrapping=TextWrapping.Wrap,Opacity=.7});
            }
        }
        estimates.Children.Add(new TextBlock{Text=L10n.T("capacity.currentSamplingTitle"),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        estimates.Children.Add(new TextBlock{Text=app.WeeklyCapacityProgress,TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(new TextBlock{Text=app.CapacityCacheStatus,FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(Button(L10n.T("s4C6D9D73BFC8"),ShowCapacityHistory));
        estimates.Children.Add(Button(L10n.T("capacity.calculateNow"),async()=>await app.CalculateCapacityNowAsync()));
        foreach(var action in new[]{"repair","clear","restart"})
            estimates.Children.Add(Button(L10n.T("maintenance."+action),()=>ShowCapacityMaintenance(action)));
        if(app.NavigationCheck)
        {
            foreach(var action in new[]{"repair","clear","restart"})
                if(estimates.Children.OfType<Button>().Count(b=>b.Content as string==L10n.T("maintenance."+action))!=1)
                    throw new InvalidOperationException("Capacity maintenance entry missing: "+action);
            Program.Log.Write("INFO","NavigationTest","Three capacity maintenance entries attached to estimate settings");
        }
        capacityStatusText=new TextBlock{Text=app.CapacityCalculationStatus,TextWrapping=TextWrapping.Wrap};
        estimates.Children.Add(capacityStatusText);
        estimates.Children.Add(new TextBlock{Text=L10n.T("capacity.batchNote"),TextWrapping=TextWrapping.Wrap,Opacity=.65});
        estimates.Children.Add(new TextBlock{Text=L10n.T("capacity.retentionNote"),TextWrapping=TextWrapping.Wrap,Opacity=.65});
        var calculation=new StackPanel{Spacing=10};
        calculation.Children.Add(new TextBlock{Text=L10n.T("s0F58A8B1B0E1"),TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=.7});
        calculation.Children.Add(new TextBlock{Text=L10n.T("capacity.stableNote"),TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=.7});
        estimates.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("s6B5C96B6A49F"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=calculation}));
        return StableExpander.Configure(new Expander{Header=L10n.T("sD9EBFF4C171F"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=estimates});
    }
    private bool capacityMaintenanceDialogOpen;
    private async Task ShowCapacityMaintenance(string action)
    {
        if(capacityMaintenanceDialogOpen)return;
        capacityMaintenanceDialogOpen=true;
        try
        {
            var title=L10n.T("maintenance."+action);
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=title,
                Content=new TextBlock{Text=L10n.T("maintenance."+action+"Confirm"),TextWrapping=TextWrapping.Wrap},
                PrimaryButtonText=title,CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            string report;
            try
            {
                report=await app.MaintainCapacityAsync(action);
                if(action=="repair"&&app.Quota.AccountKey is {} account)
                {
                    var candidates=app.CapacityRepairCandidates();
                    if(candidates.Count>0)await ShowAttribution(candidates,account);
                    await app.CalculateCapacityNowAsync();
                    report+="\n"+L10n.F("maintenance.remaining",app.CapacityRepairCandidates().Count)+"\n"+app.CapacityCalculationStatus;
                }
            }
            catch(Exception ex){report=L10n.T("maintenance.failed")+"\n"+Privacy.Redact(ex.Message);}
            await new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=title,
                Content=new ScrollViewer{MaxHeight=360,Content=new TextBlock{Text=report,TextWrapping=TextWrapping.Wrap}},
                CloseButtonText=L10n.T("s2CD0F3BE8738")}.ShowAsync();
        }
        finally{capacityMaintenanceDialogOpen=false;}
    }
    private async Task ShowAttribution(List<UsageEvent> rows,string account)
    {
        var dates=rows.Where(e=>e.AccountScope is null&&e.Timestamp is not null).Select(e=>e.LocalDate).Distinct().OrderDescending().ToList();
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(new TextBlock{Text=L10n.T("attribution.explanation"),TextWrapping=TextWrapping.Wrap});
        var date=new ComboBox{Header=L10n.T("attribution.date"),ItemsSource=dates,SelectedIndex=dates.Count>0?0:-1};panel.Children.Add(date);
        var summary=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(summary);
        var acknowledgment=new CheckBox{Content=L10n.T("attribution.ack")};panel.Children.Add(acknowledgment);
        var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("attribution.inspect"),Content=panel,
            PrimaryButtonText=L10n.T("attribution.confirm"),CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
        List<UsageEvent> preview=[];
        void Update()
        {
            preview=rows.Where(e=>e.LocalDate==date.SelectedItem as string&&e.AccountScope is null&&e.Timestamp is not null).ToList();
            var other=rows.Where(e=>e.AccountScope is not null&&e.AccountScope!=account).Sum(e=>e.Tokens.Total);
            var unknown=rows.Where(e=>e.AccountScope is null&&e.Timestamp is null).Sum(e=>e.Tokens.Total);
            summary.Text=L10n.F("attribution.preview",preview.Count,UsageNumbers.Compact(preview.Sum(e=>e.Tokens.Total)),UsageNumbers.Compact(other),UsageNumbers.Compact(unknown));
            dialog.IsPrimaryButtonEnabled=acknowledgment.IsChecked==true&&preview.Count>0&&app.Quota.Fresh&&app.Quota.AccountKey==account&&!app.IsDemo;
        }
        date.SelectionChanged+=(_,_)=>{acknowledgment.IsChecked=false;Update();};
        acknowledgment.Checked+=(_,_)=>Update();acknowledgment.Unchecked+=(_,_)=>Update();Update();
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        try{await app.ConfirmAttributionAsync(preview,account);}
        catch(Exception){await new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("attribution.retry"),Content=L10n.T("attribution.unchanged"),CloseButtonText=L10n.T("s2CD0F3BE8738")}.ShowAsync();}
    }
    private async Task ShowCapacityHistory()

    {
        var entries=new StackPanel{Spacing=12};var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        var content=new StackPanel{Spacing=12};var pageIndex=0;
        content.Children.Add(new TextBlock{Text=L10n.T("s031841781B24"),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new ScrollViewer{Content=entries,MaxHeight=440,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});content.Children.Add(controls);
        var previous=new Button{Content=L10n.T("sC9B9AE7A6144"),IsEnabled=false};var next=new Button{Content=L10n.T("s8A8542F69648")};var pageLabel=new TextBlock{VerticalAlignment=VerticalAlignment.Center};
        controls.Children.Add(previous);controls.Children.Add(pageLabel);controls.Children.Add(next);
        async Task Load()
        {
            previous.IsEnabled=next.IsEnabled=false;
            try
            {
                var records=await app.ReadCapacityHistoryAsync(pageIndex);entries.Children.Clear();
                if(records.Count==0)entries.Children.Add(new TextBlock{Text=L10n.T("s2B3447EE0CCC"),TextWrapping=TextWrapping.Wrap});
                foreach(var record in records)foreach(var sample in record.Windows)
                {
                    var ready=sample.Percent>=5&&sample.Samples>=2;
                    var same=record.Account==app.Quota.AccountKey;
                    var scope=record.Version==4&&record.PricingVersion==Pricing.CatalogVersion?L10n.T("s266D6CF4495A"):L10n.T("s6662BBA41E7D");
                    var dollars=sample.Priced>0?$"${sample.Cost*100m/(decimal)sample.Percent:N2}":L10n.T("s2CE9AC771C39");
                    entries.Children.Add(new TextBlock{Text=L10n.F("s403BBCA72EE4", record.SavedAt.ToLocalTime(), (same?L10n.T("sB56996D44E4A"):L10n.T("s92E27D68CB29")), record.Plan, sample.ResetsAt.ToLocalTime(), (ready?L10n.T("s86011ED09937"):L10n.T("s2C63A632051B")), scope, record.Version, dollars, UsageNumbers.Compact(sample.Tokens*100d/sample.Percent), 100d*sample.Priced/sample.Tokens, sample.Samples, sample.Percent, record.PricingVersion),TextWrapping=TextWrapping.Wrap,FontSize=13});
                }
                previous.IsEnabled=pageIndex>0;next.IsEnabled=records.Count==20;pageLabel.Text=L10n.F("s94289AA33F49", pageIndex+1);
            }
            catch(Exception ex){entries.Children.Clear();entries.Children.Add(new TextBlock{Text=L10n.T("s1B070A03BD9B")+Privacy.Redact(ex.Message),TextWrapping=TextWrapping.Wrap});previous.IsEnabled=pageIndex>0;}
        }
        previous.Click+=async(_,_)=>{pageIndex=Math.Max(0,pageIndex-1);await Load();};next.Click+=async(_,_)=>{pageIndex++;await Load();};
        await Load();
        await new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("s4C6D9D73BFC8"),Content=content,CloseButtonText=L10n.T("s3FD47EDCE45B")}.ShowAsync();
    }
}
