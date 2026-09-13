using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private void StartCapacityUiCheck()
    {
        if(!compact||!app.CapacityUiCheck)return;
        pinned=true; // Only this isolated preview remains visible during its automated interaction.
        ((FrameworkElement)Content).Loaded+=CapacityUiTestLoaded;
    }
    private async void CapacityUiTestLoaded(object sender,RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded-=CapacityUiTestLoaded;
        try
        {
            await Task.Delay(350);
            await VerifyCompactRefreshStability();
            NumberBox? unsavedSetting=null;
            var savedBackgroundSeconds=app.Config.BackgroundSeconds;
            var draftBackgroundSeconds=savedBackgroundSeconds==1234?1235d:1234d;
            foreach(var scenario in new[]{"current","historical","empty","revalidating"})
            {
                var historical=scenario=="historical";var empty=scenario=="empty";
                app.ConfigureCapacityUiPreview(historical,empty,scenario=="revalidating");await Task.Delay(120);
                if(scenario!="current")
                {
                    // A live settings page must update without navigating away or
                    // reopening an expander, including the no-estimate state.
                    await CapacityTestWait(()=>app.CapacitySettingsTestDashboard is {} existing&&
                        CapacitySettingsBodyReady(existing,scenario),"Open capacity settings did not refresh for "+scenario);
                    CapacityTestSettingDraft(app.CapacitySettingsTestDashboard!,unsavedSetting!,draftBackgroundSeconds,savedBackgroundSeconds);
                    Program.Log.Write("INFO","CapacityUiTest","Open capacity settings refreshed: "+scenario);
                }
                var root=(FrameworkElement)Content;root.UpdateLayout();
                var initialSize=AppWindow.Size;
                var entry=CapacityTestDescendants(root).OfType<Button>().Single(b=>AutomationProperties.GetName(b)==L10n.T("capacity.info"));
                CapacityTestInvoke(entry);
                await CapacityTestWait(()=>capacityInfoFlyout?.IsOpen==true,"Info popover did not open");
                await Task.Delay(140);root.UpdateLayout();
                if(scenario=="revalidating"&&(!capacityInfoText.Text.Contains(L10n.T("capacity.compactRevalidatingEstimate"),StringComparison.Ordinal)||
                    capacityInfoText.Text.Contains(L10n.T("capacity.compactCurrentEstimate"),StringComparison.Ordinal)))
                    throw new InvalidOperationException("Pending revaluation mislabeled the previous amount as current");
                if(!capacityInfoText.Text.Contains(app.WeeklyCapacityCompactProgress,StringComparison.Ordinal))
                    throw new InvalidOperationException("Compact popover lost the current sampling summary");
                var evidence=app.WeeklyCapacity.FirstOrDefault(item=>item.ObservedTokens>0);
                if(capacityInfoText.Text.Contains(L10n.T("capacity.compactEvidenceLegacy"),StringComparison.Ordinal)||
                    evidence is not null&&(capacityInfoText.Text.Contains(evidence.EvidenceSummary,StringComparison.Ordinal)||
                    evidence.HasCurrentPricingEvidence&&capacityInfoText.Text.Contains(L10n.F("capacity.compactEvidence",
                        evidence.PricingProfile!.ActualCoverage,evidence.PricingProfile.RequestedCoverage,evidence.PricingProfile.UnknownCoverage),StringComparison.Ordinal)))
                    throw new InvalidOperationException("Compact popover still contains mode-evidence distribution instead of a short summary");
                DependencyObject? parent=capacityInfoText;
                while(parent is not null&&parent is not FlyoutPresenter)parent=VisualTreeHelper.GetParent(parent);
                if(parent is not FlyoutPresenter presenter)throw new InvalidOperationException("Info popover presenter not found");
                presenter.UpdateLayout();
                if(CapacityTestDescendants(presenter).OfType<Button>().Any())
                    throw new InvalidOperationException("Compact info popover must not contain a settings/detail button");
                foreach(var property in new[]{ScrollViewer.HorizontalScrollModeProperty,ScrollViewer.VerticalScrollModeProperty})
                    if((ScrollMode)presenter.GetValue(property)!=ScrollMode.Disabled)throw new InvalidOperationException("Compact popover scrolling was enabled");
                foreach(var viewer in CapacityTestDescendants(presenter).OfType<ScrollViewer>())
                    if(viewer.ScrollableWidth>.5||viewer.ScrollableHeight>.5||viewer.HorizontalScrollBarVisibility!=ScrollBarVisibility.Disabled||viewer.VerticalScrollBarVisibility!=ScrollBarVisibility.Disabled)
                        throw new InvalidOperationException("Compact popover contains scrolling or clipped content");
                var natural=new TextBlock{Text=capacityInfoText.Text,FontSize=capacityInfoText.FontSize,FontFamily=capacityInfoText.FontFamily,
                    FontWeight=capacityInfoText.FontWeight,TextWrapping=TextWrapping.Wrap,Language=capacityInfoText.Language};
                natural.Measure(new Windows.Foundation.Size(capacityInfoText.ActualWidth,double.PositiveInfinity));
                var naturalAtActual=natural.DesiredSize.Height;
                // WinUI TextBlock.ActualWidth is the ink width (e.g. 208.63 for a
                // 308-DIP layout slot). Rewrapping at that width invents extra lines.
                natural.Measure(new Windows.Foundation.Size(capacityInfoText.Width,double.PositiveInfinity));
                var layout=LayoutInformation.GetLayoutSlot(capacityInfoText);
                Program.Log.Write("INFO","CapacityUiTest",$"Popover geometry {scenario}: width={capacityInfoText.ActualWidth:0.##}; configuredWidth={capacityInfoText.Width:0.##}; desired={capacityInfoText.DesiredSize.Width:0.##}x{capacityInfoText.DesiredSize.Height:0.##}; actualText={capacityInfoText.ActualHeight:0.##}; naturalAtActual={naturalAtActual:0.##}; naturalAtConfigured={natural.DesiredSize.Height:0.##}; presenter={presenter.ActualWidth:0.##}x{presenter.ActualHeight:0.##}; trimmed={capacityInfoText.IsTextTrimmed}");
                if(capacityInfoText.ActualWidth<=0||capacityInfoText.IsTextTrimmed||capacityInfoText.ActualHeight+.5<natural.DesiredSize.Height)
                    throw new InvalidOperationException("Compact popover text was trimmed or clipped");
                if(layout.Width+.5<capacityInfoText.Width||layout.Height+.5<natural.DesiredSize.Height)
                    throw new InvalidOperationException("Compact popover did not allocate the complete text layout slot");
                foreach(var element in new FrameworkElement[]{capacityInfoText})
                {
                    var bounds=element.TransformToVisual(presenter).TransformBounds(new Windows.Foundation.Rect(0,0,element.ActualWidth,element.ActualHeight));
                    if(bounds.X<-.5||bounds.Y<-.5||bounds.X+bounds.Width>presenter.ActualWidth+.5||bounds.Y+bounds.Height>presenter.ActualHeight+.5)
                        throw new InvalidOperationException("Compact popover content exceeds its presenter bounds");
                }
                Program.Log.Write("INFO","CapacityUiTest",$"Compact popover no-scroll/no-clipping passed: {scenario}");
                CapacityTestInvoke(entry);
                await CapacityTestWait(()=>capacityInfoFlyout?.IsOpen!=true,"Compact info popover did not close");
                var settings=CapacityTestDescendants(root).OfType<Button>().Single(item=>AutomationProperties.GetName(item)==L10n.T("sDF3D58C7D84B"));
                CapacityTestInvoke(settings);
                await CapacityTestWait(()=>app.CapacitySettingsTestDashboard is {selectedPage:"settings",capacitySettingsExpander:not null,capacityValuationExpander:not null},
                    "Compact footer settings gear did not open settings");
                var main=app.CapacitySettingsTestDashboard!;
                CapacityTestExpand(main.capacitySettingsExpander!);
                await CapacityTestWait(()=>main.capacityValuationExpander!.IsLoaded,"Inline valuation expander is not attached");
                CapacityTestExpand(main.capacityValuationExpander!);
                await CapacityTestWait(()=>CapacitySettingsBodyReady(main,scenario),"Inline valuation content did not open from settings");
                var mainRoot=(FrameworkElement)main.Content;mainRoot.UpdateLayout();
                if(unsavedSetting is not null)CapacityTestSettingDraft(main,unsavedSetting,draftBackgroundSeconds,savedBackgroundSeconds);
                if(capacityInfoFlyout?.IsOpen==true)throw new InvalidOperationException("Compact popover remained open after settings navigation");
                foreach(var windowRoot in new[]{root,mainRoot})
                    if(VisualTreeHelper.GetOpenPopupsForXamlRoot(windowRoot.XamlRoot).SelectMany(p=>CapacityTestDescendants(p.Child)).OfType<ContentDialog>().Any())
                        throw new InvalidOperationException("Capacity details still opened a ContentDialog");
                if(!CapacityTestDescendants(mainRoot).Any(node=>ReferenceEquals(node,main.capacityValuationContent)))
                    throw new InvalidOperationException("Valuation content is not embedded in the settings page");
                var detail=main.pageScroll;
                await CapacityTestDispatcherSettled(main);
                var header=CapacityTestDescendants(main.capacityValuationExpander).OfType<FrameworkElement>().Single(element=>element.Name=="ExpanderHeader");
                if(!CapacityTestVisibleIn(header,detail))header.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=0});
                await CapacityTestWait(()=>CapacityTestVisibleIn(header,detail),"Settings entry did not reveal the valuation header");
                Program.Log.Write("INFO","CapacityUiTest",$"Settings entry reveal settled {scenario}: {CapacityTestScrollGeometry(detail,header)}");
                if(detail.HorizontalScrollBarVisibility!=ScrollBarVisibility.Disabled||detail.ScrollableWidth>.5)
                    throw new InvalidOperationException("Capacity settings content can scroll horizontally");
                if(!historical&&!empty&&detail.ScrollableHeight<=0)throw new InvalidOperationException("Mixed-workload settings fixture did not exercise vertical scrolling");
                var lastLine=CapacityTestDescendants(main.capacityValuationContent).OfType<TextBlock>().Last();
                // Do not queue a redundant bring-into-view for an already visible
                // last line: that deferred request would compete with ChangeView
                // below even though the visibility wait has already succeeded.
                if(!CapacityTestVisibleIn(lastLine,detail))
                {
                    lastLine.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=1});
                    await CapacityTestWait(()=>CapacityTestVisibleIn(lastLine,detail),"Final valuation explanation is unreachable in settings");
                }
                var accepted=detail.ChangeView(null,detail.ScrollableHeight,null,true);
                Program.Log.Write("INFO","CapacityUiTest",$"Settings bottom requested {scenario}: accepted={accepted}; {CapacityTestScrollGeometry(detail,header)}");
                try{await CapacityTestWait(()=>Math.Abs(detail.VerticalOffset-detail.ScrollableHeight)<=1,"Capacity settings bottom is unreachable");}
                catch
                {
                    Program.Log.Write("ERROR","CapacityUiTest",$"Settings bottom failed {scenario}: accepted={accepted}; {CapacityTestScrollGeometry(detail,header)}");
                    throw;
                }
                Program.Log.Write("INFO","CapacityUiTest",$"Settings bottom reached {scenario}: {CapacityTestScrollGeometry(detail,header)}");
                if(unsavedSetting is null)
                {
                    // Prepare an unsaved input only after checking the entry's
                    // scroll destination, so opening this unrelated section does
                    // not itself shift the header under test.
                    var refresh=CapacityTestDescendants(mainRoot).OfType<Expander>().Single(item=>item.Header as string==L10n.T("s16685D3221B9"));
                    refresh.IsExpanded=true;
                    await CapacityTestWait(()=>CapacityTestDescendants(refresh).OfType<NumberBox>().Any(item=>item.Minimum==30&&item.Maximum==3600),"Background refresh draft input is not available");
                    unsavedSetting=CapacityTestDescendants(refresh).OfType<NumberBox>().Single(item=>item.Minimum==30&&item.Maximum==3600);
                    unsavedSetting.Value=draftBackgroundSeconds;
                }
                CapacityTestSettingDraft(main,unsavedSetting,draftBackgroundSeconds,savedBackgroundSeconds);
                if(new ExpanderAutomationPeer(main.capacityValuationExpander!).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                    throw new InvalidOperationException("Inline valuation expander has no accessible expand/collapse action");
                expand.Collapse();
                await CapacityTestWait(()=>main.capacityValuationExpander?.IsExpanded==false,"Inline valuation expander did not collapse");
                if(scenario=="current")
                {
                    if(new ExpanderAutomationPeer(main.capacitySettingsExpander!).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider section)
                        throw new InvalidOperationException("Capacity settings expander has no accessible expand/collapse action");
                    section.Collapse();
                    // Force genuinely different read-only content through Changed,
                    // without saving or replacing any editable settings controls.
                    app.ConfigureCapacityUiPreview(revalidating:true);
                    await CapacityTestWait(()=>CapacitySettingsBodyReady(main,"revalidating",false),"Collapsed valuation content did not refresh");
                    await CapacityTestDispatcherSettled(main);
                    if(main.capacitySettingsExpander!.IsExpanded||main.capacityValuationExpander!.IsExpanded)
                        throw new InvalidOperationException("A data refresh reopened a user-collapsed capacity section");
                    CapacityTestSettingDraft(main,unsavedSetting,draftBackgroundSeconds,savedBackgroundSeconds);
                    app.ConfigureCapacityUiPreview();
                    await CapacityTestWait(()=>CapacitySettingsBodyReady(main,"current",false),"Collapsed valuation content did not restore");
                    if(main.capacitySettingsExpander.IsExpanded||main.capacityValuationExpander.IsExpanded)
                        throw new InvalidOperationException("Restoring sampled data reopened a user-collapsed capacity section");
                    section.Expand();
                    Program.Log.Write("INFO","CapacityUiTest","Collapsed settings stay collapsed across Changed; unsaved input retained");
                }
                expand.Expand();
                await CapacityTestWait(()=>main.capacityValuationExpander?.IsExpanded==true&&main.capacityValuationContent?.ActualHeight>0,"Inline valuation expander did not reopen");
                if(AppWindow.Size.Width!=initialSize.Width||AppWindow.Size.Height!=initialSize.Height)
                    throw new InvalidOperationException("Opening settings resized the compact window");
                Program.Log.Write("INFO","CapacityUiTest",$"Capacity settings navigate/scroll/collapse passed: {scenario}");
            }
            await VerifyMainCapacityInfoEntry();
            Program.Log.Write("INFO","CapacityUiTest","Unsaved settings value and control identity survived refresh and repeated entry");
            Program.Log.Write("INFO","CapacityUiTest","Capacity settings navigation and live content passed");
            Program.Log.Write("INFO","CapacityUiTest","Capacity detail entry and compact layout passed");
        }
        catch(Exception ex){Program.Log.Write("ERROR","CapacityUiTest",ex.ToString());}
    }
    private static void CapacityTestExpand(Expander expander)
    {
        if(new ExpanderAutomationPeer(expander).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
            throw new InvalidOperationException("Settings expander has no accessible expand action");
        expand.Expand();
    }
    private async Task VerifyCompactRefreshStability()
    {
        app.ConfigureCompactRefreshPreview(0);
        await CapacityTestDispatcherSettled(this);
        await Task.Delay(120);
        await CapacityTestDispatcherSettled(this);
        var root=(FrameworkElement)Content;
        var ids=new[]{"compact-quota-card","compact-usage-card","compact-capacity-row","compact-plan-row","compact-footer-row",
            "compact-token-value","compact-request-value","compact-quota-value-0","compact-quota-progress-0","compact-quota-reset-0"};
        FrameworkElement Find(string id)=>CapacityTestDescendants(root).OfType<FrameworkElement>().Single(element=>AutomationProperties.GetAutomationId(element)==id);
        (Windows.Foundation.Rect Slot,Windows.Foundation.Rect Bounds) Geometry(FrameworkElement element)
        {
            var slot=LayoutInformation.GetLayoutSlot(element);
            if(VisualTreeHelper.GetParent(element) is not UIElement parent)throw new InvalidOperationException("Compact control has no layout parent");
            // TextBlock.ActualWidth is ink width, not its reserved layout slot.
            return (slot,parent.TransformToVisual(root).TransformBounds(slot));
        }
        var baseline=ids.ToDictionary(id=>id,id=>
        {
            var element=Find(id);var geometry=Geometry(element);
            return (Element:element,geometry.Slot,geometry.Bounds,FontSize:element is TextBlock text?text.FontSize:0);
        });
        var size=AppWindow.Size;var position=AppWindow.Position;
        var phase="baseline";string? failure=null;var layoutChecks=0;var windowChanges=0;
        bool Near(Windows.Foundation.Rect a,Windows.Foundation.Rect b)=>Math.Abs(a.X-b.X)<=.5&&Math.Abs(a.Y-b.Y)<=.5&&Math.Abs(a.Width-b.Width)<=.5&&Math.Abs(a.Height-b.Height)<=.5;
        void CheckGeometry()
        {
            if(failure is not null)return;
            try
            {
                foreach(var pair in baseline)
                {
                    var element=Find(pair.Key);var geometry=Geometry(element);
                    if(!ReferenceEquals(element,pair.Value.Element))throw new InvalidOperationException("Persistent control replaced: "+pair.Key);
                    if(!Near(geometry.Slot,pair.Value.Slot)||!Near(geometry.Bounds,pair.Value.Bounds))
                        throw new InvalidOperationException($"Compact geometry changed: {pair.Key}; slot {pair.Value.Slot} -> {geometry.Slot}; bounds {pair.Value.Bounds} -> {geometry.Bounds}");
                    if(element is TextBlock text&&text.FontSize!=pair.Value.FontSize)throw new InvalidOperationException("Compact refresh changed font size: "+pair.Key);
                }
                layoutChecks++;
            }
            catch(Exception ex){failure=phase+": "+ex.Message;}
        }
        void LayoutChanged(object? sender,object e)=>CheckGeometry();
        void WindowChanged(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowChangedEventArgs e)
        {
            if(!e.DidPositionChange&&!e.DidSizeChange)return;
            windowChanges++;
            failure??=$"{phase}: compact window changed during data refresh/reopen: size={sender.Size.Width}x{sender.Size.Height}; position={sender.Position.X},{sender.Position.Y}; resized={e.DidSizeChange}; moved={e.DidPositionChange}";
        }
        root.LayoutUpdated+=LayoutChanged;AppWindow.Changed+=WindowChanged;
        var tokenTexts=new HashSet<string>();var percentTexts=new HashSet<string>();var resetTexts=new HashSet<string>();
        try
        {
            for(var step=1;step<=18;step++)
            {
                phase="refresh-"+step;
                app.ConfigureCompactRefreshPreview(step);
                await CapacityTestDispatcherSettled(this);
                await Task.Delay(65); // Observe layout frames and native size/position notifications.
                CheckGeometry();
                var tokens=(TextBlock)Find("compact-token-value");var requests=(TextBlock)Find("compact-request-value");
                var percent=(TextBlock)Find("compact-quota-value-0");
                var rows=app.Events.Where(item=>item.LocalDate==DateTime.Today.ToString("yyyy-MM-dd")).ToList();
                if(tokens.Text!=UsageNumbers.Compact(rows.Sum(item=>item.Tokens.Total))||requests.Text!=rows.Count.ToString("N0"))
                    throw new InvalidOperationException("Stable compact controls stopped updating token/request values");
                if(percent.Text!=app.Quota.PrimaryWindows.Single().RemainingText||((ProgressBar)Find("compact-quota-progress-0")).Value!=app.Quota.PrimaryWindows.Single().Remaining)
                    throw new InvalidOperationException("Stable compact controls stopped updating quota values");
                tokenTexts.Add(tokens.Text);percentTexts.Add(percent.Text);resetTexts.Add(((TextBlock)Find("compact-quota-reset-0")).Text);
                if(AppWindow.Size.Width!=size.Width||AppWindow.Size.Height!=size.Height||AppWindow.Position.X!=position.X||AppWindow.Position.Y!=position.Y)
                    failure??=phase+": compact window bounds changed";
                if(failure is not null)throw new InvalidOperationException(failure);
            }
            if(tokenTexts.Count<4||percentTexts.Count<6||resetTexts.Count<3)throw new InvalidOperationException("Compact stability fixtures did not exercise changing values and countdowns");
            phase="hide-and-reopen";
            Hide();ShowPanel();
            if(AppWindow.Size.Width!=size.Width||AppWindow.Size.Height!=size.Height)
                failure??="Reopened compact window used an intermediate or changed size";
            await CapacityTestDispatcherSettled(this);
            await Task.Delay(160);
            CheckGeometry();
            if(failure is not null)throw new InvalidOperationException(failure);
            Program.Log.Write("INFO","CapacityUiTest",$"Compact refresh stable: 18 changes; layoutChecks={layoutChecks}; nativeBoundsChanges={windowChanges}; {size.Width}x{size.Height}; hide/reopen has no second resize");
        }
        finally{root.LayoutUpdated-=LayoutChanged;AppWindow.Changed-=WindowChanged;}
    }
    private async Task VerifyMainCapacityInfoEntry()
    {
        var main=app.CapacitySettingsTestDashboard??throw new InvalidOperationException("Main capacity test window is unavailable");
        main.ShowPage("quota");
        await CapacityTestDispatcherSettled(main);
        var root=(FrameworkElement)main.Content;
        var entry=CapacityTestDescendants(root).OfType<Button>().Single(button=>AutomationProperties.GetName(button)==L10n.T("capacity.info"));
        CapacityTestInvoke(entry);
        await CapacityTestWait(()=>main.capacityInfoFlyout?.IsOpen==true,"Main quota info popover did not open");
        if(main.capacityEvidenceButton is not {IsEnabled:true,Visibility:Visibility.Visible})
            throw new InvalidOperationException("Main window lost its settings details entry");
        CapacityTestInvoke(entry);
        await CapacityTestWait(()=>main.capacityInfoFlyout?.IsOpen!=true,"Main quota info popover did not close");
        Program.Log.Write("INFO","CapacityUiTest","Compact popover has no buttons; main details entry and footer settings gear retained");
    }
    private bool CapacitySettingsBodyReady(Dashboard main,string scenario,bool requireVisible=true)
    {
        if(main.selectedPage!="settings"||main.capacityValuationContent is not {} content||requireVisible&&content.ActualHeight<=0)return false;
        var text=string.Join("\n",content.Children.OfType<TextBlock>().Select(t=>t.Text));
        var keys=scenario=="empty"?new[]{"capacity.noValuationEvidence","capacity.currentSamplingTitle","capacity.reason.unverified-ownership"}:
            scenario=="historical"?new[]{"capacity.legacyEvidenceDetails","capacity.evidenceLegacyHistorical"}:
            new[]{"capacity.modeComposition","capacity.modelComposition","capacity.reasoningComposition","capacity.workloadMixed","capacity.valuationConditional"};
        return keys.All(key=>text.Contains(L10n.T(key),StringComparison.Ordinal))&&text.Contains(app.WeeklyCapacityProgress,StringComparison.Ordinal);
    }
    private void CapacityTestSettingDraft(Dashboard main,NumberBox input,double draft,int saved)
    {
        if(!CapacityTestDescendants((FrameworkElement)main.Content).Any(element=>ReferenceEquals(element,input))||input.Value!=draft)
            throw new InvalidOperationException("A refresh or repeated settings entry replaced the unsaved settings input");
        if(app.Config.BackgroundSeconds!=saved)throw new InvalidOperationException("UI verification unexpectedly saved a settings draft");
    }
    private static async Task CapacityTestDispatcherSettled(Dashboard main)
    {
        var settled=new TaskCompletionSource<bool>();
        if(!main.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>settled.TrySetResult(true)))
            throw new InvalidOperationException("Settings dispatcher is unavailable");
        await settled.Task;
        ((FrameworkElement)main.Content).UpdateLayout();
    }
    private static string CapacityTestScrollGeometry(ScrollViewer viewport,FrameworkElement header)=>
        $"offset={viewport.VerticalOffset:0.##}; scrollable={viewport.ScrollableHeight:0.##}; extent={viewport.ExtentHeight:0.##}; viewport={viewport.ViewportHeight:0.##}; headerY={header.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point()).Y:0.##}";
    private static bool CapacityTestVisibleIn(FrameworkElement element,ScrollViewer viewport)
    {
        if(element.ActualHeight<=0||viewport.ViewportHeight<=0)return false;
        var bounds=element.TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0,0,element.ActualWidth,element.ActualHeight));
        return bounds.Y>=-1&&bounds.Y+bounds.Height<=viewport.ViewportHeight+1;
    }
    private static IEnumerable<DependencyObject> CapacityTestDescendants(DependencyObject? root)
    {
        if(root is null)yield break;
        yield return root;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            foreach(var child in CapacityTestDescendants(VisualTreeHelper.GetChild(root,index)))yield return child;
    }
    private static void CapacityTestInvoke(Button button)
    {
        if(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Button has no accessible Invoke action");
        invoke.Invoke();
    }
    private static async Task CapacityTestWait(Func<bool> condition,string failure)
    {
        for(var retry=0;retry<100;retry++){if(condition())return;await Task.Delay(25);}
        throw new InvalidOperationException(failure);
    }
}
