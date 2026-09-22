using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private CompactContent? compactContent;

    private sealed class CompactQuotaRow
    {
        internal readonly StackPanel Root=new(){Spacing=6,Height=70};
        internal readonly TextBlock Name=CompactText(13,38,true);
        internal readonly TextBlock Value=CompactText(28,38);
        internal readonly ProgressBar Progress=new(){Minimum=0,Maximum=100,Height=4};
        internal readonly TextBlock Reset=CompactText(12,16);

        internal CompactQuotaRow(int index)
        {
            var values=CompactColumns(10,110);values.Height=38;
            Value.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
            Value.TextAlignment=TextAlignment.Right;
            values.Children.Add(Name);Grid.SetColumn(Value,1);values.Children.Add(Value);
            Reset.Opacity=.65;
            Root.Children.Add(values);Root.Children.Add(Progress);Root.Children.Add(Reset);
            AutomationProperties.SetAutomationId(Value,$"compact-quota-value-{index}");
            AutomationProperties.SetAutomationId(Progress,$"compact-quota-progress-{index}");
            AutomationProperties.SetAutomationId(Reset,$"compact-quota-reset-{index}");
        }
    }

    private sealed class CompactContent
    {
        internal readonly StackPanel Limits=new(){Spacing=12};
        internal readonly List<CompactQuotaRow> Windows=[];
        internal readonly TextBlock Unavailable=CompactText(13,70,true);
        internal readonly TextBlock Tokens=CompactText(22,30);
        internal readonly TextBlock Requests=CompactText(22,30);
        internal readonly TextBlock Capacity=CompactText(12,40,true);
        internal readonly TextBlock Footer=CompactText(12,18);
        internal readonly ContentControl Plan=new(){HorizontalContentAlignment=HorizontalAlignment.Right,VerticalContentAlignment=VerticalAlignment.Center,MaxWidth=110,Height=32};
        internal readonly TextBlock ProviderTitle=CompactText(13,32);
        internal readonly Grid CapacityRow=CompactColumns(6,22);
        internal readonly Grid PlanRow=CompactColumns(8,110);
        internal Border QuotaCard=null!;
        internal Border UsageCard=null!;
        internal readonly TextBlock Disabled=CompactText(12,40,true);
        internal Border ClaudeCard=null!;
        internal readonly TextBlock ClaudeTitle=CompactText(12,24);
        internal readonly TextBlock ClaudePlan=CompactText(11,24);
        internal readonly TextBlock[] ClaudeValues=[CompactText(12,18),CompactText(12,18)];
        internal readonly ProgressBar[] ClaudeProgress=[new(){Minimum=0,Maximum=100,Height=4},new(){Minimum=0,Maximum=100,Height=4}];
        internal readonly TextBlock[] ClaudeResets=[CompactText(11,16),CompactText(11,16)];
        internal string? PlanKey;
    }

    private static TextBlock CompactText(double size,double height,bool wrap=false)=>new()
    {
        FontSize=size,Height=height,TextWrapping=wrap?TextWrapping.Wrap:TextWrapping.NoWrap,
        MaxLines=wrap?2:1,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center
    };
    private static Grid CompactColumns(double spacing,double lastWidth)
    {
        var grid=new Grid{ColumnSpacing=spacing};
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.ColumnDefinitions.Add(new(){Width=double.IsNaN(lastWidth)?new GridLength(1,GridUnitType.Star):new GridLength(lastWidth)});
        return grid;
    }
    private void EnsureCompactContent()
    {
        if(compactContent is not null)return;
        var view=compactContent=new CompactContent();
        Border CompactCard(UIElement content,string id)
        {
            var card=Card(content);card.Padding=new Thickness(14,12,14,12);
            AutomationProperties.SetAutomationId(card,id);return card;
        }
        var quota=new Grid();quota.Children.Add(view.Limits);quota.Children.Add(view.Unavailable);
        view.PlanRow.Height=32;view.PlanRow.Children.Add(view.ProviderTitle);
        Grid.SetColumn(view.Plan,1);view.PlanRow.Children.Add(view.Plan);
        AutomationProperties.SetAutomationId(view.PlanRow,"compact-plan-row");
        var quotaBody=new StackPanel();quotaBody.Children.Add(view.PlanRow);quotaBody.Children.Add(quota);
        view.QuotaCard=CompactCard(quotaBody,"compact-quota-card");quotaPanel.Children.Add(view.QuotaCard);
        var claude=new StackPanel{Spacing=2};var claudeHeader=CompactColumns(6,140);claudeHeader.Height=24;
        claudeHeader.Children.Add(view.ClaudeTitle);Grid.SetColumn(view.ClaudePlan,1);view.ClaudePlan.TextAlignment=TextAlignment.Right;claudeHeader.Children.Add(view.ClaudePlan);
        AutomationProperties.SetAutomationId(view.ClaudePlan,"compact-claude-plan");claude.Children.Add(claudeHeader);
        for(var i=0;i<2;i++)
        {
            view.ClaudeProgress[i].Foreground=accent;
            AutomationProperties.SetAutomationId(view.ClaudeProgress[i],$"compact-claude-progress-{i}");
            claude.Children.Add(view.ClaudeValues[i]);claude.Children.Add(view.ClaudeProgress[i]);claude.Children.Add(view.ClaudeResets[i]);
        }
        view.ClaudeCard=CompactCard(claude,"compact-claude-card");view.ClaudeCard.Height=136;
        view.ClaudeCard.Visibility=app.Config.ClaudeEnabled?Visibility.Visible:Visibility.Collapsed;quotaPanel.Children.Add(view.ClaudeCard);
        var usage=CompactColumns(16,double.NaN);
        StackPanel Metric(string title,TextBlock value,string id)
        {
            var metric=new StackPanel{Spacing=4};var label=CompactText(12,18);label.Text=title;
            AutomationProperties.SetAutomationId(value,id);metric.Children.Add(label);metric.Children.Add(value);return metric;
        }
        usage.Children.Add(Metric("Codex · "+L10n.T("s8B4B6EA0DA7D"),view.Tokens,"compact-token-value"));
        var requests=Metric(L10n.T("sBDBF4C48B0A9"),view.Requests,"compact-request-value");
        Grid.SetColumn(requests,1);usage.Children.Add(requests);view.UsageCard=CompactCard(usage,"compact-usage-card");quotaPanel.Children.Add(view.UsageCard);
        view.Disabled.Text=L10n.T("source.none");quotaPanel.Children.Add(view.Disabled);
        view.Capacity.Opacity=.75;view.CapacityRow.Height=40;
        view.CapacityRow.Children.Add(view.Capacity);
        var info=CapacityInfoButton();Grid.SetColumn(info,1);view.CapacityRow.Children.Add(info);
        AutomationProperties.SetAutomationId(view.CapacityRow,"compact-capacity-row");quotaPanel.Children.Add(view.CapacityRow);
        var footer=CompactColumns(8,32);footer.Height=32;view.Footer.Opacity=.6;footer.Children.Add(view.Footer);
        var settings=new Button{Content=new FontIcon{Glyph="\uE713",FontSize=18},Width=32,Height=32,Padding=new Thickness(0),
            Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6)};
        ToolTipService.SetToolTip(settings,L10n.T("sDF3D58C7D84B"));
        AutomationProperties.SetName(settings,L10n.T("sDF3D58C7D84B"));
        settings.Click+=(_,_)=>{capacityInfoFlyout?.Hide();app.ShowSettings();if(!pinned)Hide();};
        Grid.SetColumn(settings,1);footer.Children.Add(settings);
        AutomationProperties.SetAutomationId(footer,"compact-footer-row");quotaPanel.Children.Add(footer);
    }
    private void UpdateCompactContent()
    {
        UpdateCompactClaude();
        var view=compactContent!;var quota=app.Quota;
        var codexVisibility=app.Config.CodexEnabled?Visibility.Visible:Visibility.Collapsed;
        view.QuotaCard.Visibility=codexVisibility;view.UsageCard.Visibility=codexVisibility;
        view.Disabled.Visibility=!app.Config.CodexEnabled&&!app.Config.ClaudeEnabled?Visibility.Visible:Visibility.Collapsed;
        if(!app.Config.CodexEnabled)capacityInfoFlyout?.Hide();
        var windows=quota.HasQuotaDisplay?quota.PrimaryWindows.ToArray():[];
        for(var index=0;index<windows.Length;index++)
        {
            if(index==view.Windows.Count)
            {
                var added=new CompactQuotaRow(index);added.Progress.Foreground=accent;
                view.Windows.Add(added);view.Limits.Children.Add(added.Root);
            }
            var row=view.Windows[index];var window=windows[index];row.Root.Visibility=Visibility.Visible;
            row.Name.Text=window.Label+L10n.T("sD6822B04178D");row.Value.Text=window.RemainingText;
            row.Progress.Value=window.Remaining;
            row.Reset.Text=window.ResetCountdown(DateTimeOffset.Now)+(quota.Fresh?"":L10n.T("s6749C5BF4AEA"));
            ToolTipService.SetToolTip(row.Reset,row.Reset.Text);
        }
        for(var index=windows.Length;index<view.Windows.Count;index++)view.Windows[index].Root.Visibility=Visibility.Collapsed;
        // Offline/unavailable states retain the quota slot; ordinary values never
        // change row heights or cause the whole Viewbox to rescale.
        var slots=Math.Max(1,view.Windows.Count);
        view.QuotaCard.Height=58+slots*70+(slots-1)*12;
        view.Limits.Visibility=windows.Length>0?Visibility.Visible:Visibility.Collapsed;
        view.Unavailable.Visibility=windows.Length>0?Visibility.Collapsed:Visibility.Visible;
        view.Unavailable.Text=quota.IsLocalAccount?L10n.T("sF2D563561B79"):L10n.T("sF7A79B776C96");
        ToolTipService.SetToolTip(view.Unavailable,quota.Status);
        var today=DateTime.Today.ToString("yyyy-MM-dd");
        var rows=app.Events.Where(item=>item.LocalDate==today).ToList();var tokens=rows.Sum(item=>item.Tokens.Total);
        view.Tokens.Text=app.HasLoadedHistory?UsageNumbers.Compact(tokens):L10n.T("sD04FCBDA737F");
        view.Requests.Text=app.HasLoadedHistory?rows.Count.ToString("N0"):L10n.T("sD04FCBDA737F");
        ToolTipService.SetToolTip(view.Tokens,$"{tokens:N0} Token");
        ToolTipService.SetToolTip(view.Requests,rows.Count.ToString("N0")+" · "+L10n.T("sA19F66EB1728"));
        var estimate=quota.Fresh&&quota.HasQuotaDisplay?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        view.Capacity.Text=estimate?.HistoricalAt is not null?L10n.T("capacity.previous")+": "+estimate.DollarDisplay:
            estimate is not null?L10n.F("s99933FEC8200",estimate.DollarDisplay,UsageNumbers.Compact(estimate.EstimatedTokens),estimate.PricingCoverage):
            quota.Fresh&&windows.Any(window=>window.Minutes==10080)?L10n.T("s51D06B357E84"):L10n.T("s5AFCDACD073E");
        view.CapacityRow.Visibility=app.Config.CodexEnabled&&app.Config.CapacityEnabled?Visibility.Visible:Visibility.Collapsed;
        var planKey=$"{quota.Plan}|{quota.IsLocalAccount}|{new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast}";
        if(view.PlanKey!=planKey)
        {
            view.PlanKey=planKey;var badge=PlanBadge(quota with{Fresh=true},true);
            if(badge is TextBlock text){text.TextWrapping=TextWrapping.NoWrap;text.TextTrimming=TextTrimming.CharacterEllipsis;ToolTipService.SetToolTip(text,quota.PlanDisplay);}
            view.Plan.Content=badge;
        }
        // Keep freshness outside the badge so a transient stale snapshot neither
        // rebuilds the badge nor inserts another line below it.
        view.ProviderTitle.Text="Codex"+(!quota.Fresh&&!quota.IsLocalAccount?" · "+L10n.T("s4F6E26963CA2"):"");
        AutomationProperties.SetName(view.PlanRow,quota.PlanDisplay);
        var updated=quota.FetchedAt is {} at?L10n.F("s6843540FA5C7",at.ToLocalTime()):L10n.T("s0D4EDA666026");
        var resets=quota.HasQuotaDisplay&&quota.ResetCount is {} count?L10n.F("s26ABA9EC2EFB",count):L10n.T("s382254F4153B");
        view.Footer.Text=app.Config.CodexEnabled?$"{resets}   ·   {updated}":"";ToolTipService.SetToolTip(view.Footer,view.Footer.Text);
    }
}
