using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private static FrameworkElement PlanBadge(QuotaState quota,bool small)
    {
        var plan=quota.Plan?.Trim().ToLowerInvariant();
        if(quota.IsLocalAccount||plan is not ("prolite" or "pro"))return new TextBlock{Text=quota.PlanDisplay,FontSize=13,Opacity=.7,TextWrapping=TextWrapping.Wrap};
        var gold=plan=="pro";var label=gold?"Pro 20X":"Pro 5X";
        var stack=new StackPanel{Spacing=3,HorizontalAlignment=HorizontalAlignment.Left};
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            stack.Children.Add(new TextBlock{Text=label,FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold});
        }
        else
        {
            var layers=new Grid{Padding=new Thickness(1,0,3,3)};
            void Layer(Windows.UI.Color color,double y,double x=0)
            {
                layers.Children.Add(new TextBlock{Text=label,FontFamily=new FontFamily("Segoe UI Variable Display"),FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold,CharacterSpacing=30,
                    Foreground=new SolidColorBrush(color),RenderTransform=new TranslateTransform{X=x,Y=y}});
            }
            Layer(ColorHelper.FromArgb(200,0,0,0),3,1);
            Layer(gold?ColorHelper.FromArgb(255,116,76,18):ColorHelper.FromArgb(255,89,101,119),2);
            Layer(gold?ColorHelper.FromArgb(255,184,130,39):ColorHelper.FromArgb(255,153,166,185),1);
            Layer(gold?ColorHelper.FromArgb(255,255,222,137):ColorHelper.FromArgb(255,237,243,251),0);
            var background=new LinearGradientBrush{StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(0,1)};
            background.GradientStops.Add(new GradientStop{Offset=0,Color=gold?ColorHelper.FromArgb(255,83,67,39):ColorHelper.FromArgb(255,69,77,89)});
            background.GradientStops.Add(new GradientStop{Offset=.48,Color=ColorHelper.FromArgb(255,36,39,45)});
            background.GradientStops.Add(new GradientStop{Offset=1,Color=ColorHelper.FromArgb(255,25,27,32)});
            stack.Children.Add(new Border{Child=layers,Background=background,CornerRadius=new CornerRadius(7),Padding=new Thickness(small?8:12,2,small?8:12,2),BorderThickness=new Thickness(1),
                BorderBrush=new SolidColorBrush(gold?ColorHelper.FromArgb(255,161,128,68):ColorHelper.FromArgb(255,135,147,164))});
        }
        if(!quota.Fresh)stack.Children.Add(new TextBlock{Text=L10n.T("s4F6E26963CA2"),FontSize=11,Opacity=.65});
        ToolTipService.SetToolTip(stack,L10n.F("s43DABF58694C", quota.Plan));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack,label+(quota.Fresh?"":L10n.T("s525117F3D826")));
        return stack;
    }
    private void RenderCompact()
    {
        quotaPanel.Children.Clear();
        surfaces.RemoveAll(reference=>!reference.TryGetTarget(out _));
        var quota=app.Quota;
        Border CompactCard(UIElement content)
        {
            var card=Card(content);card.Padding=new Thickness(14,12,14,12);return card;
        }
        TextBlock Label(string text,double size=13)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
        if(quota.HasQuotaDisplay&&quota.PrimaryWindows.Any())
        {
            var limits=new StackPanel{Spacing=12};
            foreach(var window in quota.PrimaryWindows)
            {
                var section=new StackPanel{Spacing=6};
                var row=new Grid{ColumnSpacing=10};row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                var name=Label(window.Label+L10n.T("sD6822B04178D"));name.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(name);
                var value=Label(window.RemainingText,28);value.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;Grid.SetColumn(value,1);row.Children.Add(value);
                section.Children.Add(row);
                section.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=4,Foreground=accent});
                var reset=Label(window.ResetCountdown(DateTimeOffset.Now)+(quota.Fresh?"":L10n.T("s6749C5BF4AEA")),12);reset.Opacity=.65;section.Children.Add(reset);
                limits.Children.Add(section);
            }
            quotaPanel.Children.Add(CompactCard(limits));
        }
        else
        {
            var unavailable=Label(quota.IsLocalAccount?L10n.T("sF2D563561B79"):L10n.T("sF7A79B776C96"));
            ToolTipService.SetToolTip(unavailable,quota.Status);quotaPanel.Children.Add(CompactCard(unavailable));
        }
        var today=DateTime.Today.ToString("yyyy-MM-dd");
        var rows=app.Events.Where(item=>item.LocalDate==today).ToList();
        var usage=new Grid{ColumnSpacing=16};usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var tokens=new StackPanel{Spacing=4};tokens.Children.Add(Label(L10n.T("s8B4B6EA0DA7D"),12));tokens.Children.Add(Label(app.HasLoadedHistory?UsageNumbers.Compact(rows.Sum(item=>item.Tokens.Total)):L10n.T("sD04FCBDA737F"),22));
        ToolTipService.SetToolTip(tokens,$"{rows.Sum(item=>item.Tokens.Total):N0} Token");
        var requests=new StackPanel{Spacing=4};requests.Children.Add(Label(L10n.T("sBDBF4C48B0A9"),12));requests.Children.Add(Label(app.HasLoadedHistory?rows.Count.ToString("N0"):L10n.T("sD04FCBDA737F"),22));
        ToolTipService.SetToolTip(requests,L10n.T("sA19F66EB1728"));
        usage.Children.Add(tokens);Grid.SetColumn(requests,1);usage.Children.Add(requests);quotaPanel.Children.Add(CompactCard(usage));
        var estimate=quota.Fresh&&quota.HasQuotaDisplay?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        var capacity=Label(estimate is not null?L10n.F("s99933FEC8200", estimate.DollarDisplay, UsageNumbers.Compact(estimate.EstimatedTokens), estimate.PricingCoverage):quota.Fresh&&quota.PrimaryWindows.Any(window=>window.Minutes==10080)?L10n.T("s51D06B357E84"):L10n.T("s5AFCDACD073E"),12);
        // Reserve the same two-line slot in every sampling state. Otherwise the
        // content-fitting window grows and moves when an estimate becomes ready.
        if(estimate?.HistoricalAt is {} historical)capacity.Text=L10n.T("capacity.previous")+": "+estimate.DollarDisplay;
        capacity.Height=40;capacity.MaxLines=2;capacity.TextTrimming=TextTrimming.CharacterEllipsis;
        capacity.Visibility=app.Config.CapacityEnabled?Visibility.Visible:Visibility.Collapsed;
        capacity.Opacity=.75;ToolTipService.SetToolTip(capacity,capacity.Text+"\n"+(estimate?.HistoricalAt is {} saved?L10n.F("capacity.previousAt",saved.ToLocalTime())+"\n":"")+app.WeeklyCapacityProgress);quotaPanel.Children.Add(capacity);
        var updated=quota.FetchedAt is {} at?L10n.F("s6843540FA5C7", at.ToLocalTime()):L10n.T("s0D4EDA666026");
        var resets=quota.HasQuotaDisplay&&quota.ResetCount is {} count?L10n.F("s26ABA9EC2EFB", count):L10n.T("s382254F4153B");
        quotaPanel.Children.Add(PlanBadge(quota,true));
        var footer=Label($"{resets}   ·   {updated}",12);footer.Opacity=.6;
        footer.MaxLines=1;footer.TextTrimming=TextTrimming.CharacterEllipsis;footer.VerticalAlignment=VerticalAlignment.Center;
        var footerRow=new Grid{ColumnSpacing=8};
        footerRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        footerRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        footerRow.Children.Add(footer);
        var settings=new Button{Content=new FontIcon{Glyph="\uE713",FontSize=18},Width=32,Height=32,Padding=new Thickness(0),
            Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6)};
        ToolTipService.SetToolTip(settings,L10n.T("sDF3D58C7D84B"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settings,L10n.T("sDF3D58C7D84B"));
        settings.Click+=(_,_)=>{app.ShowSettings();if(!pinned)Hide();};
        Grid.SetColumn(settings,1);footerRow.Children.Add(settings);quotaPanel.Children.Add(footerRow);
    }
}
