using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private static string PaceLabel(QuotaPaceKind kind)=>kind switch
    {
        QuotaPaceKind.Normal=>L10n.T("quota.pace.normal"),
        QuotaPaceKind.Fast=>L10n.T("quota.pace.fast"),
        QuotaPaceKind.Exhausted=>L10n.T("quota.pace.exhausted"),
        _=>""
    };
    private static string SnapshotAge(DateTimeOffset? observedAt,DateTimeOffset now)
    {
        if(observedAt is not {} at||at>now)return L10n.T("quota.age.unknown");
        var age=now-at;
        if(age<TimeSpan.FromMinutes(1))return L10n.T("quota.age.justNow");
        if(age<TimeSpan.FromHours(1))return L10n.F("quota.age.minutes",(int)age.TotalMinutes);
        if(age<TimeSpan.FromDays(1))return L10n.F("quota.age.hours",(int)age.TotalHours);
        return L10n.F("quota.age.days",(int)age.TotalDays);
    }
    private static TextBlock SnapshotAgeRow(string provider,DateTimeOffset? observedAt,DateTimeOffset now)
    {
        var row=ClaudeText(SnapshotAge(observedAt,now));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row,$"snapshot-age-{provider}");
        ToolTipService.SetToolTip(row,L10n.T("quota.age.hint"));
        return row;
    }
    private static TextBlock RefreshingRow(string provider)
    {
        var row=ClaudeText(L10n.T("quota.openRefresh.working"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row,$"snapshot-refreshing-{provider}");
        return row;
    }
    private static TextBlock? PaceRow(QuotaPaceKind kind,string provider,string windowKey,string windowLabel)
    {
        if(kind==QuotaPaceKind.Unavailable)return null;
        var row=new TextBlock{Text=PaceLabel(kind),FontSize=12,TextWrapping=TextWrapping.Wrap,Opacity=.8};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(row,$"quota-pace-{provider}-{windowKey}");
        ToolTipService.SetToolTip(row,L10n.F("quota.pace.hint",windowLabel));
        return row;
    }
    private Border? codexDetailsCard;
    private Grid? codexDetailsHeader;
    private void FillOverviewQuota()
    {
        var quota=app.Quota;
        overviewQuota.Children.Clear();overviewQuotaCard.Visibility=app.Config.CodexEnabled?Visibility.Visible:Visibility.Collapsed;
        if(!app.Config.CodexEnabled)return;
        var header=new Grid{ColumnSpacing=12};
        header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="Codex",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});
        var badge=PlanBadge(quota,true);badge.MaxWidth=120;badge.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(badge,1);header.Children.Add(badge);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(header,"overview-quota-header");
        overviewQuota.Children.Add(header);
        overviewQuota.Children.Add(ClaudeText(quota.SourceLabel));
        if(app.CodexOpenRefreshing)overviewQuota.Children.Add(RefreshingRow("codex"));
        if(!quota.HasQuotaDisplay){overviewQuota.Children.Add(ClaudeText(quota.Status));overviewQuota.Children.Add(SnapshotAgeRow("codex",quota.FetchedAt,DateTimeOffset.Now));return;}
        foreach(var window in quota.PrimaryWindows)
        {
            overviewQuota.Children.Add(new TextBlock{Text=L10n.F("s6D65FE80C728",window.Label,window.RemainingText),TextWrapping=TextWrapping.Wrap});
            overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            overviewQuota.Children.Add(ClaudeText(window.ResetCountdown(DateTimeOffset.Now)));
            if(PaceRow(QuotaPace.ForCodex(quota,window,app.Config.CodexEnabled,DateTimeOffset.Now),"codex",window.Key,window.Label) is {} pace)
                overviewQuota.Children.Add(pace);
        }
        var estimate=quota.Fresh&&app.Config.CapacityEnabled?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        if(estimate is not null)
        {
            var summary=new Grid{ColumnSpacing=8};summary.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});summary.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            summary.Children.Add(ClaudeText((estimate.HistoricalAt is not null?L10n.T("capacity.previous")+": ":"")+estimate.DollarDisplay));
            var info=CapacityInfoButton();Grid.SetColumn(info,1);summary.Children.Add(info);overviewQuota.Children.Add(summary);
        }
        overviewQuota.Children.Add(SnapshotAgeRow("codex",quota.FetchedAt,DateTimeOffset.Now));
    }
    private void VerifyOverviewPlanHeader()
    {
        if(overviewQuotaCard.Visibility!=Visibility.Visible)return;
        ((FrameworkElement)Content).UpdateLayout();
        if(overviewQuota.Children.FirstOrDefault() is not Grid header||header.Children.Count!=2||header.Children[0] is not TextBlock title||title.Text!="Codex")
            throw new InvalidOperationException("Overview quota title and plan are not grouped");
        if(overviewQuota.Children.OfType<Button>().Any()||overviewQuota.Children.OfType<ProgressBar>().Any(bar=>bar.Height!=5))
            throw new InvalidOperationException("Codex overview has an extra action or mismatched progress bar");
        if(!overviewQuota.Children.OfType<TextBlock>().Any(row=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(row)=="snapshot-age-codex"))
            throw new InvalidOperationException("Codex quota snapshot age missing");
        VerifyPacePresentation(overviewQuota,"codex",app.Quota.PrimaryWindows.Select(window=>(window.Key,window.Label,QuotaPace.ForCodex(app.Quota,window,app.Config.CodexEnabled,DateTimeOffset.Now))));
        if(claudeOverview is not null)
        {
            if(!claudeOverview.Children.OfType<TextBlock>().Any(row=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(row)=="snapshot-age-claude"))
                throw new InvalidOperationException("Claude quota snapshot age missing");
            VerifyPacePresentation(claudeOverview,"claude",new[]{"five_hour","seven_day"}.Select(key=>
            {
                var window=app.ClaudeQuota.Windows.FirstOrDefault(item=>item.Key==key);
                return (key,L10n.T("claude."+key),window is null?QuotaPaceKind.Unavailable:QuotaPace.ForClaude(app.ClaudeQuota,window,app.Config.ClaudeEnabled,DateTimeOffset.Now));
            }));
            var capacity=claudeOverview.Children.OfType<Grid>().FirstOrDefault(grid=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(grid)=="claude-overview-capacity");
            if(claudeOverview.Spacing!=overviewQuota.Spacing||
                claudeOverview.Children.OfType<ProgressBar>().Any(bar=>bar.Height!=5)||
                capacity is null||capacity.Children.Count!=2||capacity.Children[0] is not TextBlock summary||
                capacity.Children[1] is not Button info||Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(info)!="claude-capacity-info"||
                claudeOverview.Children.OfType<StackPanel>().Any(stack=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(stack)=="claude-weekly-capacity"))
                throw new InvalidOperationException("Claude overview does not match the compact Codex card layout");
            var estimate=ClaudeCapacityAt(DateTimeOffset.Now);
            if(!estimate.Ready&&!summary.Text.Contains(L10n.T("claude.capacity.overview."+estimate.Status),StringComparison.Ordinal))
                throw new InvalidOperationException("Claude overview hides the actual estimation reason");
        }
        foreach(FrameworkElement child in header.Children)
        {
            var rect=child.TransformToVisual(header).TransformBounds(new(0,0,child.ActualWidth,child.ActualHeight));
            if(header.ActualWidth>0&&(rect.Left<-.5||rect.Right>header.ActualWidth+.5))throw new InvalidOperationException("Overview quota header overflow");
        }
        Program.Log.Write("INFO","NavigationTest","Overview quota plan contained in header passed");
    }
    private static void VerifyPacePresentation(StackPanel panel,string provider,IEnumerable<(string Key,string Label,QuotaPaceKind Kind)> windows)
    {
        foreach(var (key,label,kind) in windows)
        {
            var id=$"quota-pace-{provider}-{key}";
            var row=panel.Children.OfType<TextBlock>().FirstOrDefault(item=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item)==id);
            if(kind==QuotaPaceKind.Unavailable)
            {
                if(row is not null)throw new InvalidOperationException("Unavailable pace was shown as normal");
                continue;
            }
            if(row is null||row.Text!=PaceLabel(kind)||ToolTipService.GetToolTip(row) is not string hint||!hint.Contains(label,StringComparison.Ordinal)||
                !hint.Contains(L10n.T("quota.pace.disclaimer"),StringComparison.Ordinal))
                throw new InvalidOperationException($"Quota pace row or window-specific hover explanation missing: {provider}/{key}, kind={kind}, row={row is not null}, textMatch={row?.Text==PaceLabel(kind)}, labelMatch={(ToolTipService.GetToolTip(row) as string)?.Contains(label,StringComparison.Ordinal)}, disclaimerMatch={(ToolTipService.GetToolTip(row) as string)?.Contains(L10n.T("quota.pace.disclaimer"),StringComparison.Ordinal)}");
        }
        Program.Log.Write("INFO","NavigationTest","Quota pace rows and unavailable-state boundaries passed");
    }
    private Border CodexDetailsCard(QuotaState quota,IReadOnlyList<UIElement> windows)
    {
        var body=new StackPanel{Spacing=8};
        var header=new Grid{ColumnSpacing=12};
        header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="Codex",FontSize=18,VerticalAlignment=VerticalAlignment.Center});
        var badge=PlanBadge(quota,true);badge.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(badge,1);header.Children.Add(badge);
        body.Children.Add(header);
        body.Children.Add(new TextBlock{Text=app.IsDemo?L10n.T("s9EE75C455D70"):quota.SourceLabel,FontSize=12,TextWrapping=TextWrapping.Wrap});
        if(app.CodexOpenRefreshing)body.Children.Add(RefreshingRow("codex"));
        var content=new StackPanel{Spacing=16,Margin=new Thickness(0,8,0,0)};
        foreach(var window in windows)content.Children.Add(window);
        if(windows.Count==0)content.Children.Add(new TextBlock{Text=quota.HasQuotaDisplay?L10n.T("s3073BC52B5B6"):L10n.T("s540071E2CE7C")+quota.Status,FontSize=14,TextWrapping=TextWrapping.Wrap,Opacity=.7});
        body.Children.Add(content);
        if(quota.HasQuotaDisplay)body.Children.Add(ClaudeText(L10n.T("s9672B36B01C7")+(quota.ResetCount is {} n?n+L10n.T("sF81526EFCE19"):L10n.T("sF36CAC96220B"))));
        body.Children.Add(SnapshotAgeRow("codex",quota.FetchedAt,DateTimeOffset.Now));
        body.Children.Add(StableExpander.Configure(new Expander{Header="Codex · "+L10n.T("sAD63795746F7"),Content=ClaudeText(quota.Status+L10n.T("s75B413F02FD0")),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        codexDetailsHeader=header;codexDetailsCard=Card(body);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(codexDetailsCard,"codex-provider-card");
        return codexDetailsCard;
    }
    private void VerifyQuotaProviderLayout()
    {
        ((FrameworkElement)Content).UpdateLayout();
        if(codexDetailsCard?.Child is not StackPanel body||body.Children[0]!=codexDetailsHeader||body.Children[1] is not TextBlock account||account.FontSize!=12)
            throw new InvalidOperationException("Codex identity is not inside its quota card");
        if(codexDetailsHeader!.Children[0] is not TextBlock title||title.Text!="Codex"||title.FontSize!=18||codexDetailsHeader.Children.Count!=2)
            throw new InvalidOperationException("Codex provider header or plan badge is missing");
        if(codexDetailsCard.Parent is not Grid providers||providers.Children[0]!=codexDetailsCard||quotaPanel.Children[0]!=providers)
            throw new InvalidOperationException("Detached provider title exists above the cards");
        if(app.Config.ClaudeEnabled)
        {
            if(claudeDetails?.Parent is not Border claudeCard||providers.Children.Count<2||providers.Children[1]!=claudeCard||claudeDetails.Children[0] is not Grid claudeHeader||claudeHeader.Children[0] is not TextBlock claudeTitle||claudeTitle.FontSize!=title.FontSize||claudeHeader.Children[1] is not TextBlock claudePlan||claudePlan.Text!=ClaudePlanLabel.Badge(app.Config.ClaudeManualPlan))
                throw new InvalidOperationException("Codex and Claude card hierarchy differs");
            if(!claudeDetails.Children.OfType<StackPanel>().Any(p=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(p)=="claude-weekly-capacity"))
                throw new InvalidOperationException("Claude quota card has no weekly comparison or sampling status");
            if(providers.ColumnDefinitions.Count>1)
            {
                var codexTop=codexDetailsCard.TransformToVisual(providers).TransformPoint(new(0,0)).Y;
                var claudeTop=claudeCard.TransformToVisual(providers).TransformPoint(new(0,0)).Y;
                if(Math.Abs(codexTop-claudeTop)>1)throw new InvalidOperationException("Provider card tops do not align");
            }
        }
        if(codexDetailsHeader.ActualWidth>0)
            foreach(FrameworkElement child in codexDetailsHeader.Children)
            {
                var rect=child.TransformToVisual(codexDetailsHeader).TransformBounds(new(0,0,child.ActualWidth,child.ActualHeight));
                if(rect.Left<-.5||rect.Right>codexDetailsHeader.ActualWidth+.5)throw new InvalidOperationException("Provider header overflow");
            }
        Program.Log.Write("INFO","NavigationTest","Provider quota cards: contained identity, compact plan, equal titles and responsive alignment passed");
    }
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
        EnsureCompactContent();
        UpdateCompactContent();
    }
}
