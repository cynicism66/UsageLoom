using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private async Task VerifySolidDropDownAsync(ComboBox choice)
    {
        // Inspect the opened native template, not only the resource dictionary.
        var section=((StackPanel)scrollContent.Content).Children.OfType<Expander>()
            .FirstOrDefault(e=>CapacityTestDescendants(e).Contains(choice));
        if(section is not null)section.IsExpanded=true;
        choice.UpdateLayout();choice.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false});
        await CapacityTestDispatcherSettled(this);
        var selected=choice.SelectedIndex;
        try
        {
            choice.IsDropDownOpen=true;
            await Task.Delay(180);
            var border=VisualTreeHelper.GetOpenPopupsForXamlRoot(choice.XamlRoot)
                .SelectMany(p=>CapacityTestDescendants(p.Child)).OfType<Border>()
                .Single(b=>b.Name=="PopupBorder");
            if(border.Background is not SolidColorBrush fill||fill.Color.A!=255||fill.Opacity!=1||
               border.BorderBrush is not SolidColorBrush stroke||stroke.Color.A!=255||stroke.Opacity!=1||
               border.BorderThickness.Left<1||border.BorderThickness.Top<1||fill.Color==stroke.Color)
                throw new InvalidOperationException($"Dropdown is not opaque with a distinct solid border: fill={border.Background?.GetType().Name}/{(border.Background as SolidColorBrush)?.Color}/{border.Background?.Opacity}, stroke={border.BorderBrush?.GetType().Name}/{(border.BorderBrush as SolidColorBrush)?.Color}/{border.BorderBrush?.Opacity}, thickness={border.BorderThickness}");
            if(!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                var expected=choice.ActualTheme==ElementTheme.Light?Windows.UI.Color.FromArgb(255,255,255,255):Windows.UI.Color.FromArgb(255,41,45,53);
                if(fill.Color!=expected)throw new InvalidOperationException("Dropdown did not follow the active theme");
            }
        }
        finally{choice.IsDropDownOpen=false;}
        if(choice.SelectedIndex!=selected)throw new InvalidOperationException("Opening dropdown changed selection");
    }
}
