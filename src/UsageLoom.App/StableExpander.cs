using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace UsageLoom.App;

/// <summary>Retains the native Expander while removing its two-stage content collapse.</summary>
internal static class StableExpander
{
    public static Expander Configure(Expander expander)
    {
        expander.VerticalAlignment=VerticalAlignment.Top;
        expander.Template=(ControlTemplate)Application.Current.Resources["StableExpanderTemplate"];
        return expander;
    }
    private static FrameworkElement? FindContent(DependencyObject root)
    {
        if(root is FrameworkElement element&&element.Name=="ExpanderContent")return element;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            if(FindContent(VisualTreeHelper.GetChild(root,index)) is {} content)return content;
        return null;
    }
    internal static bool HasVisibleContent(Expander expander)=>FindContent(expander)?.Visibility==Visibility.Visible;
}
