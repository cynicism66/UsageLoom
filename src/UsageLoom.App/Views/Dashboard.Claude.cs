using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private StackPanel? claudeOverview,claudeDetails;
    private TextBlock? claudeSettingsStatus,claudeStatusLineStatus;
    private ComboBox? claudeScopeChoice;
    private ComboBox? claudePlanChoice;
    private ToggleSwitch? claudeEnabledChoice;
    private TextBox? claudeDirectoryChoice;
    private Button? claudeApplyButton;
    private StackPanel? claudeSettingsPanel;
    private Func<ClaudeSettingsInput>? readClaudeSettings;
    private string claudeScopesKey="";
    private ClaudeQuotaSnapshot? cachedCapacityQuota;
    private ClaudeCodeSnapshot? cachedCapacityCode;
    private readonly Dictionary<string,ClaudeCapacity> cachedCapacity=new();
    private DateTimeOffset cachedCapacityAt;
    private Flyout? claudeCapacityInfoFlyout;
    private static TextBlock ClaudeText(string text,double size=12)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
    internal static string StatusLineScript=>Path.Combine(AppContext.BaseDirectory,ClaudeStatusLineReader.ScriptName);
    // Forward slashes work in both cmd and the Git Bash shell Claude Code may use on Windows.
    internal static string StatusLineSetting=>"\"statusLine\": {\"type\": \"command\", \"command\": "+
        System.Text.Json.JsonSerializer.Serialize("node \""+StatusLineScript.Replace('\\','/')+"\"",
            new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping})+"}";
    private string StatusLineStatusText()
    {
        var line=app.ClaudeStatusLine;
        if(line.Status!="snapshot")return L10n.T("claude."+line.Status);
        return L10n.F("claude.statusLine.found",line.ObservedAt!.Value.ToLocalTime())+"\n"+
            L10n.T(app.ClaudeQuota.Source=="statusline"?"claude.statusLine.inUse":"claude.statusLine.desktopNewer");
    }
    private UIElement ClaudeStatusLineSettings()
    {
        var group=new StackPanel{Spacing=6};
        group.Children.Add(ClaudeText(L10n.T("claude.statusLine.title"),14));
        group.Children.Add(ClaudeText(L10n.T("claude.statusLine.notice")));
        var command=new TextBox{Header=L10n.T("claude.statusLine.command"),Text=StatusLineSetting,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,
            FontFamily=new Microsoft.UI.Xaml.Media.FontFamily("Consolas")};
        AutomationProperties.SetAutomationId(command,"claude-statusline-command");
        group.Children.Add(command);
        group.Children.Add(ClaudeText(L10n.T("claude.statusLine.chain")));
        group.Children.Add(Button(L10n.T("claude.statusLine.copy"),()=>
        {
            var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(StatusLineSetting);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);status.Text=L10n.T("claude.statusLine.copied");
            return Task.CompletedTask;
        }));
        claudeStatusLineStatus=ClaudeText(StatusLineStatusText());
        AutomationProperties.SetAutomationId(claudeStatusLineStatus,"claude-statusline-status");
        group.Children.Add(claudeStatusLineStatus);
        AutomationProperties.SetAutomationId(group,"claude-statusline");
        return group;
    }
    private Button ClaudeCapacityInfoButton(string detail)
    {
        var flyout=new Flyout{Content=new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,MaxWidth=340,FontSize=12}};
        claudeCapacityInfoFlyout=flyout;
        var button=new Button{Content=new FontIcon{Glyph="\uE946",FontSize=12},Width=22,Height=22,MinWidth=0,MinHeight=0,
            Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Center,
            Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(4),Flyout=flyout};
        AutomationProperties.SetAutomationId(button,"claude-capacity-info");
        AutomationProperties.SetName(button,L10n.T("claude.capacity.info"));
        ToolTipService.SetToolTip(button,L10n.T("claude.capacity.info"));
        return button;
    }
    private ClaudeCapacity ClaudeCapacityAt(DateTimeOffset now,string window="seven_day")
    {
        if(cachedCapacity.Count==0||!ReferenceEquals(cachedCapacityQuota,app.ClaudeQuota)||
            !ReferenceEquals(cachedCapacityCode,app.ClaudeCode)||Math.Abs((now-cachedCapacityAt).TotalSeconds)>=30||
            cachedCapacity.Values.Any(c=>c.ResetsAt is {} reset&&reset<=now))
        {
            foreach(var key in ClaudeCapacityEstimator.Windows)cachedCapacity[key]=ClaudeCapacityEstimator.Estimate(app.ClaudeQuota,app.ClaudeCode,now,key);
            cachedCapacityQuota=app.ClaudeQuota;cachedCapacityCode=app.ClaudeCode;cachedCapacityAt=now;
        }
        return cachedCapacity[window];
    }
    // The weekly strings keep their original keys; 5-hour ones override where the window is named.
    private static string CapacityKey(string window,string suffix)=>
        window=="five_hour"&&suffix is "title" or "tokens" or "api" or "overview.resetUnavailable" or "status.resetUnavailable"?
            "claude.capacity.fiveHour."+suffix:"claude.capacity."+suffix;
    private static string CapacityValue(ClaudeCapacity estimate,bool overview)=>estimate.Ready?
        L10n.F(CapacityKey(estimate.Window,"tokens"),estimate.ProjectedTokens):
        L10n.T(CapacityKey(estimate.Window,(overview?"overview.":"status.")+estimate.Status));
    private StackPanel CapacityPanel(string window,DateTimeOffset now)
    {
        var estimate=ClaudeCapacityAt(now,window);
        var estimatePanel=new StackPanel{Spacing=3};
        AutomationProperties.SetAutomationId(estimatePanel,window=="five_hour"?"claude-five-hour-capacity":"claude-weekly-capacity");
        estimatePanel.Children.Add(ClaudeText(L10n.T(CapacityKey(window,"title")),14));
        if(estimate.Ready)
        {
            estimatePanel.Children.Add(ClaudeText(CapacityValue(estimate,false),15));
            estimatePanel.Children.Add(ClaudeText(estimate.HasApiEquivalent?
                L10n.F(CapacityKey(window,"api"),estimate.ProjectedApiEquivalent,estimate.PricingCoverage):L10n.T("claude.capacity.noApi")));
            estimatePanel.Children.Add(ClaudeText(L10n.T("claude.capacity.note")));
        }
        else estimatePanel.Children.Add(ClaudeText(CapacityValue(estimate,false)));
        estimatePanel.Children.Add(ClaudeText(L10n.F("claude.capacity.progress",estimate.PercentagePoints,estimate.Intervals)));
        ToolTipService.SetToolTip(estimatePanel,L10n.T("claude.capacity.scope"));
        return estimatePanel;
    }
    private void FillClaudeCard(StackPanel panel,bool overview=false)
    {
        var snapshot=app.ClaudeQuota;var now=DateTimeOffset.Now;panel.Children.Clear();
        var header=new Grid{ColumnSpacing=12};header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text=L10n.T("claude.title"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});
        var plan=CompactText(12,24);plan.MaxWidth=170;plan.Text=ClaudePlanLabel.Badge(app.Config.ClaudeManualPlan);plan.HorizontalAlignment=HorizontalAlignment.Right;plan.VerticalAlignment=VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(plan,"claude-plan-label");ToolTipService.SetToolTip(plan,L10n.T("claude.planNotice"));
        Grid.SetColumn(plan,1);header.Children.Add(plan);panel.Children.Add(header);
        panel.Children.Add(ClaudeText(snapshot.Describe(now)));
        if(app.ClaudeOpenRefreshing)panel.Children.Add(RefreshingRow("claude"));
        foreach(var key in new[]{"five_hour","seven_day"})
        {
            var window=snapshot.Windows.FirstOrDefault(w=>w.Key==key);
            panel.Children.Add(ClaudeText(L10n.T("claude."+key)+" · "+L10n.F("claude.remaining",window?.RemainingText(now)??"—"),14));
            panel.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window is not null&&!window.Expired(now)?100-window.Used:0,Height=5,Foreground=accent,Opacity=(window is null||window.Expired(now))?0.3:1});
            panel.Children.Add(ClaudeText(window?.Countdown(now)??L10n.T("claude.noReset")));
            if(window is not null&&PaceRow(QuotaPace.ForClaude(snapshot,window,app.Config.ClaudeEnabled,now),"claude",key,L10n.T("claude."+key)) is {} pace)
                panel.Children.Add(pace);
        }
        if(overview)
        {
            var summary=new Grid{ColumnSpacing=8};summary.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});summary.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            AutomationProperties.SetAutomationId(summary,"claude-overview-capacity");
            var estimates=ClaudeCapacityEstimator.Windows.Select(key=>ClaudeCapacityAt(now,key)).ToArray();
            summary.Children.Add(ClaudeText(string.Join("\n",estimates.Select(e=>L10n.T(CapacityKey(e.Window,"title"))+" · "+CapacityValue(e,true)))));
            var detail=string.Join("\n\n",estimates.Select(e=>L10n.T(CapacityKey(e.Window,"title"))+"\n"+
                (e.Ready?L10n.T("claude.capacity.note"):CapacityValue(e,false))+"\n"+
                L10n.F("claude.capacity.progress",e.PercentagePoints,e.Intervals)))+"\n\n"+L10n.T("claude.capacity.scope");
            var info=ClaudeCapacityInfoButton(detail);
            Grid.SetColumn(info,1);summary.Children.Add(info);panel.Children.Add(summary);
            panel.Children.Add(SnapshotAgeRow("claude",snapshot.ObservedAt,now));
            return;
        }
        foreach(var key in ClaudeCapacityEstimator.Windows)panel.Children.Add(CapacityPanel(key,now));
        panel.Children.Add(SnapshotAgeRow("claude",snapshot.ObservedAt,now));
    }
    private UIElement ClaudeDetailsCard()
    {
        claudeDetails=new(){Spacing=8};FillClaudeCard(claudeDetails);return Card(claudeDetails);
    }
    private void UpdateClaudeViews()
    {
        if(released)return;
        if(!DispatcherQueue.HasThreadAccess){DispatcherQueue.TryEnqueue(UpdateClaudeViews);return;}
        if(compact){if(compactContent is not null)UpdateCompactClaude();return;}
        if(IsStatisticsPage&&statisticsProvider=="claude"&&ClaudeHistoryViewChanged())
        {renderedFilter=null;RenderStats();}
        UpdateStatisticsActions();
        if(claudeOverview is not null)FillClaudeCard(claudeOverview,true);
        if(claudeDetails is not null)FillClaudeCard(claudeDetails);
        if(claudeSettingsStatus is not null)claudeSettingsStatus.Text=app.ClaudeQuota.Describe(DateTimeOffset.Now)+"\n"+app.ClaudeQuota.TimestampText+"\n"+L10n.T("claude.identityNotice");
        if(claudeStatusLineStatus is not null)claudeStatusLineStatus.Text=StatusLineStatusText();
        var scopes=app.ClaudeQuota.Scopes??[];var signature=string.Join("|",scopes)+"|"+app.Config.ClaudeScope;
        if(claudeScopeChoice is not null&&signature!=claudeScopesKey)
        {
            var selectedDraft=(claudeScopeChoice.SelectedItem as ComboBoxItem)?.Tag as string??app.Config.ClaudeScope??"";
            claudeScopesKey=signature;claudeScopeChoice.Items.Clear();
            claudeScopeChoice.Items.Add(new ComboBoxItem{Content=L10n.T("claude.automatic"),Tag=""});
            foreach(var scope in scopes.Concat(new[]{app.Config.ClaudeScope,selectedDraft}.Where(s=>!string.IsNullOrEmpty(s)).Select(s=>s!)).Distinct())
                claudeScopeChoice.Items.Add(new ComboBoxItem{Content=L10n.F("claude.source",scope),Tag=scope});
            claudeScopeChoice.SelectedIndex=0;
            foreach(ComboBoxItem item in claudeScopeChoice.Items)if((string)item.Tag==selectedDraft)claudeScopeChoice.SelectedItem=item;
        }
    }
    private UIElement ClaudeSettings()
    {
        var enabled=new ToggleSwitch{IsOn=app.Config.ClaudeEnabled};
        var directory=new TextBox{Header=L10n.T("claude.path"),Text=app.Config.ClaudeDataDirectory??"",IsReadOnly=app.IsDemo};
        claudeEnabledChoice=enabled;claudeDirectoryChoice=directory;
        claudeScopeChoice=new ComboBox{Header=L10n.T("claude.scope"),HorizontalAlignment=HorizontalAlignment.Stretch};claudeScopesKey="!";
        var scopeChoice=claudeScopeChoice;
        directory.TextChanged+=(_,_)=>{if(!string.Equals(directory.Text.Trim(),app.Config.ClaudeDataDirectory??"",StringComparison.OrdinalIgnoreCase))scopeChoice.SelectedIndex=0;};
        claudeSettingsStatus=ClaudeText("");
        var panel=SourceSettingsGroup(L10n.T("claude.title"),enabled,claudeVisibleChoice=SourceVisibleChoice("claude"),directory,claudeScopeChoice);
        claudePlanChoice=new ComboBox{Header=L10n.T("claude.planChoice"),HorizontalAlignment=HorizontalAlignment.Stretch};
        claudePlanChoice.Items.Add(new ComboBoxItem{Content=L10n.T("claude.planUnset"),Tag=""});
        foreach(var key in ClaudePlanLabel.Choices)claudePlanChoice.Items.Add(new ComboBoxItem{Content=ClaudePlanLabel.Display(key),Tag=key});
        claudePlanChoice.SelectedIndex=0;
        foreach(ComboBoxItem item in claudePlanChoice.Items)if((string)item.Tag==app.Config.ClaudeManualPlan)claudePlanChoice.SelectedItem=item;
        var planChoice=claudePlanChoice;panel.Children.Add(planChoice);panel.Children.Add(ClaudeText(L10n.T("claude.planNotice")));
        claudeSettingsPanel=panel;
        panel.Children.Add(ClaudeText(L10n.T("claude.notice")));panel.Children.Add(claudeSettingsStatus);
        var codeEnabled=new ToggleSwitch{Header=L10n.T("claude.code.enable"),IsOn=app.Config.ClaudeCodeEnabled};
        var codeHome=new TextBox{Header=L10n.T("claude.code.path"),Text=app.Config.ClaudeCodeHome??"",PlaceholderText=ClaudeCodeReader.DefaultHome,IsReadOnly=app.IsDemo};
        AutomationProperties.SetAutomationId(codeEnabled,"claude-code-enabled");AutomationProperties.SetAutomationId(codeHome,"claude-code-home");
        panel.Children.Add(codeEnabled);panel.Children.Add(codeHome);panel.Children.Add(ClaudeText(L10n.T("claude.code.privacy")));
        panel.Children.Add(ClaudeStatusLineSettings());
        ClaudeSettingsInput ReadDraft()
        {
            var scope=(scopeChoice.SelectedItem as ComboBoxItem)?.Tag as string;
            var directoryChanged=!string.Equals(directory.Text.Trim(),app.Config.ClaudeDataDirectory??"",StringComparison.OrdinalIgnoreCase);
            var draft=new ClaudeSettingsInput(enabled.IsOn,directory.Text,directoryChanged?null:scope,(planChoice.SelectedItem as ComboBoxItem)?.Tag as string,codeEnabled.IsOn,codeHome.Text).Validated();
            if(directoryChanged)scopeChoice.SelectedIndex=0; // An old choice must not return on the next save.
            return draft;
        }
        readClaudeSettings=ReadDraft;
        claudeApplyButton=Button(L10n.T("claude.apply"),()=>app.ConfigureClaudeAsync(ReadDraft()));
        panel.Children.Add(claudeApplyButton);
        panel.Children.Add(Button(L10n.T("claude.detect"),()=>app.RefreshClaudeAsync(true)));
        AutomationProperties.SetAutomationId(panel,"claude-settings");UpdateClaudeViews();
        return panel;
    }
    private string CompactClaudeFooter(DateTimeOffset now)=>app.ClaudeShown?SnapshotAge(app.ClaudeQuota.ObservedAt,now):"";
    private void UpdateCompactClaude()
    {
        var view=compactContent!;var visibility=app.ClaudeShown?Visibility.Visible:Visibility.Collapsed;
        if(view.ClaudeCard.Visibility!=visibility){view.ClaudeCard.Visibility=visibility;displayState=null;}
        if(!app.ClaudeShown)return;
        var snapshot=app.ClaudeQuota;var now=DateTimeOffset.Now;
        view.ClaudePlan.Text=ClaudePlanLabel.Badge(app.Config.ClaudeManualPlan);
        ToolTipService.SetToolTip(view.ClaudePlan,L10n.T("claude.planNotice"));
        // With Codex hidden the footer row is Claude's, so the snapshot age moves there.
        view.ClaudeTitle.Text="Claude"+(app.CodexShown?" · "+SnapshotAge(snapshot.ObservedAt,now):"")+(app.ClaudeOpenRefreshing?" · "+L10n.T("quota.openRefresh.working"):"");
        if(!app.CodexShown){view.Footer.Text=CompactClaudeFooter(now);ToolTipService.SetToolTip(view.Footer,view.Footer.Text);}
        for(var i=0;i<2;i++)
        {
            var key=i==0?"five_hour":"seven_day";var window=snapshot.Windows.FirstOrDefault(w=>w.Key==key);
            view.ClaudeValues[i].Text=L10n.T("claude."+key)+" · "+L10n.F("claude.remaining",window?.RemainingText(now)??"—");
            var available=window is not null&&!window.Expired(now);
            view.ClaudeProgress[i].Value=available?100-window!.Used:0;
            view.ClaudeProgress[i].Opacity=available?1:0.3;
            AutomationProperties.SetName(view.ClaudeProgress[i],view.ClaudeValues[i].Text);
            view.ClaudeResets[i].Text=window?.Countdown(now)??snapshot.Describe(now);
        }
        for(var i=0;i<2;i++)
        {
            var estimate=ClaudeCapacityAt(now,ClaudeCapacityEstimator.Windows[i]);
            view.ClaudeEstimates[i].Text=L10n.T(i==0?"claude.capacity.compact.fiveHour":"claude.capacity.compact.week")+" · "+(
                estimate.Ready?L10n.F("claude.capacity.compact.tokens",UsageNumbers.Compact((double)estimate.ProjectedTokens)):
                estimate.Status=="insufficient"?L10n.F("claude.capacity.compact.progress",estimate.PercentagePoints,estimate.Intervals):
                L10n.T(CapacityKey(estimate.Window,"overview."+estimate.Status)));
            ToolTipService.SetToolTip(view.ClaudeEstimates[i],L10n.T(CapacityKey(estimate.Window,"title"))+"\n"+
                (estimate.Ready?L10n.T("claude.capacity.note"):CapacityValue(estimate,false))+"\n"+
                L10n.F("claude.capacity.progress",estimate.PercentagePoints,estimate.Intervals));
        }
        var estimates=string.Join("\n",ClaudeCapacityEstimator.Windows.Select(key=>ClaudeCapacityAt(now,key))
            .Select(e=>L10n.T(CapacityKey(e.Window,"title"))+" · "+CapacityValue(e,false)));
        ToolTipService.SetToolTip(view.ClaudeCard,snapshot.Describe(now)+"\n"+snapshot.TimestampText+"\n"+
            estimates+"\n"+L10n.T("claude.capacity.note"));
    }
}
