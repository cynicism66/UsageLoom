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
            foreach(var scenario in new[]{"current","historical","empty","revalidating"})
            {
                var historical=scenario=="historical";var empty=scenario=="empty";
                app.ConfigureCapacityUiPreview(historical,empty,scenario=="revalidating");await Task.Delay(120);
                var root=(FrameworkElement)Content;root.UpdateLayout();
                var initialSize=AppWindow.Size;
                var entry=CapacityTestDescendants(root).OfType<Button>().Single(b=>AutomationProperties.GetName(b)==L10n.T("capacity.info"));
                CapacityTestInvoke(entry);
                await CapacityTestWait(()=>capacityInfoFlyout?.IsOpen==true,"Info popover did not open");
                await Task.Delay(140);root.UpdateLayout();
                if(scenario=="revalidating"&&(!capacityInfoText.Text.Contains(L10n.T("capacity.compactRevalidatingEstimate"),StringComparison.Ordinal)||
                    capacityInfoText.Text.Contains(L10n.T("capacity.compactCurrentEstimate"),StringComparison.Ordinal)))
                    throw new InvalidOperationException("Pending revaluation mislabeled the previous amount as current");
                if(capacityEvidenceButton is not {Visibility:Visibility.Visible,IsEnabled:true} button||button.ActualHeight<=0)
                    throw new InvalidOperationException("Valuation detail entry is missing or unreachable");
                DependencyObject? parent=capacityInfoText;
                while(parent is not null&&parent is not FlyoutPresenter)parent=VisualTreeHelper.GetParent(parent);
                if(parent is not FlyoutPresenter presenter)throw new InvalidOperationException("Info popover presenter not found");
                presenter.UpdateLayout();
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
                foreach(var element in new FrameworkElement[]{capacityInfoText,button})
                {
                    var bounds=element.TransformToVisual(presenter).TransformBounds(new Windows.Foundation.Rect(0,0,element.ActualWidth,element.ActualHeight));
                    if(bounds.X<-.5||bounds.Y<-.5||bounds.X+bounds.Width>presenter.ActualWidth+.5||bounds.Y+bounds.Height>presenter.ActualHeight+.5)
                        throw new InvalidOperationException("Compact popover content exceeds its presenter bounds");
                }
                Program.Log.Write("INFO","CapacityUiTest",$"Compact popover no-scroll/no-clipping passed: {scenario}");
                CapacityTestInvoke(button);
                ContentDialog? dialog=null;
                await CapacityTestWait(()=>
                {
                    dialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(p=>CapacityTestDescendants(p.Child))
                        .OfType<ContentDialog>().FirstOrDefault(d=>d.Title as string==L10n.T("capacity.valuationDetails"));
                    return dialog is not null;
                },"Valuation detail dialog did not open");
                await Task.Delay(140);dialog!.UpdateLayout();
                if(dialog.Content is not ScrollViewer detail||detail.HorizontalScrollMode!=ScrollMode.Disabled||detail.HorizontalScrollBarVisibility!=ScrollBarVisibility.Disabled||detail.ScrollableWidth>.5)
                    throw new InvalidOperationException("Valuation detail content can scroll horizontally");
                var text=string.Join("\n",CapacityTestDescendants(detail).OfType<TextBlock>().Select(t=>t.Text));
                foreach(var key in empty?new[]{"capacity.noValuationEvidence","capacity.currentSamplingTitle","capacity.reason.unverified-ownership"}:
                    historical?new[]{"capacity.legacyEvidenceDetails","capacity.evidenceLegacyHistorical"}:
                    new[]{"capacity.modeComposition","capacity.modelComposition","capacity.reasoningComposition","capacity.workloadMixed","capacity.valuationConditional"})
                    if(!text.Contains(L10n.T(key),StringComparison.Ordinal))throw new InvalidOperationException("Valuation detail section missing: "+key);
                if(!text.Contains(app.WeeklyCapacityProgress,StringComparison.Ordinal))throw new InvalidOperationException("Full sampling explanation was lost from details");
                if(!historical&&!empty&&detail.ScrollableHeight<=0)throw new InvalidOperationException("Mixed-workload detail fixture did not exercise vertical scrolling");
                detail.ChangeView(null,detail.ScrollableHeight,null,true);await Task.Delay(70);
                if(detail.ScrollableHeight>0&&Math.Abs(detail.VerticalOffset-detail.ScrollableHeight)>1)
                    throw new InvalidOperationException("Valuation detail bottom is unreachable");
                var close=CapacityTestDescendants(dialog).OfType<Button>().FirstOrDefault(b=>b.Content as string==dialog.CloseButtonText||b.Name=="CloseButton")
                    ??throw new InvalidOperationException("Valuation dialog close button unavailable");
                CapacityTestInvoke(close);
                await CapacityTestWait(()=>!capacityEvidenceDialogOpen,"Valuation detail dialog did not close");
                await Task.Delay(80);
                if(AppWindow.Size.Width!=initialSize.Width||AppWindow.Size.Height!=initialSize.Height)
                    throw new InvalidOperationException("Opening details resized the compact window");
                Program.Log.Write("INFO","CapacityUiTest",$"Valuation detail open/scroll/close passed: {scenario}");
            }
            Program.Log.Write("INFO","CapacityUiTest","Capacity detail entry and compact layout passed");
        }
        catch(Exception ex){Program.Log.Write("ERROR","CapacityUiTest",ex.ToString());}
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
        for(var retry=0;retry<50;retry++){if(condition())return;await Task.Delay(25);}
        throw new InvalidOperationException(failure);
    }
}
